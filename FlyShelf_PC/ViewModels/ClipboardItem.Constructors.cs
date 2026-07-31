using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using FlyShelf.Helpers;
using FlyShelf.Classes;

namespace FlyShelf.ViewModels
{
    public partial class ClipboardItem
    {
        private static readonly System.Text.RegularExpressions.Regex _rxCppCheck = new System.Text.RegularExpressions.Regex(
            @"#include\s*<[a-z.]+>|int\s+main\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
        
        private static readonly System.Text.RegularExpressions.Regex _rxTimeCheck = new System.Text.RegularExpressions.Regex(
            @"\b(?:[01]?\d|2[0-3]):[0-5]\d\b", System.Text.RegularExpressions.RegexOptions.Compiled);
        
        private static readonly System.Text.RegularExpressions.Regex _rxDurationCheck = new System.Text.RegularExpressions.Regex(
            @"\d+\s*(sec|min|hour|hr|minute|second)s?\b", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        private static readonly System.Text.RegularExpressions.Regex _rxSlashTimerCheck = new System.Text.RegularExpressions.Regex(
            @"^\/\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);
        
        private static readonly System.Text.RegularExpressions.Regex _rxAddressCheck = new System.Text.RegularExpressions.Regex(
            @"\d{1,5}\s+\w+\s+(st|street|ave|avenue|blvd|boulevard|rd|road|dr|drive|lane|ln)\b", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // ── Frozen brushes & pens for icon generation (Fix 6: avoid per-call GC pressure) ──
        private static readonly System.Windows.Media.SolidColorBrush _iconShadowDark = BrushHelper.Frozen(System.Windows.Media.Color.FromArgb(38, 0, 0, 0));
        private static readonly System.Windows.Media.SolidColorBrush _iconShadowLight = BrushHelper.Frozen(System.Windows.Media.Color.FromArgb(15, 0, 0, 0));
        private static readonly System.Windows.Media.SolidColorBrush _iconShadow30 = BrushHelper.Frozen(System.Windows.Media.Color.FromArgb(30, 0, 0, 0));
        private static readonly System.Windows.Media.SolidColorBrush _iconShadow10 = BrushHelper.Frozen(System.Windows.Media.Color.FromArgb(10, 0, 0, 0));
        private static readonly System.Windows.Media.SolidColorBrush _iconBorderGray = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(225, 225, 225));
        private static readonly System.Windows.Media.Pen _iconBorderGrayPen = CreateFrozenPen(_iconBorderGray, 1.0);
        private static readonly System.Windows.Media.SolidColorBrush _iconDarkBg = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(30, 30, 30));
        private static readonly System.Windows.Media.SolidColorBrush _iconDarkBg28 = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(28, 28, 28));
        private static readonly System.Windows.Media.SolidColorBrush _iconCyanBlue = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(14, 165, 233));
        private static readonly System.Windows.Media.SolidColorBrush _iconShackleGray = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(200, 200, 200));
        private static readonly System.Windows.Media.SolidColorBrush _iconHighlightYellow = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(250, 204, 21));
        private static readonly System.Windows.Media.SolidColorBrush _iconDarkAmber = BrushHelper.Frozen(System.Windows.Media.Color.FromRgb(202, 138, 4));

        private static System.Windows.Media.Pen CreateFrozenPen(System.Windows.Media.Brush brush, double thickness)
        {
            var pen = new System.Windows.Media.Pen(brush, thickness);
            pen.Freeze();
            return pen;
        }

        /// <summary>Parameterless constructor for object initializer syntax and JSON deserialization.</summary>
        public ClipboardItem() { }

        public void EvaluateSmartActions()
        {
            HasSmartAction = false;
            
            if (ItemType == ClipboardItemType.Pdf)
            {
                SmartActionName = "Open PDF";
                SmartActionIcon = "Eye24";
                SmartActionType = "OpenPDF";
                HasSmartAction = true;
            }
            else if (ItemType == ClipboardItemType.Document)
            {
                if (Extension == ".DOCX" || Extension == ".DOC" || Extension == ".TXT" || Extension == ".MD")
                {
                    SmartActionName = "Convert to PDF";
                    SmartActionIcon = "DocumentPdf24";
                    SmartActionType = "ConvertToPdf";
                    HasSmartAction = true;
                }
            }
            else if (ItemType == ClipboardItemType.Url || (!string.IsNullOrEmpty(RawContent) && RawContent.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
            {
                if (RawContent.Contains("zoom.us/j/", StringComparison.OrdinalIgnoreCase) || RawContent.Contains("meet.google.com/", StringComparison.OrdinalIgnoreCase))
                {
                    SmartActionName = "Join Meeting";
                    SmartActionIcon = "Video24";
                    SmartActionType = "JoinMeeting";
                }
                else
                {
                    SmartActionName = "Navigate QR Link";
                    SmartActionIcon = "QRCode24";
                    SmartActionType = "OpenBrowser";
                }
                HasSmartAction = true;
            }
            else if (ItemType == ClipboardItemType.QRCode)
            {
                if (!string.IsNullOrEmpty(RawContent) && RawContent.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    SmartActionName = "Open QR Link";
                    SmartActionIcon = "Globe24";
                    SmartActionType = "OpenBrowser";
                }
                else
                {
                    SmartActionName = "Copy QR Text";
                    SmartActionIcon = "Copy24";
                    SmartActionType = "CopyQRText";
                }
                HasSmartAction = true;
            }
            else if ((ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code) && !string.IsNullOrEmpty(RawContent))
            {
                string smartActionSample = RawContent.Length > 10000 
                    ? RawContent[..10000] 
                    : RawContent;

                if (_rxCppCheck.IsMatch(smartActionSample))
                {
                    SmartActionName = "Run C/C++";
                    SmartActionIcon = "Play24";
                    SmartActionType = "CompileAndRun";
                    HasSmartAction = true;
                }
                else if (_rxTimeCheck.IsMatch(smartActionSample) || 
                    _rxDurationCheck.IsMatch(smartActionSample) ||
                    _rxSlashTimerCheck.IsMatch(smartActionSample.Trim()))
                {
                    SmartActionName = "Set Timer";
                    SmartActionIcon = "Clock24";
                    SmartActionType = "SetTimer";
                    HasSmartAction = true;
                }
                else if (_rxAddressCheck.IsMatch(smartActionSample))
                {
                    SmartActionName = "Open Maps";
                    SmartActionIcon = "Map24";
                    SmartActionType = "OpenMap";
                    HasSmartAction = true;
                }
            }



            // ═══ COLOR DETECTION (always evaluate) ═══
            if (!string.IsNullOrEmpty(RawContent))
            {
                if (FlyShelf.Classes.ColorHelper.TryDetectColor(RawContent, out string hex, out byte cr, out byte cg, out byte cb))
                {
                    DetectedColor = hex;
                    _colorR = cr;
                    _colorG = cg;
                    _colorB = cb;
                }
            }
        }

        public ClipboardItem(string[] files)
        {
            _suppressPropertyNotifications = true; // PERF: No listeners yet
            ItemType = ClipboardItemType.Group;
            FileName = $"{files.Length} Files Grouped";
            Extension = "GROUP";
            RawContent = string.Join("\n", files);
            _suppressPropertyNotifications = false;
            FormattedSize = "Calculating size...";

            // Dynamically calculate total size in background thread to prevent UI freezing
            string[] capturedFiles = files;
            // NOTE: Task captures 'this' — OK because Dispose cleans up backing resources
            System.Threading.Tasks.Task.Run(() =>
            {
                long totalSize = 0;
                int fileCount = 0;
                int folderCount = 0;

                foreach (var path in capturedFiles)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            totalSize += new FileInfo(path).Length;
                            fileCount++;
                        }
                        else if (Directory.Exists(path))
                        {
                            var dirInfo = new DirectoryInfo(path);
                            var allFiles = dirInfo.GetFiles("*", SearchOption.AllDirectories);
                            totalSize += allFiles.Sum(f => f.Length);
                            folderCount++;
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                }

                string filesLabel = fileCount > 1 ? $"{fileCount} files" : (fileCount == 1 ? "1 file" : "");
                string foldersLabel = folderCount > 1 ? $"{folderCount} folders" : (folderCount == 1 ? "1 folder" : "");
                string separator = (fileCount > 0 && folderCount > 0) ? ", " : "";
                
                FormattedSize = $"{FormatBytes(totalSize)} • {filesLabel}{separator}{foldersLabel}";
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                });
            });


            // Generate premium overlapping diagonal stacked card icons
            GenerateStackedGroupIcon(capturedFiles);

            EvaluateSmartActions();
        }

        public ClipboardItem(string path)
        {
            _suppressPropertyNotifications = true; // PERF: No listeners yet
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        var uri = new Uri(path);
                        path = uri.LocalPath;
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }

            FilePath = path ?? string.Empty;
            try
            {
                FileName = Path.GetFileName(path) ?? string.Empty;
                Extension = Path.GetExtension(path)?.ToUpperInvariant() ?? "FILE";
            }
            catch
            {
                FileName = path ?? string.Empty;
                Extension = "FILE";
            }
            
            // Fast in-memory classification based on extension
            string ext = Extension.ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp")
            {
                ItemType = ClipboardItemType.Image;
            }
            else if (ext == ".pdf")
            {
                ItemType = ClipboardItemType.Pdf;
            }
            else if (ext == ".doc" || ext == ".docx" || ext == ".txt" || ext == ".md")
            {
                ItemType = ClipboardItemType.Document;
                if (ext == ".md" || ext == ".txt")
                {
                    if (ext == ".md") GenerateMarkdownIcon();
                    
                    try
                    {
                        var fi = new System.IO.FileInfo(path);
                        if (fi.Exists && fi.Length < 1024 * 1024) // 1MB limit for in-memory preview content
                        {
                            // Defer file reading off UI thread to prevent blocking during paste/drop
                            var readPath = path;
                            _ = System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    var content = System.IO.File.ReadAllText(readPath);
                                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => RawContent = content);
                                }
                                catch (Exception ex) { Logger.LogAction("ITEM_INIT", $"Non-critical error: {ex.Message}"); }
                            });
                        }
                    }
                    catch (Exception ex) { Logger.LogAction("ITEM_INIT", $"Non-critical error: {ex.Message}"); }
                }
            }
            else if (ext == ".cpp" || ext == ".c" || ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".js" || ext == ".py" || ext == ".cs")
            {
                ItemType = ClipboardItemType.Code;
            }
            else if (ext == ".ppt" || ext == ".pptx")
            {
                ItemType = ClipboardItemType.Presentation;
            }
            else if (ext == ".zip" || ext == ".rar" || ext == ".7z" || ext == ".tar" || ext == ".gz" || ext == ".apk")
            {
                ItemType = ClipboardItemType.Archive;
            }
            else if (ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov")
            {
                ItemType = ClipboardItemType.Video;
            }
            else if (ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".ogg")
            {
                ItemType = ClipboardItemType.Audio;
            }
            else if (path != null && (path.EndsWith('\\') || path.EndsWith('/') || Directory.Exists(path)))
            {
                ItemType = ClipboardItemType.Folder;
                Extension = "FOLDER";
                GenerateFolderIcon();
            }
            else
            {
                ItemType = ClipboardItemType.File;
            }

            // Unblock notifications — item is about to be inserted into the visual tree
            _suppressPropertyNotifications = false;

            FormattedSize = "Loading...";

            // Defer all blocking filesystem checks and I/O to background thread
            string capturedPath = FilePath;
            string capturedExt = Extension;
            ClipboardItemType preliminaryType = ItemType;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool exists = false;
                    bool isDir = false;
                    long length = 0;

                    try
                    {
                        if (!string.IsNullOrEmpty(capturedPath))
                        {
                            var fileInfo = new FileInfo(capturedPath);
                            exists = fileInfo.Exists;
                            if (exists) length = fileInfo.Length;
                            isDir = Directory.Exists(capturedPath);
                        }
                    }
                    catch { } // Best-effort: failure is acceptable

                    if (exists)
                    {
                        string sizeStr = FormatBytes(length);
                        Application.Current?.Dispatcher?.InvokeAsync(() => FormattedSize = sizeStr);
                        string lowExt = capturedExt.ToLowerInvariant();

                        if (lowExt == ".zip" || lowExt == ".apk")
                        {
                            try
                            {
                                using var archive = ZipFile.OpenRead(capturedPath);
                                var entries = archive.Entries
                                    .Where(e => !string.IsNullOrEmpty(e.Name))
                                    .Take(50)
                                    .ToList();
                                var listing = new System.Text.StringBuilder();
                                listing.AppendLine(CultureInfo.InvariantCulture, $"{entries.Count} file(s) in archive:");
                                long totalSize = 0;
                                foreach (var entry in entries)
                                {
                                    string entrySize = entry.Length > 0 ? $" ({FormatBytes(entry.Length)})" : "";
                                    listing.AppendLine(CultureInfo.InvariantCulture, $"  • {entry.FullName}{entrySize}");
                                    totalSize += entry.Length;
                                }
                                int totalEntryCount = archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
                                if (totalEntryCount > 50)
                                    listing.AppendLine(CultureInfo.InvariantCulture, $"  ... and {totalEntryCount - 50} more");
                                listing.AppendLine(CultureInfo.InvariantCulture, $"\nTotal uncompressed: {FormatBytes(totalSize)}");
                                Application.Current?.Dispatcher?.InvokeAsync(() => RawContent = listing.ToString());
                            }
                            catch { } // Best-effort: failure is acceptable
                        }

                        // Explicitly read plain text in background thread
                        bool isPlainText = lowExt == ".txt" || lowExt == ".json" || lowExt == ".md" || lowExt == ".csv" || lowExt == ".xml" || preliminaryType == ClipboardItemType.Code;
                        if (isPlainText && length < 1000000)
                            try
                            {
                                string content = File.ReadAllText(capturedPath);
                                Application.Current?.Dispatcher?.InvokeAsync(() => RawContent = content);
                            }
                            catch { } // Best-effort: failure is acceptable

                        // Trigger QR code and OCR parsing in the background
                        if (preliminaryType == ClipboardItemType.Image)
                        {
                            ScanForQRCodeAsync(capturedPath);
                            ScanForOcrTextAsync(capturedPath);
                        }
                    }
                    else if (isDir)
                    {
                        // [FIX C-01]: Wrap bound property mutations in Dispatcher to prevent cross-thread crash
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            ItemType = ClipboardItemType.Folder;
                            Extension = "FOLDER";
                        });
                        GenerateFolderIcon();
                        
                        try
                        {
                            // [FIX H-22]: Use EnumerateFiles with Take cap to prevent hangs on junction loops
                            var allFiles = Directory.EnumerateFiles(capturedPath, "*", SearchOption.AllDirectories).Take(5000).ToArray();
                            var allDirs = Directory.EnumerateDirectories(capturedPath, "*", SearchOption.AllDirectories).Take(1000).ToArray();
                            long folderSize = allFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                            string fmtSize = string.Create(CultureInfo.InvariantCulture, $"{FormatBytes(folderSize)} • {allFiles.Length} files");
                            Application.Current?.Dispatcher?.InvokeAsync(() => FormattedSize = fmtSize);
                            
                            // Build contents listing
                            var listing = new System.Text.StringBuilder();
                            listing.AppendLine(CultureInfo.InvariantCulture, $"{FileName}/");
                            listing.AppendLine(CultureInfo.InvariantCulture, $"   {allFiles.Length} file(s), {allDirs.Length} subfolder(s)");
                            listing.AppendLine();
                            
                            // [FIX M-15]: Reuse topItems array for count check instead of calling GetFileSystemEntries again
                            var allTopItems = Directory.GetFileSystemEntries(capturedPath);
                            var topItems = allTopItems.Take(30).ToArray();
                            foreach (var entry in topItems)
                            {
                                bool entryIsDir = Directory.Exists(entry);
                                string name = Path.GetFileName(entry);
                                if (entryIsDir)
                                {
                                    int subCount = 0;
                                    try { subCount = Directory.GetFileSystemEntries(entry).Length; } catch { } // Best-effort: failure is acceptable
                                    listing.AppendLine(CultureInfo.InvariantCulture, $"{name}/ ({subCount} items)");
                                }
                                else
                                {
                                    long fSize = 0;
                                    try { fSize = new FileInfo(entry).Length; } catch { } // Best-effort: failure is acceptable
                                    listing.AppendLine(CultureInfo.InvariantCulture, $"{name} ({FormatBytes(fSize)})");
                                }
                            }
                            if (allTopItems.Length > 30)
                                listing.AppendLine("  ... and more");
                            
                            string folderContent = listing.ToString();
                            Application.Current?.Dispatcher?.InvokeAsync(() => RawContent = folderContent);
                            
                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                            {
                                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                            });
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("FOLDER ZIP", $"Failed: {ex.Message}");
                            Application.Current?.Dispatcher?.InvokeAsync(() => FormattedSize = "Folder");
                        }
                    }
                    else
                    {
                        // Fallback for non-existent / remote / offline files
                        Application.Current?.Dispatcher?.InvokeAsync(() => FormattedSize = "Offline / Remote");
                        if (preliminaryType == ClipboardItemType.Image)
                        {
                            // Avoid layout breaking for offline image thumbnails by classifying as general file
                            Application.Current?.Dispatcher?.InvokeAsync(() => ItemType = ClipboardItemType.File);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(() => FormattedSize = "Unknown");  // OK: item might not be in visual tree yet
                    Classes.Logger.LogAction("CLIPBOARD_ITEM_INIT_ERR", ex.Message);
                }

                // [FIX C-06]: EvaluateSmartActions fires PropertyChanged — must run on UI thread
                // Move inside Dispatcher.InvokeAsync block instead of calling from background
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    EvaluateSmartActions();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemType)));
                });
            });
        }

        public void Execute()
        {
            try
            {
                if (ItemType == ClipboardItemType.Group)
                {
                    string[] paths = RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    FlyShelf.Classes.ShellExplorerHelper.OpenFilesAndSelect(paths);
                    return;
                }

                string target = string.Empty;
                if (!string.IsNullOrEmpty(FilePath))
                {
                    if (Extension == ".MD")
                    {
                        if (TryLaunchInCodeEditor(FilePath))
                            return;
                    }
                    target = FilePath;
                }
                else if (ItemType == ClipboardItemType.Url)
                    target = RawContent; // URL
                else if (ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code)
                {
                    // Create a scratch temp file to open Text in notepad
                    string tempFile = Path.Combine(Path.GetTempPath(), $"FlyShelf_TextDrop_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).AsSpan(0, 4)}.txt");
                    File.WriteAllText(tempFile, RawContent);
                    target = tempFile;
                }

                if (!string.IsNullOrEmpty(target))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("DEBUG", $"Failed to execute drop item: {ex.Message}");
            }
        }

        public void RefreshPhysicalStats()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            try
            {
                var fileInfo = new FileInfo(FilePath);
                if (fileInfo.Exists)
                {
                    FormattedSize = FormatBytes(fileInfo.Length);
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        // [FIX M-58]: Delegated to shared FormatHelper
        private static string FormatBytes(long bytes) => Classes.FormatHelper.FormatBytes(bytes);

        // NOTE (M-14): SHGetFileInfo is safe here — callers invoke this on the UI Dispatcher thread
        // via GenerateStackedGroupIcon's InvokeAsync, so no background-thread concern.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _shellIconCache = new();
        private static BitmapSource GetShellIconForStacking(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            if (!string.IsNullOrEmpty(ext) && _shellIconCache.TryGetValue(ext, out var cached))
                return cached;

            try
            {
                const uint SHGFI_ICON = 0x100;
                const uint SHGFI_LARGEICON = 0x0;
                const uint SHGFI_USEFILEATTRIBUTES = 0x10;
                const uint FILE_ATTRIBUTE_NORMAL = 0x80;

                var shinfo = new NativeMethods.SHFILEINFO();
                IntPtr res = NativeMethods.SHGetFileInfo(filePath, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze();
                        if (!string.IsNullOrEmpty(ext))
                        {
                            if (_shellIconCache.Count > 500) _shellIconCache.Clear();
                            _shellIconCache.TryAdd(ext, bitmapSource);
                        }
                        return bitmapSource;
                    }
                    finally
                    {
                        NativeMethods.DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable
            // Cache null result to avoid retrying for extensions that have no icon
            if (!string.IsNullOrEmpty(ext))
            {
                if (_shellIconCache.Count > 500) _shellIconCache.Clear();
                _shellIconCache.TryAdd(ext, null);
            }
            return null;
        }

        private void GenerateStackedGroupIcon(string[] files)
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    var icons = new System.Collections.Generic.List<BitmapSource>();
                    int count = Math.Min(3, files.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var icon = GetShellIconForStacking(files[i]);
                        if (icon != null)
                        {
                            icons.Add(icon);
                        }
                    }

                    if (icons.Count == 0) return;

                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // Draw cards from back (highest index) to front (index 0)
                        for (int i = icons.Count - 1; i >= 0; i--)
                        {
                            var icon = icons[i];
                            // Draw diagonal overlap:
                            // If 3 icons:
                            // i=2: x = 6, y = 34
                            // i=1: x = 20, y = 20
                            // i=0: x = 34, y = 6
                            double step = 14;
                            double startX = 20 - (icons.Count - 1) * 7;
                            double startY = 20 + (icons.Count - 1) * 7;
                            
                            double x = startX + (icons.Count - 1 - i) * step;
                            double y = startY - (icons.Count - 1 - i) * step;

                            // 1. Soft drop shadow
                            dc.DrawRoundedRectangle(_iconShadow30, null, new Rect(x + 1.5, y + 2.5, 56, 56), 8, 8);
                            dc.DrawRoundedRectangle(_iconShadow10, null, new Rect(x + 3, y + 4, 56, 56), 8, 8);

                            // 2. White card background with subtle light-grey border
                            dc.DrawRoundedRectangle(System.Windows.Media.Brushes.White, _iconBorderGrayPen, new Rect(x, y, 56, 56), 8, 8);

                            // 3. Center shell icon inside the card
                            dc.DrawImage(icon, new Rect(x + 12, y + 12, 32, 32));
                        }
                    }

                    var rtb = new RenderTargetBitmap(96, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    rtb.Freeze();
                    
                    // Assign to Icon property directly, bypassing the PNG encoder/decoder stream round-trip
                    Icon = rtb;
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("STACKED ICON ERR", ex.Message);
                }
            });
        }

        private static void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix)
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceDir, file);
                string entryName = Path.Combine(entryPrefix, relativePath);
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
            }
        }

        internal void GenerateMarkdownIcon()
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // 1. Draw soft drop shadow behind the card
                        dc.DrawRoundedRectangle(_iconShadowDark, null, new Rect(14, 14, 68, 68), 12, 12);
                        dc.DrawRoundedRectangle(_iconShadowLight, null, new Rect(16, 16, 68, 68), 12, 12);

                        // 2. Draw card background (Fluent Dark Grey)
                        var borderBrush = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.DarkGray60);
                        dc.DrawRoundedRectangle(_iconDarkBg, CreateFrozenPen(borderBrush, 1.5), new Rect(12, 12, 68, 68), 12, 12);

                        // 3. Draw text elements ("M" and "↓")
                        var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Consolas, Segoe UI, Arial"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
                        
                        var formattedM = new System.Windows.Media.FormattedText(
                            "M",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight,
                            typeface,
                            28,
                            System.Windows.Media.Brushes.White,
                            1.0); // 1.0 pixelsPerDip

                        var formattedArrow = new System.Windows.Media.FormattedText(
                            "↓",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight,
                            typeface,
                            28,
                            _iconCyanBlue, // Fluent Cyan/Blue
                            1.0); // 1.0 pixelsPerDip

                        dc.DrawText(formattedM, new Point(24, 28));
                        dc.DrawText(formattedArrow, new Point(54, 28));
                    }

                    var rtb = new RenderTargetBitmap(96, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    rtb.Freeze();
                    Icon = rtb;
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("MD ICON ERR", ex.Message);
                }
            });
        }

        internal void GeneratePasswordIcon()
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // 1. Draw soft drop shadow behind the card
                        dc.DrawRoundedRectangle(_iconShadowDark, null, new Rect(14, 14, 68, 68), 12, 12);
                        dc.DrawRoundedRectangle(_iconShadowLight, null, new Rect(16, 16, 68, 68), 12, 12);

                        // 2. Draw card background (Fluent Charcoal)
                        var bgBrush = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.DarkGray25);
                        var borderBrush = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.AmberYellow); // Gold yellow border
                        dc.DrawRoundedRectangle(bgBrush, CreateFrozenPen(borderBrush, 1.5), new Rect(12, 12, 68, 68), 12, 12);

                        // 3. Draw a modern lock shape!
                        // Lock base: rounded rect at the bottom
                        var lockBodyBrush = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.AmberYellow); // Yellow/Amber
                        dc.DrawRoundedRectangle(lockBodyBrush, null, new Rect(28, 44, 36, 26), 6, 6);

                        // Lock shackle
                        var shacklePen = new System.Windows.Media.Pen(_iconShackleGray, 4.5);
                        shacklePen.StartLineCap = System.Windows.Media.PenLineCap.Round;
                        shacklePen.EndLineCap = System.Windows.Media.PenLineCap.Round;
                        shacklePen.Freeze(); // [FIX M-26]: Freeze to avoid cross-thread issues
                        
                        var pathGeometry = new System.Windows.Media.PathGeometry();
                        var pathFigure = new System.Windows.Media.PathFigure();
                        pathFigure.StartPoint = new Point(36, 44);
                        pathFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(36, 33), true));
                        pathFigure.Segments.Add(new System.Windows.Media.ArcSegment(new Point(56, 33), new Size(10, 10), 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
                        pathFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(56, 44), true));
                        pathGeometry.Figures.Add(pathFigure);
                        pathGeometry.Freeze(); // [FIX M-27]: Freeze geometry for thread safety
                        dc.DrawGeometry(null, shacklePen, pathGeometry);

                        var darkBrush25 = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.DarkGray25);
                        dc.DrawEllipse(darkBrush25, null, new Point(46, 52), 3, 3);
                        dc.DrawRoundedRectangle(darkBrush25, null, new Rect(44.5, 53, 3, 7), 1, 1);
                    }

                    var rtb = new RenderTargetBitmap(96, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    rtb.Freeze();
                    Icon = rtb;
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("PASS ICON ERR", ex.Message);
                }
            });
        }

        internal void GenerateFolderIcon()
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // 1. Draw soft drop shadow behind the card
                        dc.DrawRoundedRectangle(_iconShadowDark, null, new Rect(14, 14, 68, 68), 12, 12);
                        dc.DrawRoundedRectangle(_iconShadowLight, null, new Rect(16, 16, 68, 68), 12, 12);

                        // 2. Draw card background (Fluent Dark Grey / Charcoal)
                        var borderBrush = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.AmberYellow); // Gold yellow border
                        dc.DrawRoundedRectangle(_iconDarkBg28, CreateFrozenPen(borderBrush, 1.5), new Rect(12, 12, 68, 68), 12, 12);

                        // 3. Draw a modern Fluent folder shape inside the card!
                        // Yellow folder body colors
                        var frontFolderBrush = BrushHelper.Frozen(FlyShelf.Helpers.ThemeColors.AmberYellow); // Main bright yellow/amber
                        var folderPen = CreateFrozenPen(_iconHighlightYellow, 1.0); // Highlight yellow

                        // Draw Folder Back Flap with Tab
                        var backGeometry = new System.Windows.Media.PathGeometry();
                        var backFigure = new System.Windows.Media.PathFigure();
                        backFigure.StartPoint = new Point(24, 62); // Bottom-left
                        backFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(24, 34), true)); // Up to tab start
                        backFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(38, 34), true)); // Tab top-left
                        backFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(44, 40), true)); // Tab slope down
                        backFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(68, 40), true)); // Right-top
                        backFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(68, 62), true)); // Right-bottom
                        backFigure.IsClosed = true;
                        backGeometry.Figures.Add(backFigure);
                        dc.DrawGeometry(_iconDarkAmber, null, backGeometry);

                        // Draw Folder Front Flap (slightly smaller, overlapping)
                        dc.DrawRoundedRectangle(frontFolderBrush, folderPen, new Rect(24, 40, 44, 22), 4, 4);
                    }

                    var rtb = new RenderTargetBitmap(96, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    rtb.Freeze();
                    Icon = rtb;
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("FOLDER ICON ERR", ex.Message);
                }
            });
        }

        private static bool TryLaunchInCodeEditor(string filePath)
        {
            // 1. Try VS Code (usually registered in PATH as 'code')
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
                if (p != null) return true;
            }
            catch (Exception ex) { Logger.LogAction("ITEM_INIT", $"Non-critical error: {ex.Message}"); }

            // 2. Try Notepad++ (standard 64-bit location)
            string npp64 = @"C:\Program Files\Notepad++\notepad++.exe";
            if (File.Exists(npp64))
            {
                try
                {
                    using (var p = Process.Start(npp64, $"\"{filePath}\"")) { } // Dispose native handle
                    return true;
                }
                catch { } // Best-effort: failure is acceptable
            }

            // 3. Try Notepad++ (standard 32-bit location)
            string npp32 = @"C:\Program Files (x86)\Notepad++\notepad++.exe";
            if (File.Exists(npp32))
            {
                try
                {
                    using (var p = Process.Start(npp32, $"\"{filePath}\"")) { } // Dispose native handle
                    return true;
                }
                catch { } // Best-effort: failure is acceptable
            }

            // 4. Try Sublime Text
            string sublime = @"C:\Program Files\Sublime Text\sublime_text.exe";
            if (File.Exists(sublime))
            {
                try
                {
                    using (var p = Process.Start(sublime, $"\"{filePath}\"")) { } // Dispose native handle
                    return true;
                }
                catch { } // Best-effort: failure is acceptable
            }

            // 5. Fallback to Notepad (present on all Windows systems)
            try
            {
                using (var p = Process.Start("notepad.exe", $"\"{filePath}\"")) { } // Dispose native handle
                return true;
            }
            catch { } // Best-effort: failure is acceptable

            return false;
        }
    }
}
