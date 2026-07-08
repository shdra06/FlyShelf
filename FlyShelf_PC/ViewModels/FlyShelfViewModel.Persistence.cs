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
        // PERF: Debounce ShelfVisibility notification during rapid deletes
        private System.Windows.Threading.DispatcherTimer? _shelfVisibilityDebounce;

        // PERF: Cached unpinned item count — avoids O(n) LINQ scans in PruneOldItems
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
        private void InvalidateUnpinnedCount() => _cachedUnpinnedCount = -1;

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
                
                string tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf").TrimEnd(Path.DirectorySeparatorChar);
                string syncedFilesDir = Path.Combine(appDataDir, "SyncedFiles").TrimEnd(Path.DirectorySeparatorChar);
                
                string fileDir = Path.GetDirectoryName(filePath)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
                
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
                    try
                    {
                        // Create backup before saving
                        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch { } // Best-effort: failure is acceptable
                        var json = System.Text.Json.JsonSerializer.Serialize(pinned);
                        string tmpPath = path + ".tmp";
                        File.WriteAllText(tmpPath, json);
                        File.Move(tmpPath, path, overwrite: true);
                    }
                    catch (Exception ex) { Classes.Logger.LogAction("PINNED_SAVE", $"Failed to serialize/write pinned items: {ex.Message}"); }
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
                    try
                    {
                        if (File.Exists(path))
                        {
                            var json = File.ReadAllText(path);
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
                                var bakJson = File.ReadAllText(bakPath);
                                return System.Text.Json.JsonSerializer.Deserialize<List<ClipboardItem>>(bakJson);
                            }
                        }
                        catch (Exception bakEx) { Classes.Logger.LogAction("PINNED_LOAD", $"Backup recovery also failed: {bakEx.Message}"); }
                    }
                    return null;
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

                            bool isGeneralFile = d.ItemType == ClipboardItemType.File || d.ItemType == ClipboardItemType.Document ||
                                d.ItemType == ClipboardItemType.Pdf || d.ItemType == ClipboardItemType.Archive ||
                                d.ItemType == ClipboardItemType.Video || d.ItemType == ClipboardItemType.Audio ||
                                d.ItemType == ClipboardItemType.Presentation;

                            if (isGeneralFile && !string.IsNullOrEmpty(d.FilePath))
                            {
                                var capturedD = d;
                                System.Threading.Tasks.Task.Run(() => {
                                    try
                                    {
                                        var icon = GetIcon(capturedD.FilePath);
                                        if (icon != null) Application.Current?.Dispatcher?.InvokeAsync(() => capturedD.Icon = icon);
                                    }
                                    catch { } // Best-effort: failure is acceptable
                                });
                            }
                            else if (d.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(d.FilePath))
                            {
                                string imagePath = d.FilePath;
                                var capturedD = d;
                                System.Threading.Tasks.Task.Run(() => {
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
                            await pinnedDispatcher.InvokeAsync(() => DroppedItems.AddRange(pinnedToAdd));
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
                Classes.Logger.LogAction($"{label} SYNC", "🔒 Blocked password item from file sync — password items are never synced");
                return;
            }

            try
            {
                long fSize = new FileInfo(filePath).Length;
                if (fSize > Classes.LicenseManager.FREE_SYNC_SIZE_LIMIT && !Classes.LicenseManager.IsPro)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ {Path.GetFileName(filePath)} ({FormatFileSize(fSize)}) exceeds 50 GB Free tier sync limit."));
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
                                Application.Current?.Dispatcher?.InvokeAsync(() =>
                                    FlyShelf.Windows.ToastWindow.ShowToast($"{label} ({FormatFileSize(fSize)}) synced directly via LAN! \ud83d\udce1"));
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
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast($"Synced via LAN! \u26a1 (Available for companion app pulling)"));
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
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"{label} ({FormatFileSize(fSize)}) synced via P2P \ud83c\udf10"));
                    return;
                }

                // No LAN success and no Cloudflare tunnel available
                FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"'{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) — no active LAN peers or Cloudflare tunnel available");
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast($"\u26a0\ufe0f {Path.GetFileName(filePath)} ({FormatFileSize(fSize)}) — no active LAN peers or Cloudflare tunnel"));
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
                        FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Clipboard has {unpinnedCount} items. {remaining} slots remaining (max {maxUnpinnedItems}).");
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

                FlyShelf.Windows.ToastWindow.ShowToast($"🗑️ Clipboard full — removed {itemsToRemove.Count} oldest items.");

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
        /// Checks if newItem is a duplicate of existing.
        /// Checks RawContent for text-based items, FilePath (case-insensitive) for file-based items,
        /// or FileName for non-screenshot identical file names.
        /// </summary>
        private bool IsDuplicate(ClipboardItem newItem, ClipboardItem existing)
        {
            if (newItem == null || existing == null) return false;

            // 1. Text-based items (Text, Code, Url)
            bool isNewTextual = newItem.ItemType == ClipboardItemType.Text || newItem.ItemType == ClipboardItemType.Code || newItem.ItemType == ClipboardItemType.Url;
            bool isExistingTextual = existing.ItemType == ClipboardItemType.Text || existing.ItemType == ClipboardItemType.Code || existing.ItemType == ClipboardItemType.Url;
            
            if (isNewTextual && isExistingTextual)
            {
                return !string.IsNullOrEmpty(newItem.RawContent) && newItem.RawContent == existing.RawContent;
            }

            // 2. File-based items (File, Document, Pdf, Archive, Video, Audio, Presentation, Folder, Image)
            // If they have FilePath, check if they are equal
            if (!string.IsNullOrEmpty(newItem.FilePath) && !string.IsNullOrEmpty(existing.FilePath))
            {
                return string.Equals(newItem.FilePath, existing.FilePath, StringComparison.OrdinalIgnoreCase);
            }

            // 3. Fallback to FileName if file paths are not available (e.g. for some custom items)
            if (!string.IsNullOrEmpty(newItem.FileName) && !string.IsNullOrEmpty(existing.FileName) && newItem.ItemType == existing.ItemType)
            {
                // Avoid false positives for generic screenshots or empty names
                if (!newItem.FileName.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(newItem.FileName, existing.FileName, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        /// <summary>
        /// Compares two images using Resolution and exact file size in bytes to determine duplicate status.
        /// </summary>
        private bool IsImageDuplicate(ClipboardItem item1, ClipboardItem item2)
        {
            if (string.IsNullOrEmpty(item1.FilePath) || string.IsNullOrEmpty(item2.FilePath))
                return false;

            if (!File.Exists(item1.FilePath) || !File.Exists(item2.FilePath))
                return false;

            try
            {
                var fi1 = new FileInfo(item1.FilePath);
                var fi2 = new FileInfo(item2.FilePath);

                // If they have the exact same file size and dimensions, they are duplicate images
                if (fi1.Length == fi2.Length && item1.FormattedSize == item2.FormattedSize)
                {
                    return true;
                }
            }
            catch { } // Best-effort: failure is acceptable

            return false;
        }

        /// <summary>
        /// Scans the first 10 entries of DroppedItems for a duplicate of newItem.
        /// If found, removes the duplicate item from DroppedItems and backing database/files.
        /// </summary>
        public void DeduplicateItem(ClipboardItem newItem)
        {
            if (newItem == null) return;

            ClipboardItem? duplicateToRemoval = null;
            int checkCount = Math.Min(10, DroppedItems.Count);
            for (int i = 0; i < checkCount; i++)
            {
                var existing = DroppedItems[i];
                if (existing == null) continue;

                if (IsDuplicate(newItem, existing))
                {
                    duplicateToRemoval = existing;
                    break;
                }
            }

            if (duplicateToRemoval != null)
            {
                Classes.Logger.LogAction("DEDUP", $"Found duplicate in first 10 entries: Type={duplicateToRemoval.ItemType}, Path={duplicateToRemoval.FilePath}, Name={duplicateToRemoval.FileName}. Removing old duplicate.");
                RemoveItem(duplicateToRemoval);
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
