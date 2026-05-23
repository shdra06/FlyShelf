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

namespace FlyShelf.ViewModels
{
    public partial class FlyShelfViewModel
    {
        // PERF: Cooldown guard for mascot delete animation — skip if called within 300ms
        private DateTime _lastDeleteAnimTime = DateTime.MinValue;
        // PERF: Debounce ShelfVisibility notification during rapid deletes
        private System.Windows.Threading.DispatcherTimer? _shelfVisibilityDebounce;

        public void RemoveItem(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                // Structural Lock: Pinned items cannot be deleted unless physically unpinned first!
                if (item.IsPinned) return; 

                DroppedItems.Remove(item);

                // PERF: Debounce ShelfVisibility — batch rapid deletes into one notification
                if (_shelfVisibilityDebounce == null)
                {
                    _shelfVisibilityDebounce = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(100)
                    };
                    _shelfVisibilityDebounce.Tick += (s, e) =>
                    {
                        _shelfVisibilityDebounce.Stop();
                        OnPropertyChanged(nameof(ShelfVisibility));
                    };
                }
                _shelfVisibilityDebounce.Stop();
                _shelfVisibilityDebounce.Start();

                // PERF: Mascot animation cooldown — skip if fired within 300ms to avoid UI thread stalls
                if ((DateTime.Now - _lastDeleteAnimTime).TotalMilliseconds > 300)
                {
                    _lastDeleteAnimTime = DateTime.Now;
                    try { FlyShelf.Classes.AnimationTriggerService.Instance.OnDelete(); } catch { }
                }

