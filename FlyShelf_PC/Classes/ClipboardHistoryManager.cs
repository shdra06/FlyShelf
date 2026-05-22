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

        /// <summary>Maximum items to retain in history. Oldest items are evicted beyond this cap.</summary>
        private const int MAX_HISTORY_ITEMS = 1000;
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
            try
            {
                var items = new List<ViewModels.ClipboardItem>();

                // Step 1: Load compacted snapshot
                if (File.Exists(_historyPath))
                {
                    var json = File.ReadAllText(_historyPath);
                    var snapshot = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(json);
                    if (snapshot != null)
                        items.AddRange(snapshot);
                }

                // Step 2: Replay journal entries on top of snapshot
                if (File.Exists(_journalPath))
                {
                    var lines = File.ReadAllLines(_journalPath);
                    foreach (var line in lines)
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
                                        items.Insert(0, entry.Item);
                                    break;
                                case "delete":
                                    if (!string.IsNullOrEmpty(entry.ItemId))
                                        items.RemoveAll(i => GetItemId(i) == entry.ItemId);
                                    break;
                                case "clear":
                                    items.Clear();
                                    break;
                            }
                        }
                        catch { /* Skip malformed journal entries */ }
                    }
                    _journalEntryCount = lines.Length;
                }

                // Enforce cap
                if (items.Count > MAX_HISTORY_ITEMS)
                    items = items.Take(MAX_HISTORY_ITEMS).ToList();

                Logger.LogAction("HISTORY_LOAD", $"Loaded {items.Count} items (snapshot + {_journalEntryCount} journal entries)");

                // If journal was large, auto-compact on load
                if (_journalEntryCount > 0)
                {
                    var itemsCopy = new List<ViewModels.ClipboardItem>(items);
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { CompactNow(itemsCopy); }
                        catch { }
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

        /// <summary>
        /// Appends a new item to the journal (fast — single line append, no full rewrite).
        /// </summary>
        public static void AppendToJournal(ViewModels.ClipboardItem item)
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);
                var entry = new JournalEntry { Action = "add", Item = item, ItemId = GetItemId(item) };
                var json = JsonSerializer.Serialize(entry);
                lock (_lock)
                {
                    File.AppendAllText(_journalPath, json + "\n");
                    _journalEntryCount++;

                    // Auto-compact when journal gets large
                    if (_journalEntryCount >= COMPACTION_THRESHOLD)
                    {
                        ScheduleCompaction();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("JOURNAL_WRITE_ERROR", $"Failed to append journal: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends a delete entry to the journal.
        /// </summary>
        public static void AppendDeleteToJournal(ViewModels.ClipboardItem item)
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);
                var entry = new JournalEntry { Action = "delete", ItemId = GetItemId(item) };
                var json = JsonSerializer.Serialize(entry);
                lock (_lock)
                {
                    File.AppendAllText(_journalPath, json + "\n");
                    _journalEntryCount++;
                }
            }
            catch { }
        }

        /// <summary>
        /// Saves clipboard history to disk. Debounced — waits 500ms after last call to avoid disk thrashing.
        /// This performs a FULL compaction (snapshot rewrite + journal clear).
        /// </summary>
        public static void SaveHistoryDebounced(ObservableCollection<ViewModels.ClipboardItem> items)
        {
            // CRITICAL: Do NOT acquire _lock here — this runs on the UI thread via CollectionChanged.
            // If CompactNow holds _lock on a background thread, the UI thread would deadlock.
            var newTimer = new Timer(_ => SaveHistoryNow(items), null, 500, Timeout.Infinite);
            var oldTimer = Interlocked.Exchange(ref _debounceTimer, newTimer);
            oldTimer?.Dispose();
        }

        /// <summary>
        /// Immediately saves clipboard history to disk (full compaction).
        /// </summary>
        public static void SaveHistoryNow(ObservableCollection<ViewModels.ClipboardItem> items)
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);

                // Take a snapshot on the UI thread — use InvokeAsync to avoid blocking the UI
                List<ViewModels.ClipboardItem> snapshot = null;
                try
                {
                    if (System.Windows.Application.Current != null)
                    {
                        var op = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            return items.ToList();
                        });
                        op.Wait(); // Wait on the threadpool thread, not the UI thread
                        snapshot = op.Result;
                    }
                    else
                    {
                        snapshot = items.ToList();
                    }
                }
                catch
                {
                    return; // Collection was being modified, skip this save
                }

                // Enforce cap before saving
                if (snapshot.Count > MAX_HISTORY_ITEMS)
                    snapshot = snapshot.Take(MAX_HISTORY_ITEMS).ToList();

                CompactNow(snapshot);
            }
            catch (Exception ex)
            {
                Logger.LogAction("HISTORY_SAVE_ERROR", $"Failed to save history: {ex.Message}");
            }
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
                    var options = new JsonSerializerOptions { WriteIndented = false };
                    var json = JsonSerializer.Serialize(items, options);

                    // Write to temp file first, then atomic rename for safety
                    var tempPath = _historyPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _historyPath, true);

                    // Clear journal
                    if (File.Exists(_journalPath))
                        File.Delete(_journalPath);
                    _journalEntryCount = 0;

                    Logger.LogAction("HISTORY_COMPACT", $"Compacted {items.Count} items, journal cleared");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("HISTORY_COMPACT_ERROR", $"Compaction failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Schedules a compaction 5 seconds in the future (debounced).
        /// </summary>
        private static void ScheduleCompaction()
        {
            _compactionTimer?.Dispose();
            _compactionTimer = new Timer(_ =>
            {
                // Read current state from disk for compaction
                try
                {
                    var items = LoadHistoryRaw();
                    if (items.Count > 0)
                        CompactNow(items);
                }
                catch { }
            }, null, 5000, Timeout.Infinite);
        }

        /// <summary>
        /// Internal: loads without logging or auto-compaction (used by ScheduleCompaction).
        /// </summary>
        private static List<ViewModels.ClipboardItem> LoadHistoryRaw()
        {
            var items = new List<ViewModels.ClipboardItem>();
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                var snapshot = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(json);
                if (snapshot != null) items.AddRange(snapshot);
            }
            if (File.Exists(_journalPath))
            {
                foreach (var line in File.ReadAllLines(_journalPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                        if (entry?.Action == "add" && entry.Item != null)
                            items.Insert(0, entry.Item);
                        else if (entry?.Action == "delete" && entry.ItemId != null)
                            items.RemoveAll(i => GetItemId(i) == entry.ItemId);
                        else if (entry?.Action == "clear")
                            items.Clear();
                    }
                    catch { }
                }
            }
            return items.Take(MAX_HISTORY_ITEMS).ToList();
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
                        File.Delete(filePath);
                    }
                }
            }
            catch { }
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
                    try { Directory.Delete(parentSandboxDir); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SANDBOX_SCAVENGE_ERR", $"Scavenge failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a deterministic ID for a clipboard item (for journal delete tracking).
        /// </summary>
        private static string GetItemId(ViewModels.ClipboardItem item)
        {
            return $"{item.ItemType}_{item.DateCopied.Ticks}_{item.GetHashCode():X4}";
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
