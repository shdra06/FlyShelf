using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AdvanceClip.ViewModels
{
    public enum ClipboardItemType
    {
        File,
        Url,
        Text,
        Image,
        Code,
        Document,
        Archive,
        Video,
        Audio,
        Presentation,
        QRCode,
        Pdf,
        Folder,
        Group
    }

    public partial class ClipboardItem : INotifyPropertyChanged
    {
        public DateTime DateCopied { get; set; } = DateTime.Now;
        public string FilePath { get; set; } = string.Empty;
        
        /// <summary>
        /// For Folder items: path to the auto-generated temp zip for transfer.
        /// </summary>
        public string ZippedArchivePath { get; set; } = string.Empty;

        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
                }
            }
        }

        public string Extension { get; set; } = string.Empty;
        
        public string AssociatedContextTitle { get; set; } = string.Empty;

        public string SourceDeviceName { get; set; } = "Local";
        public string SourceDeviceType { get; set; } = "PC";
        public string TransferMethod { get; set; } = "Local"; // Local, LAN, Cloud, Cloudflare, ForceSend

        /// <summary>
        /// Computed display badge combining transfer method emoji + device name.
        /// Used in XAML for the transfer badge overlay.
        /// </summary>
        public string TransferBadge
        {
            get
            {
                string emoji = TransferMethod switch
                {
                    "LAN" => "📡",
                    "Cloud" => "☁️",
                    "Cloudflare" => "🌐",
                    "ForceSend" => "🎯",
                    _ => "📋"
                };
                string deviceEmoji = SourceDeviceType switch
                {
                    "Mobile" => "📱",
                    "PC" => "💻",
                    _ => ""
                };
                if (SourceDeviceName == "Local") return $"{emoji} Local";
                return $"{deviceEmoji} {SourceDeviceName} · {emoji} {TransferMethod}";
            }
        }
        public bool HasTransferBadge => SourceDeviceName != "Local";

        /// <summary>
        /// Creates a lightweight copy for Firebase sync, overriding RawContent with a download URL
        /// without mutating the original item displayed in the FlyShelf.
        /// </summary>
        public ClipboardItem CloneForSync(string downloadUrl)
        {
            return new ClipboardItem
            {
                DateCopied = this.DateCopied,
                FilePath = this.FilePath,
                FileName = this.FileName,
                Extension = this.Extension,
                ItemType = this.ItemType,
                FormattedSize = this.FormattedSize,
                RawContent = downloadUrl, // Override Raw with the download URL for remote sync
                SourceDeviceName = this.SourceDeviceName,
                SourceDeviceType = this.SourceDeviceType,
                TransferMethod = this.TransferMethod
            };
        }

        private BitmapImage? _icon;
        
        [JsonIgnore]
        public BitmapImage? Icon
        {
            get => _icon;
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                }
            }
        }
        private string _formattedSize = string.Empty;
        public string FormattedSize 
        { 
            get => _formattedSize; 
            set 
            { 
                if (_formattedSize != value) 
                {
                    _formattedSize = value; 
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize))); 
                }
            } 
        }
        
        private ClipboardItemType _itemType = ClipboardItemType.File;
        public ClipboardItemType ItemType
        {
            get => _itemType;
            set
            {
                if (_itemType != value)
                {
                    _itemType = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemType)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLongText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                }
            }
        }

        private string _rawContent = string.Empty;
        public string RawContent
        {
            get => _rawContent;
            set
            {
                if (_rawContent != value)
                {
                    _rawContent = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLongText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                }
            }
        }

        private bool _isExpanded;
        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                }
            }
        }

        [JsonIgnore]
        public bool IsLongText
        {
            get
            {
                if (ItemType != ClipboardItemType.Text && ItemType != ClipboardItemType.Code && ItemType != ClipboardItemType.Url)
                    return false;
                if (string.IsNullOrEmpty(RawContent))
                    return false;
                
                if (RawContent.Length > 260)
                    return true;
                
                int lineCount = 0;
                int index = 0;
                while ((index = RawContent.IndexOf('\n', index)) != -1)
                {
                    lineCount++;
                    index++;
                    if (lineCount > 4)
                        return true;
                }
                return false;
            }
        }

        [JsonIgnore]
        public double CollapsedMaxHeight => IsLongText ? (IsExpanded ? double.PositiveInfinity : 100.0) : 57.0;

        [JsonIgnore]
        public string ExpandToggleText => IsExpanded ? "▴" : "▾";

        private System.Windows.Input.ICommand? _toggleExpandCommand;
        [JsonIgnore]
        public System.Windows.Input.ICommand ToggleExpandCommand
        {
            get
            {
                if (_toggleExpandCommand == null)
                {
                    _toggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
                }
                return _toggleExpandCommand;
            }
        }
        public bool IsImagePreview => (ItemType == ClipboardItemType.Image || ItemType == ClipboardItemType.QRCode) && Extension != "DOWNLOADING";
        public bool IsGifPreview => IsImagePreview && !string.IsNullOrEmpty(FilePath) && FilePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        public bool IsStaticImagePreview => IsImagePreview && !IsGifPreview;
        public string GifFilePath => IsGifPreview ? FilePath : "";
        public bool IsDocPreview => ItemType == ClipboardItemType.Document && (Extension == ".DOCX" || Extension == ".DOC" || Extension == ".TXT");
        public bool IsPdfPreview => ItemType == ClipboardItemType.Pdf;
        public bool IsUrlPreview => ItemType == ClipboardItemType.Url;
        public bool IsCodePreview => ItemType == ClipboardItemType.Code;
        public bool IsGroupPreview => ItemType == ClipboardItemType.Group;
        public bool IsShareablePreview => true;
        
        // Context Menu Discriminators
        public bool IsTerminalPreview => Extension == ".BAT" || Extension == ".CMD" || Extension == ".PS1";
        public bool IsCPlusPlusPreview => Extension == ".CPP" || Extension == ".C";
        public string FormatIdentifier 
        { 
            get 
            {
                if (ItemType == ClipboardItemType.Image) return "Image/Bitmap";
                if (ItemType == ClipboardItemType.Text) return "Raw Text";
                if (ItemType == ClipboardItemType.Code) return "Code Snippet";
                if (ItemType == ClipboardItemType.Folder) return "Folder";
                if (ItemType == ClipboardItemType.Archive) return "Archive";
                if (ItemType == ClipboardItemType.Group) return "Grouped Items";
                return string.IsNullOrEmpty(Extension) ? "Unknown File" : Extension + " Object";
            }
        }
        
        private bool _isPinned;
        public bool IsPinned 
        {
            get => _isPinned;
            set
            {
                if (_isPinned != value)
                {
                    _isPinned = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinLabel)));
                }
            }
        }
        /// <summary>Dynamic label for the context menu pin toggle.</summary>
        public string PinLabel => _isPinned ? "Unpin Drop" : "Pin Drop";
        
        private bool _isSelected;
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        private bool _isCheckedForMerge;
        [JsonIgnore]
        public bool IsCheckedForMerge
        {
            get => _isCheckedForMerge;
            set
            {
                if (_isCheckedForMerge != value)
                {
                    _isCheckedForMerge = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCheckedForMerge)));
                }
            }
        }

        public bool IsQRCodePreview => ItemType == ClipboardItemType.QRCode;
        public bool IsVideoPreview => ItemType == ClipboardItemType.Video;
        public bool IsArchivePreview => ItemType == ClipboardItemType.Archive;
        public bool IsFolderPreview => ItemType == ClipboardItemType.Folder;
        public bool IsTextPreview => ItemType == ClipboardItemType.Text;
        public bool IsAudioPreview => ItemType == ClipboardItemType.Audio;
        public bool IsPresentationPreview => ItemType == ClipboardItemType.Presentation;
        public bool IsFilePreview => ItemType == ClipboardItemType.File;
        /// <summary>True for any item backed by a file on disk (images, docs, archives, etc.)</summary>
        public bool HasFilePath => !string.IsNullOrEmpty(FilePath);
        public bool CanShowInExplorer => HasFilePath || ItemType == ClipboardItemType.Group;

        private bool _isSuggestedContext;
        [JsonIgnore]
        public bool IsSuggestedContext
        {
            get => _isSuggestedContext;
            set
            {
                if (_isSuggestedContext != value)
                {
                    _isSuggestedContext = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSuggestedContext)));
                }
            }
        }

        // --- P2P FILE TRANSFER PROGRESS ---
        private double _transferProgress;
        [JsonIgnore]
        public double TransferProgress 
        { 
            get => _transferProgress; 
            set { if(_transferProgress!=value){ _transferProgress=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferProgress))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransferring))); } } 
        }
        
        private string _transferStatusText = "";
        [JsonIgnore]
        public string TransferStatusText 
        { 
            get => _transferStatusText; 
            set { if(_transferStatusText!=value){ _transferStatusText=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferStatusText))); } } 
        }
        
        [JsonIgnore]
        public bool IsTransferring => _transferProgress > 0 && _transferProgress < 100;
        
        // --- SMART CHIPS PROPERTIES ---
        private bool _hasSmartAction;
        public bool HasSmartAction { get => _hasSmartAction; set { if(_hasSmartAction!=value){_hasSmartAction=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSmartAction)));} } }
        
        private string _smartActionName = "";
        public string SmartActionName { get => _smartActionName; set { if(_smartActionName!=value){_smartActionName=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartActionName)));} } }
        
        private string _smartActionIcon = "Play24";
        public string SmartActionIcon { get => _smartActionIcon; set { if(_smartActionIcon!=value){_smartActionIcon=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartActionIcon)));} } }
        
        private string _smartActionType = "";
        public string SmartActionType { get => _smartActionType; set { if(_smartActionType!=value){_smartActionType=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartActionType)));} } }



        // --- COLOR DETECTION PROPERTIES ---
        private string _detectedColor = "";
        public string DetectedColor { get => _detectedColor; set { if(_detectedColor!=value){_detectedColor=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedColor))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetectedColor))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedColorBrush)));} } }
        public bool HasDetectedColor => !string.IsNullOrEmpty(_detectedColor);

        private byte _colorR, _colorG, _colorB;
        [JsonIgnore] public byte ColorR => _colorR;
        [JsonIgnore] public byte ColorG => _colorG;
        [JsonIgnore] public byte ColorB => _colorB;

        [JsonIgnore]
        public System.Windows.Media.SolidColorBrush DetectedColorBrush => HasDetectedColor ? AdvanceClip.Classes.ColorHelper.ToBrush(_detectedColor) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);

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
                if (Extension == ".DOCX" || Extension == ".DOC")
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
                if (System.Text.RegularExpressions.Regex.IsMatch(RawContent, @"(#include\s*<[a-z.]+>|int\s+main\s*\()"))
                {
                    SmartActionName = "Run C/C++";
                    SmartActionIcon = "Play24";
                    SmartActionType = "CompileAndRun";
                    HasSmartAction = true;
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(RawContent, @"\b(?:[01]?\d|2[0-3]):[0-5]\d\b") || 
                    System.Text.RegularExpressions.Regex.IsMatch(RawContent.ToLower(), @"\d+\s*(sec|min|hour|hr|minute|second)s?\b") ||
                    System.Text.RegularExpressions.Regex.IsMatch(RawContent.Trim(), @"^\/\d+$"))
                {
                    SmartActionName = "Set Timer";
                    SmartActionIcon = "Clock24";
                    SmartActionType = "SetTimer";
                    HasSmartAction = true;
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(RawContent.ToLower(), @"\d{1,5}\s+\w+\s+(st|street|ave|avenue|blvd|boulevard|rd|road|dr|drive|lane|ln)\b"))
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
                if (AdvanceClip.Classes.ColorHelper.TryDetectColor(RawContent, out string hex, out byte cr, out byte cg, out byte cb))
                {
                    DetectedColor = hex;
                    _colorR = cr;
                    _colorG = cg;
                    _colorB = cb;
                }
            }
        }

        
        // Default constructor for standard objects
        public ClipboardItem() { }

        public ClipboardItem(string[] files)
        {
            ItemType = ClipboardItemType.Group;
            FileName = $"{files.Length} Files Grouped";
            Extension = "GROUP";
            RawContent = string.Join("\n", files);
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
                    catch { }
                }

                string filesLabel = fileCount > 1 ? $"{fileCount} files" : (fileCount == 1 ? "1 file" : "");
                string foldersLabel = folderCount > 1 ? $"{folderCount} folders" : (folderCount == 1 ? "1 folder" : "");
                string separator = (fileCount > 0 && folderCount > 0) ? ", " : "";
                
                FormattedSize = $"{FormatBytes(totalSize)} • {filesLabel}{separator}{foldersLabel}";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
            });

            EvaluateSmartActions();
        }

        public ClipboardItem(string path)
        {
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
                catch { }
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
            
            try
            {
                bool exists = false;
                bool isDir = false;
                long length = 0;

                try
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        var fileInfo = new FileInfo(path);
                        exists = fileInfo.Exists;
                        if (exists) length = fileInfo.Length;
                        isDir = Directory.Exists(path);
                    }
                }
                catch { }

                if (exists)
                {
                    FormattedSize = FormatBytes(length);
                    // Classify obvious extensions
                    string ext = Extension.ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp")
                    {
                        ItemType = ClipboardItemType.Image;
                        ScanForQRCodeAsync(path);
                        ScanForOcrTextAsync(path);
                    }
                    else if (ext == ".pdf")
                    {
                        ItemType = ClipboardItemType.Pdf;
                    }
                    else if (ext == ".doc" || ext == ".docx" || ext == ".txt")
                    {
                        ItemType = ClipboardItemType.Document;
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
                        // List archive contents for .zip files
                        if (ext == ".zip" || ext == ".apk")
                        {
                            try
                            {
                                using var archive = ZipFile.OpenRead(path);
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
                            catch { }
                        }
                    }
                    else if (ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov")
                    {
                        ItemType = ClipboardItemType.Video;
                    }
                    else if (ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".ogg")
                    {
                        ItemType = ClipboardItemType.Audio;
                    }
                    else
                    {
                        // Explicit Fallback for any unknown binary payload to physically guarantee Web Client Distribution capability!
                        ItemType = ClipboardItemType.Document;
                    }
                }
                else if (isDir)
                {
                    // Folder copied — set lightweight properties immediately, defer heavy I/O
                    ItemType = ClipboardItemType.Folder;
                    Extension = "FOLDER";
                    FileName = Path.GetFileName(path) ?? "Folder";
                    FormattedSize = "Scanning...";
                    
                    // Heavy enumeration + zip runs on background thread
                    string capturedPath = path;
                    string capturedName = FileName;
                    Task.Run(() => {
                        try
                        {
                            var allFiles = Directory.GetFiles(capturedPath, "*", SearchOption.AllDirectories);
                            var allDirs = Directory.GetDirectories(capturedPath, "*", SearchOption.AllDirectories);
                            long folderSize = allFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                            FormattedSize = $"{FormatBytes(folderSize)} • {allFiles.Length} files";
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                            
                            // Build contents listing
                            var listing = new System.Text.StringBuilder();
                            listing.AppendLine($"📁 {capturedName}/");
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
                                    try { subCount = Directory.GetFileSystemEntries(entry).Length; } catch { }
                                    listing.AppendLine($"  📂 {name}/ ({subCount} items)");
                                }
                                else
                                {
                                    long fSize = 0;
                                    try { fSize = new FileInfo(entry).Length; } catch { }
                                    listing.AppendLine($"  📄 {name} ({FormatBytes(fSize)})");
                                }
                            }
                            if (Directory.GetFileSystemEntries(capturedPath).Length > 30)
                                listing.AppendLine($"  ... and more");
                            
                            RawContent = listing.ToString();
                            
                            // Zip for cross-device transfer
                            string tempZip = Path.Combine(Path.GetTempPath(), $"FlyShelf_{capturedName}_{DateTime.Now:HHmmss}.zip");
                            if (File.Exists(tempZip)) File.Delete(tempZip);
                            ZipFile.CreateFromDirectory(capturedPath, tempZip, CompressionLevel.Fastest, true);
                            ZippedArchivePath = tempZip;
                            var zipInfo = new FileInfo(tempZip);
                            FormattedSize = $"{FormatBytes(folderSize)} → {FormatBytes(zipInfo.Length)} zipped • {allFiles.Length} files";
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ZippedArchivePath)));
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("FOLDER ZIP", $"Failed: {ex.Message}");
                            FormattedSize = "Folder";
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                        }
                    });
                }
                else
                {
                    // Fallback for non-existent / remote / offline files/directories
                    FormattedSize = "Offline / Remote";
                    string ext = Extension.ToLowerInvariant();
                    if (path != null && (path.EndsWith("\\") || path.EndsWith("/")))
                    {
                        ItemType = ClipboardItemType.Folder;
                        Extension = "FOLDER";
                    }
                    else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp")
                    {
                        // To prevent layout breaking on offline images where a thumbnail is unavailable,
                        // classify them as ClipboardItemType.File.
                        ItemType = ClipboardItemType.File;
                    }
                    else if (ext == ".pdf")
                    {
                        ItemType = ClipboardItemType.Pdf;
                    }
                    else if (ext == ".doc" || ext == ".docx" || ext == ".txt")
                    {
                        ItemType = ClipboardItemType.Document;
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
                    else
                    {
                        ItemType = ClipboardItemType.File;
                    }
                }

                // Explicitly bind the Raw Content buffer natively securely mapping the File Execution Constraints!
                string xExt = Extension.ToLowerInvariant();
                bool isPlainText = xExt == ".txt" || xExt == ".json" || xExt == ".md" || xExt == ".csv" || xExt == ".xml" || ItemType == ClipboardItemType.Code;
                
                if (isPlainText && exists && length < 1000000)
                {
                    try { RawContent = File.ReadAllText(path); } catch { }
                }
            }
            catch
            {
                FormattedSize = "Unknown";
            }
            EvaluateSmartActions();
        }

        public void Execute()
        {
            try
            {
                if (ItemType == ClipboardItemType.Group)
                {
                    string[] paths = RawContent.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    AdvanceClip.Classes.ShellExplorerHelper.OpenFilesAndSelect(paths);
                    return;
                }

                string target = string.Empty;
                if (!string.IsNullOrEmpty(FilePath))
                    target = FilePath;
                else if (ItemType == ClipboardItemType.Url)
                    target = RawContent; // URL
                else if (ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code)
                {
                    // Create a scratch temp file to open Text in notepad
                    string tempFile = Path.Combine(Path.GetTempPath(), $"AdvanceClip_TextDrop_{Guid.NewGuid().ToString().Substring(0, 4)}.txt");
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
                Console.WriteLine($"Failed to execute drop item: {ex.Message}");
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
            catch { }
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

        public event PropertyChangedEventHandler? PropertyChanged;
        
    }
}
