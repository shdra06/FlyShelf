// ---------------------------------------------------------------
// FlyShelfViewModel � Drop Handler & Utilities
// HandleDrop (file/text/image processing), SHFILEINFO interop,
// SaveGlobalSettings, RelayCommand
// Split from FlyShelfViewModel.cs for modularity
// ---------------------------------------------------------------using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace FlyShelf.ViewModels
{
    public partial class FlyShelfViewModel
    {
        public void HandleDrop(IDataObject data, bool forceClipboardSync = false, bool skipCloudSync = false, string? sourceDevice = null, string? sourceDeviceType = null, string? transferMethod = null)
        {
            string[]? files = null;
            string? text = null;
            BitmapSource? bitmap = null;

            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                    files = data.GetData(DataFormats.FileDrop) as string[];
                    
                if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
                {
                    var fName = data.GetData("FileNameW") as string[];
                    if (fName != null && fName.Length > 0 && fName[0] != null) files = fName;
                }
                
                if ((files == null || files.Length == 0) && data.GetDataPresent("text/uri-list"))
                {
                    string uriText = data.GetData("text/uri-list") as string;
                    if (!string.IsNullOrEmpty(uriText))
                    {
                        var lines = uriText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        var parsedPaths = new System.Collections.Generic.List<string>();
                        foreach (var l in lines)
                        {
                            string p = l.Trim();
                            if (p.StartsWith("file:///")) p = new Uri(p).LocalPath;
                            if (File.Exists(p) || Directory.Exists(p)) parsedPaths.Add(p);
                        }
                        if (parsedPaths.Count > 0) files = parsedPaths.ToArray();
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable

            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap))
                    bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
                if (bitmap == null && data.GetDataPresent(typeof(BitmapSource)))
                    bitmap = data.GetData(typeof(BitmapSource)) as BitmapSource;
                if (bitmap == null && data.GetDataPresent(DataFormats.Dib))
                    bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;

                if (bitmap != null && bitmap.CanFreeze && !bitmap.IsFrozen)
                    bitmap.Freeze(); // Frozen to be safe for background threads
            }
            catch { } // Best-effort: failure is acceptable

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

        internal void HandleDropInternal(string[]? files, BitmapSource? bitmap, string? text, bool forceClipboardSync, bool skipCloudSync, string? sourceDevice = null, string? sourceDeviceType = null, string? transferMethod = null, string? sourceAppName = null, BitmapSource? sourceAppIcon = null)
        {
            if (files != null && files.Length > 0)
            {
                // ═══ THEME IMPORT: Intercept .flyshelf-theme files ═══
                if (files.Length == 1)
                {
                    string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                    if (ext == ".flyshelf-theme" || ext == ".flyshelftheme")
                    {
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            string importedName = Classes.ThemeManager.Instance.ImportTheme(files[0]);
                            if (importedName != null)
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast($"🎨 Theme '{importedName}' imported!");
                                Classes.ThemeManager.Instance.SetActiveTheme(importedName);
                            }
                            else
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast("❌ Invalid theme file");
                            }
                        });
                        return;
                    }
                }

                if (files.Length > 10)
                {
                    // Group files together! (No deduplication check)
                    var groupItem = new ClipboardItem(files);
                    if (sourceDevice != null) groupItem.SourceDeviceName = sourceDevice;
                    if (sourceDeviceType != null) groupItem.SourceDeviceType = sourceDeviceType;
                    if (transferMethod != null) groupItem.TransferMethod = transferMethod;
                    if (!string.IsNullOrEmpty(sourceAppName)) groupItem.SourceAppName = sourceAppName;
                    if (sourceAppIcon != null) groupItem.SourceAppIcon = sourceAppIcon;
                    
                    Application.Current.Dispatcher.InvokeAsync(() =>
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

                // Phase 2: Batch-insert into ObservableCollection on UI thread
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Run deduplication for each individual new item
                    foreach (var newItem in newItems.Select(x => x.item))
                    {
                        DeduplicateItem(newItem);
                    }
                    DroppedItems.InsertRange(0, newItems.Select(x => x.item));
                    PruneOldItems();
                    OnPropertyChanged(nameof(ShelfVisibility));

                    if (!string.IsNullOrEmpty(sourceDevice))
                    {
                        foreach (var newItem in newItems.Select(x => x.item))
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
                    FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(first.ItemType.ToString(), first.FileName ?? first.RawContent?.Substring(0, Math.Min(40, first.RawContent?.Length ?? 0)) ?? "");
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
                                        int decodeWidth = IsScrolling ? 48 : 300;
                                        var bmp = LoadImageThumbnail(filePath, decodeWidth);
                                        if (bmp != null)
                                        {
                                            Application.Current.Dispatcher.InvokeAsync(() =>
                                            {
                                                item.Icon = bmp;
                                                item.IsLoadedHighQuality = !IsScrolling;
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
                                            Application.Current.Dispatcher.InvokeAsync(() =>
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

                                await SyncFileToDevicesAsync(filePath, item, label: "FILE");
                            }
                        }
                    });
                }

                // Clipboard writeback
                if (forceClipboardSync && files.Length <= 10)
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
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
                Application.Current.Dispatcher.InvokeAsync(() =>
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
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                RemoveItem(capturedItemToCheck);
                            });
                        }
                    });

                    string tempFile = Classes.ClipboardHistoryManager.GetPersistentImagePath();

                    // ═══ IMAGE SIZE ENFORCEMENT ═══
                    // Max supported preview: 4K (3840×2160)
                    // Images wider/taller than 4K: downscale to 4K for saving, but do NOT load thumbnail — show filename only.
                    // Images with estimated raw size > 15MB (uncompressed BGRA32): reject entirely to prevent massive writes.
                    const int MAX_SUPPORTED_WIDTH  = 3840; // 4K
                    const int MAX_SUPPORTED_HEIGHT = 2160;
                    const long MAX_RAW_BYTES = 15L * 1024 * 1024; // 15 MB uncompressed cap

                    int srcW = bitmap.PixelWidth;
                    int srcH = bitmap.PixelHeight;
                    long rawBytes = (long)srcW * srcH * 4; // 4 bytes per pixel (BGRA32)

                    bool isOversized   = srcW > MAX_SUPPORTED_WIDTH || srcH > MAX_SUPPORTED_HEIGHT;
                    bool isTooBig      = rawBytes > MAX_RAW_BYTES;

                    if (isTooBig)
                    {
                        // Completely refuse to save — too large even downscaled
                        Classes.Logger.LogAction("CLIPBOARD", $"⛔ Image rejected: {srcW}×{srcH} raw={rawBytes / 1024 / 1024}MB exceeds {MAX_RAW_BYTES / 1024 / 1024}MB limit. Not saving.");
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            item.FileName = $"Image too large to save ({srcW}×{srcH})";
                            item.RawContent = $"Image ({srcW}×{srcH}) — too large to store";
                        });
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
                                encoder.Save(fs);
                            }

                        // Load thumbnail image — only for images within the 4K limit
                        BitmapImage? bitmapImage = null;
                        if (!isOversized)
                        {
                            try
                            {
                                int decodeWidth = IsScrolling ? 48 : 300;
                                bitmapImage = LoadImageThumbnail(tempFile, decodeWidth);
                            }
                            catch (Exception iconEx)
                            {
                                Classes.Logger.LogAction("ICON FILE", $"Failed to load saved thumbnail: {iconEx.Message}");
                            }
                        }


                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (bitmapImage != null)
                                item.Icon = bitmapImage; // Swap to perfect thumbnail

                            item.IsLoadedHighQuality = !IsScrolling;

                            item.FilePath = tempFile;

                            // Now that FilePath is written to disk, check for duplicate image in the first 10 items
                            ClipboardItem? duplicateImage = null;
                            int checkCount = Math.Min(10, DroppedItems.Count);
                            for (int i = 1; i < checkCount; i++) // Start at index 1 because current item is at index 0
                            {
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
                                var dataObj = new System.Windows.DataObject();
                                dataObj.SetImage(bitmap);
                                var dropList = new System.Collections.Specialized.StringCollection();
                                dropList.Add(tempFile);
                                dataObj.SetFileDropList(dropList);
                                Classes.ClipboardHelper.SafeSetDataObject(dataObj, true, suppressEcho: true, echoDelayMs: 500);
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
                        Application.Current.Dispatcher.InvokeAsync(() =>
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
                if (string.IsNullOrWhiteSpace(text)) return;

                if (text.Length < 10000)
                {
                    string visibleCheck = System.Text.RegularExpressions.Regex.Replace(text, 
                        @"[\u200B-\u200F\u2028-\u202F\u2060-\u206F\uFE00-\uFE0F\uFEFF\u00AD]", "");
                    if (string.IsNullOrWhiteSpace(visibleCheck)) return;
                }

                FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Processing Text payload length: {text.Length} (no-dedup)");

                string capturedText = text;
                bool capturedForceSync = forceClipboardSync;

                System.Threading.Tasks.Task.Run(() =>
                {
                    ClipboardItem? item = null;

                    try
                    {
                        string possiblePath = capturedText;

                        // Strip surrounding quotes (VS Code / terminals often wrap paths in quotes)
                        if (possiblePath.Length >= 2 && possiblePath[0] == '"' && possiblePath[possiblePath.Length - 1] == '"')
                            possiblePath = possiblePath.Substring(1, possiblePath.Length - 2);
                        else if (possiblePath.Length >= 2 && possiblePath[0] == '\'' && possiblePath[possiblePath.Length - 1] == '\'')
                            possiblePath = possiblePath.Substring(1, possiblePath.Length - 2);

                        // Handle file:// and file:/// URI schemes with percent-encoded chars
                        if (possiblePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        {
                            try { possiblePath = new Uri(possiblePath).LocalPath; } catch { } // Best-effort: failure is acceptable
                        }

                        // Normalize path separators (VS Code on Windows sometimes uses forward slashes)
                        possiblePath = possiblePath.Replace('/', '\\');

                        // Trim trailing whitespace/newlines that may sneak in from drag payloads
                        possiblePath = possiblePath.TrimEnd();
                        
                        if (File.Exists(possiblePath))
                        {
                            FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Seamlessly resolved ambiguous text format to a localized physical file: {possiblePath}");
                            item = new ClipboardItem(possiblePath);
                        }
                    }
                    catch { } // Best-effort: failure is acceptable

                    if (item == null)
                    {
                        item = new ClipboardItem();
                        item._suppressPropertyNotifications = true; // PERF: suppress during construction
                        item.RawContent = capturedText;
                        item.FormattedSize = string.Empty;

                        if (Uri.TryCreate(capturedText, UriKind.Absolute, out Uri? uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
                        {
                            string cleanUrl = _rxUtmClean.Replace(capturedText, string.Empty).TrimEnd('?', '&');
                            
                            item.RawContent = cleanUrl;
                            item.ItemType = ClipboardItemType.Url;
                            item.FileName = cleanUrl;
                            item.Extension = "LINK";
                        }
                        else
                        {
                            bool classified = false;

                            // ═══ FILE PATH FALLBACK ═══
                            // If the text looks like a file path with a known document extension but
                            // File.Exists() failed (e.g. stale path, network drive, VS Code workspace-relative),
                            // still classify it as a proper document card instead of plain text.
                            string trimmedText = capturedText.Trim();
                            bool looksLikeFilePath = (trimmedText.Contains("\\") || trimmedText.Contains("/"))
                                                     && !trimmedText.Contains("\n")
                                                     && trimmedText.Length < 1000;
                            if (looksLikeFilePath)
                            {
                                string lowTrimmed = trimmedText.ToLowerInvariant();
                                if (lowTrimmed.EndsWith(".md") || lowTrimmed.EndsWith(".txt") || lowTrimmed.EndsWith(".doc") || lowTrimmed.EndsWith(".docx"))
                                {
                                    string pathExt = Path.GetExtension(trimmedText).ToUpperInvariant();
                                    item.ItemType = ClipboardItemType.Document;
                                    item.Extension = pathExt;
                                    item.FileName = Path.GetFileName(trimmedText);
                                    item.FilePath = trimmedText;
                                    item.RawContent = capturedText;

                                    if (pathExt == ".MD")
                                    {
                                        // Read file contents if the file actually exists (best-effort)
                                        try { if (File.Exists(trimmedText)) item.RawContent = File.ReadAllText(trimmedText); } catch { } // Best-effort: failure is acceptable
                                        item.GenerateMarkdownIcon();
                                    }

                                    classified = true;
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
                                    item.FileName = mdDisplay.Length > 5000 ? mdDisplay.Substring(0, 5000) + "..." : mdDisplay;
                                    item.GenerateMarkdownIcon();
                                }
                                else if (IsProperCode(classificationSample))
                                {
                                    item.ItemType = ClipboardItemType.Code;
                                    item.RawContent = capturedText;
                                
                                    if (classificationSample.Contains("std::") || classificationSample.Contains("<iostream>") || classificationSample.Contains("<cstdlib>") || classificationSample.Contains("<vector>") || classificationSample.Contains("using namespace") || _rxCpp.IsMatch(classificationSample))
                                    {
                                        item.Extension = "C++";
                                    }
                                    else if (classificationSample.Contains("<stdio.h>") || classificationSample.Contains("<stdlib.h>") || classificationSample.Contains("<string.h>") || _rxC.IsMatch(classificationSample))
                                    {
                                        item.Extension = "C";
                                    }
                                    else if (classificationSample.Contains("def ") || classificationSample.Contains("import ") || classificationSample.Contains("self.") || _rxPython.IsMatch(classificationSample))
                                    {
                                        item.Extension = "PYTHON";
                                    }
                                    else if (classificationSample.Contains("public class") || classificationSample.Contains("System.out") || classificationSample.Contains("@Override") || _rxJava.IsMatch(classificationSample))
                                    {
                                        item.Extension = "JAVA";
                                    }
                                    else if (_rxJs.IsMatch(classificationSample))
                                    {
                                        item.Extension = "JS";
                                    }
                                    else if (classificationSample.Contains("public class") || classificationSample.Contains("private void") || classificationSample.Contains("Console.") || classificationSample.Contains("namespace ") || _rxCs.IsMatch(classificationSample))
                                    {
                                        item.Extension = "C#";
                                    }
                                    else if (_rxSql.IsMatch(classificationSample))
                                    {
                                        item.Extension = "SQL";
                                    }
                                    else if (classificationSample.TrimStart().StartsWith("{\"") || classificationSample.TrimStart().StartsWith("[{\""))
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
                                    item.FileName = shortText.Length > 5000 ? shortText.Substring(0, 5000) + "..." : shortText;
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
                                        if (lower.StartsWith("sk-") || lower.StartsWith("pk-") || lower.StartsWith("ghp_") || 
                                            lower.Contains("key") || lower.Contains("api") || lower.Contains("token") || lower.Contains("secret"))
                                        {
                                            label = "API Key";
                                        }
                                        item.FileName = label;
                                        item.GeneratePasswordIcon();
                                    }
                                    else
                                    {
                                        item.Extension = "TEXT";
                                        item.FileName = displayText.Length > 5000 ? displayText.Substring(0, 5000) + "..." : displayText;
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
                        string txtFp = $"TXT::{normalizedContent.Substring(0, Math.Min(200, normalizedContent.Length))}";
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
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DeduplicateAndInsert(item);

                        FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? item.RawContent?.Substring(0, Math.Min(40, item.RawContent?.Length ?? 0)) ?? "");

                        if (item.SmartActionType == "SetTimer" && _rxSlashTimer.IsMatch(item.RawContent.Trim()))
                        {
                            var tw = new FlyShelf.Windows.TimerWindow(item.RawContent.Trim());
                            tw.Show();
                        }

                        if (capturedForceSync)
                        {
                            Classes.ClipboardHelper.SafeSetText(item.RawContent, suppressEcho: true, echoDelayMs: 500);
                        }

                        if (!string.IsNullOrEmpty(sourceDevice))
                        {
                            string normalizedContent = NormalizeTextForFingerprint(item.RawContent ?? "");
                            string txtFp = $"TXT::{normalizedContent.Substring(0, Math.Min(200, normalizedContent.Length))}";
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Reliably loads an image file as a thumbnail BitmapImage of specified decodeWidth.
        /// Uses StreamSource (not UriSource) to avoid URI-related loading failures.
        /// </summary>
        public static BitmapImage? LoadImageThumbnail(string filePath, int decodeWidth = 300)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(fileBytes))
                {
                    bmp.BeginInit();
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    if (decodeWidth > 0)
                    {
                        bmp.DecodePixelWidth = decodeWidth;
                    }
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("THUMB_LOAD_FAIL", $"Failed to load image bytes for {filePath}: {ex.Message}");
                return null;
            }
        }

        public BitmapSource? GetIcon(string filePath)
        {
            const uint SHGFI_ICON = 0x100;
            const uint SHGFI_LARGEICON = 0x0;
            const uint SHGFI_USEFILEATTRIBUTES = 0x10;
            const uint FILE_ATTRIBUTE_NORMAL = 0x80;

            // For document types (PDF, DOCX, etc.), always use extension-based lookup
            // so every file of the same type shows the same consistent icon (the default
            // app's file-type icon) rather than varying per-file association.
            string ext = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            bool forceGenericIcon = ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx"
                                         or ".ppt" or ".pptx" or ".odt" or ".ods" or ".odp"
                                         or ".rtf" or ".csv" or ".epub";

            // PRIORITY 1: If the file exists on disk and is NOT a document type,
            // query the shell without SHGFI_USEFILEATTRIBUTES. This returns the icon
            // of the actual default application (e.g. VLC's cone for .mp4).
            if (!forceGenericIcon && !string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
                try
                {
                    SHFILEINFO shinfo = new SHFILEINFO();
                    IntPtr res = SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);

                    if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                                shinfo.hIcon,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());

                            bitmapSource.Freeze();
                            return bitmapSource;
                        }
                        finally
                        {
                            DestroyIcon(shinfo.hIcon);
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }

            // FALLBACK: File missing or first call failed — use extension-based lookup.
            // SHGFI_USEFILEATTRIBUTES returns a generic icon for the file type based
            // on whichever app is registered as the default handler for this extension.
            try
            {
                SHFILEINFO shinfo = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(filePath, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());

                        bitmapSource.Freeze();
                        return bitmapSource;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }

            // LAST RESORT: If SHGetFileInfo failed even with SHGFI_USEFILEATTRIBUTES
            // (e.g. malformed path, null bytes, very long path), retry with a clean
            // dummy filename using just the extension. This guarantees the system's
            // default app icon is returned for known extensions like .pdf, .docx, etc.
            if (!string.IsNullOrEmpty(ext))
            {
                try
                {
                    string dummyPath = "file" + ext; // e.g. "file.pdf"
                    SHFILEINFO shinfo = new SHFILEINFO();
                    IntPtr res = SHGetFileInfo(dummyPath, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

                    if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                                shinfo.hIcon,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());

                            bitmapSource.Freeze();
                            return bitmapSource;
                        }
                        finally
                        {
                            DestroyIcon(shinfo.hIcon);
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }

            return null;
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

            // 2. 1st Priority: Strong Entry Points & Function Calling / Signature Detections
            // If the text contains these explicit identifiers, we prioritize marking it as code immediately.
            bool hasEntryPoint = text.Contains("int main") || 
                                 text.Contains("void main") || 
                                 text.Contains("public static void main") ||
                                 text.Contains("using namespace std") ||
                                 text.Contains("#include <") ||
                                 text.Contains("System.Console.WriteLine") ||
                                 text.Contains("Console.WriteLine") ||
                                 text.Contains("System.out.println") ||
                                 text.Contains("console.log(");

            if (hasEntryPoint) return true;

            // Regex checking standard function definition or function call signatures (e.g. "myFunc(args)", "void foo()")
            // Matches optional return keyword, function name, parentheses block, and an ending marker (brace, semicolon, arrow)
            try
            {
                if (_rxFunction.IsMatch(text)) return true;
            }
            catch { }

            // 3. Fallback: Structural & Punctuation Constraints
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;

            // 3a. Very short contents (1 or 2 lines)
            if (lines.Length <= 2)
            {
                if (!_rxCode.IsMatch(text)) return false;

                // For single-line or double-line, require high confidence code syntax indicators
                bool hasCodePunctuation = text.Contains(";") || text.Contains("{") || text.Contains("}") || text.Contains("=>") || text.Contains("/*") || text.Contains("*/") || text.Contains("//");
                bool hasCodeStructure = text.Contains("(") && text.Contains(")");
                bool hasCommonStart = text.StartsWith("#include") || text.StartsWith("import ") || text.StartsWith("def ") || text.StartsWith("from ") || text.StartsWith("using ");

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
                bool hasCodeSuffix = line.EndsWith(";") || line.EndsWith("{") || line.EndsWith("}") || line.EndsWith(",") || line.EndsWith(":") || line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*") || line.StartsWith("#") || line.StartsWith("import ") || line.StartsWith("export ");

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
            bool hasAbsoluteIndicators = text.Contains(";") || text.Contains("{") || text.Contains("}") || text.Contains("=>") || text.Contains("</") || text.Contains("/>") || text.Contains("/*") || text.Contains("*/") || text.Contains("//") || text.Contains("def ") || text.Contains("import ") || text.Contains("#include");

            if (!hasAbsoluteIndicators) return false;

            // Proper code must have at least 35% code line density
            return codeDensity >= 0.35;
        }

        public static bool DetectIfPasswordOrApiKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            
            // Limit length (passwords/keys are usually between 6 and 128 characters)
            if (trimmed.Length < 6 || trimmed.Length > 128) return false;

            // Cannot contain newlines
            if (trimmed.Contains("\n") || trimmed.Contains("\r")) return false;

            // Split into words
            string[] words = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 2) return false;

            // ── NEGATIVE PATTERNS: things that are NOT passwords ──

            string lower = trimmed.ToLowerInvariant();

            // File paths (e.g., "E:\Comfy-Desktop", "C:\Users\...", "/home/user/...")
            if (trimmed.Contains(":\\") || trimmed.Contains(":/") || 
                trimmed.StartsWith("\\\\") || trimmed.StartsWith("/") ||
                trimmed.Contains("\\") || System.IO.Path.IsPathRooted(trimmed))
                return false;

            // URLs and URIs (e.g., "http://...", "https://...", "ftp://...", "localhost:3000")
            if (lower.StartsWith("http://") || lower.StartsWith("https://") || 
                lower.StartsWith("ftp://") || lower.StartsWith("file://") ||
                lower.StartsWith("ws://") || lower.StartsWith("wss://") ||
                lower.StartsWith("ssh://") || lower.StartsWith("git://") ||
                lower.Contains("://") || lower.StartsWith("localhost") ||
                lower.StartsWith("www."))
                return false;

            // Email addresses
            if (trimmed.Contains("@") && trimmed.Contains(".") && !trimmed.Contains(" "))
                return false;

            // File extensions (e.g., "readme.md", "index.html", "package.json")
            string[] commonExtensions = { ".txt", ".md", ".cs", ".ts", ".js", ".py", ".json", ".xml",
                ".html", ".css", ".jpg", ".png", ".gif", ".pdf", ".exe", ".dll", ".zip",
                ".config", ".yaml", ".yml", ".toml", ".log", ".bat", ".sh", ".ps1",
                ".xaml", ".csproj", ".sln", ".gradle", ".kt", ".swift", ".dart" };
            foreach (var ext in commonExtensions)
            {
                if (lower.EndsWith(ext)) return false;
            }

            // Common non-password words/phrases people copy
            if (lower == "password" || lower == "username" || lower == "admin" ||
                lower.StartsWith("hello") || lower.StartsWith("test") ||
                lower.StartsWith("example") || lower.StartsWith("sample") ||
                lower.StartsWith("version") || lower.StartsWith("release"))
                return false;

            // Pure numbers (phone numbers, IDs, etc.) -- not passwords
            if (trimmed.All(c => char.IsDigit(c) || c == '-' || c == '+' || c == '(' || c == ')' || c == ' '))
                return false;

            // Plain English words: if it contains only letters and is a single word, skip
            // (real passwords mix character types with randomness, not readable words)
            if (words.Length == 1 && trimmed.All(char.IsLetter))
                return false;

            // ── POSITIVE PATTERNS: things that ARE passwords/keys ──

            // Known API key prefixes (high confidence)
            if (lower.StartsWith("sk-") || lower.StartsWith("pk-") || lower.StartsWith("ghp_") || 
                lower.StartsWith("key_") || lower.StartsWith("api_") || lower.StartsWith("token_") || 
                lower.StartsWith("secret_") || lower.StartsWith("pwd_") || lower.StartsWith("passwd_") ||
                lower.StartsWith("auth_") || lower.StartsWith("bearer ") ||
                lower.StartsWith("eyj"))  // JWT tokens start with base64 of {"
            {
                return true;
            }

            // Test each word for password-like entropy
            foreach (var w in words)
            {
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
                // This prevents "MyFolder-123" (3 types) from triggering
                if (charTypes >= 4 && w.Length >= 8) return true;
                if (charTypes >= 3 && w.Length >= 12) return true;

                // High-entropy API key/token: 20+ chars, alphanumeric, with mixed case or digits
                // But exclude anything with path separators
                if (w.Length >= 20 && hasUpper && hasLower && hasDigit && 
                    !w.Contains("\\") && !w.Contains("/") &&
                    w.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '+'))
                {
                    // Extra check: ensure it's not just camelCase words (e.g., "MyApplicationSettings")
                    // Real tokens have runs of random chars, not readable words
                    int consecutiveDigits = 0;
                    int maxConsecutiveDigits = 0;
                    foreach (char c in w)
                    {
                        if (char.IsDigit(c)) { consecutiveDigits++; maxConsecutiveDigits = Math.Max(maxConsecutiveDigits, consecutiveDigits); }
                        else consecutiveDigits = 0;
                    }
                    // Real API keys typically have digit runs or are mostly random
                    if (maxConsecutiveDigits >= 2 || w.Count(char.IsDigit) >= 3) return true;
                }
            }

            return false;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public class RelayCommand<T> : ICommand
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
