using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Persists clipboard history (text + images) to disk so items survive app restarts.
    /// Uses an append-only journal for writes (fast, no full-file rewrite) with periodic compaction.
    /// Images are stored permanently in %AppData%\FlyShelf\Images\.
    /// Metadata is serialized to %AppData%\FlyShelf\clipboard_history.json (compacted form).
    /// Journal entries are appended to %AppData%\FlyShelf\clipboard_journal.jsonl.
    /// </summary>
    public static class ClipboardHistoryManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _historyPath = Path.Combine(_appDataDir, "clipboard_history.json");
        private static readonly string _journalPath = Path.Combine(_appDataDir, "clipboard_journal.jsonl");
        private static readonly string _imagesDir = Path.Combine(_appDataDir, "Images");

        private static Timer? _debounceTimer;
        private static Timer? _compactionTimer;
        private static readonly object _lock = new object();
        private static int _journalEntryCount = 0;
        private static volatile bool _isHistoryFullyLoaded = false;
        private static int _maxLoadedItemCount = 0;

        /// <summary>Maximum items to retain in history. Oldest items are evicted beyond this cap.</summary>
        private static int MAX_HISTORY_ITEMS => FlyShelf.Classes.LicenseManager.GetHistoryCap();
        private const int ABSOLUTE_MAX_ITEMS = 2500;
        /// <summary>Compact after this many journal entries to prevent unbounded file growth.</summary>
        private const int COMPACTION_THRESHOLD = 100;

        /// <summary>
        /// Returns the permanent image storage directory, creating it if needed.
        /// </summary>
        public static string GetPersistentImageDir()
        {
            Directory.CreateDirectory(_imagesDir);
            return _imagesDir;
        }

        /// <summary>
        /// Generates a unique permanent path for a clipboard image.
        /// </summary>
        public static string GetPersistentImagePath()
        {
            Directory.CreateDirectory(_imagesDir);
            return Path.Combine(_imagesDir, $"FlyShelf_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString().Substring(0, 4)}.png");
        }

        /// <summary>
        /// Loads persisted clipboard history from disk.
        /// First loads the compacted snapshot, then replays the journal on top.
        /// Returns empty list if no history exists or on error.
        /// </summary>
        public static List<ViewModels.ClipboardItem> LoadHistory()
        {
            // M2 FIX: Read file content OUTSIDE the lock to avoid holding it during
            // potentially slow file I/O. Only parsing and collection mutation inside the lock.
            string? snapshotJson = null;
            string? backupJson = null;
            string[]? journalLines = null;

            try
            {
                if (File.Exists(_historyPath))
                {
                    try { snapshotJson = RunWithRetry(() => File.ReadAllText(_historyPath)); }
                    catch (JsonException)
                    {
                        string backupPath = _historyPath + ".bak";
                        if (File.Exists(backupPath))
                            backupJson = RunWithRetry(() => File.ReadAllText(backupPath));
                        else
                            throw;
                    }
                    catch { /* Will fall through to empty list */ }
                }
                if (File.Exists(_journalPath))
                {
                    try { journalLines = RunWithRetry(() => File.ReadAllLines(_journalPath)); }
                    catch { /* Will fall through with null journalLines */ }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("HISTORY_LOAD_ERROR", $"Failed to read history files: {ex.Message}");
                return new List<ViewModels.ClipboardItem>();
            }

            lock (_lock)
            {
                try
                {
                    var items = new List<ViewModels.ClipboardItem>();

                    // Step 1: Parse compacted snapshot
                    string? jsonToParse = snapshotJson ?? backupJson;
                    if (jsonToParse != null)
                    {
                        try
                        {
                            var snapshot = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(jsonToParse);
                            if (snapshot != null)
                            {
                                foreach (var snapshotItem in snapshot)
                                {
                                    if (snapshotItem.IsPassword)
                                    {
                                        snapshotItem.RawContent = SecureStorage.Decrypt(snapshotItem.RawContent);
                                    }
                                    if (IsValidClipboardItem(snapshotItem))
                                        items.Add(snapshotItem);
                                    else
                                        Logger.LogAction("HISTORY_CLEANUP", $"Pruned dead/deleted snapshot item: {snapshotItem.FileName ?? snapshotItem.RawContent}");
                                }
                            }
                            if (backupJson != null && snapshotJson == null)
                            {
                                Logger.LogAction("HISTORY_RECOVERY", $"Successfully recovered {items.Count} valid items from backup database!");
                            }
                        }
                        catch (JsonException jsonEx)
                        {
                            Logger.LogAction("HISTORY_LOAD_ERROR", $"Snapshot failed to deserialize: {jsonEx.Message}");
                        }
                    }

                    // Step 2: Replay journal entries on top of snapshot
                    if (journalLines != null)
                    {
                        foreach (var line in journalLines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                                if (entry == null) continue;

                                switch (entry.Action)
                                {
                                    case "add":
                                        if (entry.Item != null)
                                        {
                                            if (entry.Item.IsPassword)
                                            {
                                                entry.Item.RawContent = SecureStorage.Decrypt(entry.Item.RawContent);
                                            }
                                            if (IsValidClipboardItem(entry.Item))
                                                items.Insert(0, entry.Item);
                                            else
                                                Logger.LogAction("HISTORY_CLEANUP", $"Pruned dead/deleted item from journal: {entry.Item.FileName ?? entry.Item.RawContent}");
                                        }
                                        break;
                                    case "delete":
                                        if (!string.IsNullOrEmpty(entry.ItemId))
                                            items.RemoveAll(i => i.ItemId == entry.ItemId);
                                        break;
                                    case "clear":
                                        items.Clear();
                                        break;
                                }
                            }
                            catch { /* Skip malformed journal entries */ }
                        }
                        _journalEntryCount = journalLines.Length;
                    }

                    // Enforce cap
                    if (items.Count > MAX_HISTORY_ITEMS)
                        items = items.Take(MAX_HISTORY_ITEMS).ToList();

                    _maxLoadedItemCount = items.Count;
                    _isHistoryFullyLoaded = true;

                    Logger.LogAction("HISTORY_LOAD", $"Loaded {items.Count} items (snapshot + {_journalEntryCount} journal entries)");

                    // If journal was large, auto-compact on load
                    if (_journalEntryCount > 0)
                    {
                        var itemsCopy = new List<ViewModels.ClipboardItem>(items);
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try { CompactNow(itemsCopy); }
                            catch (Exception ex) { Logger.LogAction("HISTORY_COMPACT", $"Auto-compact on load failed: {ex.Message}"); }
                        });
                    }

                    return items;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HISTORY_LOAD_ERROR", $"Failed to load history: {ex.Message}");
                    return new List<ViewModels.ClipboardItem>();
                }
            }
        }

        /// <summary>
        /// Appends a new item to the journal (fast — async I/O, no blocking).
        /// </summary>
        public static void AppendToJournal(ViewModels.ClipboardItem item)
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);
                var diskItem = CloneForDisk(item);
                var entry = new JournalEntry { Action = "add", Item = diskItem, ItemId = GetItemId(item) };
                var json = JsonSerializer.Serialize(entry);
                var line = json + "\n";

                // Fire-and-forget async I/O — doesn't block caller
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_lock)
                    {
                        try
                        {
                            RunWithRetry(() => File.AppendAllText(_journalPath, line));
                            _journalEntryCount++;

                            // Auto-compact when journal gets large
                            if (_journalEntryCount >= COMPACTION_THRESHOLD)
                            {
                                ScheduleCompaction();
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("JOURNAL_WRITE_ERROR", $"Async journal write failed: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("JOURNAL_WRITE_ERROR", $"Failed to prepare journal entry: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends a delete entry to the journal (async — non-blocking).
        /// </summary>
        public static void AppendDeleteToJournal(ViewModels.ClipboardItem item)
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);
                var entry = new JournalEntry { Action = "delete", ItemId = GetItemId(item) };
                var json = JsonSerializer.Serialize(entry);
                var line = json + "\n";

                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_lock)
                    {
                        try
                        {
                            RunWithRetry(() => File.AppendAllText(_journalPath, line));
                            _journalEntryCount++;
                        }
                        catch (Exception ex) { Logger.LogAction("HISTORY_JOURNAL", $"Async delete write failed: {ex.Message}"); }
                    }
                });
            }
            catch (Exception ex) { Logger.LogAction("HISTORY_JOURNAL", $"Failed to prepare delete entry: {ex.Message}"); }
        }

        /// <summary>
        /// Saves clipboard history to disk. Debounced — waits 1500ms after last call to avoid disk thrashing.
        /// This performs a FULL compaction (snapshot rewrite + journal clear).
        /// Accepts a copy of the list to avoid collection modified exceptions.
        /// </summary>
        private static volatile int _saveGeneration;

        public static void SaveHistoryDebounced(List<ViewModels.ClipboardItem> items)
        {
            int generation = System.Threading.Interlocked.Increment(ref _saveGeneration);

            var newTimer = new Timer(_ =>
            {
                // Only run if no newer save was requested while we were waiting
                if (generation != _saveGeneration) return;

                try
                {
                    var snapshot = items;
                    // Enforce cap before saving
                    if (snapshot.Count > MAX_HISTORY_ITEMS)
                        snapshot = snapshot.Take(MAX_HISTORY_ITEMS).ToList();

                    CompactNow(snapshot);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HISTORY_SAVE_ERROR", $"Failed to save history: {ex.Message}");
                }
            }, null, 1500, Timeout.Infinite);

            var oldTimer = Interlocked.Exchange(ref _debounceTimer, newTimer);
            oldTimer?.Dispose();
        }

        /// <summary>
        /// Writes the full snapshot to disk and clears the journal.
        /// Atomic: writes to temp file first, then renames.
        /// </summary>
        private static void CompactNow(List<ViewModels.ClipboardItem> items)
        {
            lock (_lock)
            {
                try
                {
                    // SAFETY: Refuse to overwrite a larger database with a smaller one
                    // This prevents data loss from race conditions where a stale snapshot
                    // (e.g. from before deferred items loaded) would overwrite the full DB.
                    if (!_isHistoryFullyLoaded)
                    {
                        Logger.LogAction("HISTORY_COMPACT_ABORT", "Refusing to compact: history not fully loaded yet.");
                        return;
                    }

                    if (items.Count < _maxLoadedItemCount * 0.5 && _maxLoadedItemCount > 10)
                    {
                        Logger.LogAction("HISTORY_COMPACT_ABORT", $"Refusing to compact: new={items.Count} vs cached max={_maxLoadedItemCount}. Possible stale snapshot.");
                        return;
                    }

                    _maxLoadedItemCount = Math.Max(_maxLoadedItemCount, items.Count);

                    var diskItems = items.Select(CloneForDisk).ToList();
                    var options = new JsonSerializerOptions { WriteIndented = false };
                    var json = JsonSerializer.Serialize(diskItems, options);

                    // Write to temp file first, then atomic rename for safety
                    var tempPath = _historyPath + ".tmp";
                    if (!DiskSpaceHelper.HasSufficientDiskSpace(_historyPath, json.Length * 2 + 1_000_000))
                    {
                        Logger.LogAction("CLIPBOARD", "Insufficient disk space to save history — skipping write");
                        return;
                    }
                    RunWithRetry(() => File.WriteAllText(tempPath, json));

                    // Create a backup copy before moving the temp file to historyPath
                    if (File.Exists(_historyPath))
                    {
                        try
                        {
                            RunWithRetry(() => File.Copy(_historyPath, _historyPath + ".bak", true));
                        }
                        catch { } // Best-effort: failure is acceptable
                    }

                    RunWithRetry(() => File.Move(tempPath, _historyPath, true));

                    // Clear journal
                    if (File.Exists(_journalPath))
                        RunWithRetry(() => File.Delete(_journalPath));
                    _journalEntryCount = 0;

                    Logger.LogAction("HISTORY_COMPACT", $"Compacted {items.Count} items, journal cleared");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HISTORY_COMPACT_ERROR", $"Compaction failed: {ex.Message}");
                    // CHM-1 FIX: Clean up stale .tmp file so it doesn't accumulate on repeated failures
                    try { File.Delete(_historyPath + ".tmp"); } catch { } // Best-effort: failure is acceptable
                }
            }
        }

        /// <summary>
        /// Schedules a compaction 5 seconds in the future (debounced).
        /// </summary>
        private static void ScheduleCompaction()
        {
            var newTimer = new Timer(_ =>
            {
                // Read current state from disk for compaction
                try
                {
                    var items = LoadHistoryRaw();
                    if (items.Count > 0)
                        CompactNow(items);
                }
                catch (Exception ex) { Logger.LogAction("HISTORY_COMPACT", $"Scheduled compaction failed: {ex.Message}"); }
            }, null, 5000, Timeout.Infinite);
            var oldTimer = Interlocked.Exchange(ref _compactionTimer, newTimer);
            oldTimer?.Dispose();
        }

        /// <summary>
        /// Internal: loads without logging or auto-compaction (used by ScheduleCompaction).
        /// </summary>
        private static List<ViewModels.ClipboardItem> LoadHistoryRaw()
        {
            lock (_lock)
            {
                var items = new List<ViewModels.ClipboardItem>();
                if (File.Exists(_historyPath))
                {
                    try
                    {
                        var json = RunWithRetry(() => File.ReadAllText(_historyPath));
                        var snapshot = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(json);
                        if (snapshot != null)
                        {
                            foreach (var snapshotItem in snapshot)
                            {
                                if (snapshotItem.IsPassword)
                                {
                                    snapshotItem.RawContent = SecureStorage.Decrypt(snapshotItem.RawContent);
                                }
                                if (IsValidClipboardItem(snapshotItem))
                                    items.Add(snapshotItem);
                            }
                        }
                    }
                    catch (Exception ex) { Logger.LogAction("HISTORY_LOAD", $"Failed to load history snapshot: {ex.Message}"); }
                }
                if (File.Exists(_journalPath))
                {
                    try
                    {
                        foreach (var line in RunWithRetry(() => File.ReadAllLines(_journalPath)))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                                if (entry?.Action == "add" && entry.Item != null)
                                {
                                    if (entry.Item.IsPassword)
                                    {
                                        entry.Item.RawContent = SecureStorage.Decrypt(entry.Item.RawContent);
                                    }
                                    if (IsValidClipboardItem(entry.Item))
                                        items.Insert(0, entry.Item);
                                }
                                else if (entry?.Action == "delete" && entry.ItemId != null)
                                    items.RemoveAll(i => i.ItemId == entry.ItemId);
                                else if (entry?.Action == "clear")
                                    items.Clear();
                            }
                            catch (Exception ex) { Logger.LogAction("HISTORY_JOURNAL", $"Failed to parse journal line: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) { Logger.LogAction("HISTORY_LOAD", $"Failed to read journal: {ex.Message}"); }
                }
                return items.Take(MAX_HISTORY_ITEMS).ToList();
            }
        }

        /// <summary>
        /// Deletes the persistent image file for a clipboard item (when user deletes an item).
        /// </summary>
        public static void DeletePersistentImage(ViewModels.ClipboardItem item)
        {
            if (item != null)
            {
                DeletePersistentImage(item.FilePath, item.ItemType);
            }
        }

        /// <summary>
        /// Deletes the persistent image file for a clipboard item in a thread-safe way.
        /// </summary>
        public static void DeletePersistentImage(string filePath, ViewModels.ClipboardItemType itemType)
        {
            try
            {
                if (itemType == ViewModels.ClipboardItemType.Image ||
                    itemType == ViewModels.ClipboardItemType.QRCode)
                {
                    if (!string.IsNullOrEmpty(filePath) && 
                        filePath.Contains(_imagesDir) && 
                        File.Exists(filePath))
                    {
                        RunWithRetry(() => File.Delete(filePath));
                    }
                }
            }
            catch (Exception ex) { Logger.LogAction("HISTORY_DELETE", $"Failed to delete persistent image: {ex.Message}"); }
        }

        /// <summary>
        /// Cleans up temporary sandbox folders older than 24 hours in the %TEMP%\FlyShelf_Sandbox directory.
        /// </summary>
        public static void ScavengeSandboxDirectories()
        {
            try
            {
                string parentSandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox");
                if (!Directory.Exists(parentSandboxDir)) return;

                var subDirs = Directory.GetDirectories(parentSandboxDir, "*", SearchOption.TopDirectoryOnly);
                int prunedCount = 0;
                foreach (var dir in subDirs)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (dirInfo.Exists && dirInfo.LastWriteTime < DateTime.Now.AddDays(-1))
                        {
                            Directory.Delete(dir, true);
                            prunedCount++;
                        }
                    }
                    catch { /* Ignore locked or non-deletable directories */ }
                }
                if (prunedCount > 0)
                {
                    Logger.LogAction("SANDBOX_SCAVENGE", $"Successfully pruned {prunedCount} stale sandbox directories.");
                }

                // If parent directory is completely empty, clean it up too
                if (!Directory.EnumerateFileSystemEntries(parentSandboxDir).Any())
                {
                    try { Directory.Delete(parentSandboxDir); } catch { } // Best-effort: failure is acceptable
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SANDBOX_SCAVENGE_ERR", $"Scavenge failed: {ex.Message}");
            }

            // Also clean up stale FlyShelf zip files older than 24 hours
            ScavengeStaleZipFiles();
        }

        /// <summary>
        /// Deletes FlyShelf_*.zip temp files older than 24 hours from %TEMP%.
        /// Prevents unbounded temp storage growth from zip creation.
        /// </summary>
        public static void ScavengeStaleZipFiles()
        {
            try
            {
                string tempDir = Path.GetTempPath();
                var zipFiles = Directory.GetFiles(tempDir, "FlyShelf_*.zip", SearchOption.TopDirectoryOnly);
                int prunedCount = 0;
                long reclaimedBytes = 0;

                foreach (var file in zipFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Exists && fileInfo.LastWriteTime < DateTime.Now.AddDays(-1))
                        {
                            reclaimedBytes += fileInfo.Length;
                            File.Delete(file);
                            prunedCount++;
                        }
                    }
                    catch { /* Ignore locked files */ }
                }

                if (prunedCount > 0)
                {
                    double reclaimedMB = reclaimedBytes / (1024.0 * 1024.0);
                    Logger.LogAction("ZIP_SCAVENGE", $"Deleted {prunedCount} stale zip files, reclaimed {reclaimedMB:F1} MB.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("ZIP_SCAVENGE_ERR", $"Scavenge failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates if a clipboard item is valid (removes dead/deleted file entries immediately).
        /// </summary>
        private static bool IsValidClipboardItem(ViewModels.ClipboardItem item)
        {
            if (item == null) return false;

            bool isFileBased = item.ItemType == ViewModels.ClipboardItemType.Image ||
                               item.ItemType == ViewModels.ClipboardItemType.QRCode ||
                               item.ItemType == ViewModels.ClipboardItemType.File ||
                               item.ItemType == ViewModels.ClipboardItemType.Document ||
                               item.ItemType == ViewModels.ClipboardItemType.Pdf ||
                               item.ItemType == ViewModels.ClipboardItemType.Archive ||
                               item.ItemType == ViewModels.ClipboardItemType.Video ||
                               item.ItemType == ViewModels.ClipboardItemType.Audio ||
                               item.ItemType == ViewModels.ClipboardItemType.Presentation;

            if (isFileBased)
            {
                if (string.IsNullOrEmpty(item.FilePath) || (!File.Exists(item.FilePath) && !Directory.Exists(item.FilePath)))
                {
                    return false; // Skip dead or deleted file entries
                }
            }
            return true;
        }

        /// <summary>
        /// Generates a deterministic ID for a clipboard item (for journal delete tracking).
        /// </summary>
        private static T RunWithRetry<T>(Func<T> action, int retries = 3, int delayMs = 100)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    return action();
                }
                catch (IOException) when (i < retries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException) when (i < retries - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }
            return action();
        }

        private static void RunWithRetry(Action action, int retries = 3, int delayMs = 100)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (i < retries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException) when (i < retries - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }
            action();
        }

        /// <summary>
        /// Computes a deterministic FNV-1a hash from a string.
        /// Unlike String.GetHashCode(), this is stable across process restarts and .NET versions.
        /// </summary>
        private static int Fnv1aHash(string input)
        {
            const uint fnvOffsetBasis = 2166136261;
            const uint fnvPrime = 16777619;

            uint hash = fnvOffsetBasis;
            foreach (char c in input)
            {
                hash ^= (byte)(c & 0xFF);
                hash *= fnvPrime;
                hash ^= (byte)(c >> 8);
                hash *= fnvPrime;
            }
            return unchecked((int)hash);
        }

        private static string GetItemId(ViewModels.ClipboardItem item)
        {
            // Use a deterministic FNV-1a hash based on content — stable across process restarts.
            // String.GetHashCode() is randomized per-process in .NET 6+ and cannot be used.
            string contentKey = item.RawContent ?? item.FileName ?? item.FilePath ?? "";
            int stableHash = Fnv1aHash(contentKey);
            return $"{item.ItemType}_{item.DateCopied.Ticks}_{stableHash:X8}";
        }

        private static ViewModels.ClipboardItem CloneForDisk(ViewModels.ClipboardItem item)
        {
            if (item == null) return null!;
            return new ViewModels.ClipboardItem
            {
                DateCopied = item.DateCopied,
                FilePath = item.FilePath,
                FileName = item.FileName,
                Extension = item.Extension,
                ItemType = item.ItemType,
                FormattedSize = item.FormattedSize,
                RawContent = item.IsPassword ? SecureStorage.Encrypt(item.RawContent) : item.RawContent,
                IsPassword = item.IsPassword,
                IsPinned = item.IsPinned,
                AssociatedContextTitle = item.AssociatedContextTitle,
                SourceDeviceName = item.SourceDeviceName,
                SourceDeviceType = item.SourceDeviceType,
                TransferMethod = item.TransferMethod,
                ZippedArchivePath = item.ZippedArchivePath
            };
        }

        /// <summary>Journal entry for append-only log.</summary>
        private class JournalEntry
        {
            public string Action { get; set; } = ""; // "add", "delete", "clear"
            public ViewModels.ClipboardItem? Item { get; set; }
            public string? ItemId { get; set; }
        }
    }
}
