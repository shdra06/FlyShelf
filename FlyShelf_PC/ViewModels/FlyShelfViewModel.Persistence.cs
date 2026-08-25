// ---------------------------------------------------------------
// FlyShelfViewModel — Persistence, Lifecycle & Item Management
// RemoveItem, TogglePin, ClearShelf, PruneOldItems, AutoCleanup,
// SavePinnedItems, LoadPinnedItems, PersistHistory
// Split from FlyShelfViewModel.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using FlyShelf.Classes;

namespace FlyShelf.ViewModels
{
    public partial class FlyShelfViewModel
    {
        // PERF: Cooldown guard for mascot delete animation — skip if called within 300ms
        private DateTime _lastDeleteAnimTime = DateTime.MinValue;
        private static readonly object _pinnedLock = new();
        // PERF: Debounce ShelfVisibility notification during rapid deletes
        private System.Windows.Threading.DispatcherTimer? _shelfVisibilityDebounce;

        // PERF [FIX 5]: Cached unpinned item count with incremental updates
        // Avoids O(n) LINQ scans on every CollectionChanged — only recounts on Reset
        private int _cachedUnpinnedCount = -1;

        private int CachedUnpinnedCount
        {
            get
            {
                if (_cachedUnpinnedCount < 0)
                    _cachedUnpinnedCount = DroppedItems.Count(i => !i.IsPinned);
                return _cachedUnpinnedCount;
            }
        }
        private void InvalidateUnpinnedCount(System.Collections.Specialized.NotifyCollectionChangedEventArgs e = null)
        {
            if (e == null || _cachedUnpinnedCount < 0)
            {
                _cachedUnpinnedCount = -1; // Force full recount on next access
                return;
            }

            switch (e.Action)
            {
                case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                    if (e.NewItems != null)
                        foreach (ClipboardItem item in e.NewItems)
                            if (!item.IsPinned) _cachedUnpinnedCount++;
                    break;
                case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                    if (e.OldItems != null)
                        foreach (ClipboardItem item in e.OldItems)
                            if (!item.IsPinned) _cachedUnpinnedCount--;
                    break;
                default:
                    _cachedUnpinnedCount = -1; // Move, Replace, Reset — full recount
                    break;
            }
        }

