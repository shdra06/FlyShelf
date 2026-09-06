using System;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Threading;
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

    public partial class ClipboardItem : INotifyPropertyChanged, IDisposable
    {
        // ═══ Named Constants ═══
        private const int DisplayTextTruncationLimit = 150;
        private const int RawContentPreviewLimit = 300;
        private const int LargeTextSpillThreshold = 100_000;
        private const int SpillPreviewLength = 200;
        private const int LongTextThreshold = 260;
        private const int MaxCollapsedLines = 4;
        private const double CollapsedMaxHeightLong = 100.0;
        private const double CollapsedMaxHeightShort = 57.0;
        private const int ExpandedDisplayTextLimit = 2000;
        private const double ExpandedMaxHeightLimit = 800.0;

        public DateTime DateCopied { get; set; } = DateTime.Now;
        public string FilePath { get; set; } = string.Empty;
        
        /// <summary>
        /// For Group/Folder items: path to the on-demand temp zip for transfer.
        /// Set when user clicks "Convert to .zip" hover button.
        /// </summary>
        public string ZippedArchivePath
        {
            get => _zippedArchivePath;
            set
            {
                if (_zippedArchivePath != value)
                {
                    _zippedArchivePath = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ZippedArchivePath)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasZipArchive)));
                }
            }
        }
        private string _zippedArchivePath = string.Empty;

        /// <summary>
        /// True when a zip archive has been created for this Group/Folder item.
        /// Used by XAML DataTriggers for hover button visibility.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasZipArchive => !string.IsNullOrEmpty(ZippedArchivePath) && System.IO.File.Exists(ZippedArchivePath);

        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            set
            {
                if (_suppressPropertyNotifications) { _fileName = value; return; }
                if (_fileName != value)
                {
                    _fileName = value;
                    _lowerFileName = null; // invalidate cache
                    _displayText = null;   // invalidate display cache
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
                }
            }
        }

        /// <summary>
        /// Truncated display text for the card preview. Caps at 300 chars when collapsed
        /// to prevent WPF TextBlock.MeasureOverride from processing thousands of characters
        /// for wrap computation — the #1 source of scroll jitter on text-heavy items.
        /// Full text only shown when user expands the card.
        /// </summary>
        private string? _displayText;
        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                if (IsExpanded)
                {
                    var source = !string.IsNullOrEmpty(_fileName) ? _fileName : (RawContent ?? string.Empty);
                    if (TotalWordCount <= PhasedWordChunkSize || _visibleWordCount >= TotalWordCount)
                    {
                        return source;
                    }
                    int charIdx = GetCharIndexForWordCount(source, _visibleWordCount);
                    return charIdx < source.Length ? string.Concat(source.AsSpan(0, charIdx), "…") : source;
                }
                if (_displayText != null) return _displayText;
                if (_fileName.Length <= DisplayTextTruncationLimit)
                {
                    _displayText = _fileName.TrimStart('\r', '\n');
                }
                else
                {
                    _displayText = _fileName.AsSpan(0, DisplayTextTruncationLimit).ToString().TrimStart('\r', '\n') + "…";
                }
                return _displayText;
            }
        }

        /// <summary>Cached lowercase FileName — avoids per-search ToLowerInvariant allocations.</summary>
        private string? _lowerFileName;
        [System.Text.Json.Serialization.JsonIgnore]
        public string LowerFileName => _lowerFileName ??= (FileName ?? string.Empty).ToLowerInvariant();

        private string _extension = string.Empty;
        public string Extension
        {
            get => _extension;
            set
            {
                if (_extension != value)
                {
                    _extension = value;
                    _cachedSemanticIconGlyph = null;
                    _cachedCardSubtitle = null;
                    _cachedFormatIdentifier = null;
                    if (_suppressPropertyNotifications) return;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Extension)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSemanticIcon)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SemanticIconGlyph)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreviewImage)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardSubtitle)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormatIdentifier)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMarkdownPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkdownPreviewContent)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanQuickLook)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDocPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPdfPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImagePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsJsonPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCodePreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminalPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCPlusPlusPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCsvPreview)));
                }
            }
        }
        
        public string AssociatedContextTitle { get; set; } = string.Empty;

        private string _sourceDeviceName = "Local";
        public string SourceDeviceName
        {
            get => _sourceDeviceName;
            set
            {
                if (_sourceDeviceName != value)
                {
                    _sourceDeviceName = value;
                    _cachedTransferBadge = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceDeviceName)));
                }
            }
        }
        private string _sourceDeviceType = "PC";
        public string SourceDeviceType
        {
            get => _sourceDeviceType;
            set
            {
                if (_sourceDeviceType != value)
                {
                    _sourceDeviceType = value;
                    _cachedTransferBadge = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceDeviceType)));
                }
            }
        }
        private string _transferMethod = "Local";
        public string TransferMethod
        {
            get => _transferMethod;
            set
            {
                if (_transferMethod != value)
                {
                    _transferMethod = value;
                    _cachedTransferBadge = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferMethod)));
                }
            }
        }

        // ═══ Source App Tracking ═══
        public string SourceAppName { get; set; } = "";
        public bool HasSourceApp => !string.IsNullOrEmpty(SourceAppName);


        /// <summary>
        /// Computed display badge combining transfer method emoji + device name.
        /// Used in XAML for the transfer badge overlay.
        /// </summary>
        private string? _cachedTransferBadge;
        public string TransferBadge => _cachedTransferBadge ??= ComputeTransferBadge();

        private string ComputeTransferBadge()
        {
            string emoji = TransferMethod switch
            {
                "LAN" => "",
                "Cloud" => "",
                "Cloudflare" => "",
                _ => ""
            };
            string deviceEmoji = SourceDeviceType switch
            {
                "Mobile" => "",
                "PC" => "",
                _ => ""
            };
            if (SourceDeviceName == "Local") return $"{emoji} Local";
            return $"{deviceEmoji} {SourceDeviceName} · {emoji} {TransferMethod}";
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
                TransferMethod = this.TransferMethod,
                SourceAppName = this.SourceAppName,
                // [FIX M-29]: Include properties needed by remote device
                IsPinned = this.IsPinned,
                IsPassword = this.IsPassword,
                _detectedColor = this._detectedColor,
            };
        }

        private BitmapSource? _icon;
        private int _thumbnailLoadState = 0; // 0 = idle, 1 = loading, 2 = loaded
        
        [JsonIgnore]
        public BitmapSource? Icon
        {
            get
            {
                if (_icon == null && (ItemType == ClipboardItemType.Image || ItemType == ClipboardItemType.QRCode) && !string.IsNullOrEmpty(FilePath))
                {
                    EnsureThumbnailLoadedAsync();
                }
                return _icon;
            }
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    if (_icon == null)
                    {
                        Interlocked.Exchange(ref _thumbnailLoadState, 0);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _thumbnailLoadState, 2);
                    }
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                }
            }
        }

        /// <summary>
        /// Self-healing thumbnail loader: asynchronously guarantees that the thumbnail is loaded from cache/disk
        /// and dispatched to the UI whenever requested.
        /// </summary>
        public void EnsureThumbnailLoadedAsync()
        {
            if (IsLoadedHighQuality && _icon != null) return;
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;
            if (Interlocked.CompareExchange(ref _thumbnailLoadState, 1, 0) != 0) return;

            IsLoadingHighQuality = true;
            string fp = FilePath;

            Task.Run(() =>
            {
                try
                {
                    var bmp = Classes.ImageThumbnailManager.LoadThumbnail(fp, 300);
                    if (bmp != null)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            _icon = bmp;
                            IsLoadedHighQuality = true;
                            IsLoadingHighQuality = false;
                            Interlocked.Exchange(ref _thumbnailLoadState, 2);
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    else
                    {
                        IsLoadingHighQuality = false;
                        Interlocked.Exchange(ref _thumbnailLoadState, 0);
                    }
                }
                catch
                {
                    IsLoadingHighQuality = false;
                    Interlocked.Exchange(ref _thumbnailLoadState, 0);
                }
            });
        }

        /// <summary>
        /// Forces the thumbnail to be reloaded from disk by invalidating
        /// the in-memory cache and resetting the load state.
        /// Call this after modifying the underlying image file (e.g., annotation save).
        /// </summary>
        public void ForceRefreshThumbnail()
        {
            // 1. Evict from the static thumbnail cache
            Classes.ImageThumbnailManager.InvalidateCache(FilePath);

            // 2. Reset the load state so EnsureThumbnailLoadedAsync will re-run
            _icon = null;
            IsLoadedHighQuality = false;
            Interlocked.Exchange(ref _thumbnailLoadState, 0);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));

            // 3. Trigger fresh async reload
            EnsureThumbnailLoadedAsync();
        }

        private BitmapSource? _sourceAppIcon;
        
        /// <summary>
        /// Icon of the source application (Chrome, Notepad, VS Code, etc.) that the content was copied from.
        /// Displayed on Text/URL/Code cards that don't have their own file-type icon.
        /// </summary>
        [JsonIgnore]
        public BitmapSource? SourceAppIcon
        {
            get => _sourceAppIcon;
            set
            {
                if (_sourceAppIcon != value)
                {
                    _sourceAppIcon = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceAppIcon)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSourceAppIcon)));
                }
            }
        }

        [JsonIgnore]
        public bool HasSourceAppIcon => _sourceAppIcon != null;

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
                    // [SECURITY FIX v2.1.0]: Use deterministic FNV-1a hash instead of
                    // String.GetHashCode() which is randomized per-process in .NET 6+ (M-14)
                    int stableHash = FlyShelf.Classes.ClipboardHistoryManager.Fnv1aHash(hashInput);
                    _cachedId = $"{ItemType}_{DateCopied.Ticks}_{stableHash:X8}";
                }
                return _cachedId;
            }
        }

        // PERF: Suppress notifications during construction — item isn't in visual tree yet
        // TODO [L-16]: Consider renaming to SuppressPropertyNotifications (property pattern for internal access)
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
                    _cachedIsLongText = null;
                    _cachedSemanticIconGlyph = null;
                    _cachedCardSubtitle = null;
                    _cachedFormatIdentifier = null;
                    if (_suppressPropertyNotifications) return;
                    // [FIX M-30]: Use SafeNotify so one bad subscriber doesn't skip the rest
                    SafeNotify(nameof(ItemType));
                    SafeNotify(nameof(IsLongText));
                    SafeNotify(nameof(CollapsedMaxHeight));
                    SafeNotify(nameof(ExpandToggleText));
                    
                    // Notify all visual preview triggers to re-evaluate dynamically
                    SafeNotify(nameof(IsImagePreview));
                    SafeNotify(nameof(IsStaticImagePreview));
                    SafeNotify(nameof(IsGifPreview));
                    SafeNotify(nameof(IsDocPreview));
                    SafeNotify(nameof(IsPdfPreview));
                    SafeNotify(nameof(IsUrlPreview));
                    SafeNotify(nameof(IsCodePreview));
                    SafeNotify(nameof(IsGroupPreview));
                    SafeNotify(nameof(IsQRCodePreview));
                    SafeNotify(nameof(IsVideoPreview));
                    SafeNotify(nameof(IsArchivePreview));
                    SafeNotify(nameof(IsFolderPreview));
                    SafeNotify(nameof(IsTextPreview));
                    SafeNotify(nameof(IsAudioPreview));
                    SafeNotify(nameof(IsPresentationPreview));
                    SafeNotify(nameof(IsFilePreview));
                    SafeNotify(nameof(IsMarkdownPreview));
                    SafeNotify(nameof(MarkdownPreviewContent));
                    SafeNotify(nameof(CanQuickLook));
                    SafeNotify(nameof(IsJsonPreview));
                    SafeNotify(nameof(IsTerminalPreview));
                    SafeNotify(nameof(IsCPlusPlusPreview));
                    SafeNotify(nameof(IsCsvPreview));

                    // Trigger-consolidation computed properties
                    SafeNotify(nameof(ShowSemanticIcon));
                    SafeNotify(nameof(SemanticIconGlyph));
                    SafeNotify(nameof(HasPreviewImage));
                    SafeNotify(nameof(CardSubtitle));
                }
            }
        }

        private string _rawContent = string.Empty;
        private string? _rawContentBackingFile = null; // For very large texts spilled to disk
        // [FIX M-03]: Use int + Interlocked for atomic check-and-set instead of volatile bool
        private int _isLoadingSpilledContentFlag; // 0 = idle, 1 = loading
        private bool _backingFileVerified; // PERF [FIX 2]: Cache File.Exists result for backing file

        /// <summary>
        /// Full raw text content. No character limit — unlimited text is supported.
        /// For extremely large texts (>10M chars), content is spilled to a temporary
        /// backing file to prevent out-of-memory issues, but remains fully accessible.
        /// </summary>
        public string RawContent
        {
            get
            {
                // If content was spilled to disk, reload it asynchronously to prevent UI thread freeze.
                // PERF: Previously did synchronous File.ReadAllText here, which blocked the UI thread
                // when WPF evaluated bindings during scroll/virtualization.
                if (_rawContent == null && !string.IsNullOrEmpty(_rawContentBackingFile)
                    && (_backingFileVerified || System.IO.File.Exists(_rawContentBackingFile)))
                {
                    // [FIX M-03]: Atomic check-and-set — prevents double file reads
                    if (Interlocked.CompareExchange(ref _isLoadingSpilledContentFlag, 1, 0) == 0)
                    {
                        string backingFile = _rawContentBackingFile;
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                string loaded = System.IO.File.ReadAllText(backingFile);
                                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                {
                                    _rawContent = loaded;
                                    Interlocked.Exchange(ref _isLoadingSpilledContentFlag, 0);
                                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLongText)));
                                });
                            }
                            catch
                            {
                                Interlocked.Exchange(ref _isLoadingSpilledContentFlag, 0);
                            }
                        });
                    }
                    return string.Empty; // Return empty immediately while loading
                }
                return _rawContent ?? string.Empty;
            }
            set
            {
                // No character limit — store the full text
                string newValue = value ?? string.Empty;

                // For very large texts (>10M), spill to backing file to prevent OOM
                // but keep a truncated preview in memory for UI responsiveness
                if (newValue.Length > LargeTextSpillThreshold)
                {
                    try
                    {
                        // [FIX H-11]: Delete previous spill file before creating a new one
                        if (!string.IsNullOrEmpty(_rawContentBackingFile)) try { File.Delete(_rawContentBackingFile); } catch { }

                        string spillDir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "FlyShelf", "LargeTextSpill");
                        System.IO.Directory.CreateDirectory(spillDir);
                        _rawContentBackingFile = System.IO.Path.Combine(spillDir,
                            $"spill_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}_{Guid.NewGuid().ToString().Substring(0, 6)}.txt");
                        _backingFileVerified = true; // PERF [FIX 2]: Mark backing file as valid
                        // [FIX C-03]: Keep truncated preview in memory until write completes.
                        // Previously set _rawContent = null immediately, causing getter to read partially-written file.
                        string backingPath = _rawContentBackingFile;
                        _rawContent = newValue.Length > SpillPreviewLength ? newValue[..SpillPreviewLength] + "…" : newValue;
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                System.IO.File.WriteAllText(backingPath, newValue);
                                // Only null out _rawContent after successful write
                                _rawContent = null;
                            }
                            catch (Exception ex)
                            {
                                // [FIX C-3]: Don't null out _rawContent if write failed — keep in-memory copy
                                Classes.Logger.LogCrash("ClipboardItem_SpillWrite", ex);
                            }
                        });
                        _lowerContent = null; // invalidate cache
                        _cachedIsLongText = null;
                        _cachedCardSubtitle = null;

                        // [FIX C-1]: Raise PropertyChanged and return early so the fall-through
                        // below doesn't re-assign the huge string back into _rawContent.
                        _rawContentPreview = null; // invalidate preview cache
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContentPreview)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLongText)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                    }
                    catch { /* If spill fails, keep in memory anyway */ }

                    return;
                }

                if (!ReferenceEquals(_rawContent, newValue) && _rawContent != newValue)
                {
                    _rawContent = newValue;
                    _lowerContent = null; // invalidate cache
                    _cachedIsLongText = null;
                    _cachedCardSubtitle = null;
                    _rawContentPreview = null; // invalidate preview cache
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContentPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLongText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                }
            }
        }

        /// <summary>
        /// Truncated preview of RawContent for UI binding in scrollable lists.
        /// Caps at 300 chars when collapsed to prevent WPF TextBlock.MeasureOverride
        /// from processing thousands of characters for wrap computation during scroll.
        /// Full text is returned when IsExpanded=true.
        /// </summary>
        public const int PhasedWordChunkSize = 500;
        private int _visibleWordCount = PhasedWordChunkSize;

        [JsonIgnore]
        public int VisibleWordCount
        {
            get => _visibleWordCount;
            set
            {
                if (_visibleWordCount != value)
                {
                    _visibleWordCount = value;
                    _rawContentPreview = null;
                    _displayText = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleWordCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContentPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPhasedExpansion)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMorePhases)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowingWordsText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadMoreButtonText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingWordsCount)));
                }
            }
        }

        private int? _cachedTotalWordCount;
        [JsonIgnore]
        public int TotalWordCount => _cachedTotalWordCount ??= ComputeWordCount(!string.IsNullOrEmpty(RawContent) ? RawContent : FileName);

        private int? _cachedTotalLineCount;
        [JsonIgnore]
        public int TotalLineCount => _cachedTotalLineCount ??= ComputeLineCount(!string.IsNullOrEmpty(RawContent) ? RawContent : FileName);

        public static int ComputeWordCount(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }
            return count;
        }

        public static int ComputeLineCount(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') count++;
            }
            return count;
        }

        public static int GetCharIndexForWordCount(string text, int targetWords)
        {
            if (string.IsNullOrEmpty(text) || targetWords <= 0) return 0;
            int count = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                    if (count > targetWords)
                    {
                        return i;
                    }
                }
            }
            return text.Length;
        }

        public void AdvancePhase(int wordCount = PhasedWordChunkSize)
        {
            VisibleWordCount = Math.Min(TotalWordCount, _visibleWordCount + wordCount);
        }

        public void ShowAllPhase()
        {
            VisibleWordCount = Math.Max(TotalWordCount, int.MaxValue / 2);
        }

        public void CollapsePhase()
        {
            IsExpanded = false;
        }

        [JsonIgnore]
        public bool HasPhasedExpansion => IsExpanded && IsLongText && TotalWordCount > PhasedWordChunkSize;

        [JsonIgnore]
        public bool HasMorePhases => IsExpanded && _visibleWordCount < TotalWordCount;

        [JsonIgnore]
        public int RemainingWordsCount => Math.Max(0, TotalWordCount - _visibleWordCount);

        [JsonIgnore]
        public string ShowingWordsText => $"Showing {Math.Min(_visibleWordCount, TotalWordCount):N0} of {TotalWordCount:N0} words ({TotalLineCount:N0} lines)";

        [JsonIgnore]
        public string ReadMoreButtonText => $"Read next 500 words (+{Math.Min(PhasedWordChunkSize, RemainingWordsCount):N0})";

        /// <summary>
        /// Truncated preview of RawContent for UI binding in scrollable lists.
        /// Caps at 300 chars when collapsed. When expanded, yields phased ~500 word progressive chunks.
        /// </summary>
        private string? _rawContentPreview;
        [JsonIgnore]
        public string RawContentPreview
        {
            get
            {
                if (_rawContentPreview != null) return _rawContentPreview;
                var raw = RawContent ?? string.Empty;
                if (IsExpanded)
                {
                    if (TotalWordCount <= PhasedWordChunkSize || _visibleWordCount >= TotalWordCount)
                    {
                        _rawContentPreview = raw;
                    }
                    else
                    {
                        int charIdx = GetCharIndexForWordCount(raw, _visibleWordCount);
                        _rawContentPreview = charIdx < raw.Length ? string.Concat(raw.AsSpan(0, charIdx), "…") : raw;
                    }
                    return _rawContentPreview;
                }

                if (raw.Length <= RawContentPreviewLimit)
                {
                    _rawContentPreview = raw;
                }
                else
                {
                    _rawContentPreview = string.Concat(raw.AsSpan(0, RawContentPreviewLimit), "…");
                }
                return _rawContentPreview;
            }
        }

        /// <summary>Cached lowercase RawContent — avoids per-search ToLowerInvariant allocations.</summary>
        private string? _lowerContent;

        public void ClearLowerContentCache()
        {
            _lowerContent = null;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string LowerContent
        {
            get
            {
                var raw = RawContent ?? string.Empty;
                // [FIX H-11]: Don't cache when spill is still loading — would permanently cache empty string
                if (_isLoadingSpilledContentFlag != 0) return raw.ToLowerInvariant();
                return _lowerContent ??= raw.ToLowerInvariant();
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
                    if (!_isExpanded)
                    {
                        _visibleWordCount = PhasedWordChunkSize; // Reset to 500 words on collapse
                    }
                    _rawContentPreview = null; // invalidate preview — expanded state changed
                    _displayText = null;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleWordCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CollapsedMaxHeight)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandToggleText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContentPreview)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPhasedExpansion)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMorePhases)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowingWordsText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadMoreButtonText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingWordsCount)));
                }
            }
        }

        private bool? _cachedIsLongText;
        [JsonIgnore]
        public bool IsLongText => _cachedIsLongText ??= ComputeIsLongText();

        private bool ComputeIsLongText()
        {
            if (!string.IsNullOrEmpty(_rawContentBackingFile)) return true; // Spilled text is always long
            if (ItemType != ClipboardItemType.Text && ItemType != ClipboardItemType.Code && ItemType != ClipboardItemType.Url)
                return false;
            if (string.IsNullOrEmpty(RawContent))
                return false;
            
            if (RawContent.Length > LongTextThreshold)
                return true;
            
            int lineCount = 0;
            int index = 0;
            while ((index = RawContent.IndexOf('\n', index)) != -1)
            {
                lineCount++;
                index++;
                if (lineCount > MaxCollapsedLines)
                    return true;
            }
            return false;
        }

        [JsonIgnore]
        public double CollapsedMaxHeight => IsLongText ? (IsExpanded ? double.PositiveInfinity : CollapsedMaxHeightLong) : CollapsedMaxHeightShort;

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
        public bool IsDocPreview
        {
            get
            {
                if (ItemType != ClipboardItemType.Document) return false;
                string norm = (Extension ?? "").TrimStart('.').ToUpperInvariant();
                return norm is "DOCX" or "DOC" or "TXT" or "RTF" or "ODT" or "LOG" or "CSV";
            }
        }
        public bool IsPdfPreview => ItemType == ClipboardItemType.Pdf || (Extension != null && Extension.TrimStart('.').Equals("PDF", StringComparison.OrdinalIgnoreCase));
        public bool IsUrlPreview => ItemType == ClipboardItemType.Url;
        public bool IsCodePreview => ItemType == ClipboardItemType.Code;
        public bool IsMarkdownPreview => 
            Extension == "MARKDOWN" ||
            (Extension != null && Extension.TrimStart('.').Equals("MD", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(FilePath) && FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

        public bool CanConvertDocToPdf
        {
            get
            {
                if (IsPdfPreview || IsImagePreview) return false;
                if (ItemType == ClipboardItemType.Document || ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code || IsMarkdownPreview) return true;
                string norm = (Extension ?? "").TrimStart('.').ToUpperInvariant();
                if (norm is "DOCX" or "DOC" or "TXT" or "MD" or "MARKDOWN" or "RTF" or "ODT" or "LOG" or "CSV" or "HTML" or "HTM" or "JSON" or "XML" or "YAML" or "YML" or "CS" or "JS" or "PY" or "CPP" or "JAVA") return true;
                if (!string.IsNullOrEmpty(FilePath))
                {
                    string ext = System.IO.Path.GetExtension(FilePath).ToUpperInvariant().TrimStart('.');
                    if (ext is "DOCX" or "DOC" or "TXT" or "MD" or "RTF" or "ODT" or "LOG" or "CSV" or "HTML" or "HTM" or "JSON" or "XML" or "YAML" or "YML" or "CS" or "JS" or "PY" or "CPP" or "JAVA") return true;
                }
                return false;
            }
        }

        public bool IsApk => 
            Extension == "APK" || 
            Extension == ".APK" || 
            Extension == "AAB" || 
            Extension == ".AAB" ||
            (!string.IsNullOrEmpty(FilePath) && (FilePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || FilePath.EndsWith(".aab", StringComparison.OrdinalIgnoreCase)));

        public bool CanQuickLook => IsImagePreview || IsQRCodePreview || IsMarkdownPreview || IsDocPreview || IsPdfPreview || IsCodePreview;

        /// <summary>
        /// Returns the markdown source text for rich rendering in the clipboard card.
        /// For markdown text items, this is the RawContent. For .md file items, RawContent
        /// contains the file contents (read on capture). Limited to 3000 chars for perf.
        /// </summary>
        public string MarkdownPreviewContent
        {
            get
            {
                if (!IsMarkdownPreview) return string.Empty;
                string content = !string.IsNullOrEmpty(RawContent) ? RawContent : FileName;
                // Limit preview to 3000 chars for rendering performance in the card
                return content.Length > 3000 ? string.Concat(content.AsSpan(0, 3000), "\n...") : content;
            }
        }
        public bool IsJsonPreview => 
            (ItemType == ClipboardItemType.Code && Extension == "JSON") ||
            (ItemType == ClipboardItemType.Text && Extension == "JSON");
        public bool HasEmail { get; set; }
        public bool HasPhoneNumber { get; set; }
        public bool IsMathExpression { get; set; }
        public bool IsBase64Content { get; set; }
        public bool IsEpochTimestamp { get; set; }
        public string SmartBadge { get; set; } = "";
        public bool HasSmartBadge => !string.IsNullOrEmpty(SmartBadge);
        public bool IsGroupPreview => ItemType == ClipboardItemType.Group;
        public bool IsShareablePreview => true;
        
        // Context Menu Discriminators — normalize Extension (may or may not have leading dot)
        public bool IsTerminalPreview
        {
            get
            {
                string norm = (Extension ?? "").TrimStart('.').ToUpperInvariant();
                return norm is "BAT" or "CMD" or "PS1";
            }
        }
        public bool IsCPlusPlusPreview
        {
            get
            {
                string norm = (Extension ?? "").TrimStart('.').ToUpperInvariant();
                return norm is "CPP" or "C" or "H" or "HPP" or "CXX" or "CC";
            }
        }
        public bool IsCsvPreview
        {
            get
            {
                string norm = (Extension ?? "").TrimStart('.').ToUpperInvariant();
                return norm is "CSV";
            }
        }
        private string? _cachedFormatIdentifier;
        public string FormatIdentifier => _cachedFormatIdentifier ??= ComputeFormatIdentifier();

        private string ComputeFormatIdentifier() 
        {
            if (ItemType == ClipboardItemType.Image) return "Image/Bitmap";
            if (ItemType == ClipboardItemType.Text && Extension == "MARKDOWN") return "Markdown";
            if (ItemType == ClipboardItemType.Text) return "Raw Text";
            if (ItemType == ClipboardItemType.Code) return "Code Snippet";
            if (ItemType == ClipboardItemType.Folder) return "Folder";
            if (ItemType == ClipboardItemType.Archive) return "Archive";
            if (ItemType == ClipboardItemType.Group) return "Grouped Items";
            return string.IsNullOrEmpty(Extension) ? "Unknown File" : Extension + " Object";
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

        private bool _isFirstTenItem = false;
        [JsonIgnore]
        public bool IsFirstTenItem
        {
            get => _isFirstTenItem;
            set
            {
                if (_isFirstTenItem != value)
                {
                    _isFirstTenItem = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFirstTenItem)));
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
        /// <summary>True when the item can be renamed in FlyShelf (any file-backed item, not passwords).
        /// Real files (.json, .py, .cs, .txt etc.) get classified as Code/Text during import but are still renamable.</summary>
        public bool CanRename => HasFilePath && !IsPassword;

        private bool _isRenaming;
        /// <summary>True when the user is editing this item's name directly on the card in-place (File Explorer style).</summary>
        [JsonIgnore]
        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (_isRenaming != value)
                {
                    _isRenaming = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
                }
            }
        }

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
        public string DetectedColor { get => _detectedColor; set { if(_detectedColor!=value){_detectedColor=value; _cachedDetectedColorBrush = null; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedColor))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetectedColor))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedColorBrush)));} } }
        public bool HasDetectedColor => !string.IsNullOrEmpty(_detectedColor);

        private byte _colorR, _colorG, _colorB;
        [JsonIgnore] public byte ColorR => _colorR;
        [JsonIgnore] public byte ColorG => _colorG;
        [JsonIgnore] public byte ColorB => _colorB;

        // PERF: Cache the brush to avoid allocating a new SolidColorBrush on every binding evaluation during scroll
        // Uses the pre-frozen system brush — no need for a static constructor
        private static readonly System.Windows.Media.SolidColorBrush _transparentBrush = System.Windows.Media.Brushes.Transparent;
        private System.Windows.Media.SolidColorBrush _cachedDetectedColorBrush;
        [JsonIgnore]
        public System.Windows.Media.SolidColorBrush DetectedColorBrush
        {
            get
            {
                if (!HasDetectedColor) return _transparentBrush;
                if (_cachedDetectedColorBrush == null)
                {
                    var brush = FlyShelf.Classes.ColorHelper.ToBrush(_detectedColor);
                    // [FIX C-02]: Freeze brush to prevent cross-thread crash during scroll
                    if (brush.CanFreeze) brush.Freeze();
                    _cachedDetectedColorBrush = brush;
                }
                return _cachedDetectedColorBrush;
            }
        }

        // --- PASSWORD MANAGEMENT PROPERTIES ---
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
                if (!string.IsNullOrEmpty(_rawContentBackingFile)) return false; // too long to be a password
                if (ItemType != ClipboardItemType.Text || IsPassword) return false;
                if (string.IsNullOrEmpty(RawContent)) return false;
                
                string trimmed = RawContent.Trim();
                if (trimmed.Length == 0) return false;
                
                // Exclude URLs, web addresses, and file paths
                if (trimmed.Contains("://") || trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('?') ||
                    trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("meet.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("zoom.", StringComparison.OrdinalIgnoreCase))
                    return false;

                // [FIX M-53]: Count words without allocating a string array
                int wordCount = 1;
                bool inSep = false;
                foreach (char c in trimmed)
                {
                    bool isSep = c == ' ' || c == '\r' || c == '\n' || c == '\t';
                    if (isSep && !inSep) wordCount++;
                    inSep = isSep;
                }
                return wordCount >= 1 && wordCount <= 2;
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

        [JsonIgnore]
        public DateTime? LeftViewportTime { get; set; }

        // ═══ COMPUTED PROPERTIES FOR TRIGGER CONSOLIDATION ═══
        // These replace 100+ XAML DataTriggers with single bindings.
        // Phase 1: Add properties. Phase 2 (later): Update XAML bindings.

        /// <summary>Whether the semantic type icon should be visible (replaces 11 MultiDataTriggers).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool ShowSemanticIcon => ItemType != ClipboardItemType.Text || !string.IsNullOrEmpty(FileName);

        private string? _cachedSemanticIconGlyph;
        /// <summary>Icon glyph name based on item type + extension (replaces 12 DataTriggers).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string SemanticIconGlyph => _cachedSemanticIconGlyph ??= ComputeSemanticIconGlyph();

        private string ComputeSemanticIconGlyph()
        {
            if (IsApk) return "Phone24";
            if (IsMarkdownPreview) return "Document24";
            return ItemType switch
            {
                ClipboardItemType.Image => "Image24",
                ClipboardItemType.Pdf => "DocumentPdf24",
                ClipboardItemType.Archive => "FolderZip24",
                ClipboardItemType.Video => "Video24",
                ClipboardItemType.Audio => "MusicNote224",
                ClipboardItemType.Code => "Code24",
                ClipboardItemType.Folder => "Folder24",
                ClipboardItemType.Presentation => "SlideLayout24",
                ClipboardItemType.Document => (Extension?.ToLowerInvariant().TrimStart('.')) switch
                {
                    "xls" or "xlsx" or "csv" or "ods" => "Table24",
                    "ppt" or "pptx" or "key" => "SlideLayout24",
                    _ => "Document24"
                },
                ClipboardItemType.File => (Extension?.ToLowerInvariant().TrimStart('.')) switch
                {
                    "apk" or "aab" or "xapk" or "apks" => "Phone24",
                    "pdf" => "DocumentPdf24",
                    "doc" or "docx" or "txt" or "rtf" => "Document24",
                    "xls" or "xlsx" or "csv" => "Table24",
                    "ppt" or "pptx" => "SlideLayout24",
                    "zip" or "rar" or "7z" or "tar" or "gz" or "iso" => "FolderZip24",
                    "mp3" or "wav" or "flac" or "aac" => "MusicNote224",
                    "mp4" or "avi" or "mkv" or "mov" => "Video24",
                    "exe" or "msi" or "deb" or "rpm" => "AppGeneric24",
                    "cpp" or "c" or "py" or "js" or "ts" or "cs" or "java" => "Code24",
                    _ => "Document24"
                },
                ClipboardItemType.Url => "Link24",
                _ => "ClipboardText24"
            };
        }

        /// <summary>Whether this item has a preview image to show.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasPreviewImage => ItemType == ClipboardItemType.Image || ItemType == ClipboardItemType.File;

        private string? _cachedCardSubtitle;
        /// <summary>Summary display text for the card subtitle area.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string CardSubtitle => _cachedCardSubtitle ??= ComputeCardSubtitle();

        private string ComputeCardSubtitle()
        {
            if (ItemType == ClipboardItemType.Image) 
                return !string.IsNullOrEmpty(FormattedSize) && FormattedSize != "Loading..." ? $"Image • {FormattedSize}" : "Image";
            if (ItemType == ClipboardItemType.Url) return "Link";
            if (ItemType == ClipboardItemType.Folder) return "Folder";
            
            if (IsApk)
            {
                string label = Extension == "AAB" ? "AAB" : "APK";
                return !string.IsNullOrEmpty(FormattedSize) && FormattedSize != "Loading..." ? $"{label} • {FormattedSize}" : label;
            }

            if (ItemType == ClipboardItemType.File || ItemType == ClipboardItemType.Document || ItemType == ClipboardItemType.Pdf || ItemType == ClipboardItemType.Archive || ItemType == ClipboardItemType.Video || ItemType == ClipboardItemType.Audio || ItemType == ClipboardItemType.Presentation)
            {
                string extLabel = Extension?.ToUpperInvariant()?.TrimStart('.') ?? "FILE";
                if (extLabel == "MARKDOWN") extLabel = "MD";
                return !string.IsNullOrEmpty(FormattedSize) && FormattedSize != "Loading..." ? $"{extLabel} • {FormattedSize}" : extLabel;
            }

            if (ItemType == ClipboardItemType.Code)
            {
                string extLabel = Extension?.ToUpperInvariant()?.TrimStart('.') ?? "CODE";
                return !string.IsNullOrEmpty(FormattedSize) && FormattedSize != "Loading..." ? $"{extLabel} • {FormattedSize}" : extLabel;
            }
            
            // Text: show character count
            if (!string.IsNullOrEmpty(_rawContentBackingFile))
            {
                try {
                    var fi = new System.IO.FileInfo(_rawContentBackingFile);
                    return fi.Exists ? $"{fi.Length:N0} chars" : "Empty";
                } catch { return "Spilled"; }
            }
            var len = RawContent?.Length ?? 0;
            return len > 0 ? $"{len:N0} chars" : "Empty";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // [FIX M-30]: Isolate each PropertyChanged invocation so one bad subscriber
        // doesn't prevent the remaining notifications from firing.
        private void SafeNotify(string propertyName)
        {
            try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); } catch { }
        }

        // ═══ IDisposable — release BitmapSource refs and large strings ═══
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // [FIX M-40]: Cancel any pending background tasks
            try { _disposeCts?.Cancel(); _disposeCts?.Dispose(); } catch { }
            _disposeCts = null;

            if (Icon is System.Windows.Media.Imaging.BitmapImage bitmapImage && !bitmapImage.IsFrozen && bitmapImage.StreamSource != null)
            {
                try { bitmapImage.StreamSource.Dispose(); } catch { }
            }
            Icon = null;

            if (SourceAppIcon is System.Windows.Media.Imaging.BitmapImage srcImage && !srcImage.IsFrozen && srcImage.StreamSource != null)
            {
                try { srcImage.StreamSource.Dispose(); } catch { }
            }
            SourceAppIcon = null;
            _rawContent = string.Empty;
            _zippedArchivePath = string.Empty;

            // [SECURITY FIX v2.1.0]: Clean up spilled large-text backing file on dispose (H-04)
            if (!string.IsNullOrEmpty(_rawContentBackingFile))
            {
                // [FIX BTN-9]: Removed blocking spin-wait that could stall UI for 500ms during collection clear.
                // If spilled content is still loading, just let GC handle cleanup.
                if (Interlocked.CompareExchange(ref _isLoadingSpilledContentFlag, 0, 0) != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"ClipboardItem.Dispose: spill-load still active for {FileName}");
                }
                try { System.IO.File.Delete(_rawContentBackingFile); } catch { }
                _rawContentBackingFile = null;
                _backingFileVerified = false;
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>Cancellation source for background tasks. Cancelled in Dispose().</summary>
        private CancellationTokenSource? _disposeCts = new();
        
    }
}

