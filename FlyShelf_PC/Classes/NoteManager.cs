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
        public string LastEditedDisplay => LastEdited.ToString("h:mm tt");

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

        [JsonIgnore] public string PinIcon => _isPinned ? "📌" : "";

        // ── Sort Order (for drag-reorder) ───────────────────────
        private int _sortOrder;
        public int SortOrder
        {
            get => _sortOrder;
            set { if (_sortOrder != value) { _sortOrder = value; OnPropertyChanged(nameof(SortOrder)); } }
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
    public class SubBulletItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        private string _text = "";
        public string Text
        {
            get => _text;
            set { if (_text != value) { _text = value; OnPropertyChanged(nameof(Text)); } }
        }

        private bool _isDone;
        public bool IsDone
        {
            get => _isDone;
            set { if (_isDone != value) { _isDone = value; OnPropertyChanged(nameof(IsDone)); } }
        }

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

    /// <summary>A single freeform text card within a NoteDay. Multiple sections let users
    /// visually separate different notes under one day.</summary>
    public class FreeformSection : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

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
        private static List<NoteDay> _allDays = new();
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static int _isDirty = 0;
        private static bool _isLoaded;

        // NM-4 FIX: Retry wrapper for transient file-lock conflicts (mirrors TodoManager.RunWithRetry)
        private static T RunWithRetry<T>(Func<T> action, int maxAttempts = 3, int delayMs = 100)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try { return action(); }
                catch when (i < maxAttempts - 1) { System.Threading.Thread.Sleep(delayMs); }
            }
            return action(); // Final attempt — let exception propagate
        }

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
                        _allDays = new List<NoteDay>();
                        _isLoaded = true;
                        return;
                    }

                    // NM-4 FIX: Use RunWithRetry so a brief file lock from concurrent .bak copy doesn't immediately fall to backup recovery
                    string json = RunWithRetry(() => File.ReadAllText(_notesPath));
                    var loaded = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null)
                    {
                        // Normalize all dates to local timezone to prevent UTC timezone shift bugs
                        foreach (var d in loaded)
                        {
                            d.Date = d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date;
                            d.MigrateFreeformIfNeeded(); // Migrate legacy FreeformContent → FreeformSections
                        }

                        // Sort newest first
                        _allDays = loaded.OrderByDescending(d => d.Date).ToList();
                        
                        // Populate visible days list
                        FilterVisibleDays();
                    }
                    else
                    {
                        _days = new ObservableCollection<NoteDay>();
                        _allDays = new List<NoteDay>();
                    }
                    _isLoaded = true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES", $"Failed to load notes: {ex.Message}");
                    
                    // Attempt backup recovery
                    string backupPath = _notesPath + ".bak";
                    if (File.Exists(backupPath))
                    {
                        try
                        {
                            string backupJson = File.ReadAllText(backupPath);
                            var loadedBackup = JsonSerializer.Deserialize<List<NoteDay>>(backupJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (loadedBackup != null)
                            {
                                foreach (var d in loadedBackup)
                                {
                                    d.Date = d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date;
                                    d.MigrateFreeformIfNeeded();
                                }
                                _allDays = loadedBackup.OrderByDescending(d => d.Date).ToList();
                                FilterVisibleDays();
                                _isLoaded = true;
                                Logger.LogAction("NOTES", "Successfully recovered notes from backup file (.bak)!");
                                return;
                            }
                        }
                        catch (Exception backupEx)
                        {
                            Logger.LogAction("NOTES", $"Backup recovery also failed: {backupEx.Message}");
                        }
                    }

                    _days = new ObservableCollection<NoteDay>();
                    _allDays = new List<NoteDay>();
                }
            }
        }

        private static void FilterVisibleDays()
        {
            int maxDays = LicenseManager.GetNoteHistoryDays();
            if (maxDays < int.MaxValue)
            {
                DateTime cutoff = DateTime.Today.AddDays(-maxDays);
                var visible = _allDays.Where(d => d.Date.Date >= cutoff.Date).ToList();
                if (_allDays.Count > visible.Count)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        UpgradePrompt.ShowNoteHistoryLimit());
                }
                _days = new ObservableCollection<NoteDay>(visible);
            }
            else
            {
                _days = new ObservableCollection<NoteDay>(_allDays);
            }
        }

        public static NoteDay GetOrCreateDay(DateTime date)
        {
            lock (_lock)
            {
                var dateOnly = date.Date;
                var existing = _allDays.FirstOrDefault(d => d.Date.Date == dateOnly);
                if (existing == null)
                {
                    existing = new NoteDay { Date = dateOnly };
                    existing.MigrateFreeformIfNeeded(); // Ensure at least one freeform section
                    _allDays.Add(existing);
                    _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                    
                    // If it falls within the visible days, insert it into _days too
                    int maxDays = LicenseManager.GetNoteHistoryDays();
                    if (maxDays == int.MaxValue || dateOnly >= DateTime.Today.AddDays(-maxDays))
                    {
                        int insertIdx = 0;
                        while (insertIdx < _days.Count && _days[insertIdx].Date > dateOnly)
                        {
                            insertIdx++;
                        }
                        _days.Insert(insertIdx, existing);
                    }
                    ScheduleSave();
                }
                return existing;
            }
        }

        public static NoteDay EnsureToday()
        {
            return GetOrCreateDay(DateTime.Today);
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

        public static NoteBullet InsertBullet(NoteDay day, int index, string content = "")
        {
            var bullet = new NoteBullet { Content = content };
            if (index < 0 || index >= day.Bullets.Count)
            {
                day.Bullets.Add(bullet);
            }
            else
            {
                day.Bullets.Insert(index, bullet);
            }
            
            for (int i = 0; i < day.Bullets.Count; i++)
            {
                day.Bullets[i].SortOrder = i;
            }
            
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
            // NM-1 FIX: Cap image dimensions to 4096×4096 to prevent huge PNG writes
            const int MAX_DIM = 4096;
            if (image.PixelWidth > MAX_DIM || image.PixelHeight > MAX_DIM)
            {
                double scale = Math.Min((double)MAX_DIM / image.PixelWidth, (double)MAX_DIM / image.PixelHeight);
                var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                    image,
                    new System.Windows.Media.ScaleTransform(scale, scale));
                scaled.Freeze();
                image = scaled;
            }

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
        public static void MarkDirty()
        {
            Interlocked.Exchange(ref _isDirty, 1);
            ScheduleSave();
        }

        /// <summary>
        /// Schedule a debounced save (2-second cooldown).
        /// </summary>
        private static void ScheduleSave()
        {
            Interlocked.Exchange(ref _isDirty, 1);
            lock (_lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ =>
                {
                    if (Interlocked.CompareExchange(ref _isDirty, 0, 1) == 0) return; // was already clean
                    SaveNow();
                }, null, 2000, Timeout.Infinite);
            }
        }

        /// <summary>Immediately persist to disk (atomic write).</summary>
        public static void SaveNow()
        {
            // DEADLOCK FIX: Snapshot the collection OUTSIDE the lock.
            // Previously, Dispatcher.Invoke() was called while holding _lock,
            // causing AB-BA deadlock when the UI thread also needed _lock.
            if (!_isLoaded) return;

            // NM-2a FIX: Serialize to JSON on the calling thread so the snapshot is
            // truly atomic with respect to the ObservableCollection. Only the file
            // I/O runs on a background thread.
            List<NoteDay> snapshot;
            try
            {
                // Must read ObservableCollection on UI thread if it was created there
                if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        List<NoteDay> snap;
                        try { snap = _days.ToList(); } catch { return; }
                        // NM-2a FIX: Serialize immediately on the UI thread
                        string jsonStr;
                        try
                        {
                            jsonStr = SerializeSnapshot(snap);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("NOTES", $"Failed to serialize notes snapshot: {ex.Message}");
                            return;
                        }
                        Task.Run(() => SaveSnapshotJson(snap, jsonStr));
                    });
                    return;
                }
                else
                {
                    snapshot = _days.ToList();
                }
            }
            catch
            {
                try { snapshot = _days.ToList(); } catch { return; }
            }

            // NM-2a FIX: Serialize on this thread before handing off to background
            string json;
            try
            {
                json = SerializeSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"Failed to serialize notes snapshot: {ex.Message}");
                return;
            }

            // Run merge + file I/O on a background thread so it doesn't block the UI thread
            Task.Run(() => SaveSnapshotJson(snapshot, json));
        }

        /// <summary>Serialize a snapshot list to JSON string (called on the thread that owns the data).</summary>
        private static string SerializeSnapshot(List<NoteDay> snapshot)
        {
            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>Merge snapshot into _allDays, re-serialize under lock, and write to disk.</summary>
        private static void SaveSnapshotJson(List<NoteDay> snapshot, string preSerializedJson)
        {
            string json;
            // NM-2 FIX: Merge both lock acquisitions into one so _allDays cannot be mutated
            // between the merge step and the serialization step.
            lock (_lock)
            {
                // Merge snapshot back into _allDays
                var visibleDates = new HashSet<DateTime>(snapshot.Select(d => d.Date.Date));
                int maxDays = LicenseManager.GetNoteHistoryDays();
                DateTime? cutoff = maxDays < int.MaxValue ? (DateTime?)DateTime.Today.AddDays(-maxDays) : null;

                // 1. Remove visible days from _allDays if they are no longer present in the snapshot (user deleted them)
                _allDays.RemoveAll(d => {
                    if (cutoff.HasValue && d.Date.Date < cutoff.Value.Date) return false; // Keep hidden history
                    return !visibleDates.Contains(d.Date.Date); // Delete if it was visible but user removed it
                });

                // 2. Add or update days from snapshot
                foreach (var snapDay in snapshot)
                {
                    int idx = _allDays.FindIndex(d => d.Date.Date == snapDay.Date.Date);
                    if (idx >= 0)
                    {
                        _allDays[idx] = snapDay;
                    }
                    else
                    {
                        _allDays.Add(snapDay);
                    }
                }

                // Sort newest first
                _allDays = _allDays.OrderByDescending(d => d.Date).ToList();

                // Re-serialize _allDays (which now includes hidden history) inside the lock
                try
                {
                    json = JsonSerializer.Serialize(_allDays, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES", $"Failed to serialize notes: {ex.Message}");
                    return;
                }
            }

            try
            {
                if (!Directory.Exists(_appDataDir))
                    Directory.CreateDirectory(_appDataDir);

                // Create backup copy first
                if (File.Exists(_notesPath))
                {
                    try
                    {
                        File.Copy(_notesPath, _notesPath + ".bak", overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("NOTES", $"Failed to create notes backup: {ex.Message}");
                    }
                }

                // Atomic write: tmp → rename
                string tmpPath = _notesPath + ".tmp";
                if (!DiskSpaceHelper.HasSufficientDiskSpace(_notesPath, json.Length * 2 + 1_000_000))
                {
                    Logger.LogAction("NOTES", "Insufficient disk space to save notes — skipping write");
                    return;
                }
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _notesPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"Failed to save notes: {ex.Message}");
            }
        }

        /// <summary>
        /// Search all notes for a query string. Returns matching bullets with their parent day.
        /// </summary>
        // NM-15b FIX: Cap search results to prevent UI freezes on large note histories
        private const int MAX_SEARCH_RESULTS = 200;

        public static List<(NoteDay Day, NoteBullet Bullet)> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();
            string q = query.Trim();

            var results = new List<(NoteDay, NoteBullet)>();
            foreach (var day in _days)
            {
                foreach (var bullet in day.Bullets)
                {
                    bool matchContent = FuzzyMatcher.IsMatch(q, bullet.Content ?? "");
                    bool matchHeader = FuzzyMatcher.IsMatch(q, bullet.Header ?? "");
                    bool matchTags = bullet.Tags.Any(t => FuzzyMatcher.IsMatch(q, t));
                    if (matchContent || matchHeader || matchTags)
                    {
                        results.Add((day, bullet));
                        if (results.Count >= MAX_SEARCH_RESULTS) return results;
                    }
                }
                // Also search freeform content — create a virtual bullet for display
                if (!string.IsNullOrEmpty(day.FreeformContent) && FuzzyMatcher.IsMatch(q, day.FreeformContent))
                {
                    var virtualBullet = new NoteBullet
                    {
                        Id = "freeform_" + day.Date.Ticks,
                        Content = day.FreeformContent.Length > 200 ? day.FreeformContent[..200] + "..." : day.FreeformContent,
                        CreatedAt = day.Date
                    };
                    results.Add((day, virtualBullet));
                    if (results.Count >= MAX_SEARCH_RESULTS) return results;
                }
            }
            return results;
        }

        // ═══════════════════════════════════════════════════════════
        // EXPORT
        // ═══════════════════════════════════════════════════════════

        /// <summary>Export a day's notes as Markdown text.</summary>
        public static string ExportToMarkdown(NoteDay day)
        {
            if (day == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Notes — {day.Date:MMMM d, yyyy}");
            sb.AppendLine();

            if (day.IsFreeformMode || day.FreeformSections.Any(s => !string.IsNullOrEmpty(s.Content)))
            {
                foreach (var section in day.FreeformSections)
                {
                    if (!string.IsNullOrEmpty(section.Content))
                    {
                        sb.AppendLine(section.Content);
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                }
            }

            foreach (var bullet in day.Bullets)
            {
                if (!string.IsNullOrEmpty(bullet.Header))
                    sb.AppendLine($"## {bullet.Header}");
                if (!string.IsNullOrEmpty(bullet.Content))
                    sb.AppendLine(bullet.Content);
                if (bullet.HasTags)
                    sb.AppendLine($"*Tags: {bullet.TagsDisplay}*");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>Export a day's notes as plain text.</summary>
        public static string ExportToText(NoteDay day)
        {
            if (day == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Notes — {day.Date:MMMM d, yyyy}");
            sb.AppendLine(new string('─', 40));
            sb.AppendLine();

            if (day.IsFreeformMode || day.FreeformSections.Any(s => !string.IsNullOrEmpty(s.Content)))
            {
                foreach (var section in day.FreeformSections)
                {
                    if (!string.IsNullOrEmpty(section.Content))
                    {
                        sb.AppendLine(section.Content);
                        sb.AppendLine();
                    }
                }
            }

            foreach (var bullet in day.Bullets)
            {
                if (!string.IsNullOrEmpty(bullet.Header))
                    sb.AppendLine($"• {bullet.Header}");
                if (!string.IsNullOrEmpty(bullet.Content))
                    sb.AppendLine($"  {bullet.Content}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>Permanently deletes a bullet from the given day.</summary>
        public static void DeleteBullet(NoteDay day, NoteBullet bullet)
        {
            day.Bullets.Remove(bullet);
            ScheduleSave();
        }

        // ═══════════════════════════════════════════════════════════
        // MOBILE SYNC
        // ═══════════════════════════════════════════════════════════

        public static string GetSyncPayload()
        {
            List<NoteDay> snapshot;
            lock (_lock)
            {
                if (!_isLoaded) return "[]";
                try { snapshot = _days.ToList(); } catch { return "[]"; }
            }

            // Build sync-safe payload
            var payload = snapshot.Select(day => {
                long lastMod = 0;
                foreach (var b in day.Bullets)
                {
                    long bTs = new DateTimeOffset(b.LastEdited).ToUnixTimeMilliseconds();
                    if (bTs > lastMod) lastMod = bTs;
                }
                foreach (var s in day.FreeformSections)
                {
                    long sTs = new DateTimeOffset(s.CreatedAt).ToUnixTimeMilliseconds();
                    if (sTs > lastMod) lastMod = sTs;
                }
                if (lastMod == 0) lastMod = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();

                return new {
                    Date = day.Date.ToString("o"),
                    IsFreeformMode = day.IsFreeformMode,
                    Bullets = day.Bullets.Select(b => new {
                        b.Id, b.Header, b.Content, b.IsCollapsed,
                        b.ImageDisplayWidth, b.ImageDisplayWidth2,
                        CreatedAt = b.CreatedAt.ToString("o"),
                        LastEdited = b.LastEdited.ToString("o"),
                        b.Tags, b.Color, b.IsPinned, b.SortOrder,
                        SubBullets = b.SubBullets.Select(sb => new { sb.Id, sb.Text, sb.IsDone }).ToList()
                    }).ToList(),
                    FreeformSections = day.FreeformSections.Select(s => new {
                        s.Id, s.Content, CreatedAt = s.CreatedAt.ToString("o")
                    }).ToList(),
                    LastModified = lastMod
                };
            }).ToList();

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        }

        public static void MergeFromMobile(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                if (!_isLoaded) Load();

                var remoteDays = JsonSerializer.Deserialize<List<NoteDay>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (remoteDays == null || remoteDays.Count == 0) return;

                bool changed = false;
                lock (_lock)
                {
                    foreach (var remoteDay in remoteDays)
                    {
                        remoteDay.Date = remoteDay.Date.Kind == DateTimeKind.Utc ? remoteDay.Date.ToLocalTime().Date : remoteDay.Date.Date;
                        remoteDay.MigrateFreeformIfNeeded();

                        long remoteMod = remoteDay.LastModified ?? 0;

                        var localDay = _allDays.FirstOrDefault(d => d.Date.Date == remoteDay.Date.Date);
                        if (localDay == null)
                        {
                            // New day from mobile — add it
                            _allDays.Add(remoteDay);
                            changed = true;
                        }
                        else
                        {
                            // Compare LastModified — mobile wins if newer
                            long localMod = 0;
                            foreach (var b in localDay.Bullets)
                            {
                                long bTs = new DateTimeOffset(b.LastEdited).ToUnixTimeMilliseconds();
                                if (bTs > localMod) localMod = bTs;
                            }
                            foreach (var s in localDay.FreeformSections)
                            {
                                long sTs = new DateTimeOffset(s.CreatedAt).ToUnixTimeMilliseconds();
                                if (sTs > localMod) localMod = sTs;
                            }

                            if (remoteMod > localMod)
                            {
                                localDay.Bullets = new System.Collections.ObjectModel.ObservableCollection<NoteBullet>(remoteDay.Bullets);
                                localDay.FreeformSections = new System.Collections.ObjectModel.ObservableCollection<FreeformSection>(remoteDay.FreeformSections);
                                localDay.IsFreeformMode = remoteDay.IsFreeformMode;
                                changed = true;
                            }
                        }
                    }
                    if (changed)
                    {
                        _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                        FilterVisibleDays();
                    }
                }
                if (changed) ScheduleSave();
                Logger.LogAction("NOTES_SYNC", $"Merged {remoteDays.Count} days from mobile (changed={changed})");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES_SYNC", $"MergeFromMobile failed: {ex.Message}");
            }
        }
    }
}
