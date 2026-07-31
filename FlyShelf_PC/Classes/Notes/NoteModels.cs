// ---------------------------------------------------------------
// Note Data Models — Extracted from NoteManager.cs
// [FIX M-59]: Separated model classes from persistence logic for
//   better modularity, testability, and maintainability.
// Contains: NoteBullet, SubBulletItem, FreeformImage,
//           FreeformSection, NoteDay
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Classes
{
    // ═══════════════════════════════════════════════════════════
    // DATA MODELS
    // ═══════════════════════════════════════════════════════════

    public class NoteBullet : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        
        private string _header = "";
        public string Header
        {
            get => _header;
            set { if (_header != value) { _header = value; OnPropertyChanged(nameof(Header)); } }
        }

        private string _content = "";
        public string Content
        {
            get => _content;
            set { if (_content != value) { _content = value; OnPropertyChanged(nameof(Content)); } }
        }

        /// <summary>
        /// Optional path to an embedded image stored in Notes/Images/ folder.
        /// When set, the bullet card renders the image below the text.
        /// </summary>
        private string _imagePath = "";
        [JsonIgnore] private bool? _hasImageCache;
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    _hasImageCache = null; // Invalidate cache on path change
                    OnPropertyChanged(nameof(ImagePath));
                    OnPropertyChanged(nameof(HasImage));
                }
            }
        }

        [JsonIgnore]
        public bool HasImage
        {
            get
            {
                if (_hasImageCache == null)
                    _hasImageCache = !string.IsNullOrEmpty(_imagePath) && File.Exists(_imagePath);
                return _hasImageCache.Value;
            }
        }

        private string _imagePath2 = "";
        [JsonIgnore] private bool? _hasImage2Cache;
        public string ImagePath2
        {
            get => _imagePath2;
            set
            {
                if (_imagePath2 != value)
                {
                    _imagePath2 = value;
                    _hasImage2Cache = null; // Invalidate cache on path change
                    OnPropertyChanged(nameof(ImagePath2));
                    OnPropertyChanged(nameof(HasImage2));
                }
            }
        }

        [JsonIgnore]
        public bool HasImage2
        {
            get
            {
                if (_hasImage2Cache == null)
                    _hasImage2Cache = !string.IsNullOrEmpty(_imagePath2) && File.Exists(_imagePath2);
                return _hasImage2Cache.Value;
            }
        }

        private bool _isCollapsed = true;
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set { if (_isCollapsed != value) { _isCollapsed = value; OnPropertyChanged(nameof(IsCollapsed)); OnPropertyChanged(nameof(CollapseIcon)); } }
        }

        [JsonIgnore]
        public string CollapseIcon => _isCollapsed ? "▸" : "▾";

        private double _imageDisplayWidth = 200;
        public double ImageDisplayWidth
        {
            get => _imageDisplayWidth;
            set { if (Math.Abs(_imageDisplayWidth - value) > 0.5) { _imageDisplayWidth = value; OnPropertyChanged(nameof(ImageDisplayWidth)); } }
        }

        private double _imageDisplayWidth2 = 200;
        public double ImageDisplayWidth2
        {
            get => _imageDisplayWidth2;
            set { if (Math.Abs(_imageDisplayWidth2 - value) > 0.5) { _imageDisplayWidth2 = value; OnPropertyChanged(nameof(ImageDisplayWidth2)); } }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        private DateTime _lastEdited = DateTime.Now;
        public DateTime LastEdited
        {
            get => _lastEdited;
            set
            {
                if (_lastEdited != value)
                {
                    _lastEdited = value;
                    OnPropertyChanged(nameof(LastEdited));
                    OnPropertyChanged(nameof(LastEditedDisplay));
                }
            }
        }

        [JsonIgnore]
        public string LastEditedDisplay => LastEdited.ToString("h:mm tt", CultureInfo.InvariantCulture);

        // ── Tags ────────────────────────────────────────────────
        private List<string> _tags = new();
        public List<string> Tags
        {
            get => _tags;
            set { _tags = value ?? new(); OnPropertyChanged(nameof(Tags)); OnPropertyChanged(nameof(HasTags)); OnPropertyChanged(nameof(TagsDisplay)); }
        }

        [JsonIgnore] public bool HasTags => _tags.Count > 0;
        [JsonIgnore] public string TagsDisplay => string.Join(", ", _tags);

        // ── Color Accent ────────────────────────────────────────
        private string _color = "";
        public string Color
        {
            get => _color;
            set { if (_color != value) { _color = value ?? ""; OnPropertyChanged(nameof(Color)); OnPropertyChanged(nameof(HasColor)); } }
        }

        [JsonIgnore] public bool HasColor => !string.IsNullOrEmpty(_color);

        // ── Pin / Favorite ──────────────────────────────────────
        private bool _isPinned;
        public bool IsPinned
        {
            get => _isPinned;
            set { if (_isPinned != value) { _isPinned = value; OnPropertyChanged(nameof(IsPinned)); OnPropertyChanged(nameof(PinIcon)); } }
        }

        [JsonIgnore] public string PinIcon => _isPinned ? "" : "";

        // ── Sort Order (for drag-reorder) ───────────────────────
        private int _sortOrder;
        public int SortOrder
        {
            get => _sortOrder;
            set { if (_sortOrder != value) { _sortOrder = value; OnPropertyChanged(nameof(SortOrder)); } }
        }

        // ── Device Origin (tracks which device created/edited this bullet) ──
        public string? CreatedByDevice { get; set; }
        public string? LastEditedByDevice { get; set; }

        private static readonly string[] DeviceColors = { "#4A62EB", "#E94560", "#34D399", "#F59E0B", "#8B5CF6", "#06B6D4", "#EC4899", "#10B981" };
        public static string GetDeviceColor(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return "#666680";
            int hash = Math.Abs(deviceName.GetHashCode());
            return DeviceColors[hash % DeviceColors.Length];
        }

        [JsonIgnore]
        public string DeviceDotColor => GetDeviceColor(CreatedByDevice);

        [JsonIgnore]
        public string DeviceTooltip
        {
            get
            {
                if (string.IsNullOrEmpty(CreatedByDevice) && string.IsNullOrEmpty(LastEditedByDevice))
                    return "Local";
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(CreatedByDevice))
                    parts.Add($"Created by: {CreatedByDevice}");
                if (!string.IsNullOrEmpty(LastEditedByDevice))
                    parts.Add($"Last edited by: {LastEditedByDevice}");
                return string.Join("\n", parts);
            }
        }

        // ── Sub-bullets (nested items inside this bullet card) ──
        private ObservableCollection<SubBulletItem> _subBullets = new();
        public ObservableCollection<SubBulletItem> SubBullets
        {
            get => _subBullets;
            set { _subBullets = value ?? new(); OnPropertyChanged(nameof(SubBullets)); OnPropertyChanged(nameof(HasSubBullets)); }
        }

        [JsonIgnore]
        public bool HasSubBullets => _subBullets.Count > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Called after mutating SubBullets directly to refresh HasSubBullets binding.</summary>
        public void OnSubBulletsChanged() => OnPropertyChanged(nameof(HasSubBullets));
    }

    /// <summary>
    /// A single sub-bullet item nested inside a NoteBullet card.
    /// </summary>
    public partial class SubBulletItem : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

        [ObservableProperty]
        private string _text = "";

        [ObservableProperty]
        private bool _isDone;
    }


    public class FreeformImage : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

        private string _imagePath = "";
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged(nameof(ImagePath));
                    OnPropertyChanged(nameof(HasImage));
                }
            }
        }

        [JsonIgnore]
        public bool HasImage => !string.IsNullOrEmpty(_imagePath) && File.Exists(_imagePath);

        private double _displayWidth = 200;
        public double DisplayWidth
        {
            get => _displayWidth;
            set { if (Math.Abs(_displayWidth - value) > 0.5) { _displayWidth = value; OnPropertyChanged(nameof(DisplayWidth)); } }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A single freeform text card within a NoteDay. Multiple sections let users
    /// visually separate different notes under one day.</summary>
    public class FreeformSection : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

        private string _title = "";
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(HasTitle)); } }
        }

        [JsonIgnore]
        public bool HasTitle => !string.IsNullOrEmpty(_title);

        private string _content = "";
        public string Content
        {
            get => _content;
            set { if (_content != value) { _content = value; OnPropertyChanged(nameof(Content)); } }
        }

        /// <summary>Images embedded in this section card (up to 5 for Pro, 1 for Free).</summary>
        private ObservableCollection<FreeformImage> _images = new();
        public ObservableCollection<FreeformImage> Images
        {
            get => _images;
            set { _images = value; OnPropertyChanged(nameof(Images)); }
        }

        /// <summary>Rich formatted content stored as XAML (used by expand window). Plain Content is kept in sync.</summary>
        public string RichContent { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class NoteDay : INotifyPropertyChanged
    {
        /// <summary>Date-only key (time zeroed). Used for identification and sorting.</summary>
        public DateTime Date { get; set; } = DateTime.Today;

        /// <summary>Pre-formatted display: "27, May" — no year.</summary>
        [JsonIgnore]
        public string DisplayDate => Date.ToString("dd, MMM", CultureInfo.InvariantCulture);

        /// <summary>Just the day number for the collapsed sidebar.</summary>
        [JsonIgnore]
        public string DayNumber => Date.Day.ToString(CultureInfo.InvariantCulture);

        /// <summary>Abbreviated month for hover tooltip.</summary>
        [JsonIgnore]
        public string MonthName => Date.ToString("MMM", CultureInfo.InvariantCulture);

        /// <summary>Full display for hover: "27, May"</summary>
        [JsonIgnore]
        public string FullLabel => DisplayDate;

        private ObservableCollection<NoteBullet> _bullets = new();
        public ObservableCollection<NoteBullet> Bullets
        {
            get => _bullets;
            set { _bullets = value; OnPropertyChanged(nameof(Bullets)); }
        }

        /// <summary>Multiple freeform text sections. Each renders as a separate card.</summary>
        private ObservableCollection<FreeformSection> _freeformSections = new();
        public ObservableCollection<FreeformSection> FreeformSections
        {
            get => _freeformSections;
            set { _freeformSections = value; OnPropertyChanged(nameof(FreeformSections)); }
        }

        /// <summary>Legacy single-string property. On deserialization, if non-empty and
        /// FreeformSections is empty, it migrates into FreeformSections[0].
        /// Getter joins all sections for search/export compatibility.</summary>
        private string _freeformContent = "";
        public string FreeformContent
        {
            get
            {
                // Return joined content from all sections for search/export
                if (_freeformSections.Count > 0)
                    return string.Join("\n\n---\n\n", _freeformSections.Select(s => s.Content));
                return _freeformContent;
            }
            set
            {
                _freeformContent = value ?? "";
                OnPropertyChanged(nameof(FreeformContent));
            }
        }

        /// <summary>Call after deserialization to migrate legacy FreeformContent into sections.</summary>
        public void MigrateFreeformIfNeeded()
        {
            if (_freeformSections.Count == 0 && !string.IsNullOrEmpty(_freeformContent))
            {
                _freeformSections.Add(new FreeformSection { Content = _freeformContent });
                _freeformContent = ""; // Clear legacy field now that we've migrated
            }
            // Ensure there's always at least one section for freeform mode
            if (_freeformSections.Count == 0)
            {
                _freeformSections.Add(new FreeformSection());
            }
        }

        /// <summary>Images embedded in freeform mode. Shown in a strip below the text area.</summary>
        private ObservableCollection<FreeformImage> _freeformImages = new();
        public ObservableCollection<FreeformImage> FreeformImages
        {
            get => _freeformImages;
            set { _freeformImages = value; OnPropertyChanged(nameof(FreeformImages)); }
        }

        private bool _isFreeformMode;
        public bool IsFreeformMode
        {
            get => _isFreeformMode;
            set { if (_isFreeformMode != value) { _isFreeformMode = value; OnPropertyChanged(nameof(IsFreeformMode)); OnPropertyChanged(nameof(IsBulletMode)); } }
        }

        [JsonIgnore]
        public bool IsBulletMode => !_isFreeformMode;

        [JsonIgnore]
        public bool IsToday => Date.Date == DateTime.Today;

        public long? LastModified { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