        public void RemoveItem(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                // Structural Lock: Pinned items cannot be deleted unless physically unpinned first!
                if (item.IsPinned) return; 

                // Stop audio playback if removing the active playing item
                if (item.IsAudioPlaying)
                {
                    ClipboardItem.StopActivePlayback();
                }

                DroppedItems.Remove(item);
                InvalidateUnpinnedCount();
                item.Dispose();

                // PERF: Only notify ShelfVisibility when the list actually becomes empty
                // (that's the only time the computed property changes). Skipping this for
                // non-empty → non-empty transitions avoids a redundant layout pass that
                // caused a visible "double refresh" animation on each delete.
                if (DroppedItems.Count == 0)
                {
                    OnPropertyChanged(nameof(ShelfVisibility));
                }

                // PERF: Mascot animation cooldown — skip if fired within 300ms to avoid UI thread stalls
                if ((DateTime.Now - _lastDeleteAnimTime).TotalMilliseconds > 300)
                {
                    _lastDeleteAnimTime = DateTime.Now;
                    try { FlyShelf.Classes.AnimationTriggerService.Instance.OnDelete(); } catch { } // Best-effort: failure is acceptable
                }

                // Cleanup backing file + DB delete asynchronously in background
                var itemCopy = item;
                string filePath = item.FilePath;
                string zippedPath = item.ZippedArchivePath;
                ClipboardItemType itemType = item.ItemType;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Classes.ClipboardHistoryManager.AppendDeleteToJournal(itemCopy);
                        CleanupTempFile(filePath);
                        CleanupTempFile(zippedPath);
                        Classes.ClipboardHistoryManager.DeletePersistentImage(filePath, itemType);
                    }
                    catch { } // Best-effort: failure is acceptable
                });
            }
        }

        /// <summary>
        /// Deletes multiple clipboard entries in a high-performance, thread-safe bulk operation.
        /// Excludes pinned items automatically.
        /// </summary>
        public void BulkRemoveItems(IEnumerable<ClipboardItem> items)
        {
            if (items == null) return;
            var itemList = items.Where(i => i != null && !i.IsPinned && DroppedItems.Contains(i)).ToList();
            if (itemList.Count == 0) return;

            // Stop active playback if any of these items are playing audio
            foreach (var item in itemList)
            {
                if (item.IsAudioPlaying)
                {
                    ClipboardItem.StopActivePlayback();
                    break;
                }
            }

            // Perform bulk removal in memory
            DroppedItems.RemoveRange(itemList);
            InvalidateUnpinnedCount();
            foreach (var item in itemList) item.Dispose();

            if (DroppedItems.Count == 0)
            {
                OnPropertyChanged(nameof(ShelfVisibility));
            }

            // Perform off-thread scavenge & delete logging
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in itemList)
                {
                    try
                    {
                        Classes.ClipboardHistoryManager.AppendDeleteToJournal(item);
                        CleanupTempFile(item.FilePath);
                        CleanupTempFile(item.ZippedArchivePath);
                        Classes.ClipboardHistoryManager.DeletePersistentImage(item.FilePath, item.ItemType);
                    }
                    catch { } // Best-effort: failure is acceptable
                }
            });

            // Persist the updated history to disk
            PersistHistory();
        }

        /// <summary>
        /// Deletes the backing file only if it resides inside the system temp directory or the app's synced files directory.
        /// User's real files (dragged from Explorer) are never touched.
        /// </summary>
        private void CleanupTempFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                
                string tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf").TrimEnd(Path.DirectorySeparatorChar);
                string syncedFilesDir = Path.Combine(appDataDir, "SyncedFiles").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                
                string fileDir = (Path.GetDirectoryName(filePath)?.TrimEnd(Path.DirectorySeparatorChar) ?? "") + Path.DirectorySeparatorChar;
                
                if (fileDir.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase) ||
                    fileDir.StartsWith(syncedFilesDir, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(filePath);
                }
            }
            catch { /* Silently ignore - file may be locked */ }
        }

        public void TogglePin(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                if (!item.IsPinned) // Trying to pin a new item
                {
                    int currentPinnedCount = DroppedItems.Count(i => i.IsPinned);
                    if (currentPinnedCount >= Classes.LicenseManager.GetPinLimit())
                    {
                        Classes.UpgradePrompt.ShowPinLimit();
                        return;
                    }
                }

                item.IsPinned = !item.IsPinned;
                InvalidateUnpinnedCount();
                
                SavePinnedItems();
                PersistHistory();

                // Pin state is self-evident from UI — no floating tip needed
                
                // Pinned items stay wherever they are — no sorting
            }
        }

        public void OpenItem(ClipboardItem item)
        {
            item?.Execute();
        }

        public void ClearShelf()
        {
            // Stop any active audio playback when clearing the shelf
            ClipboardItem.StopActivePlayback();

            var volatileItems = DroppedItems.Where(i => !i.IsPinned).ToList();

            if (volatileItems.Count > 0)
            {
                DroppedItems.RemoveRange(volatileItems);
                InvalidateUnpinnedCount();
                OnPropertyChanged(nameof(ShelfVisibility));
                SavePinnedItems();
                
                // Append deletes and clean up files asynchronously
                System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (var item in volatileItems)
                    {
                        try
                        {
                            Classes.ClipboardHistoryManager.AppendDeleteToJournal(item);
                            CleanupTempFile(item.FilePath);
                            CleanupTempFile(item.ZippedArchivePath);
                            Classes.ClipboardHistoryManager.DeletePersistentImage(item.FilePath, item.ItemType);
                        }
                        catch { } // Best-effort: failure is acceptable
                    }
                });
            }
        }
        
        public void SortForContext(string currentContextTitle)
        {
            if (string.IsNullOrWhiteSpace(currentContextTitle)) return;
            
            var itemsList = DroppedItems.ToList();
            var sorted = itemsList.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.AssociatedContextTitle) && string.Equals(x.AssociatedContextTitle, currentContextTitle, StringComparison.OrdinalIgnoreCase))
                                  .ThenByDescending(x => x.DateCopied)
                                  .ToList();
                                  
            bool needsReorder = false;
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].IsSuggestedContext = !string.IsNullOrWhiteSpace(sorted[i].AssociatedContextTitle) && string.Equals(sorted[i].AssociatedContextTitle, currentContextTitle, StringComparison.OrdinalIgnoreCase);
                if (i < DroppedItems.Count && !object.ReferenceEquals(DroppedItems[i], sorted[i])) 
                {
                    needsReorder = true;
                }
            }

            if (needsReorder)
            {
                // FlyShelf Phase 2.1: Use logical pointer swapping rather than destructive visual tree clears!
                // This eliminates the 1.5s visual freeze spike on large payload buffers!
                // PERF: Suspend DB writes during the Move loop to prevent N CollectionChanged → N PersistHistory calls
                _isDatabaseWriteSuspended = true;
                DroppedItems.SuppressNotifications();
                try
                {
                    // Build O(1) index map to avoid O(n) IndexOf per iteration
                    var currentIndexMap = new Dictionary<ClipboardItem, int>(DroppedItems.Count);
                    for (int idx = 0; idx < DroppedItems.Count; idx++)
                        currentIndexMap[DroppedItems[idx]] = idx;

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (currentIndexMap.TryGetValue(sorted[i], out int actualIndex) && actualIndex != i)
                        {
                            DroppedItems.Move(actualIndex, i);
                            // Update index map after move: items between i and actualIndex shift
                            for (int j = i; j <= actualIndex && j < DroppedItems.Count; j++)
                                currentIndexMap[DroppedItems[j]] = j;
                        }
                    }
                }
                finally
                {
                    DroppedItems.ResumeNotifications();
                    _isDatabaseWriteSuspended = false;
                    // Single persist after all moves complete
                    PersistHistory();
                }
            }
        }

        private string GetDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "FlyShelf");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pinned_items.json");
        }

        public void SavePinnedItems()
        {
            try
            {
                var pinned = DroppedItems.Where(i => i.IsPinned).ToList();
                string path = GetDbPath();
                System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_pinnedLock)
                    {
                        try
                        {
                            if (!Classes.DiskSpaceHelper.HasSufficientDiskSpace(path, 1_000_000))
                            {
                                Classes.Logger.LogAction("PINNED_SAVE", "Insufficient disk space");
                                return;
                            }
                            // Create backup before saving
                            try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { } // Best-effort: failure is acceptable
                            var json = System.Text.Json.JsonSerializer.Serialize(pinned);
                            string tmpPath = path + ".tmp";
                            File.WriteAllText(tmpPath, json);
                            File.Move(tmpPath, path, overwrite: true);
                        }
                        catch (Exception ex) { Classes.Logger.LogAction("PINNED_SAVE", $"Failed to serialize/write pinned items: {ex.Message}"); }
                    }
                });
            }
            catch (Exception ex) { Classes.Logger.LogAction("PINNED_SAVE", $"Failed to gather pinned items: {ex.Message}"); }
        }

        public async Task LoadPinnedItemsAsync()
        {
            try
            {
                string path = GetDbPath();
                var docs = await System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_pinnedLock)
                    {
                        try
                        {
                            if (File.Exists(path))
                            {
                                var json = Classes.FileRetryHelper.RunWithRetry(() => File.ReadAllText(path));
                                return System.Text.Json.JsonSerializer.Deserialize<List<ClipboardItem>>(json);
                            }
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("PINNED_LOAD", $"Failed to deserialize pinned items JSON: {ex.Message}");
                            // Fallback: try loading from .bak file
                            try
                            {
                                string bakPath = path + ".bak";
                                if (File.Exists(bakPath))
                                {
                                    Classes.Logger.LogAction("PINNED_LOAD", "Attempting recovery from .bak file");
                                    var bakJson = Classes.FileRetryHelper.RunWithRetry(() => File.ReadAllText(bakPath));
                                    return System.Text.Json.JsonSerializer.Deserialize<List<ClipboardItem>>(bakJson);
                                }
                            }
                            catch (Exception bakEx) { Classes.Logger.LogAction("PINNED_LOAD", $"Backup recovery also failed: {bakEx.Message}"); }
                        }
                        return null;
                    }
                });

                if (docs != null)
                {
                    var seenKeys = new HashSet<string>();
                    var pinnedToAdd = new List<ClipboardItem>();
                    foreach (var d in docs)
                    {
                        try
                        {
                            string key = GetDeduplicationKey(d);
                            if (!string.IsNullOrEmpty(key) && !seenKeys.Add(key))
                                continue;

                            bool isFileBased = d.ItemType == ClipboardItemType.Image || d.ItemType == ClipboardItemType.QRCode ||
                                d.ItemType == ClipboardItemType.File || d.ItemType == ClipboardItemType.Document ||
                                d.ItemType == ClipboardItemType.Pdf || d.ItemType == ClipboardItemType.Archive ||
                                d.ItemType == ClipboardItemType.Video || d.ItemType == ClipboardItemType.Audio ||
                                d.ItemType == ClipboardItemType.Presentation;

                            if (isFileBased)
                            {
                                if (string.IsNullOrEmpty(d.FilePath) || (!File.Exists(d.FilePath) && !Directory.Exists(d.FilePath)))
                                {
                                    Classes.Logger.LogAction("PINNED_CLEANUP", $"Pruned dead/deleted pinned item: {d.FileName ?? d.RawContent}");
                                    continue; // Skip loading dead/deleted file entries
                                }
                            }

                            if (IsEffectivelyEmpty(d)) continue;

                            d.IsPinned = true;

                            // Regenerate non-serialized icons (Icon is [JsonIgnore])
                            // Must match the regeneration logic in LoadPersistedHistoryAsync
                            if (d.IsPassword)
                                d.GeneratePasswordIcon();
                            else if (d.ItemType == ClipboardItemType.Folder)
                                d.GenerateFolderIcon();
                            else if (d.ItemType == ClipboardItemType.Document && d.Extension == ".MD")
                                d.GenerateMarkdownIcon();

                            bool isGeneralFile = d.ItemType == ClipboardItemType.File || d.ItemType == ClipboardItemType.Document ||
                                d.ItemType == ClipboardItemType.Pdf || d.ItemType == ClipboardItemType.Archive ||
                                d.ItemType == ClipboardItemType.Video || d.ItemType == ClipboardItemType.Audio ||
                                d.ItemType == ClipboardItemType.Presentation;

                            if (isGeneralFile)
                            {
                                var icon = Classes.ShellIconManager.GetIcon(d.FilePath, d.Extension);
                                if (icon != null)
                                    d.Icon = icon;
                                else if (d.IsApk)
                                    d.GenerateApkIcon();
                            }
                            else if (d.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(d.FilePath))
                            {
                                string imagePath = d.FilePath;
                                var capturedD = d;
                                _ = System.Threading.Tasks.Task.Run(() => {
                                    try 
                                    {
                                        // Always use 300px — this runs on a background thread
                                        var bmp = LoadImageThumbnail(imagePath, 300);
                                        if (bmp != null)
                                        {
                                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                                            {
                                                capturedD.Icon = bmp;
                                                capturedD.IsLoadedHighQuality = true;
                                            });
                                        }
                                    } catch { } // Best-effort: failure is acceptable
                                });
                            }
                            pinnedToAdd.Add(d);
                        }
                        catch (Exception ex) { Classes.Logger.LogAction("PINNED_LOAD", $"Failed to process pinned item: {ex.Message}"); }
                    }

                    if (pinnedToAdd.Count > 0)
                    {
                        var pinnedDispatcher = Application.Current?.Dispatcher;
                        if (pinnedDispatcher != null)
                            await pinnedDispatcher.InvokeAsync(() => { DroppedItems.AddRange(pinnedToAdd); InvalidateUnpinnedCount(); });
                    }
                }
            }
            catch (Exception ex) { Classes.Logger.LogAction("PINNED_LOAD", $"Failed to load pinned items: {ex.Message}"); }
        }


        /// <summary>
        /// Searches the history database using FTS5 and populates DroppedItems with results, loading icons asynchronously.
        /// </summary>
        [Obsolete("Search results are handled by MainWindow.Search.cs filter — this method is a no-op.")]
        public async Task SearchHistoryAsync(string query)
        {
            // DB persistence disabled — search in-memory
            IsSearchActive = true;
            try
            {
                string q = (query ?? "");
                var results = DroppedItems.Where(i =>
                    (!string.IsNullOrEmpty(i.RawContent) && i.RawContent.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(i.FileName) && i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase))
                ).ToList();
                // Re-populate with matches (no DB query)
                // Note: search results shown from in-memory items only
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("SEARCH_ERROR", $"Failed to execute search: {ex.Message}");
            }
            await System.Threading.Tasks.Task.CompletedTask;
        }

        // Undo history removed — user preference: no backup of deleted items
        public void UndoDelete() { }

        public void LaunchSandbox(ClipboardItem item)
        {
            if (item != null) item.OpenSandbox();
        }



        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
        }

        // ═══ Cloud Echo Prevention ═══
        // Tracks content that arrived from Firebase/cloud so HandleDrop doesn't re-push it
        private static readonly Dictionary<string, long> _recentCloudContent = new();
        private static readonly object _cloudContentLock = new();
        
        /// <summary>Normalizes line endings and whitespace to prevent echo loops due to platform/formatting variations.</summary>
        public static string NormalizeTextForFingerprint(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        /// <summary>Mark content as cloud-sourced so it won't be re-pushed to Firebase.</summary>
        public void MarkAsCloudSourced(string contentFingerprint)
        {
            lock (_cloudContentLock)
            {
                _recentCloudContent[contentFingerprint] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PruneCloudContent();
            }
        }
        
        /// <summary>Check if content was recently received from cloud (shouldn't be re-pushed).</summary>
        private bool IsCloudSourced(string contentFingerprint)
        {
            lock (_cloudContentLock)
            {
                PruneCloudContent();
                return _recentCloudContent.ContainsKey(contentFingerprint) && 
                       (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _recentCloudContent[contentFingerprint]) < 30_000;
            }
        }

        /// <summary>Evicts stale entries (>30s) and enforces a hard cap of 100.</summary>
        private void PruneCloudContent()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stale = _recentCloudContent.Where(kv => now - kv.Value > 30_000).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _recentCloudContent.Remove(k);
            // Hard cap — remove oldest if still too large
            while (_recentCloudContent.Count > 100)
            {
                var oldest = _recentCloudContent.MinBy(kv => kv.Value);
                if (oldest.Key != null) _recentCloudContent.Remove(oldest.Key);
                else break;
            }
        }

        /// <summary>
        /// Unified file sync: Cloudflare tunnel → Firebase Storage → log-only fallback.
        /// Replaces 3 previously duplicated sync blocks.
        /// </summary>
        private async System.Threading.Tasks.Task SyncFileToDevicesAsync(string filePath, ClipboardItem item, long maxFirebaseBytes = 25 * 1024 * 1024, string label = "FILE")
        {
            // SECURITY: Password items must NEVER be synced to any device
            if (item.IsPassword)
            {
                Classes.Logger.LogAction($"{label} SYNC", "Blocked password item from file sync — password items are never synced");
                return;
            }

            try
            {
                long fSize = new FileInfo(filePath).Length;
                if (fSize > Classes.LicenseManager.FREE_SYNC_SIZE_LIMIT && !Classes.LicenseManager.IsPro)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"{Path.GetFileName(filePath)} ({FormatFileSize(fSize)}) exceeds 50 GB Free tier sync limit."));
                    return;
                }

                var srv = LocalServer;
                bool tunnelOk = FlyShelf.Classes.CloudDiscoveryManager.CachedTunnelVerified;

                bool hasLanPeers = FlyShelf.Classes.PeerManager.Instance != null && FlyShelf.Classes.PeerManager.Instance.AliveCount > 0;
                bool hasMobilePollers = srv != null && srv.GetDirectlyConnectedDeviceCount() > 0;
                bool lanSuccess = false;

                // PRIORITY 1: Try direct high-speed local LAN transfer first
                if (hasLanPeers || hasMobilePollers)
                {
                    if (hasLanPeers)
                    {
                        FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"Syncing '{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) directly via high-speed LAN");
                        try
                        {
                            int delivered = await FlyShelf.Classes.PeerManager.Instance.PushFileToAllPeers(
                                filePath, item.FileName ?? Path.GetFileName(filePath), item.ItemType.ToString());

                            if (delivered > 0)
                            {
                                lanSuccess = true;
                                // Silenced — sync toasts on every capture are too spammy
                            }
                            else
                            {
                                FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"LAN direct sync failed — no active peers accepted the file. Falling back to Cloudflare...");
                            }
                        }
                        catch (Exception ex)
                        {
                            FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"Direct LAN transfer error: {ex.Message}. Falling back to Cloudflare...");
                        }
                    }

                    // Fallback to local server polling if direct push didn't succeed but companions are connected
                    if (hasMobilePollers && !lanSuccess)
                    {
                        FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"'{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) placed on local server for companion app pulling");
                        lanSuccess = true;
                        // Silenced — sync toasts on every capture are too spammy
                    }
                }

                if (lanSuccess)
                {
                    return;
                }

                // PRIORITY 2: Fallback to Cloudflare P2P push if local LAN is not available/failed
                if (srv != null && !string.IsNullOrEmpty(srv.GlobalUrl) && srv.GlobalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase) && tunnelOk)
                {
                    string downloadUrl = $"{srv.GlobalUrl}/download?path={Uri.EscapeDataString(filePath)}";
                    FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"Sending '{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) via Cloudflare P2P");
                    var syncItem = item.CloneForSync(downloadUrl);
                    await FlyShelf.Classes.CloudDiscoveryManager.PushToCloudHub(syncItem);
                    // Silenced — sync toasts on every capture are too spammy
                    return;
                }

                // No LAN success and no Cloudflare tunnel available
                FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"'{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) — no active LAN peers or Cloudflare tunnel available");
                // Silenced — "no LAN peers" toast on every capture is the worst offender
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prunes oldest unpinned items beyond the cap to prevent unbounded memory growth.
        /// </summary>
        public void PruneOldItems()
        {
            int maxUnpinnedItems = Classes.LicenseManager.GetHistoryCap();
            int warningThreshold = Classes.LicenseManager.IsPro ? 2000 : 150;
            int totalCount = DroppedItems.Count;
            
            // Show warning starting at warningThreshold items (but don't prune yet)
            if (totalCount >= warningThreshold && totalCount <= maxUnpinnedItems)
            {
                int unpinnedCount = CachedUnpinnedCount;
                if (unpinnedCount >= warningThreshold && unpinnedCount < maxUnpinnedItems)
                {
                    // Warn every 50 items for Free (or 100 for Pro) to give frequent heads-up
                    int step = Classes.LicenseManager.IsPro ? 100 : 50;
                    if (unpinnedCount % step == 0)
                    {
                        int remaining = maxUnpinnedItems - unpinnedCount;
                        FlyShelf.Windows.ToastWindow.ShowToast($"Clipboard has {unpinnedCount} items. {remaining} slots remaining (max {maxUnpinnedItems}).");
                    }
                }
            }

            int unpinnedTotal = CachedUnpinnedCount;
            if (unpinnedTotal <= maxUnpinnedItems) return;

            // Collect unpinned items to remove (from end of the list, oldest first)
            var itemsToRemove = new List<ClipboardItem>();
            
            var itemsToRemoveFromDropped = new List<ClipboardItem>();
            for (int i = DroppedItems.Count - 1; i >= 0 && unpinnedTotal - itemsToRemove.Count > maxUnpinnedItems; i--)
            {
                if (!DroppedItems[i].IsPinned)
                {
                    itemsToRemove.Add(DroppedItems[i]);
                    itemsToRemoveFromDropped.Add(DroppedItems[i]);
                }
            }
            if (itemsToRemoveFromDropped.Count > 0)
            {
                DroppedItems.RemoveRange(itemsToRemoveFromDropped);
                foreach (var item in itemsToRemoveFromDropped) item.Dispose();
            }
            
            if (itemsToRemove.Count > 0)
            {
                var filesToCleanup = new List<(string path, string zippedPath, ClipboardItemType type)>();
                foreach (var item in itemsToRemove)
                {
                    filesToCleanup.Add((item.FilePath, item.ZippedArchivePath, item.ItemType));
                }

                FlyShelf.Windows.ToastWindow.ShowToast($"Clipboard full — removed {itemsToRemove.Count} oldest items.");

                // Background cleanup
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (var (path, zippedPath, type) in filesToCleanup)
                    {
                        try { CleanupTempFile(path); } catch { } // Best-effort: failure is acceptable
                        try { CleanupTempFile(zippedPath); } catch { } // Best-effort: failure is acceptable
                        try { Classes.ClipboardHistoryManager.DeletePersistentImage(path, type); } catch { } // Best-effort: failure is acceptable
                    }
                });

                PersistHistory();
            }
        }

        public Visibility ShelfVisibility => DroppedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ═══ FAST INSERT — Only skip exact back-to-back duplicates ═══
        /// <summary>
        /// Inserts a ClipboardItem at position 0. Only skips if the exact same content
        /// is already at index 0 (back-to-back copy). No full-list scanning.
        /// </summary>
        public bool InsertWithDedup(ClipboardItem newItem)
        {
            if (DroppedItems.Count > 0)
            {
                var top = DroppedItems[0];
                // Only skip exact back-to-back duplicate at position 0
                if (newItem.ItemType == top.ItemType)
                {
                    // Text: same RawContent at top
                    if ((newItem.ItemType == ClipboardItemType.Text || newItem.ItemType == ClipboardItemType.Code || newItem.ItemType == ClipboardItemType.Url)
                        && !string.IsNullOrEmpty(newItem.RawContent) && newItem.RawContent == top.RawContent)
                        return false;
                    // File: same FilePath at top
                    if (!string.IsNullOrEmpty(newItem.FilePath) && newItem.FilePath == top.FilePath)
                        return false;
                    // Image: same dimensions within 3s
                    if ((newItem.ItemType == ClipboardItemType.Image || newItem.ItemType == ClipboardItemType.QRCode)
                        && !string.IsNullOrEmpty(newItem.FormattedSize) && newItem.FormattedSize == top.FormattedSize
                        && (DateTime.Now - top.DateCopied).TotalSeconds < 3.0)
                        return false;
                }
            }

            // Deduplicate against the first 10 items
            DeduplicateItem(newItem);

            DroppedItems.Insert(0, newItem);
            InvalidateUnpinnedCount();

            // ═══ NETWORKING AUTO-STAGE HOOK ═══
            // When a file is copied, auto-stage it for network sending
            try
            {
                if (NetworkFileQueue.Instance != null 
                    && (newItem.ItemType == ClipboardItemType.File || newItem.ItemType == ClipboardItemType.Folder)
                    && !string.IsNullOrEmpty(newItem.FilePath))
                {
                    NetworkFileQueue.Instance.StageFromClipboard(newItem);
                }
            }
            catch { /* Network staging is best-effort — never block clipboard */ }

            return true;
        }

        /// <summary>
        /// Extracts and canonicalizes a file path from a string (handling file:// URIs, quotes, slashes, etc.).
        /// </summary>
        public static string? ExtractNormalizedPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string text = input.Trim();

            // Handle file:// and file:/// URI schemes
            if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    text = new Uri(text).LocalPath;
                    if (text.Length >= 3 && text[0] == '/' && char.IsLetter(text[1]) && text[2] == ':')
                        text = text.Substring(1);
                }
                catch { }
            }
            // Strip surrounding quotes
            else if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            {
                text = text.Substring(1, text.Length - 2).Trim();
            }

            if (text.Contains('\n') || text.Length > 1000) return null;
            text = text.Replace('/', '\\').TrimEnd();

            // Check if string is a Windows drive rooted path
            if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && text[2] == '\\')
            {
                try { return Path.GetFullPath(text).TrimEnd('\\'); }
                catch { return text.TrimEnd('\\'); }
            }

            // Check if it exists on disk directly
            try
            {
                if (File.Exists(text) || Directory.Exists(text))
                    return Path.GetFullPath(text).TrimEnd('\\');
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Universal, bulletproof deduplication checking across all file and content types.
        /// Handles canonical paths, cross-type filename matching, raw text, and visual images.
        /// </summary>
        private bool IsDuplicate(ClipboardItem newItem, ClipboardItem existing)
        {
            if (newItem == null || existing == null) return false;
            if (ReferenceEquals(newItem, existing)) return true;

            // ── 1. CANONICAL FILE PATH MATCH (Direct FilePath or Path inside RawContent) ──
            string? path1 = !string.IsNullOrEmpty(newItem.FilePath) ? ExtractNormalizedPath(newItem.FilePath) : ExtractNormalizedPath(newItem.RawContent);
            string? path2 = !string.IsNullOrEmpty(existing.FilePath) ? ExtractNormalizedPath(existing.FilePath) : ExtractNormalizedPath(existing.RawContent);

            if (!string.IsNullOrEmpty(path1) && !string.IsNullOrEmpty(path2))
            {
                if (string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // ── 2. FILENAME MATCH FOR FILE-BASED / DOCUMENT ITEMS ──
            if (!string.IsNullOrEmpty(newItem.FileName) && !string.IsNullOrEmpty(existing.FileName))
            {
                if (string.Equals(newItem.FileName.Trim(), existing.FileName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    bool isFile1 = !string.IsNullOrEmpty(newItem.FilePath) || newItem.ItemType == ClipboardItemType.File || newItem.ItemType == ClipboardItemType.Document || newItem.ItemType == ClipboardItemType.Pdf || newItem.ItemType == ClipboardItemType.Archive || newItem.ItemType == ClipboardItemType.Video || newItem.ItemType == ClipboardItemType.Audio || newItem.ItemType == ClipboardItemType.Presentation || newItem.IsMarkdownPreview;
                    bool isFile2 = !string.IsNullOrEmpty(existing.FilePath) || existing.ItemType == ClipboardItemType.File || existing.ItemType == ClipboardItemType.Document || existing.ItemType == ClipboardItemType.Pdf || existing.ItemType == ClipboardItemType.Archive || existing.ItemType == ClipboardItemType.Video || existing.ItemType == ClipboardItemType.Audio || existing.ItemType == ClipboardItemType.Presentation || existing.IsMarkdownPreview;

                    if (isFile1 && isFile2)
                    {
                        // Same filename across file categories (e.g. Document vs File vs Markdown)
                        if (string.IsNullOrEmpty(newItem.FilePath) || string.IsNullOrEmpty(existing.FilePath))
                            return true;
                        if (string.Equals(newItem.FilePath, existing.FilePath, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            // ── 3. RAW CONTENT MATCH (across all text, code, markdown, url, documents) ──
            if (!string.IsNullOrEmpty(newItem.RawContent) && !string.IsNullOrEmpty(existing.RawContent))
            {
                string c1 = newItem.RawContent.Trim().Replace("\r\n", "\n");
                string c2 = existing.RawContent.Trim().Replace("\r\n", "\n");
                if (IsNearDuplicateText(c1, c2))
                {
                    return true;
                }
            }
            else if (!string.IsNullOrEmpty(newItem.FileName) && !string.IsNullOrEmpty(existing.FileName) &&
                     newItem.ItemType == ClipboardItemType.Text && existing.ItemType == ClipboardItemType.Text)
            {
                string f1 = newItem.FileName.Trim();
                string f2 = existing.FileName.Trim();
                if (IsNearDuplicateText(f1, f2))
                {
                    return true;
                }
            }

            // ── 4. IMAGE VISUAL & DIMENSION MATCH ──
            if ((newItem.ItemType == ClipboardItemType.Image || newItem.ItemType == ClipboardItemType.QRCode) &&
                (existing.ItemType == ClipboardItemType.Image || existing.ItemType == ClipboardItemType.QRCode))
            {
                if (IsImageDuplicate(newItem, existing))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Ultra-fast, zero-allocation similarity check for near-identical text (typo fixes, sequential edits, minor revisions).
        /// Returns true if two text snippets are identical or have >= 88% structural similarity.
        /// </summary>
        public static bool IsNearDuplicateText(string s1, string s2)
        {
            if (string.Equals(s1, s2, StringComparison.Ordinal)) return true;
            if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return true;

            int len1 = s1.Length;
            int len2 = s2.Length;
            int maxLen = Math.Max(len1, len2);
            int minLen = Math.Min(len1, len2);

            if (maxLen < 6) return false;

            // Disparity too large to be an immediate typo fix or minor revision
            if (Math.Abs(len1 - len2) > Math.Max(15, (int)(maxLen * 0.25))) return false;

            // Phase 1: Fast Common Prefix + Common Suffix (0 heap allocation, O(N))
            int prefix = 0;
            while (prefix < minLen && s1[prefix] == s2[prefix])
                prefix++;

            int suffix = 0;
            while (suffix < (minLen - prefix) && s1[len1 - 1 - suffix] == s2[len2 - 1 - suffix])
                suffix++;

            int commonCover = prefix + suffix;

            // If common prefix + suffix covers >= 88% of the longer string, it's an immediate typo fix / edit
            if (commonCover >= (int)(maxLen * 0.88))
                return true;

            // Phase 2: Bounded Levenshtein for short/medium strings (<= 400 chars)
            if (maxLen <= 400)
            {
                int maxAllowedEdits = Math.Max(2, (int)(maxLen * 0.12));
                int distance = BoundedLevenshtein(s1, s2, maxAllowedEdits);
                if (distance <= maxAllowedEdits)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Highly optimized, stack-allocated 2-row bounded Levenshtein distance.
        /// Early-exits as soon as the minimum edit cost exceeds maxDistance.
        /// </summary>
        private static int BoundedLevenshtein(string s, string t, int maxDistance)
        {
            int n = s.Length;
            int m = t.Length;

            if (Math.Abs(n - m) > maxDistance) return maxDistance + 1;
            if (n == 0) return m;
            if (m == 0) return n;

            Span<int> prev = stackalloc int[m + 1];
            Span<int> curr = stackalloc int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 0; i < n; i++)
            {
                curr[0] = i + 1;
                int minInRow = curr[0];

                for (int j = 0; j < m; j++)
                {
                    int cost = (s[i] == t[j]) ? 0 : 1;
                    int val = Math.Min(curr[j] + 1, Math.Min(prev[j + 1] + 1, prev[j] + cost));
                    curr[j + 1] = val;
                    if (val < minInRow) minInRow = val;
                }

                if (minInRow > maxDistance) return maxDistance + 1;

                curr.CopyTo(prev);
            }

            return prev[m];
        }

        /// <summary>
        /// Compares two images using Resolution and exact file size in bytes to determine duplicate status.
        /// </summary>
        private bool IsImageDuplicate(ClipboardItem item1, ClipboardItem item2)
        {
            if (!string.IsNullOrEmpty(item1.FormattedSize) && item1.FormattedSize == item2.FormattedSize)
            {
                if (!string.IsNullOrEmpty(item1.FilePath) && !string.IsNullOrEmpty(item2.FilePath) && 
                    File.Exists(item1.FilePath) && File.Exists(item2.FilePath))
                {
                    try
                    {
                        var fi1 = new FileInfo(item1.FilePath);
                        var fi2 = new FileInfo(item2.FilePath);
                        if (fi1.Length == fi2.Length) return true;
                    }
                    catch { }
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Scans DroppedItems (first 10 entries) for duplicates or near-duplicate revisions of newItem.
        /// If found, removes the old duplicate item from DroppedItems and backing storage.
        /// </summary>
        public void DeduplicateItem(ClipboardItem newItem)
        {
            if (newItem == null) return;

            var duplicatesToRemove = new List<ClipboardItem>();
            int checkCount = Math.Min(10, DroppedItems.Count);
            for (int i = 0; i < checkCount; i++)
            {
                if (i >= DroppedItems.Count) break;
                var existing = DroppedItems[i];
                if (existing == null || ReferenceEquals(existing, newItem) || existing.IsPinned) continue;

                if (IsDuplicate(newItem, existing))
                {
                    duplicatesToRemove.Add(existing);
                }
            }

            if (duplicatesToRemove.Count > 0)
            {
                foreach (var duplicate in duplicatesToRemove)
                {
                    Classes.Logger.LogAction("DEDUP", $"Found duplicate/revision: Type={duplicate.ItemType}, Path={duplicate.FilePath}, Name={duplicate.FileName}. Removing old duplicate.");
                }
                BulkRemoveItems(duplicatesToRemove);
            }
        }

        /// <summary>
        /// One-pass shelf-wide cleanup that prunes any existing duplicate items.
        /// </summary>
        public void RemoveAllExistingDuplicates()
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenContents = new HashSet<string>(StringComparer.Ordinal);
            var toRemove = new List<ClipboardItem>();

            for (int i = 0; i < DroppedItems.Count; i++)
            {
                var item = DroppedItems[i];
                if (item == null) continue;

                string? normPath = !string.IsNullOrEmpty(item.FilePath) 
                    ? ExtractNormalizedPath(item.FilePath) 
                    : ExtractNormalizedPath(item.RawContent);

                if (!string.IsNullOrEmpty(normPath))
                {
                    if (!seenPaths.Add(normPath))
                    {
                        toRemove.Add(item);
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(item.RawContent) && item.RawContent.Length < 100000)
                {
                    string normContent = item.RawContent.Trim().Replace("\r\n", "\n");
                    if (!seenContents.Add(normContent))
                    {
                        toRemove.Add(item);
                        continue;
                    }
                }
            }

            foreach (var dup in toRemove)
            {
                RemoveItem(dup);
            }
        }

        /// <summary>
        /// Deduplicates newItem against the first 10 entries and inserts it at index 0.
        /// </summary>
        public void DeduplicateAndInsert(ClipboardItem newItem)
        {
            if (newItem == null) return;
            DeduplicateItem(newItem);
            DroppedItems.Insert(0, newItem);
            InvalidateUnpinnedCount();
            PruneOldItems();
            OnPropertyChanged(nameof(ShelfVisibility));
        }

        // ═══ AUTO-CLEANUP TIMER ═══
        private System.Threading.Timer? _cleanupTimer;

        /// <summary>
        /// Starts the auto-cleanup timer that runs at startup and every 6 hours.
        /// Deletes unpinned items older than ClipboardRetentionDays from DB and memory.
        /// </summary>
        public void StartAutoCleanupTimer()
        {
            // Run cleanup after 10 seconds (let app finish loading), then every 6 hours
            _cleanupTimer = new System.Threading.Timer(_ =>
            {
                RunAutoCleanup();
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(6));
        }

        private void RunAutoCleanup()
        {
            try
            {
                int retentionDays = Classes.SettingsManager.Current.ClipboardRetentionDays;
                if (retentionDays <= 0) return;

                var cutoff = DateTime.Now.AddDays(-retentionDays);

                if (Application.Current != null)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var toRemove = DroppedItems.Where(i => !i.IsPinned && i.DateCopied < cutoff).ToList();
                        if (toRemove.Count > 0)
                        {
                            BulkRemoveItems(toRemove);
                            PersistHistory();
                            OnPropertyChanged(nameof(ShelfVisibility));
                            Classes.Logger.LogAction("AUTO_CLEANUP", $"Removed {toRemove.Count} expired items (retention: {retentionDays} days).");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("AUTO_CLEANUP_ERROR", $"Cleanup failed: {ex.Message}");
            }
        }

    }
}