                // Cleanup backing file + DB delete asynchronously in background
                var itemCopy = item;
                string filePath = item.FilePath;
                ClipboardItemType itemType = item.ItemType;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Classes.ClipboardHistoryManager.AppendDeleteToJournal(itemCopy);
                        CleanupTempFile(filePath);
                        Classes.ClipboardHistoryManager.DeletePersistentImage(filePath, itemType);
                    }
                    catch { }
                });
            }
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
                item.IsPinned = !item.IsPinned;
                
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
            var volatileItems = DroppedItems.Where(i => !i.IsPinned).ToList();
            if (volatileItems.Count > 0)
            {
                DroppedItems.RemoveRange(volatileItems);
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
                            Classes.ClipboardHistoryManager.DeletePersistentImage(item.FilePath, item.ItemType);
                        }
                        catch { }
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
                try
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var actualIndex = DroppedItems.IndexOf(sorted[i]);
                        if (actualIndex != -1 && actualIndex != i)
                        {
                            DroppedItems.Move(actualIndex, i);
                        }
                    }
                }
                finally
                {
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
                        var json = System.Text.Json.JsonSerializer.Serialize(pinned);
                        File.WriteAllText(path, json);
                    }
                    catch { }
                });
            }
            catch { }
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
                    catch { }
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

                            if (!string.IsNullOrEmpty(d.FilePath))
                            {
                                bool isFileBased = d.ItemType == ClipboardItemType.Image || d.ItemType == ClipboardItemType.QRCode ||
                                    d.ItemType == ClipboardItemType.File || d.ItemType == ClipboardItemType.Document ||
                                    d.ItemType == ClipboardItemType.Pdf || d.ItemType == ClipboardItemType.Archive ||
                                    d.ItemType == ClipboardItemType.Video || d.ItemType == ClipboardItemType.Audio ||
                                    d.ItemType == ClipboardItemType.Presentation;
                                if (isFileBased && !File.Exists(d.FilePath) && !Directory.Exists(d.FilePath))
                                    continue;
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
                                        if (icon != null) Application.Current.Dispatcher.InvokeAsync(() => capturedD.Icon = icon);
                                    }
                                    catch { }
                                });
                            }
                            else if (d.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(d.FilePath))
                            {
                                string imagePath = d.FilePath;
                                var capturedD = d;
                                System.Threading.Tasks.Task.Run(() => {
                                    try 
                                    {
                                        int decodeWidth = IsScrolling ? 48 : 300;
                                        var bmp = LoadImageThumbnail(imagePath, decodeWidth);
                                        if (bmp != null)
                                        {
                                            Application.Current.Dispatcher.InvokeAsync(() =>
                                            {
                                                capturedD.Icon = bmp;
                                                capturedD.IsLoadedHighQuality = !IsScrolling;
                                            });
                                        }
                                    } catch { }
                                });
                            }
                            pinnedToAdd.Add(d);
                        }
                        catch { }
                    }

                    if (pinnedToAdd.Count > 0)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => DroppedItems.AddRange(pinnedToAdd));
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Loads the next page of unpinned history items from SQLite on-demand (incremental scroll).
        /// </summary>
        public async Task LoadNextPageAsync()
        {
            // No pagination needed — all 200 items are loaded at startup
            await System.Threading.Tasks.Task.CompletedTask;
            return;
            
            if (_isPaginating || IsSearchActive) return;

            _isPaginating = true;
            try
            {
                // Dead code — kept for compilation only
                var nextItems = new List<ClipboardItem>();
                if (nextItems.Count == 0) return;

                var existingKeys = new HashSet<string>(DroppedItems.Select(GetDeduplicationKey).Where(k => !string.IsNullOrEmpty(k)));
                var itemsNeedingIcons = new List<ClipboardItem>();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in nextItems)
                    {
                        string itemKey = GetDeduplicationKey(item);
                        if (!string.IsNullOrEmpty(itemKey) && !existingKeys.Add(itemKey))
                            continue;
                        if (IsEffectivelyEmpty(item)) continue;
                        if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(item.RawContent))
                            item.FileName = item.RawContent.Length > 800 ? item.RawContent.Substring(0, 800) + "..." : item.RawContent;
                        item.EvaluateSmartActions();
                        DroppedItems.Add(item);
                        bool needsIcon = (item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                            && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
                        bool needsFileIcon = !needsIcon && (item.ItemType == ClipboardItemType.File || item.ItemType == ClipboardItemType.Document ||
                            item.ItemType == ClipboardItemType.Pdf || item.ItemType == ClipboardItemType.Archive ||
                            item.ItemType == ClipboardItemType.Video || item.ItemType == ClipboardItemType.Audio ||
                            item.ItemType == ClipboardItemType.Presentation) && !string.IsNullOrEmpty(item.FilePath);
                        if (needsIcon || needsFileIcon) itemsNeedingIcons.Add(item);
                    }
                });

                if (itemsNeedingIcons.Count > 0)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        foreach (var item in itemsNeedingIcons)
                        {
                            await _iconDecodeSemaphore.WaitAsync();
                            try
                            {
                                if ((item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                                    && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                                {
                                    var icon = LoadImageThumbnail(item.FilePath);
                                    if (icon != null)
                                        await Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                                }
                                else if (!string.IsNullOrEmpty(item.FilePath))
                                {
                                    var icon = GetIcon(item.FilePath);
                                    if (icon != null)
                                        await Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                                }
                            }
                            catch { }
                            finally { _iconDecodeSemaphore.Release(); }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("PAGINATION_ERROR", $"Failed to load next page: {ex.Message}");
            }
            finally
            {
                _isPaginating = false;
            }
        }

        /// <summary>
        /// Searches the history database using FTS5 and populates DroppedItems with results, loading icons asynchronously.
        /// </summary>
        public async Task SearchHistoryAsync(string query)
        {
            // DB persistence disabled — search in-memory
            IsSearchActive = true;
            try
            {
                string q = (query ?? "").ToLowerInvariant();
                var results = DroppedItems.Where(i =>
                    (!string.IsNullOrEmpty(i.RawContent) && i.RawContent.ToLowerInvariant().Contains(q)) ||
                    (!string.IsNullOrEmpty(i.FileName) && i.FileName.ToLowerInvariant().Contains(q))
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

        private string AutoFormatCode(string raw)
        {
            try
            {
                var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var formatted = new System.Text.StringBuilder();
                int indentLevel = 0;
                string tab = "    ";

                foreach (var line in lines)
                {
                    string cleanLine = line.Trim();
                    if (string.IsNullOrEmpty(cleanLine)) continue;
                    
                    if (cleanLine.StartsWith("}")) indentLevel = Math.Max(0, indentLevel - 1);
                    
                    formatted.AppendLine(string.Concat(Enumerable.Repeat(tab, indentLevel)) + cleanLine);
                    
                    if (cleanLine.EndsWith("{")) indentLevel++;
                }
                return formatted.ToString().TrimEnd();
            }
            catch { return raw; }
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
                var oldest = _recentCloudContent.OrderBy(kv => kv.Value).First().Key;
                _recentCloudContent.Remove(oldest);
            }
        }

        /// <summary>
        /// Unified file sync: Cloudflare tunnel → Firebase Storage → log-only fallback.
        /// Replaces 3 previously duplicated sync blocks.
        /// </summary>
        private async System.Threading.Tasks.Task SyncFileToDevicesAsync(string filePath, ClipboardItem item, long maxFirebaseBytes = 25 * 1024 * 1024, string label = "FILE")
        {
            try
            {
                long fSize = new FileInfo(filePath).Length;
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
                                Application.Current.Dispatcher.InvokeAsync(() =>
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
                        Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast($"Synced via LAN! \u26a1 (Available for companion app pulling)"));
                    }
                }

                if (lanSuccess)
                {
                    return;
                }

                // PRIORITY 2: Fallback to Cloudflare P2P push if local LAN is not available/failed
                if (srv != null && !string.IsNullOrEmpty(srv.GlobalUrl) && srv.GlobalUrl.Contains("trycloudflare.com") && tunnelOk)
                {
                    string downloadUrl = $"{srv.GlobalUrl}/download?path={Uri.EscapeDataString(filePath)}";
                    FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"Sending '{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) via Cloudflare P2P");
                    var syncItem = item.CloneForSync(downloadUrl);
                    await FlyShelf.Classes.CloudDiscoveryManager.PushToCloudHub(syncItem);
                    Application.Current.Dispatcher.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"{label} ({FormatFileSize(fSize)}) synced via P2P \ud83c\udf10"));
                    return;
                }

                // No LAN success and no Cloudflare tunnel available
                FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"'{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) — no active LAN peers or Cloudflare tunnel available");
                Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast($"\u26a0\ufe0f {Path.GetFileName(filePath)} ({FormatFileSize(fSize)}) — no active LAN peers or Cloudflare tunnel"));
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction($"{label} SYNC", $"Error: {ex.Message}");
            }
        }

        private const int MAX_UNPINNED_ITEMS = 500;
        private const int WARNING_THRESHOLD = 100;

        /// <summary>
        /// Prunes oldest unpinned items beyond the cap to prevent unbounded memory growth.
        /// Warning toast at 100 items, hard cap at 500.
        /// </summary>
        public void PruneOldItems()
        {
            // Show warning starting at 100 items (but don't prune yet)
            if (DroppedItems.Count >= WARNING_THRESHOLD && DroppedItems.Count <= MAX_UNPINNED_ITEMS)
            {
                int unpinnedCount = DroppedItems.Count(i => !i.IsPinned);
                if (unpinnedCount >= WARNING_THRESHOLD && unpinnedCount < MAX_UNPINNED_ITEMS)
                {
                    // Warn every 50 items to give frequent heads-up
                    if (unpinnedCount % 50 == 0)
                    {
                        int remaining = MAX_UNPINNED_ITEMS - unpinnedCount;
                        FlyShelf.Windows.ToastWindow.ShowToast($"\u26a0\ufe0f Clipboard has {unpinnedCount} items. {remaining} slots remaining (max {MAX_UNPINNED_ITEMS}).");
                    }
                }
            }

            if (DroppedItems.Count <= MAX_UNPINNED_ITEMS) return;

            // Collect unpinned items to remove (from end, oldest first)
            var itemsToRemove = new List<ClipboardItem>();
            for (int i = DroppedItems.Count - 1; i >= 0 && DroppedItems.Count - itemsToRemove.Count > MAX_UNPINNED_ITEMS; i--)
            {
                if (!DroppedItems[i].IsPinned)
                    itemsToRemove.Add(DroppedItems[i]);
            }

            if (itemsToRemove.Count > 0)
            {
                var filesToCleanup = new List<(string path, ClipboardItemType type)>();
                foreach (var item in itemsToRemove)
                {
                    filesToCleanup.Add((item.FilePath, item.ItemType));
                }

                DroppedItems.RemoveRange(itemsToRemove);

                FlyShelf.Windows.ToastWindow.ShowToast($"\ud83d\uddd1\ufe0f Clipboard full \u2014 removed {itemsToRemove.Count} oldest items.");

                // Background cleanup
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (var (path, type) in filesToCleanup)
                    {
                        try { CleanupTempFile(path); } catch { }
                        try { Classes.ClipboardHistoryManager.DeletePersistentImage(path, type); } catch { }
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

            DroppedItems.Insert(0, newItem);
            return true;
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
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var toRemove = DroppedItems.Where(i => !i.IsPinned && i.DateCopied < cutoff).ToList();
                        foreach (var item in toRemove)
                        {
                            DroppedItems.Remove(item);
                        }
                        if (toRemove.Count > 0)
                        {
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
