// ---------------------------------------------------------------
// NoteManager — Quick Notes Data Model & Persistence
// Stores per-day notes with bullet points and freeform mode.
// Persisted to %AppData%\FlyShelf\notes.json
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace FlyShelf.Classes
{
    // ═══════════════════════════════════════════════════════════
    // DATA MODELS
    // ═══════════════════════════════════════════════════════════

    public class NoteBullet : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        
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

        private string _imagePath2 = "";
        public string ImagePath2
        {
            get => _imagePath2;
            set
            {
                if (_imagePath2 != value)
                {
                    _imagePath2 = value;
                    OnPropertyChanged(nameof(ImagePath2));
                    OnPropertyChanged(nameof(HasImage2));
                }
            }
        }

        [JsonIgnore]
        public bool HasImage2 => !string.IsNullOrEmpty(_imagePath2) && File.Exists(_imagePath2);

        private bool _isCollapsed;
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
        public string LastEditedDisplay => LastEdited.ToString("h:mm tt");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class FreeformImage : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

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

    public class NoteDay : INotifyPropertyChanged
    {
        /// <summary>Date-only key (time zeroed). Used for identification and sorting.</summary>
        public DateTime Date { get; set; } = DateTime.Today;

        /// <summary>Pre-formatted display: "27, May" — no year.</summary>
        [JsonIgnore]
        public string DisplayDate => Date.ToString("dd, MMM");

        /// <summary>Just the day number for the collapsed sidebar.</summary>
        [JsonIgnore]
        public string DayNumber => Date.Day.ToString();

        /// <summary>Abbreviated month for hover tooltip.</summary>
        [JsonIgnore]
        public string MonthName => Date.ToString("MMM");

        /// <summary>Full display for hover: "27, May"</summary>
        [JsonIgnore]
        public string FullLabel => DisplayDate;

        private ObservableCollection<NoteBullet> _bullets = new();
        public ObservableCollection<NoteBullet> Bullets
        {
            get => _bullets;
            set { _bullets = value; OnPropertyChanged(nameof(Bullets)); }
        }

        private string _freeformContent = "";
        public string FreeformContent
        {
            get => _freeformContent;
            set { if (_freeformContent != value) { _freeformContent = value; OnPropertyChanged(nameof(FreeformContent)); } }
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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ═══════════════════════════════════════════════════════════
    // PERSISTENCE MANAGER
    // ═══════════════════════════════════════════════════════════

    public static class NoteManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _notesPath = Path.Combine(_appDataDir, "notes.json");
        private static readonly string _notesImagesDir = Path.Combine(_appDataDir, "Notes", "Images");

        private static ObservableCollection<NoteDay> _days = new();
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static volatile bool _isDirty;

        /// <summary>All note days, sorted newest-first.</summary>
        public static ObservableCollection<NoteDay> Days => _days;

        /// <summary>Returns the permanent image storage directory for notes, creating it if needed.</summary>
        public static string GetImagesDirectory()
        {
            if (!Directory.Exists(_notesImagesDir))
                Directory.CreateDirectory(_notesImagesDir);
            return _notesImagesDir;
        }

        /// <summary>
        /// Load notes from disk. Call once at startup or on first panel open.
        /// </summary>
        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_notesPath))
                    {
                        _days = new ObservableCollection<NoteDay>();
                        return;
                    }

                    string json = File.ReadAllText(_notesPath);
                    var loaded = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null)
                    {
                        // Sort newest first
                        var sorted = loaded.OrderByDescending(d => d.Date).ToList();
                        _days = new ObservableCollection<NoteDay>(sorted);

                        // Trim notes beyond tier history limit
                        int maxDays = LicenseManager.GetNoteHistoryDays();
                        if (maxDays < int.MaxValue)
                        {
                            DateTime cutoff = DateTime.Today.AddDays(-maxDays);
                            var trimmed = sorted.Where(d => d.Date >= cutoff).ToList();
                            if (sorted.Count > trimmed.Count)
                            {
                                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                    UpgradePrompt.ShowNoteHistoryLimit());
                            }
                            _days = new ObservableCollection<NoteDay>(trimmed);
                        }
                    }
                    else
                    {
                        _days = new ObservableCollection<NoteDay>();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES", $"Failed to load notes: {ex.Message}");
                    _days = new ObservableCollection<NoteDay>();
                }
            }
        }

        /// <summary>
        /// Ensures today's NoteDay exists. Returns it.
        /// </summary>
        public static NoteDay EnsureToday()
        {
            var today = DateTime.Today;
            var existing = _days.FirstOrDefault(d => d.Date.Date == today);
            if (existing != null) return existing;

            var newDay = new NoteDay { Date = today };
            // Insert at top (newest first)
            _days.Insert(0, newDay);
            ScheduleSave();
            return newDay;
        }

        /// <summary>Check if a day exists.</summary>
        public static bool HasDay(DateTime date) => _days.Any(d => d.Date.Date == date.Date);

        /// <summary>Get a specific day's data.</summary>
        public static NoteDay? GetDay(DateTime date) => _days.FirstOrDefault(d => d.Date.Date == date.Date);

        /// <summary>Add a bullet to a specific day.</summary>
        public static NoteBullet AddBullet(NoteDay day, string content = "")
        {
            var bullet = new NoteBullet { Content = content };
            day.Bullets.Add(bullet);
            ScheduleSave();
            return bullet;
        }

        /// <summary>Remove a bullet from a day.</summary>
        public static void RemoveBullet(NoteDay day, NoteBullet bullet)
        {
            day.Bullets.Remove(bullet);
            // Clean up image file if exists
            if (bullet.HasImage)
            {
                try { File.Delete(bullet.ImagePath); } catch { }
            }
            ScheduleSave();
        }

        /// <summary>
        /// Save an image from clipboard/file to the notes images directory.
        /// Returns the saved file path.
        /// </summary>
        public static string SaveImage(System.Windows.Media.Imaging.BitmapSource image)
        {
            var dir = GetImagesDirectory();
            string filename = $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.png";
            string path = Path.Combine(dir, filename);

            using var stream = new FileStream(path, FileMode.Create);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            encoder.Save(stream);

            return path;
        }

        /// <summary>Mark data as dirty — will persist on next debounce cycle.</summary>
        public static void MarkDirty() => ScheduleSave();

        /// <summary>
        /// Schedule a debounced save (2-second cooldown).
        /// </summary>
        private static void ScheduleSave()
        {
            _isDirty = true;
            lock (_lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ =>
                {
                    if (!_isDirty) return;
                    _isDirty = false;
                    SaveNow();
                }, null, 2000, Timeout.Infinite);
            }
        }

        /// <summary>Immediately persist to disk (atomic write).</summary>
        public static void SaveNow()
        {
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_appDataDir))
                        Directory.CreateDirectory(_appDataDir);

                    // Snapshot the data
                    List<NoteDay> snapshot;
                    try
                    {
                        // Must read ObservableCollection on UI thread if it was created there
                        if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                        {
                            snapshot = System.Windows.Application.Current.Dispatcher.Invoke(() => _days.ToList());
                        }
                        else
                        {
                            snapshot = _days.ToList();
                        }
                    }
                    catch
                    {
                        snapshot = _days.ToList();
                    }

                    string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });

                    // Atomic write: tmp → rename
                    string tmpPath = _notesPath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _notesPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES", $"Failed to save notes: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Search all notes for a query string. Returns matching bullets with their parent day.
        /// </summary>
        public static List<(NoteDay Day, NoteBullet Bullet)> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();
            string q = query.Trim().ToLowerInvariant();

            var results = new List<(NoteDay, NoteBullet)>();
            foreach (var day in _days)
            {
                foreach (var bullet in day.Bullets)
                {
                    bool matchContent = !string.IsNullOrEmpty(bullet.Content) && bullet.Content.ToLowerInvariant().Contains(q);
                    bool matchHeader = !string.IsNullOrEmpty(bullet.Header) && bullet.Header.ToLowerInvariant().Contains(q);
                    if (matchContent || matchHeader)
                    {
                        results.Add((day, bullet));
                    }
                }
                // Also search freeform content — create a virtual bullet for display
                if (!string.IsNullOrEmpty(day.FreeformContent) && day.FreeformContent.ToLowerInvariant().Contains(q))
                {
                    var virtualBullet = new NoteBullet
                    {
                        Id = "freeform_" + day.Date.Ticks,
                        Content = day.FreeformContent.Length > 200 ? day.FreeformContent[..200] + "..." : day.FreeformContent,
                        CreatedAt = day.Date
                    };
                    results.Add((day, virtualBullet));
                }
            }
            return results;
        }
    }
}
