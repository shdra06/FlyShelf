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
        public string TransferMethod { get; set; } = "Local"; // Local, LAN, Cloudflare

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
                    "Cloud" => "☀",
                    "Cloudflare" => "🌐",
                    _ => "📋"
                };
                string deviceEmoji = SourceDeviceType switch
                {
                    "Mobile" => "📱",
                    "PC" => "💻",
                    _ => ""
                };
                if (SourceDeviceName == "Local") return $"{emoji} Local";
                return $"{deviceEmoji} {SourceDeviceName} Â· {emoji} {TransferMethod}";
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

        private BitmapSource? _icon;
        
        [JsonIgnore]
        public BitmapSource? Icon
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

        private bool _isVisibleInViewport;
        [JsonIgnore]
        public bool IsVisibleInViewport
        {
            get => _isVisibleInViewport;
            set
            {
                if (_isVisibleInViewport != value)
                {
                    _isVisibleInViewport = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisibleInViewport)));
                    EvaluateViewportVisibility();
                }
            }
        }

        private void EvaluateViewportVisibility()
        {
            if (_isVisibleInViewport)
            {
                if (_icon == null && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var bmp = FlyShelfViewModel.LoadImageThumbnail(FilePath, 300);
                            if (bmp != null)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => Icon = bmp);
                            }
                        }
                        catch { }
                    });
                }
            }
            else
            {
                if (_icon != null && !IsPinned && (ItemType == ClipboardItemType.Image || ItemType == ClipboardItemType.QRCode || ItemType == ClipboardItemType.Pdf))
                {
                    _icon = null;
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
        
        private string? _cachedId;
        
        [JsonIgnore]
        public string ItemId
        {
            get
            {
                if (_cachedId == null)
                {
                    string contentKey = RawContent ?? FileName ?? FilePath ?? "";
                    string hashInput = contentKey.Length > 1000 ? contentKey.Substring(0, 1000) : contentKey;
                    int stableHash = hashInput.GetHashCode(StringComparison.Ordinal);
                    _cachedId = $"{ItemType}_{DateCopied.Ticks}_{stableHash:X8}";
                }
                return _cachedId;
            }
        }

        // PERF: Suppress notifications during construction — item isn't in visual tree yet
        [JsonIgnore]
        internal bool _suppressPropertyNotifications = false;

        private ClipboardItemType _itemType = ClipboardItemType.File;
        public ClipboardItemType ItemType
        {
            get => _itemType;
            set
            {
                if (_itemType != value)
                {
                    _itemType = value;
                    if (_suppressPropertyNotifications) return;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemType)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLongText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                    
                    // Notify all visual preview triggers to re-evaluate dynamically
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImagePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStaticImagePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGifPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDocPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPdfPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUrlPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCodePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGroupPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsQRCodePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVideoPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsArchivePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFolderPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTextPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPresentationPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFilePreview)));
                }
            }
        }

        private string _rawContent = string.Empty;
        public string RawContent
        {
            get => _rawContent;
            set
            {
                // Cap at 2M chars to prevent unbounded memory growth while supporting up to 50K lines of developer code
                string capped = value?.Length > 2_000_000 ? value.Substring(0, 2_000_000) : value;
                if (_rawContent != capped)
                {
                    _rawContent = capped;
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
        public string ExpandToggleText => IsExpanded ? "â–´" : "â–¾";

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
        public bool IsDocPreview => ItemType == ClipboardItemType.Document && (Extension == ".DOCX" || Extension == ".DOC" || Extension == ".TXT" || Extension == ".MD");
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
        public System.Windows.Media.SolidColorBrush DetectedColorBrush => HasDetectedColor ? FlyShelf.Classes.ColorHelper.ToBrush(_detectedColor) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);        // --- PASSWORD MANAGEMENT PROPERTIES ---
        private bool _isPassword;
        public bool IsPassword
        {
            get => _isPassword;
            set
            {
                if (_isPassword != value)
                {
                    _isPassword = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPassword)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConvertToPassword)));
                }
            }
        }

        [JsonIgnore]
        public bool CanConvertToPassword
        {
            get
            {
                if (ItemType != ClipboardItemType.Text || IsPassword) return false;
                if (string.IsNullOrEmpty(RawContent)) return false;
                
                string trimmed = RawContent.Trim();
                if (trimmed.Length == 0) return false;
                
                int words = trimmed.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                return words >= 1 && words <= 2;
            }
        }


        private bool _isLoadedHighQuality;
        [JsonIgnore]
        public bool IsLoadedHighQuality
        {
            get => _isLoadedHighQuality;
            set
            {
                if (_isLoadedHighQuality != value)
                {
                    _isLoadedHighQuality = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadedHighQuality)));
                }
            }
        }

        private bool _isLoadingHighQuality;
        [JsonIgnore]
        public bool IsLoadingHighQuality
        {
            get => _isLoadingHighQuality;
            set
            {
                if (_isLoadingHighQuality != value)
                {
                    _isLoadingHighQuality = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingHighQuality)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        
    }
}

