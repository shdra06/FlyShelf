// ---------------------------------------------------------------
// FlyShelfViewModel � Drop Handler & Utilities
// HandleDrop (file/text/image processing), SHFILEINFO interop,
// SaveGlobalSettings, RelayCommand
// Split from FlyShelfViewModel.cs for modularity
// ---------------------------------------------------------------using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.Classes;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace FlyShelf.ViewModels
{
    public partial class FlyShelfViewModel
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _shellIconCache = new();
        private static readonly char[] NewLineChars = { '\r', '\n' };
        private static readonly char[] SpaceSeparator = { ' ' };
        private static readonly string[] CommonExtensions = { ".txt", ".md", ".cs", ".ts", ".js", ".py", ".json", ".xml",
            ".html", ".css", ".jpg", ".png", ".gif", ".pdf", ".exe", ".dll", ".zip",
            ".config", ".yaml", ".yml", ".toml", ".log", ".bat", ".sh", ".ps1",
            ".xaml", ".csproj", ".sln", ".gradle", ".kt", ".swift", ".dart" };
        public void HandleDrop(IDataObject data, bool forceClipboardSync = false, bool skipCloudSync = false, string? sourceDevice = null, string? sourceDeviceType = null, string? transferMethod = null)
        {
            try
            {
            string[]? files = null;
            string? text = null;
            BitmapSource? bitmap = null;

            // ═══ PHASE 1: Extract file paths (cheap COM calls) ═══
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                    files = data.GetData(DataFormats.FileDrop) as string[];
                    
                if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
                {
                    var fName = data.GetData("FileNameW") as string[];
                    // [FIX TEXT-DROP]: Only accept FileNameW if it's an actual file system path.
                    // Chrome/Edge provide FileNameW for URL drags with temp shortcut paths.
                    if (fName != null && fName.Length > 0 && !string.IsNullOrEmpty(fName[0])
                        && (File.Exists(fName[0]) || Directory.Exists(fName[0])))
                        files = fName;
                }
                
                if ((files == null || files.Length == 0) && data.GetDataPresent("text/uri-list"))
                {
                    string uriText = data.GetData("text/uri-list") as string;
                    if (!string.IsNullOrEmpty(uriText))
                    {
                        var lines = uriText.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries);
                        var parsedPaths = new System.Collections.Generic.List<string>();
                        foreach (var l in lines)
                        {
                            string p = l.Trim();
                            if (p.StartsWith("file:///", StringComparison.Ordinal))
                            {
                                try
                                {
                                    p = new Uri(p).LocalPath;
                                    // FIX: percent-encoded colon (%3A) → "/c:/path" instead of "C:\path"
                                    if (p.Length >= 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == ':')
                                        p = p.Substring(1);
                                }
                                catch { continue; }
                            }
                            if (File.Exists(p) || Directory.Exists(p)) parsedPaths.Add(p);
                        }
                        if (parsedPaths.Count > 0)
                            files = parsedPaths.ToArray();
                        else if (text == null)
                        {
                            // [FIX TEXT-DROP]: If uri-list contains no valid file paths (e.g. https:// URLs),
                            // preserve the raw URI as text content — don't discard it.
                            text = uriText.Trim();
                        }
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable

            // ═══ PHASE 2: Check for bitmap data presence (cheap) ═══
            // Only check GetDataPresent (lightweight COM probe) on UI thread.
            // Defer the expensive GetData (full bitmap decode) to background if files are empty.
            bool hasBitmapData = false;
            try
            {
                hasBitmapData = data.GetDataPresent(DataFormats.Bitmap) ||
                                data.GetDataPresent(typeof(BitmapSource)) ||
                                data.GetDataPresent(DataFormats.Dib);
            }
            catch (Exception ex) { Logger.LogAction("DROP", $"Non-critical: {ex.Message}"); }

            // If we have files, skip the expensive bitmap extraction entirely —
            // file-based images will be loaded from disk (much faster than OLE decode).
            if (hasBitmapData && (files == null || files.Length == 0))
            {
                // Must extract bitmap on STA thread (OLE COM requirement),
                // but do it ONLY when there are no file paths available.
                try
                {
                    if (data.GetDataPresent("PNG"))
                    {
                        try
                        {
                            if (data.GetData("PNG") is System.IO.MemoryStream ms)
                            {
                                bitmap = System.Windows.Media.Imaging.BitmapFrame.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                            }
                        }
                        catch { }
                    }
                    if (bitmap == null && data.GetDataPresent(DataFormats.Bitmap))
                        bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (bitmap == null && data.GetDataPresent(typeof(BitmapSource)))
                        bitmap = data.GetData(typeof(BitmapSource)) as BitmapSource;
                    if (bitmap == null && data.GetDataPresent(DataFormats.Dib))
                    {
                        try
                        {
                            bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
                        }
                        catch { }
                    }

                    if (bitmap != null && bitmap.CanFreeze && !bitmap.IsFrozen)
                        bitmap.Freeze(); // Frozen to be safe for background threads
                }
                catch { } // Best-effort: failure is acceptable
            }

            if (bitmap == null && (files == null || files.Length == 0))
            {
                try
                {
                    if (data.GetDataPresent(DataFormats.UnicodeText))
                        text = data.GetData(DataFormats.UnicodeText) as string;
                    if (string.IsNullOrEmpty(text) && data.GetDataPresent(DataFormats.Text))
                        text = data.GetData(DataFormats.Text) as string;
                }
                catch { } // Best-effort: failure is acceptable
            }

            // Route all heavy tasks, zipping, file-saving and collection processing to background thread
            System.Threading.Tasks.Task.Run(() => 
                HandleDropInternal(files, bitmap, text, forceClipboardSync, skipCloudSync, sourceDevice, sourceDeviceType, transferMethod));
            }
            catch (Exception ex)
            {
                Logger.LogCrash("HandleDrop", ex);
            }
        }

        internal void HandleDropInternal(string[]? files, BitmapSource? bitmap, string? text, bool forceClipboardSync, bool skipCloudSync, string? sourceDevice = null, string? sourceDeviceType = null, string? transferMethod = null, string? sourceAppName = null, BitmapSource? sourceAppIcon = null)
        {
            try
            {
            // H-04/H-06: Freeze sourceAppIcon at entry so it's thread-safe for all downstream usage
            if (sourceAppIcon != null && sourceAppIcon.CanFreeze && !sourceAppIcon.IsFrozen)
                sourceAppIcon.Freeze();

            if (files != null && files.Length > 0)
            {
                // ═══ THEME IMPORT: Intercept .flyshelf-theme files ═══
                if (files.Length == 1)
                {
                    string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                    if (ext == ".flyshelf-theme" || ext == ".flyshelftheme")
                    {
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            string importedName = Classes.ThemeManager.Instance.ImportTheme(files[0]);
                            if (importedName != null)
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast($"Theme '{importedName}' imported!");
                                Classes.ThemeManager.Instance.SetActiveTheme(importedName);
                            }
                            else
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast("Invalid theme file");
                            }
                        });
                        return;
                    }
                }

                if (files.Length > 20)
                {
                    // Group files together! (No deduplication check)
                    var groupItem = new ClipboardItem(files);
                    if (sourceDevice != null) groupItem.SourceDeviceName = sourceDevice;
                    if (sourceDeviceType != null) groupItem.SourceDeviceType = sourceDeviceType;
                    if (transferMethod != null) groupItem.TransferMethod = transferMethod;
                    if (!string.IsNullOrEmpty(sourceAppName)) groupItem.SourceAppName = sourceAppName;
                    if (sourceAppIcon != null) groupItem.SourceAppIcon = sourceAppIcon;
                    
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        DeduplicateAndInsert(groupItem);
                    });

                    // PERF: Journal the single item (fast append) instead of full serialize
                    FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(groupItem);

                    FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Grouped {files.Length} files into a single Group item.");

                    // Instantly push SSE event to connected mobile clients
                    FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(
                        groupItem.ItemType.ToString(), 
                        groupItem.FileName);



                    return;
                }

                // ═══ BATCH FILE PROCESSING ═══
                const int MAX_FILES_PER_BATCH = 100;
                if (files.Length > MAX_FILES_PER_BATCH)
                {
                    FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Batch capped: {files.Length} files → processing first {MAX_FILES_PER_BATCH}");
                    files = files.Take(MAX_FILES_PER_BATCH).ToArray();
                }

                // Phase 1: Collect items (No duplicate scanning or bumping!)
                var newItems = new List<(ClipboardItem item, string path)>();
                foreach (string file in files)
                {
                    var item = new ClipboardItem(file);
                    if (sourceDevice != null) item.SourceDeviceName = sourceDevice;
                    if (sourceDeviceType != null) item.SourceDeviceType = sourceDeviceType;
                    if (transferMethod != null) item.TransferMethod = transferMethod;
                    if (!string.IsNullOrEmpty(sourceAppName)) item.SourceAppName = sourceAppName;
                    if (sourceAppIcon != null) item.SourceAppIcon = sourceAppIcon;
                    newItems.Add((item, file));
                }

                // Deduplicate within the batch itself first
                var distinctNewItems = new List<ClipboardItem>();
                var seenBatchKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (item, _) in newItems)
                {
                    string key = GetDeduplicationKey(item);
                    if (string.IsNullOrEmpty(key) || seenBatchKeys.Add(key))
                    {
                        distinctNewItems.Add(item);
                    }
                }

                // Phase 2: Batch-insert into ObservableCollection on UI thread
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    var duplicatesToRemove = new List<ClipboardItem>();
                    foreach (var newItem in distinctNewItems)
                    {
                        int checkCount = Math.Min(10, DroppedItems.Count);
                        for (int i = 0; i < checkCount; i++)
                        {
                            var existing = DroppedItems[i];
                            if (existing != null && !existing.IsPinned && !duplicatesToRemove.Contains(existing) && IsDuplicate(newItem, existing))
                            {
                                duplicatesToRemove.Add(existing);
                            }
                        }
                    }

                    if (duplicatesToRemove.Count > 0)
                    {
                        BulkRemoveItems(duplicatesToRemove);
                    }

                    DroppedItems.InsertRange(0, distinctNewItems);
                    PruneOldItems();
                    OnPropertyChanged(nameof(ShelfVisibility));

                    if (!string.IsNullOrEmpty(sourceDevice))
                    {
                        foreach (var newItem in distinctNewItems)
                        {
                            string fileFp = $"IMG::{newItem.FormattedSize}";
                            MarkAsCloudSourced(fileFp);
                        }
                        PersistHistory();
                    }
                });

                // PERF: Journal each item individually (fast JSONL append)
                foreach (var (item, _) in newItems)
                {
                    FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(item);
                }

                FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Batch inserted {newItems.Count} files directly (no-dedup)");

                if (newItems.Count > 0)
                {
                    var first = newItems[0].item;
                    FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(first.ItemType.ToString(), first.FileName ?? (first.RawContent != null ? first.RawContent[..Math.Min(40, first.RawContent.Length)] : ""));
                }

                // Phase 3: Background — load icons + run sync (completely off the UI thread)
                if (newItems.Count > 0)
                {
                    var capturedNewItems = newItems.ToList();
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        foreach (var (item, filePath) in capturedNewItems)
                        {
                            await _iconDecodeSemaphore.WaitAsync();
                            try
                            {
                                if (item.ItemType == ClipboardItemType.Image)
                                {
                                    try
                                    {
                                        var bmp = LoadImageThumbnail(filePath, 300);
                                        if (bmp != null)
                                        {
                                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                                            {
                                                item.Icon = bmp;
                                                item.IsLoadedHighQuality = true;
                                            });
                                        }
                                    }
                                    catch { } // Best-effort: failure is acceptable
                                }
                                else
                                {
                                    // Skip shell icon fetch for items that already have a custom vector icon
                                    // (e.g. .md files which use GenerateMarkdownIcon())
                                    if (item.Icon == null)
                                    {
                                        var icon = GetIcon(filePath);
                                        if (icon != null)
                                        {
                                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                                            {
                                                // Double-check: don't overwrite if custom icon was set in the meantime
                                                if (item.Icon == null)
                                                    item.Icon = icon;
                                            });
                                        }
                                    }
                                }
                            }
                            finally { _iconDecodeSemaphore.Release(); }

                            // Cloud Discovery sync
                            if (capturedNewItems.Count > 10 || !FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery || !FlyShelf.Classes.SettingsManager.Current.EnableOutgoingSync || skipCloudSync)
                                continue;

                            var archPath = FlyShelf.Classes.SettingsManager.Current.CustomArchiveExtractionPath;
                            if (string.IsNullOrWhiteSpace(archPath)) archPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Extracted");
                            bool isGlobalDownload = filePath.StartsWith(archPath, StringComparison.OrdinalIgnoreCase);

                            if (!isGlobalDownload)
                            {


                                string fileExt = Path.GetExtension(filePath).ToLowerInvariant();
                                if (fileExt is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial")
                                    continue;

                                try { using var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                                catch (IOException) { continue; }
                                catch { } // Best-effort: failure is acceptable

                                // Echo prevention: don't re-sync files we received from peers
                                try
                                {
                                    var fi = new System.IO.FileInfo(filePath);
                                    string fileFp = $"FILE::{fi.Name}::{fi.Length}";
                                    if (IsCloudSourced(fileFp))
                                    {
                                        FlyShelf.Classes.Logger.LogAction("SYNC ECHO GUARD", $"Blocked re-sync of cloud-sourced file: {fi.Name}");
                                        continue;
                                    }
                                }
                                catch { } // Best-effort: failure is acceptable

                                await SyncFileToDevicesAsync(filePath, item, label: "FILE");
                            }
                        }
                    });
                }

                // Clipboard writeback
                if (forceClipboardSync && files.Length <= 10)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var dropList = new System.Collections.Specialized.StringCollection();
                        dropList.Add(files[0]);
                        Classes.ClipboardHelper.SafeSetFileDropList(dropList, suppressEcho: true, echoDelayMs: 500);
                    });
                }
            }
            else if (bitmap != null)
            {
                FlyShelf.Classes.Logger.LogAction("DRAG IN", "Processing Bitmap image payload (no-dedup)");
                
                var item = new ClipboardItem();
                if (sourceDevice != null) item.SourceDeviceName = sourceDevice;
                if (sourceDeviceType != null) item.SourceDeviceType = sourceDeviceType;
                if (transferMethod != null) item.TransferMethod = transferMethod;
                if (!string.IsNullOrEmpty(sourceAppName)) item.SourceAppName = sourceAppName;
                if (sourceAppIcon != null) item.SourceAppIcon = sourceAppIcon;
                item._suppressPropertyNotifications = true; // PERF: No listeners yet
                item.ItemType = ClipboardItemType.Image;
                item.FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
                item.Extension = "IMAGE";
                item.FormattedSize = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}";
                item._suppressPropertyNotifications = false;
                
                item.EvaluateSmartActions();

                // PERF: Scale raw frozen bitmap in memory instantly using TransformedBitmap on background thread
                try
                {
                    double scale = 300.0 / bitmap.PixelWidth;
                    if (scale < 1.0)
                    {
                        var scaledBmp = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
                        scaledBmp.Freeze(); // Frozen for cross-thread binding
                        item.Icon = scaledBmp;
                    }
                    else
                    {
                        item.Icon = bitmap;
                    }
                }
                catch (Exception thumbEx)
                {
                    Classes.Logger.LogAction("ICON IMMEDIATE", $"Inline scale failed: {thumbEx.Message}");
                }

                // Insert immediately into DroppedItems on UI thread
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    DeduplicateAndInsert(item);
                });

                // PERF: Journal the bitmap item immediately (fast append)
                FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(item);

                // Write PNG to disk and do follow-up operations completely in background thread
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Transparency (Ghost) check: backend process that happens after a few seconds
                    var capturedBmpToCheck = bitmap;
                    var capturedItemToCheck = item;
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(3000); // Wait 3 seconds
                        bool isGhostImage = false;
                        try
                        {
                            // Safe on background thread: source bitmap is Frozen, FormatConvertedBitmap on frozen source is thread-safe
                            var converted = new FormatConvertedBitmap(capturedBmpToCheck, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                            converted.Freeze();
                            int w = converted.PixelWidth;
                            int h = converted.PixelHeight;
                            byte[] pixel = new byte[4];
                            int transparentCount = 0;
                            const int gridSize = 4;
                            for (int gy = 0; gy < gridSize; gy++)
                            {
                                int y = (gy * 2 + 1) * h / (gridSize * 2);
                                for (int gx = 0; gx < gridSize; gx++)
                                {
                                    int x = (gx * 2 + 1) * w / (gridSize * 2);
                                    converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
                                    if (pixel[3] < 10) transparentCount++;
                                }
                            }
                            if (transparentCount >= 15)
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"⛔ Detected ghost image ({w}x{h}) — {transparentCount}/16 samples transparent. Removing...");
                                isGhostImage = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("CLIPBOARD", $"⚠️ Ghost image check error: {ex.Message}");
                        }

                        if (isGhostImage)
                        {
                            var ghostDispatcher = Application.Current?.Dispatcher;
                            if (ghostDispatcher != null)
                            {
                                await ghostDispatcher.InvokeAsync(() =>
                                {
                                    RemoveItem(capturedItemToCheck);
                                });
                            }
                        }
                    });

                    string tempFile = Classes.ClipboardHistoryManager.GetPersistentImagePath();

                    // ═══ IMAGE SIZE ENFORCEMENT ═══
                    // Max supported preview: 4K (3840×2160)
                    // Images wider/taller than 4K: downscale to 4K for saving, but do NOT load thumbnail — show filename only.
                    // Images with estimated raw size > 15MB (uncompressed BGRA32): reject entirely to prevent massive writes.
                    const int MAX_SUPPORTED_WIDTH  = 3840; // 4K
                    const int MAX_SUPPORTED_HEIGHT = 2160;
                    const long MAX_RAW_BYTES = 120L * 1024 * 1024; // 120 MB uncompressed cap (supports up to 8K)

                    int srcW = bitmap.PixelWidth;
                    int srcH = bitmap.PixelHeight;
                    long rawBytes = (long)srcW * srcH * 4; // 4 bytes per pixel (BGRA32)

                    bool isOversized   = srcW > MAX_SUPPORTED_WIDTH || srcH > MAX_SUPPORTED_HEIGHT;
                    bool isTooBig      = rawBytes > MAX_RAW_BYTES;

                    if (isTooBig)
                    {
                        // Completely refuse to save — too large even downscaled
                        Classes.Logger.LogAction("CLIPBOARD", $"⛔ Image rejected: {srcW}×{srcH} raw={rawBytes / 1024 / 1024}MB exceeds {MAX_RAW_BYTES / 1024 / 1024}MB limit. Not saving.");
                        var sizeDispatcher = Application.Current?.Dispatcher;
                        if (sizeDispatcher != null)
                        {
                            await sizeDispatcher.InvokeAsync(() =>
                            {
                                item.FileName = $"Image too large to save ({srcW}×{srcH})";
                                item.RawContent = $"Image ({srcW}×{srcH}) — too large to store";
                            });
                        }
                        return; // Skip disk write entirely
                    }

                    // For oversized (>4K but within raw limit): downscale for storage, but skip thumbnail
                    BitmapSource saveSource = bitmap;
                    if (isOversized)
                    {
                        double scaleDown = Math.Min((double)MAX_SUPPORTED_WIDTH / srcW, (double)MAX_SUPPORTED_HEIGHT / srcH);
                        var downscaled = new TransformedBitmap(bitmap, new ScaleTransform(scaleDown, scaleDown));
                        downscaled.Freeze();
                        saveSource = downscaled;
                        Classes.Logger.LogAction("CLIPBOARD", $"⚠️ Image >4K ({srcW}×{srcH}): downscaling to {saveSource.PixelWidth}×{saveSource.PixelHeight} for storage. No thumbnail.");
                    }

                    FormatConvertedBitmap? convertedBmp = null;
                    try
                    {
                        // Safe on background thread: source bitmap is Frozen, FormatConvertedBitmap on frozen source is thread-safe
                        convertedBmp = new FormatConvertedBitmap(saveSource, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                        convertedBmp.Freeze();
                    }
                    catch (Exception convEx)
                    {
                        Classes.Logger.LogAction("CLIPBOARD", $"⚠️ FormatConvertedBitmap conversion failed: {convEx.Message}");
                    }

                    if (convertedBmp != null)
                    {
                        try
                        {
                            using (var fs = new FileStream(tempFile, FileMode.Create))
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(convertedBmp));
                                if (!Classes.DiskSpaceHelper.HasSufficientDiskSpace(tempFile, 10_000_000))
                                {
                                    Classes.Logger.LogAction("IMAGE_SAVE", "Insufficient disk space");
                                    return;
                                }
                                encoder.Save(fs);
                            }

                        // Load thumbnail image — always load from saved file (even if oversized,
                        // the file on disk is already downscaled to ≤4K by the encoder above).
                        // Always use 300px decode width for sharp thumbnails — this runs on a
                        // background thread so the UI thread is not blocked.
                        BitmapSource? bitmapImage = null;
                        try
                        {
                            bitmapImage = LoadImageThumbnail(tempFile, 300);
                        }
                        catch (Exception iconEx)
                        {
                            Classes.Logger.LogAction("ICON FILE", $"Failed to load saved thumbnail: {iconEx.Message}");
                        }


                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            if (bitmapImage != null)
                            {
                                item.Icon = bitmapImage; // Swap to perfect thumbnail
                                item.IsLoadedHighQuality = true;
                            }
                            // Don't set IsLoadedHighQuality if bitmapImage is null — this ensures
                            // RenderVisibleThumbnails will retry loading instead of permanently skipping

                            item.FilePath = tempFile;

                            // Now that FilePath is written to disk, check for duplicate image in the first 10 items
                            ClipboardItem? duplicateImage = null;
                            int checkCount = Math.Min(10, DroppedItems.Count);
                            for (int i = 1; i < checkCount; i++) // Start at index 1 because current item is at index 0
                            {
                                if (i >= DroppedItems.Count) break; // Bounds safety: collection may have been mutated
                                var existing = DroppedItems[i];
                                if (existing != null && existing.ItemType == ClipboardItemType.Image)
                                {
                                    if (IsImageDuplicate(item, existing))
                                    {
                                        duplicateImage = existing;
                                        break;
                                    }
                                }
                            }
                            if (duplicateImage != null)
                            {
                                Classes.Logger.LogAction("DEDUP", $"Found duplicate image: {duplicateImage.FilePath}. Removing older duplicate.");
                                RemoveItem(duplicateImage);
                            }
                            item.ScanForQRCodeAsync(tempFile);
                            OnPropertyChanged(nameof(ShelfVisibility));

                            // Notify mobile clients
                            FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? "");

                            if (!forceClipboardSync)
                            {
                                // Don't re-write clipboard for passive captures.
                                // The image is already on the clipboard from the user's copy action.
                                // Re-writing caused race conditions that left the clipboard empty,
                                // breaking Ctrl+V paste. We just save the image to our history.
                            }
                            
                            if (forceClipboardSync)
                            {
                                Classes.ClipboardHelper.SafeSetImage(item.Icon, suppressEcho: true, echoDelayMs: 500);
                            }

                            // Cloud discovery sync (with echo prevention logic)
                            if (FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery && FlyShelf.Classes.SettingsManager.Current.EnableOutgoingSync && !skipCloudSync)
                            {
                                string imgFp = $"IMG::{item.FormattedSize}";
                                if (!IsCloudSourced(imgFp))
                                {
                                    string capturedTempFile = tempFile;
                                    var capturedItem = item;
                                    _ = System.Threading.Tasks.Task.Run(async () => await SyncFileToDevicesAsync(capturedTempFile, capturedItem, maxFirebaseBytes: 5 * 1024 * 1024, label: "IMAGE"));
                                }
                                else
                                {
                                    FlyShelf.Classes.Logger.LogAction("IMAGE SYNC", "Skipped — image arrived from cloud (echo prevention)");
                                }
                            }

                            // If it was a remote transfer, make sure we persist history and set cloud-sourced flag!
                            if (!string.IsNullOrEmpty(sourceDevice))
                            {
                                string imgFp = $"IMG::{item.FormattedSize}";
                                MarkAsCloudSourced(imgFp);
                                PersistHistory();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("IMAGE CORE", $"Failed to encode image: {ex.Message}");
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            item.ItemType = ClipboardItemType.Text;
                            item.FileName = "Image Failed to Decode!";
                            item.RawContent = "The browser or system exported an image payload that could not be rasterized.";
                            item.Extension = "ERROR";
                            OnPropertyChanged(nameof(ShelfVisibility));
                        });
                    }
                }
                });
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Trim().TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(text) || text == "\\n" || text == "\\r\\n" || text == "\\r") return;

                if (text.Length < 10000)
                {
                    string visibleCheck = System.Text.RegularExpressions.Regex.Replace(text, 
                        @"[\u200B-\u200F\u2028-\u202F\u2060-\u206F\uFE00-\uFE0F\uFEFF\u00AD]", "");
                    if (string.IsNullOrWhiteSpace(visibleCheck) || visibleCheck == "\\n" || visibleCheck == "\\r\\n") return;
                }

                FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Processing Text payload length: {text.Length} (no-dedup)");

                string capturedText = text;
                bool capturedForceSync = forceClipboardSync;

                System.Threading.Tasks.Task.Run(() =>
                {
                    ClipboardItem? item = null;

                    try
                    {
                        string? normPath = ExtractNormalizedPath(capturedText);
                        if (!string.IsNullOrEmpty(normPath))
                        {
                            if (File.Exists(normPath))
                            {
                                FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Resolved text path to file: {normPath}");
                                item = new ClipboardItem(normPath);

                                // If item doesn't have a custom vector icon (e.g. Markdown or APK), load shell icon
                                if (item.Icon == null)
                                {
                                    string p = normPath;
                                    var it = item;
                                    _ = System.Threading.Tasks.Task.Run(() =>
                                    {
                                        var icon = GetIcon(p);
                                        if (icon != null)
                                        {
                                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                                            {
                                                if (it.Icon == null) it.Icon = icon;
                                            });
                                        }
                                    });
                                }
                            }
                            else if (Directory.Exists(normPath))
                            {
                                FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Resolved text path to folder: {normPath}");
                                item = new ClipboardItem(normPath);
                                item.ItemType = ClipboardItemType.Folder;
                                item.Extension = "FOLDER";
                                item.GenerateFolderIcon();
                            }
                        }
                    }
                    catch { } // Best-effort: failure is acceptable

                    if (item == null)
                    {
                        item = new ClipboardItem();
                        item._suppressPropertyNotifications = true; // PERF: suppress during construction
                        item.RawContent = capturedText;
                        item.FormattedSize = string.Empty;

                        if (IsWebUrl(capturedText, out string? cleanUrl))
                        {
                            item.RawContent = cleanUrl;
                            item.ItemType = ClipboardItemType.Url;
                            item.FileName = cleanUrl;
                            item.Extension = "LINK";
                        }
                        else
                        {
                            bool classified = false;

                            // ═══ 1. SVG MARKUP DETECTION ═══
                            if (Classes.ImageThumbnailManager.IsSvgMarkup(capturedText))
                            {
                                item.ItemType = ClipboardItemType.Image;
                                item.Extension = "SVG";
                                item.FileName = "Vector Graphic.svg";
                                item.RawContent = capturedText;
                                item.Icon = Classes.ImageThumbnailManager.RenderSvgFromMarkup(capturedText, 300);
                                if (item.Icon != null) item.IsLoadedHighQuality = true;
                                classified = true;
                            }

                            // ═══ 2. UNIVERSAL FILE PATH DETECTION (Physical files, URLs, or path strings) ═══
                            if (!classified)
                            {
                                string trimmedText = capturedText.Trim();
                                string potentialPath = trimmedText;
                                if (potentialPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        potentialPath = new Uri(potentialPath).LocalPath;
                                        if (potentialPath.Length >= 3 && potentialPath[0] == '/' && char.IsLetter(potentialPath[1]) && potentialPath[2] == ':')
                                            potentialPath = potentialPath.Substring(1);
                                    }
                                    catch { }
                                }
                                else if (potentialPath.Length >= 2 && ((potentialPath[0] == '"' && potentialPath[^1] == '"') || (potentialPath[0] == '\'' && potentialPath[^1] == '\'')))
                                {
                                    potentialPath = potentialPath.Substring(1, potentialPath.Length - 2).Trim();
                                }

                                bool isPhysical = File.Exists(potentialPath) || Directory.Exists(potentialPath);
                                bool looksLikeFilePath = isPhysical || 
                                    (((potentialPath.Contains('\\') || potentialPath.Contains('/')) || (potentialPath.Length >= 2 && char.IsLetter(potentialPath[0]) && potentialPath[1] == ':'))
                                     && !potentialPath.Contains('\n')
                                     && potentialPath.Length < 1000);

                                if (looksLikeFilePath)
                                {
                                    try
                                    {
                                        string normalizedPath = potentialPath.Replace('/', '\\').TrimEnd();
                                        string candidateFileName = Path.GetFileName(normalizedPath);
                                        string candidateExt = Path.GetExtension(normalizedPath);

                                        if (!string.IsNullOrEmpty(candidateFileName) && (!string.IsNullOrEmpty(candidateExt) && candidateExt.Length >= 2 && candidateExt.Length <= 8 || isPhysical))
                                        {
                                            // Construct item with full file classification rules
                                            item = new ClipboardItem(normalizedPath);
                                            item.FilePath = normalizedPath;
                                            item.FileName = candidateFileName;
                                            if (item.ItemType == ClipboardItemType.Image && item.Icon == null && File.Exists(normalizedPath))
                                            {
                                                var thumb = Classes.ImageThumbnailManager.LoadThumbnail(normalizedPath, 300);
                                                if (thumb != null)
                                                {
                                                    item.Icon = thumb;
                                                    item.IsLoadedHighQuality = true;
                                                }
                                            }
                                            classified = true;
                                        }
                                    }
                                    catch { }
                                }
                            }

                            if (!classified)
                            {
                                string classificationSample = capturedText.Length > 10000 
                                    ? capturedText.Substring(0, 10000) 
                                    : capturedText;

                                // Markdown detection FIRST — IsProperCode() treats # headings
                                // as code indicators, so markdown must be checked before code.
                                bool isMarkdown = FlyShelf.Classes.MarkdownDetector.IsMarkdown(classificationSample);

                                if (isMarkdown)
                                {
                                    item.ItemType = ClipboardItemType.Text;
                                    item.RawContent = capturedText;
                                    item.Extension = "MARKDOWN";
                                    string mdDisplay = capturedText.Trim();
                                    item.FileName = mdDisplay.Length > 5000 ? string.Concat(mdDisplay.AsSpan(0, 5000), "...") : mdDisplay;
                                    item.GenerateMarkdownIcon();
                                }
                                else if (IsProperCode(classificationSample))
                                {
                                    item.ItemType = ClipboardItemType.Code;
                                    item.RawContent = capturedText;
                                
                                    if (classificationSample.Contains("std::", StringComparison.Ordinal) || classificationSample.Contains("<iostream>", StringComparison.Ordinal) || classificationSample.Contains("<cstdlib>", StringComparison.Ordinal) || classificationSample.Contains("<vector>", StringComparison.Ordinal) || classificationSample.Contains("using namespace", StringComparison.Ordinal) || _rxCpp.IsMatch(classificationSample))
                                    {
                                        item.Extension = "C++";
                                    }
                                    else if (classificationSample.Contains("<stdio.h>", StringComparison.Ordinal) || classificationSample.Contains("<stdlib.h>", StringComparison.Ordinal) || classificationSample.Contains("<string.h>", StringComparison.Ordinal) || _rxC.IsMatch(classificationSample))
                                    {
                                        item.Extension = "C";
                                    }
                                    else if (classificationSample.Contains("def ", StringComparison.Ordinal) || classificationSample.Contains("import ", StringComparison.Ordinal) || classificationSample.Contains("self.", StringComparison.Ordinal) || _rxPython.IsMatch(classificationSample))
                                    {
                                        item.Extension = "PYTHON";
                                    }
                                    else if (classificationSample.Contains("public class", StringComparison.Ordinal) || classificationSample.Contains("System.out", StringComparison.Ordinal) || classificationSample.Contains("@Override", StringComparison.Ordinal) || _rxJava.IsMatch(classificationSample))
                                    {
                                        item.Extension = "JAVA";
                                    }
                                    else if (_rxJs.IsMatch(classificationSample))
                                    {
                                        item.Extension = "JS";
                                    }
                                    else if (classificationSample.Contains("public class", StringComparison.Ordinal) || classificationSample.Contains("private void", StringComparison.Ordinal) || classificationSample.Contains("Console.", StringComparison.Ordinal) || classificationSample.Contains("namespace ", StringComparison.Ordinal) || _rxCs.IsMatch(classificationSample))
                                    {
                                        item.Extension = "C#";
                                    }
                                    else if (_rxSql.IsMatch(classificationSample))
                                    {
                                        item.Extension = "SQL";
                                    }
                                    else if (classificationSample.TrimStart().StartsWith("{\"", StringComparison.Ordinal) || classificationSample.TrimStart().StartsWith("[{\"", StringComparison.Ordinal))
                                    {
                                        item.Extension = "JSON";
                                    }
                                    else if (_rxHtml.IsMatch(classificationSample))
                                    {
                                        item.Extension = "HTML";
                                    }
                                    else
                                    {
                                        item.Extension = "CODE";
                                    }
                                    string shortText = capturedText.Trim();
                                    item.FileName = shortText.Length > 5000 ? string.Concat(shortText.AsSpan(0, 5000), "...") : shortText;
                                }
                                else
                                {
                                    item.ItemType = ClipboardItemType.Text;
                                    string displayText = capturedText.Trim();
                                    // [SECURITY FIX v2.1.0]: Re-enable sensitive data auto-detection (M-08)
                                    if (DetectIfPasswordOrApiKey(displayText))
                                    {
                                        item.IsPassword = true;
                                        item.Extension = "PASSWORD";
                                        string label = "Protected Password";
                                        string lower = displayText.ToLowerInvariant();
                                        if (lower.StartsWith("sk-", StringComparison.Ordinal) || lower.StartsWith("pk-", StringComparison.Ordinal) || lower.StartsWith("ghp_", StringComparison.Ordinal) || 
                                            lower.Contains("key", StringComparison.Ordinal) || lower.Contains("api", StringComparison.Ordinal) || lower.Contains("token", StringComparison.Ordinal) || lower.Contains("secret", StringComparison.Ordinal))
                                        {
                                            label = "API Key";
                                        }
                                        item.FileName = label;
                                        item.GeneratePasswordIcon();
                                    }
                                    else
                                    {
                                        item.Extension = "TEXT";
                                        item.FileName = displayText.Length > 5000 ? string.Concat(displayText.AsSpan(0, 5000), "...") : displayText;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        item._suppressPropertyNotifications = true; // PERF: suppress during construction
                    }

                    // ═══ SMART CONTENT DETECTION ═══
                    if (item.ItemType == ClipboardItemType.Text && !item.IsPassword)
                    {
                        string sample = (item.RawContent ?? item.FileName ?? "").Trim();
                        if (string.IsNullOrEmpty(sample) && !string.IsNullOrEmpty(capturedText))
                            sample = capturedText.Trim();
                        
                        if (FlyShelf.Classes.SmartContentDetector.IsValidJson(sample))
                        {
                            item.Extension = "JSON";
                            item.SmartBadge = "JSON";
                        }
                        else if (FlyShelf.Classes.SmartContentDetector.IsEpochTimestamp(sample))
                        {
                            item.SmartBadge = "EPOCH";
                            item.IsEpochTimestamp = true;
                        }
                        else if (FlyShelf.Classes.SmartContentDetector.IsBase64(sample))
                        {
                            item.SmartBadge = "BASE64";
                            item.IsBase64Content = true;
                        }
                        else if (FlyShelf.Classes.SmartContentDetector.IsMathExpression(sample))
                        {
                            item.SmartBadge = "MATH";
                            item.IsMathExpression = true;
                        }
                        
                        // These are non-exclusive — item can have email AND be text
                        if (FlyShelf.Classes.SmartContentDetector.ContainsEmail(sample))
                            item.HasEmail = true;
                        if (FlyShelf.Classes.SmartContentDetector.ContainsPhoneNumber(sample))
                            item.HasPhoneNumber = true;
                    }

                    if (item != null)
                    {
                        if (sourceDevice != null) item.SourceDeviceName = sourceDevice;
                        if (sourceDeviceType != null) item.SourceDeviceType = sourceDeviceType;
                        if (transferMethod != null) item.TransferMethod = transferMethod;
                        if (!string.IsNullOrEmpty(sourceAppName)) item.SourceAppName = sourceAppName;
                        if (sourceAppIcon != null) item.SourceAppIcon = sourceAppIcon;
                    }

                    // PERF: Un-suppress before insert — item is about to enter the visual tree
                    item._suppressPropertyNotifications = false;

                    item.EvaluateSmartActions();

                    // Cloud Discovery sync (with echo prevention logic)
                    if (FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery && FlyShelf.Classes.SettingsManager.Current.EnableOutgoingSync && !skipCloudSync)
                    {
                        string normalizedContent = NormalizeTextForFingerprint(item.RawContent ?? "");
                        string txtFp = string.Concat("TXT::", normalizedContent.AsSpan(0, Math.Min(200, normalizedContent.Length)));
                        if (!IsCloudSourced(txtFp))
                        {
                            FlyShelf.Classes.SyncQueue.Enqueue(item);
                        }
                        else
                        {
                            FlyShelf.Classes.Logger.LogAction("TEXT SYNC", "Skipped — text arrived from cloud (echo prevention)");
                        }
                    }

                    // Insert directly on UI thread with deduplication
                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        DeduplicateAndInsert(item);

                        FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? (item.RawContent != null ? item.RawContent[..Math.Min(40, item.RawContent.Length)] : ""));

                        if (item.SmartActionType == "SetTimer" && _rxSlashTimer.IsMatch(item.RawContent.Trim()))
                        {
                            var tw = new FlyShelf.Windows.TimerWindow(item.RawContent.Trim());
                            WindowHelper.ShowInForeground(tw);
                        }

                        if (capturedForceSync)
                        {
                            Classes.ClipboardHelper.SafeSetText(item.RawContent, suppressEcho: true, echoDelayMs: 500);
                        }

                        if (!string.IsNullOrEmpty(sourceDevice))
                        {
                            string normalizedContent = NormalizeTextForFingerprint(item.RawContent ?? "");
                            string txtFp = string.Concat("TXT::", normalizedContent.AsSpan(0, Math.Min(200, normalizedContent.Length)));
                            MarkAsCloudSourced(txtFp);
                            PersistHistory();
                        }

                        OnPropertyChanged(nameof(ShelfVisibility));
                    });

                    // PERF: Journal the text item (fast JSONL append)
                    FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(item);
                });
            }
            }
            catch (Exception ex)
            {
                Logger.LogCrash("HandleDropInternal", ex);
            }
        }

        // P/Invoke declarations and SHFILEINFO struct centralized in NativeMethods.cs

        /// <summary>
        /// Reliably loads an image file as a thumbnail BitmapSource of specified decodeWidth.
        /// Supports standard raster formats (PNG, JPG, BMP, WEBP, GIF, ICO, TIFF) and vector SVG.
        /// </summary>
        public static BitmapSource? LoadImageThumbnail(string filePath, int decodeWidth = 300)
        {
            return Classes.ImageThumbnailManager.LoadThumbnail(filePath, decodeWidth);
        }

        public BitmapSource? GetIcon(string filePath)
        {
            return Classes.ShellIconManager.GetIcon(filePath);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SaveGlobalSettings()
        {
            FlyShelf.Classes.SettingsManager.Save();
            FlyShelf.Windows.ToastWindow.ShowToast("System Configuration Saved ✅");
        }

        /// <summary>
        /// Highly robust code classification algorithm to prevent plain text documents,
        /// dictionary definitions or scraped web pages from being classified as code snippets.
        /// First prioritizes strong function calling or int main signatures.
        /// </summary>
        private static bool IsProperCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            // 1. Minimum character threshold verification
            // Activate code classification ONLY when the sample meets a minimum length (e.g. 15 characters)
            if (text.Length < 15) return false;

            // Guard: skip expensive regex classification for extremely large text (> 50 KB)
            // to avoid catastrophic backtracking in _rxCode's 60+ alternation pattern
            if (text.Length > 50000) return false;

            // 2. 1st Priority: Strong Entry Points & Function Calling / Signature Detections
            // If the text contains these explicit identifiers, we prioritize marking it as code immediately.
            bool hasEntryPoint = text.Contains("int main", StringComparison.Ordinal) || 
                                 text.Contains("void main", StringComparison.Ordinal) || 
                                 text.Contains("public static void main", StringComparison.Ordinal) ||
                                 text.Contains("using namespace std", StringComparison.Ordinal) ||
                                 text.Contains("#include <", StringComparison.Ordinal) ||
                                 text.Contains("System.Console.WriteLine", StringComparison.Ordinal) ||
                                 text.Contains("Console.WriteLine", StringComparison.Ordinal) ||
                                 text.Contains("System.out.println", StringComparison.Ordinal) ||
                                 text.Contains("console.log(", StringComparison.Ordinal);

            if (hasEntryPoint) return true;

            // Regex checking standard function definition or function call signatures (e.g. "myFunc(args)", "void foo()")
            // Matches optional return keyword, function name, parentheses block, and an ending marker (brace, semicolon, arrow)
            try
            {
                if (_rxFunction.IsMatch(text)) return true;
            }
            catch (Exception ex) { Logger.LogAction("DROP", $"Non-critical: {ex.Message}"); }

            // 3. Fallback: Structural & Punctuation Constraints
            var lines = text.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;

            // 3a. Very short contents (1 or 2 lines)
            if (lines.Length <= 2)
            {
                if (!_rxCode.IsMatch(text)) return false;

                // For single-line or double-line, require high confidence code syntax indicators
                bool hasCodePunctuation = text.Contains(';') || text.Contains('{') || text.Contains('}') || text.Contains("=>", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal) || text.Contains("*/", StringComparison.Ordinal) || text.Contains("//", StringComparison.Ordinal);
                bool hasCodeStructure = text.Contains('(') && text.Contains(')');
                bool hasCommonStart = text.StartsWith("#include", StringComparison.Ordinal) || text.StartsWith("import ", StringComparison.Ordinal) || text.StartsWith("def ", StringComparison.Ordinal) || text.StartsWith("from ", StringComparison.Ordinal) || text.StartsWith("using ", StringComparison.Ordinal);

                return hasCodePunctuation || hasCodeStructure || hasCommonStart;
            }

            // 3b. Multi-line documents (3 or more lines)
            // Prevent webpage copy-pastes (like GitHub repos, lists, or articles) from being marked as code
            int codeLineCount = 0;
            int nonCodeLineCount = 0;

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Parts of code structure like solo brackets/commas
                if (line == "{" || line == "}" || line == "[" || line == "]" || line == "(" || line == ")" || line == "};" || line == "];" || line == ",")
                {
                    codeLineCount++;
                    continue;
                }

                bool lineMatchesCode = _rxCode.IsMatch(line);
                bool hasCodeSuffix = line.EndsWith(';') || line.EndsWith('{') || line.EndsWith('}') || line.EndsWith(',') || line.EndsWith(':') || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith('*') || line.StartsWith('#') || line.StartsWith("import ", StringComparison.Ordinal) || line.StartsWith("export ", StringComparison.Ordinal);

                if (lineMatchesCode || hasCodeSuffix)
                {
                    codeLineCount++;
                }
                else
                {
                    nonCodeLineCount++;
                }
            }

            int totalValuableLines = codeLineCount + nonCodeLineCount;
            if (totalValuableLines == 0) return false;

            double codeDensity = (double)codeLineCount / totalValuableLines;

            // Must contain some basic code punctuation overall
            bool hasAbsoluteIndicators = text.Contains(';') || text.Contains('{') || text.Contains('}') || text.Contains("=>", StringComparison.Ordinal) || text.Contains("</", StringComparison.Ordinal) || text.Contains("/>", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal) || text.Contains("*/", StringComparison.Ordinal) || text.Contains("//", StringComparison.Ordinal) || text.Contains("def ", StringComparison.Ordinal) || text.Contains("import ", StringComparison.Ordinal) || text.Contains("#include", StringComparison.Ordinal);

            if (!hasAbsoluteIndicators) return false;

            // Proper code must have at least 35% code line density
            return codeDensity >= 0.35;
        }

        public static bool IsWebUrl(string text, out string? cleanUrl)
        {
            cleanUrl = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains(' ')) return false;

            // 1. Check with scheme (http://, https://, ftp://)
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uriResult) &&
                (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps || uriResult.Scheme == Uri.UriSchemeFtp))
            {
                cleanUrl = _rxUtmClean.Replace(trimmed, string.Empty).TrimEnd('?', '&');
                return true;
            }

            // 2. Check schemeless web URLs (e.g., meet.google.com/..., github.com/..., www.example.com, localhost:3000, 192.168.1.1:8080)
            if (trimmed.Contains('.') || trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Must not be a local Windows file path
                if (trimmed.Contains('\\') || trimmed.Contains(":/") || trimmed.Contains(":\\")) return false;

                if (Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out Uri? httpsUri) &&
                    httpsUri.Scheme == Uri.UriSchemeHttps &&
                    !string.IsNullOrEmpty(httpsUri.Host))
                {
                    string host = httpsUri.Host.ToLowerInvariant();
                    if (host == "localhost" || host.Contains('.'))
                    {
                        int lastDot = host.LastIndexOf('.');
                        if (lastDot > 0 && lastDot < host.Length - 1)
                        {
                            string tld = host.Substring(lastDot + 1);
                            bool isNumeric = int.TryParse(tld, out _); // IPv4
                            bool isValidTld = tld.Length >= 2 && tld.Length <= 24 && tld.All(char.IsLetter);

                            if (isValidTld || isNumeric)
                            {
                                bool isFileExtension = CommonExtensions.Contains("." + tld);
                                bool hasUrlIndicators = trimmed.Contains('/') || trimmed.Contains('?') || trimmed.Contains(':') || 
                                                        host.StartsWith("www.") || host.StartsWith("meet.") || host.StartsWith("zoom.") || 
                                                        host.StartsWith("api.") || host.StartsWith("app.") || host.StartsWith("docs.");

                                if (!isFileExtension || hasUrlIndicators)
                                {
                                    cleanUrl = _rxUtmClean.Replace(trimmed, string.Empty).TrimEnd('?', '&');
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        public static bool DetectIfPasswordOrApiKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            
            // Limit length (passwords/keys are usually between 6 and 128 characters)
            if (trimmed.Length < 6 || trimmed.Length > 128) return false;

            // Cannot contain newlines
            if (trimmed.Contains('\n') || trimmed.Contains('\r')) return false;

            // Split into words
            string[] words = trimmed.Split(SpaceSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 2) return false;

            // ── NEGATIVE PATTERNS: things that are NOT passwords ──

            string lower = trimmed.ToLowerInvariant();

            // 1. File paths (e.g., "E:\Comfy-Desktop", "C:\Users\...", "/home/user/...")
            if (trimmed.Contains(":\\", StringComparison.Ordinal) || trimmed.Contains(":/", StringComparison.Ordinal) || 
                trimmed.StartsWith("\\\\", StringComparison.Ordinal) || trimmed.StartsWith('/') ||
                trimmed.Contains('\\') || System.IO.Path.IsPathRooted(trimmed))
                return false;

            // 2. URLs, URIs, and Web Links (http, https, ftp, file, ws, wss, ssh, git, www, localhost, ://)
            if (lower.StartsWith("http://", StringComparison.Ordinal) || lower.StartsWith("https://", StringComparison.Ordinal) || 
                lower.StartsWith("ftp://", StringComparison.Ordinal) || lower.StartsWith("file://", StringComparison.Ordinal) ||
                lower.StartsWith("ws://", StringComparison.Ordinal) || lower.StartsWith("wss://", StringComparison.Ordinal) ||
                lower.StartsWith("ssh://", StringComparison.Ordinal) || lower.StartsWith("git://", StringComparison.Ordinal) ||
                lower.Contains("://", StringComparison.Ordinal) || lower.StartsWith("localhost", StringComparison.Ordinal) ||
                lower.StartsWith("www.", StringComparison.Ordinal))
                return false;

            // 3. Schemeless URLs, Web addresses, meeting links, web paths, and query parameter strings
            // (e.g., "meet.google.com/...", "github.com/...", "zoom.us/...", "domain.com/path?key=1")
            if (trimmed.Contains('/') || trimmed.Contains('?') || (trimmed.Contains('&') && trimmed.Contains('=')))
                return false;

            // Domain endings & subdomains
            if (lower.EndsWith(".com") || lower.EndsWith(".org") || lower.EndsWith(".net") || lower.EndsWith(".io") || 
                lower.EndsWith(".ai") || lower.EndsWith(".co") || lower.EndsWith(".in") || lower.EndsWith(".app") || 
                lower.EndsWith(".dev") || lower.EndsWith(".xyz") || lower.EndsWith(".me") || lower.EndsWith(".to") || 
                lower.EndsWith(".gg") || lower.EndsWith(".gov") || lower.EndsWith(".edu") || lower.EndsWith(".info"))
                return false;

            if (lower.StartsWith("meet.") || lower.StartsWith("zoom.") || lower.StartsWith("teams.") || 
                lower.StartsWith("mail.") || lower.StartsWith("docs.") || lower.StartsWith("drive.") || 
                lower.StartsWith("app.") || lower.StartsWith("web.") || lower.StartsWith("portal."))
                return false;

            if (IsWebUrl(trimmed, out _))
                return false;

            // 4. Email addresses
            if (trimmed.Contains('@') && trimmed.Contains('.') && !trimmed.Contains(' '))
                return false;

            // 5. File extensions (e.g., "readme.md", "index.html", "package.json")
            foreach (var ext in CommonExtensions)
            {
                if (lower.EndsWith(ext, StringComparison.Ordinal)) return false;
            }

            // 6. Common non-password words/phrases people copy
            if (lower == "password" || lower == "username" || lower == "admin" ||
                lower.StartsWith("hello", StringComparison.Ordinal) || lower.StartsWith("test", StringComparison.Ordinal) ||
                lower.StartsWith("example", StringComparison.Ordinal) || lower.StartsWith("sample", StringComparison.Ordinal) ||
                lower.StartsWith("version", StringComparison.Ordinal) || lower.StartsWith("release", StringComparison.Ordinal))
                return false;

            // 7. Pure numbers (phone numbers, IDs, etc.) -- not passwords
            if (trimmed.All(c => char.IsDigit(c) || c == '-' || c == '+' || c == '(' || c == ')' || c == ' '))
                return false;

            // 8. Plain English words: if it contains only letters and is a single word, skip
            if (words.Length == 1 && trimmed.All(char.IsLetter))
                return false;

            // ── POSITIVE PATTERNS: things that ARE passwords/keys ──

            // Known API key prefixes (high confidence)
            if (lower.StartsWith("sk-", StringComparison.Ordinal) || lower.StartsWith("pk-", StringComparison.Ordinal) || lower.StartsWith("ghp_", StringComparison.Ordinal) || 
                lower.StartsWith("key_", StringComparison.Ordinal) || lower.StartsWith("api_", StringComparison.Ordinal) || lower.StartsWith("token_", StringComparison.Ordinal) || 
                lower.StartsWith("secret_", StringComparison.Ordinal) || lower.StartsWith("pwd_", StringComparison.Ordinal) || lower.StartsWith("passwd_", StringComparison.Ordinal) ||
                lower.StartsWith("auth_", StringComparison.Ordinal) || lower.StartsWith("bearer ", StringComparison.Ordinal) ||
                lower.StartsWith("eyj", StringComparison.Ordinal))  // JWT tokens start with base64 of {"
            {
                return true;
            }

            // Test each word for password-like entropy
            foreach (var w in words)
            {
                // Disallow characters typical of URLs or filesystem paths
                if (w.Contains('/') || w.Contains('\\') || w.Contains('?') || w.Contains('&') || w.Contains('=')) continue;

                bool hasUpper = w.Any(char.IsUpper);
                bool hasLower = w.Any(char.IsLower);
                bool hasDigit = w.Any(char.IsDigit);
                bool hasSymbol = w.Any(c => !char.IsLetterOrDigit(c));

                int charTypes = 0;
                if (hasUpper) charTypes++;
                if (hasLower) charTypes++;
                if (hasDigit) charTypes++;
                if (hasSymbol) charTypes++;

                // Require ALL 4 character types for shorter strings, or 3+ for longer ones
                if (charTypes >= 4 && w.Length >= 8) return true;
                if (charTypes >= 3 && w.Length >= 12 && hasSymbol && (hasUpper || hasLower) && hasDigit) return true;

                // High-entropy API key/token: 20+ chars, alphanumeric, with mixed case or digits
                if (w.Length >= 20 && hasUpper && hasLower && hasDigit && 
                    w.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '+'))
                {
                    int consecutiveDigits = 0;
                    int maxConsecutiveDigits = 0;
                    foreach (char c in w)
                    {
                        if (char.IsDigit(c)) { consecutiveDigits++; maxConsecutiveDigits = Math.Max(maxConsecutiveDigits, consecutiveDigits); }
                        else consecutiveDigits = 0;
                    }
                    if (maxConsecutiveDigits >= 2 || w.Count(char.IsDigit) >= 3) return true;
                }
            }

            return false;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        public RelayCommand(Action<T> execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (parameter is T typedParameter)
            {
                _execute(typedParameter);
            }
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
