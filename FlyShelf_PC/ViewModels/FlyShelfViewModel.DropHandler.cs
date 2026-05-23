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
        public void HandleDrop(IDataObject data, bool forceClipboardSync = false, bool skipCloudSync = false)
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
            catch { }

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
            catch { }

            if (bitmap == null && (files == null || files.Length == 0))
            {
                try
                {
                    if (data.GetDataPresent(DataFormats.UnicodeText))
                        text = data.GetData(DataFormats.UnicodeText) as string;
                    if (string.IsNullOrEmpty(text) && data.GetDataPresent(DataFormats.Text))
                        text = data.GetData(DataFormats.Text) as string;
                }
                catch { }
            }

            // Route all heavy tasks, zipping, file-saving and collection processing to background thread
            System.Threading.Tasks.Task.Run(() => 
                HandleDropInternal(files, bitmap, text, forceClipboardSync, skipCloudSync));
        }

        private void HandleDropInternal(string[]? files, BitmapSource? bitmap, string? text, bool forceClipboardSync, bool skipCloudSync)
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
                    
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DroppedItems.Insert(0, groupItem);
                        PruneOldItems();
                        OnPropertyChanged(nameof(ShelfVisibility));
                    });

                    FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Grouped {files.Length} files into a single Group item.");

                    // Instantly push SSE event to connected mobile clients
                    FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(
                        groupItem.ItemType.ToString(), 
                        groupItem.FileName);

                    // Sync Group item to alive LAN PC peers in background task
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        // Wait up to 60s for the ZIP file to finish compressing in the background thread
                        for (int wait = 0; wait < 120; wait++)
                        {
                            if (!string.IsNullOrEmpty(groupItem.ZippedArchivePath) && File.Exists(groupItem.ZippedArchivePath))
                                break;
                            await System.Threading.Tasks.Task.Delay(500);
                        }

                        if (!string.IsNullOrEmpty(groupItem.ZippedArchivePath) && File.Exists(groupItem.ZippedArchivePath))
                        {
                            var peers = Classes.PeerManager.Instance.GetAliveLanPcPeers();
                            foreach (var peer in peers)
                            {
                                await Classes.PeerManager.Instance.TrySendGroupToPeer(peer, groupItem);
                            }
                        }
                    });

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
                    newItems.Add((new ClipboardItem(file), file));
                }

                // Phase 2: Batch-insert into ObservableCollection on UI thread
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    DroppedItems.InsertRange(0, newItems.Select(x => x.item));
                    PruneOldItems();
                    OnPropertyChanged(nameof(ShelfVisibility));
                });

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
                                    catch { }
                                }
                                else
                                {
                                    var icon = GetIcon(filePath);
                                    if (icon != null)
                                    {
                                        Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                                    }
                                }
                            }
                            finally { _iconDecodeSemaphore.Release(); }

                            // Cloud Discovery sync
                            if (capturedNewItems.Count > 10 || !FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery || skipCloudSync)
                                continue;

                            var archPath = FlyShelf.Classes.SettingsManager.Current.CustomArchiveExtractionPath;
                            if (string.IsNullOrWhiteSpace(archPath)) archPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Extracted");
                            bool isGlobalDownload = filePath.StartsWith(archPath, StringComparison.OrdinalIgnoreCase);

                            if (!isGlobalDownload)
                            {
                                if (item.ItemType == ClipboardItemType.Folder)
                                {
                                    var capturedItem = item;
                                    for (int wait = 0; wait < 120; wait++)
                                    {
                                        if (!string.IsNullOrEmpty(capturedItem.ZippedArchivePath) && File.Exists(capturedItem.ZippedArchivePath))
                                            break;
                                        await System.Threading.Tasks.Task.Delay(500);
                                    }
                                    if (!string.IsNullOrEmpty(capturedItem.ZippedArchivePath) && File.Exists(capturedItem.ZippedArchivePath))
                                        await SyncFileToDevicesAsync(capturedItem.ZippedArchivePath, capturedItem, label: "FOLDER");
                                    continue;
                                }

                                string fileExt = Path.GetExtension(filePath).ToLowerInvariant();
                                if (fileExt is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial")
                                    continue;

                                try { using var probe = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                                catch (IOException) { continue; }
                                catch { }

                                await SyncFileToDevicesAsync(filePath, item, label: "FILE");
                            }
                        }
                    });
                }

                // Clipboard writeback
                if (forceClipboardSync && files.Length <= 10)
                {
                    Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            MainWindow.SetWritingClipboard(true);
                            var dropList = new System.Collections.Specialized.StringCollection();
                            dropList.Add(files[0]);
                            System.Windows.Clipboard.SetFileDropList(dropList);
                            await System.Threading.Tasks.Task.Delay(500);
                        }
                        catch { }
                        finally { MainWindow.SetWritingClipboard(false); }
                    });
                }
            }
            else if (bitmap != null)
            {
                FlyShelf.Classes.Logger.LogAction("DRAG IN", "Processing Bitmap image payload (no-dedup)");
                
                var item = new ClipboardItem();
                item.ItemType = ClipboardItemType.Image;
                item.FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
                item.Extension = "IMAGE";
                item.FormattedSize = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}";
                
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
                    DroppedItems.Insert(0, item);
                    PruneOldItems();
                });

                // Write PNG to disk and do follow-up operations completely in background thread
                System.Threading.Tasks.Task.Run(() =>
                {
                    string tempFile = Classes.ClipboardHistoryManager.GetPersistentImagePath();
                    
                    try
                    {
                        var convertedBmp = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                        convertedBmp.Freeze();
                        
                        using (var fs = new FileStream(tempFile, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(convertedBmp));
                            encoder.Save(fs);
                        }

                        // Load thumbnail image
                        BitmapImage? bitmapImage = null;
                        try
                        {
                            int decodeWidth = IsScrolling ? 48 : 300;
                            bitmapImage = LoadImageThumbnail(tempFile, decodeWidth);
                        }
                        catch (Exception iconEx)
                        {
                            Classes.Logger.LogAction("ICON FILE", $"Failed to load saved thumbnail: {iconEx.Message}");
                        }

                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (bitmapImage != null)
                                item.Icon = bitmapImage; // Swap to perfect thumbnail

                            item.IsLoadedHighQuality = !IsScrolling;

                            item.FilePath = tempFile;
                            item.ScanForQRCodeAsync(tempFile);
                            OnPropertyChanged(nameof(ShelfVisibility));

                            // Notify mobile clients
                            FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? "");

                            if (!forceClipboardSync)
                            {
                                try
                                {
                                    MainWindow.SetWritingClipboard(true);
                                    var dataObj = new System.Windows.DataObject();
                                    dataObj.SetImage(bitmap);
                                    var dropList = new System.Collections.Specialized.StringCollection();
                                    dropList.Add(tempFile);
                                    dataObj.SetFileDropList(dropList);
                                    System.Windows.Clipboard.SetDataObject(dataObj, true);
                                }
                                catch { }
                                _ = System.Threading.Tasks.Task.Run(async () =>
                                {
                                    await System.Threading.Tasks.Task.Delay(500);
                                    MainWindow.SetWritingClipboard(false);
                                });
                            }
                            
                            if (forceClipboardSync)
                            {
                                try
                                {
                                    MainWindow.SetWritingClipboard(true);
                                    System.Windows.Clipboard.SetImage(item.Icon);
                                }
                                catch { }
                                _ = System.Threading.Tasks.Task.Run(async () =>
                                {
                                    await System.Threading.Tasks.Task.Delay(500);
                                    MainWindow.SetWritingClipboard(false);
                                });
                            }

                            // Cloud discovery sync (with echo prevention logic)
                            if (FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery && !skipCloudSync)
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
                });
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Trim().TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(text)) return;

                string visibleCheck = System.Text.RegularExpressions.Regex.Replace(text, 
                    @"[\u200B-\u200F\u2028-\u202F\u2060-\u206F\uFE00-\uFE0F\uFEFF\u00AD]", "");
                if (string.IsNullOrWhiteSpace(visibleCheck)) return;

                FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Processing Text payload length: {text.Length} (no-dedup)");

                string capturedText = text;
                bool capturedForceSync = forceClipboardSync;

                System.Threading.Tasks.Task.Run(() =>
                {
                    ClipboardItem? item = null;

                    try
                    {
                        string possiblePath = capturedText;
                        if (possiblePath.StartsWith("file:///"))
                        {
                            possiblePath = new Uri(possiblePath).LocalPath;
                        }
                        
                        if (File.Exists(possiblePath))
                        {
                            FlyShelf.Classes.Logger.LogAction("DRAG IN", $"Seamlessly resolved ambiguous text format to a localized physical file: {possiblePath}");
                            item = new ClipboardItem(possiblePath);
                        }
                    }
                    catch { }

                    if (item == null)
                    {
                        item = new ClipboardItem();
                        item.RawContent = capturedText;
                        item.FormattedSize = string.Empty;
                    }

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
                        bool isTerminal = _rxTerminal.IsMatch(capturedText);
                        bool isCode = _rxCode.IsMatch(capturedText);
                        
                        if (isTerminal || isCode)
                        {
                            item.ItemType = ClipboardItemType.Code;
                            item.RawContent = isTerminal ? capturedText : AutoFormatCode(capturedText);
                            
                            if (isTerminal)
                            {
                                item.Extension = "TERM";
                            }
                            else if (capturedText.Contains("std::") || capturedText.Contains("<iostream>") || capturedText.Contains("<cstdlib>") || capturedText.Contains("<vector>") || capturedText.Contains("using namespace") || _rxCpp.IsMatch(capturedText))
                            {
                                item.Extension = "C++";
                            }
                            else if (capturedText.Contains("<stdio.h>") || capturedText.Contains("<stdlib.h>") || capturedText.Contains("<string.h>") || _rxC.IsMatch(capturedText))
                            {
                                item.Extension = "C";
                            }
                            else if (_rxPython.IsMatch(capturedText))
                            {
                                item.Extension = "PYTHON";
                            }
                            else if (_rxJava.IsMatch(capturedText))
                            {
                                item.Extension = "JAVA";
                            }
                            else if (_rxJs.IsMatch(capturedText))
                            {
                                item.Extension = "JS";
                            }
                            else if (capturedText.Contains("public class") || capturedText.Contains("private void") || capturedText.Contains("Console.") || capturedText.Contains("namespace ") || _rxCs.IsMatch(capturedText))
                            {
                                item.Extension = "C#";
                            }
                            else if (_rxSql.IsMatch(capturedText))
                            {
                                item.Extension = "SQL";
                            }
                            else if (capturedText.TrimStart().StartsWith("{\"") || capturedText.TrimStart().StartsWith("[{\""))
                            {
                                item.Extension = "JSON";
                            }
                            else if (_rxHtml.IsMatch(capturedText))
                            {
                                item.Extension = "HTML";
                            }
                            else
                            {
                                item.Extension = "CODE";
                            }
                            string shortText = capturedText.Trim();
                            item.FileName = shortText.Length > 800 ? shortText.Substring(0, 800) + "..." : shortText;
                        }
                        else
                        {
                            item.ItemType = ClipboardItemType.Text;
                            item.Extension = "TEXT";
                            string displayText = capturedText.Trim();
                            item.FileName = displayText.Length > 800 ? displayText.Substring(0, 800) + "..." : displayText;
                        }
                    }

                    item.EvaluateSmartActions();

                    // Cloud Discovery sync (with echo prevention logic)
                    if (FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery && !skipCloudSync)
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

                    // Insert directly on UI thread (No duplicate checking/removing!)
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DroppedItems.Insert(0, item);
                        PruneOldItems();

                        FlyShelf.Classes.NetworkSyncServer.Instance?.NotifyClipboardChanged(item.ItemType.ToString(), item.FileName ?? item.RawContent?.Substring(0, Math.Min(40, item.RawContent?.Length ?? 0)) ?? "");

                        if (item.SmartActionType == "SetTimer" && _rxSlashTimer.IsMatch(item.RawContent.Trim()))
                        {
                            var tw = new FlyShelf.Windows.TimerWindow(item.RawContent.Trim());
                            tw.Show();
                        }

                        if (capturedForceSync)
                        {
                            try
                            {
                                MainWindow.SetWritingClipboard(true);
                                System.Windows.Clipboard.SetText(item.RawContent);
                            }
                            catch { }
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(500);
                                MainWindow.SetWritingClipboard(false);
                            });
                        }

                        OnPropertyChanged(nameof(ShelfVisibility));
                    });
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

            var bmp = new BitmapImage();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }

        public BitmapSource? GetIcon(string filePath)
        {
            try
            {
                const uint SHGFI_ICON = 0x100;
                const uint SHGFI_LARGEICON = 0x0;
                const uint SHGFI_USEFILEATTRIBUTES = 0x10;
                const uint FILE_ATTRIBUTE_NORMAL = 0x80;

                SHFILEINFO shinfo = new SHFILEINFO();
                // SHGFI_USEFILEATTRIBUTES: icon from extension even if file is missing
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
