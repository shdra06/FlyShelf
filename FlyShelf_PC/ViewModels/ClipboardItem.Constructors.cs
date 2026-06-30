using System;
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
            else if (ItemType == ClipboardItemType.Url || (!string.IsNullOrEmpty(RawContent) && RawContent.StartsWith("http")))
            {
                string r = RawContent.ToLower();
                if (r.Contains("zoom.us/j/") || r.Contains("meet.google.com/"))
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
                if (!string.IsNullOrEmpty(RawContent) && RawContent.ToLower().StartsWith("http"))
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
                    ? RawContent.Substring(0, 10000) 
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
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
                if (ext == ".md")
                {
                    GenerateMarkdownIcon();
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
            else if (path != null && (path.EndsWith("\\") || path.EndsWith("/") || Directory.Exists(path)))
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
                        FormattedSize = FormatBytes(length);
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
                                listing.AppendLine($"📦 {entries.Count} file(s) in archive:");
                                long totalSize = 0;
                                foreach (var entry in entries)
                                {
                                    string entrySize = entry.Length > 0 ? $" ({FormatBytes(entry.Length)})" : "";
                                    listing.AppendLine($"  • {entry.FullName}{entrySize}");
                                    totalSize += entry.Length;
                                }
                                if (archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name)) > 50)
                                    listing.AppendLine($"  ... and {archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name)) - 50} more");
                                listing.AppendLine($"\nTotal uncompressed: {FormatBytes(totalSize)}");
                                RawContent = listing.ToString();
                            }
                            catch { } // Best-effort: failure is acceptable
                        }

                        // Explicitly read plain text in background thread
                        bool isPlainText = lowExt == ".txt" || lowExt == ".json" || lowExt == ".md" || lowExt == ".csv" || lowExt == ".xml" || preliminaryType == ClipboardItemType.Code;
                        if (isPlainText && length < 1000000)
                        {
                            try { RawContent = File.ReadAllText(capturedPath); } catch { } // Best-effort: failure is acceptable
                        }

                        // Trigger QR code and OCR parsing in the background
                        if (preliminaryType == ClipboardItemType.Image)
                        {
                            ScanForQRCodeAsync(capturedPath);
                            ScanForOcrTextAsync(capturedPath);
                        }
                    }
                    else if (isDir)
                    {
                        // Folder copied — process content enumeration and zipping in background
                        ItemType = ClipboardItemType.Folder;
                        Extension = "FOLDER";
                        GenerateFolderIcon();
                        
                        try
                        {
                            var allFiles = Directory.GetFiles(capturedPath, "*", SearchOption.AllDirectories);
                            var allDirs = Directory.GetDirectories(capturedPath, "*", SearchOption.AllDirectories);
                            long folderSize = allFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                            FormattedSize = $"{FormatBytes(folderSize)} • {allFiles.Length} files";
                            
                            // Build contents listing
                            var listing = new System.Text.StringBuilder();
                            listing.AppendLine($"📁 {FileName}/");
                            listing.AppendLine($"   {allFiles.Length} file(s), {allDirs.Length} subfolder(s)");
                            listing.AppendLine();
                            
                            var topItems = Directory.GetFileSystemEntries(capturedPath).Take(30).ToArray();
                            foreach (var entry in topItems)
                            {
                                bool entryIsDir = Directory.Exists(entry);
                                string name = Path.GetFileName(entry);
                                if (entryIsDir)
                                {
                                    int subCount = 0;
                                    try { subCount = Directory.GetFileSystemEntries(entry).Length; } catch { } // Best-effort: failure is acceptable
                                    listing.AppendLine($"  📂 {name}/ ({subCount} items)");
                                }
                                else
                                {
                                    long fSize = 0;
                                    try { fSize = new FileInfo(entry).Length; } catch { } // Best-effort: failure is acceptable
                                    listing.AppendLine($"  📄 {name} ({FormatBytes(fSize)})");
                                }
                            }
                            if (Directory.GetFileSystemEntries(capturedPath).Length > 30)
                                listing.AppendLine($"  ... and more");
                            
                            RawContent = listing.ToString();
                            
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("FOLDER ZIP", $"Failed: {ex.Message}");
                            FormattedSize = "Folder";
                        }
                    }
                    else
                    {
                        // Fallback for non-existent / remote / offline files
                        FormattedSize = "Offline / Remote";
                        if (preliminaryType == ClipboardItemType.Image)
                        {
                            // Avoid layout breaking for offline image thumbnails by classifying as general file
                            ItemType = ClipboardItemType.File;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FormattedSize = "Unknown";
                    Classes.Logger.LogAction("CLIPBOARD_ITEM_INIT_ERR", ex.Message);
                }

                // Final properties synchronization to update the UI
                EvaluateSmartActions();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemType)));
            });
        }

        public void Execute()
        {
            try
            {
                if (ItemType == ClipboardItemType.Group)
                {
                    string[] paths = RawContent.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
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
                    string tempFile = Path.Combine(Path.GetTempPath(), $"FlyShelf_TextDrop_{Guid.NewGuid().ToString().Substring(0, 4)}.txt");
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

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < suffixes.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            return $"{dblSByte:0.##} {suffixes[i]}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
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
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static BitmapSource GetShellIconForStacking(string filePath)
        {
            try
            {
                const uint SHGFI_ICON = 0x100;
                const uint SHGFI_LARGEICON = 0x0;
                const uint SHGFI_USEFILEATTRIBUTES = 0x10;
                const uint FILE_ATTRIBUTE_NORMAL = 0x80;

                SHFILEINFO shinfo = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(filePath, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
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
            return null;
        }

        private void GenerateStackedGroupIcon(string[] files)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
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
                            dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 0, 0, 0)), null, new Rect(x + 1.5, y + 2.5, 56, 56), 8, 8);
                            dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(10, 0, 0, 0)), null, new Rect(x + 3, y + 4, 56, 56), 8, 8);

                            // 2. White card background with subtle light-grey border
                            var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(225, 225, 225));
                            dc.DrawRoundedRectangle(System.Windows.Media.Brushes.White, new System.Windows.Media.Pen(borderBrush, 1.0), new Rect(x, y, 56, 56), 8, 8);

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
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // 1. Draw soft drop shadow behind the card
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(38, 0, 0, 0)), null, new Rect(14, 14, 68, 68), 12, 12);
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(15, 0, 0, 0)), null, new Rect(16, 16, 68, 68), 12, 12);

                        // 2. Draw card background (Fluent Dark Grey)
                        var bgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30));
                        var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60));
                        dc.DrawRoundedRectangle(bgBrush, new System.Windows.Media.Pen(borderBrush, 1.5), new Rect(12, 12, 68, 68), 12, 12);

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
                            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 165, 233)), // Fluent Cyan/Blue
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
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // 1. Draw soft drop shadow behind the card
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(38, 0, 0, 0)), null, new Rect(14, 14, 68, 68), 12, 12);
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(15, 0, 0, 0)), null, new Rect(16, 16, 68, 68), 12, 12);

                        // 2. Draw card background (Fluent Charcoal)
                        var bgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 25));
                        var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 179, 8)); // Gold yellow border
                        dc.DrawRoundedRectangle(bgBrush, new System.Windows.Media.Pen(borderBrush, 1.5), new Rect(12, 12, 68, 68), 12, 12);

                        // 3. Draw a modern lock shape!
                        // Lock base: rounded rect at the bottom
                        var lockBodyBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 179, 8)); // Yellow/Amber
                        dc.DrawRoundedRectangle(lockBodyBrush, null, new Rect(28, 44, 36, 26), 6, 6);

                        // Lock shackle
                        var shacklePen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)), 4.5);
                        shacklePen.StartLineCap = System.Windows.Media.PenLineCap.Round;
                        shacklePen.EndLineCap = System.Windows.Media.PenLineCap.Round;
                        
                        var pathGeometry = new System.Windows.Media.PathGeometry();
                        var pathFigure = new System.Windows.Media.PathFigure();
                        pathFigure.StartPoint = new Point(36, 44);
                        pathFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(36, 33), true));
                        pathFigure.Segments.Add(new System.Windows.Media.ArcSegment(new Point(56, 33), new Size(10, 10), 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
                        pathFigure.Segments.Add(new System.Windows.Media.LineSegment(new Point(56, 44), true));
                        pathGeometry.Figures.Add(pathFigure);
                        dc.DrawGeometry(null, shacklePen, pathGeometry);

                        // Lock keyhole
                        dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 25)), null, new Point(46, 52), 3, 3);
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 25)), null, new Rect(44.5, 53, 3, 7), 1, 1);
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
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var visual = new System.Windows.Media.DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // 1. Draw soft drop shadow behind the card
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(38, 0, 0, 0)), null, new Rect(14, 14, 68, 68), 12, 12);
                        dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(15, 0, 0, 0)), null, new Rect(16, 16, 68, 68), 12, 12);

                        // 2. Draw card background (Fluent Dark Grey / Charcoal)
                        var bgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 28));
                        var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 179, 8)); // Gold yellow border
                        dc.DrawRoundedRectangle(bgBrush, new System.Windows.Media.Pen(borderBrush, 1.5), new Rect(12, 12, 68, 68), 12, 12);

                        // 3. Draw a modern Fluent folder shape inside the card!
                        // Yellow folder body colors
                        var backFolderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(202, 138, 4)); // Darker yellow/amber
                        var frontFolderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 179, 8)); // Main bright yellow/amber
                        var folderPen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21)), 1.0); // Highlight yellow

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
                        dc.DrawGeometry(backFolderBrush, null, backGeometry);

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
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
                if (p != null) return true;
            }
            catch { }

            // 2. Try Notepad++ (standard 64-bit location)
            string npp64 = @"C:\Program Files\Notepad++\notepad++.exe";
            if (File.Exists(npp64))
            {
                try
                {
                    Process.Start(npp64, $"\"{filePath}\"");
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
                    Process.Start(npp32, $"\"{filePath}\"");
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
                    Process.Start(sublime, $"\"{filePath}\"");
                    return true;
                }
                catch { } // Best-effort: failure is acceptable
            }

            // 5. Fallback to Notepad (present on all Windows systems)
            try
            {
                Process.Start("notepad.exe", $"\"{filePath}\"");
                return true;
            }
            catch { } // Best-effort: failure is acceptable

            return false;
        }
    }
}
