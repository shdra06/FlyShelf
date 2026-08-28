using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Classes
{
    // ═══════════════════════════════════════════════════════════
    // ENUMS
    // ═══════════════════════════════════════════════════════════

    public enum TodoPriority
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    public enum TodoRecurrence
    {
        None = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3
    }

    public enum TodoSortMode
    {
        Manual = 0,
        Priority = 1,
        DueDate = 2,
        Alphabetical = 3,
        CreatedAt = 4
    }

    // ═══════════════════════════════════════════════════════════
    // TODO ITEM
    // ═══════════════════════════════════════════════════════════

    public partial class TodoItem : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [ObservableProperty]
        private string _text = "";

        [ObservableProperty]
        private bool _isDone;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [ObservableProperty]
        private DateTime _lastEdited = DateTime.Now;

        // ── Priority ────────────────────────────────────────────
        private TodoPriority _priority = TodoPriority.None;
        public TodoPriority Priority
        {
            get => _priority;
            set
            {
                if (_priority != value)
                {
                    _priority = value;
                    OnPropertyChanged(nameof(Priority));
                    OnPropertyChanged(nameof(HasPriority));
                    OnPropertyChanged(nameof(PriorityDisplay));
                    OnPropertyChanged(nameof(PriorityColor));
                }
            }
        }

        [JsonIgnore] public bool HasPriority => _priority != TodoPriority.None;
        [JsonIgnore]
        public string PriorityDisplay => _priority switch
        {
            TodoPriority.High => "!!!",
            TodoPriority.Medium => "!!",
            TodoPriority.Low => "!",
            _ => ""
        };
        [JsonIgnore]
        public string PriorityColor => _priority switch
        {
            TodoPriority.High => "#FF4444",
            TodoPriority.Medium => "#F59E0B",
            TodoPriority.Low => "#22C55E",
            _ => "#666680"
        };

        // ── Due Date ────────────────────────────────────────────
        private DateTime? _dueDate;
        public DateTime? DueDate
        {
            get => _dueDate;
            set
            {
                if (_dueDate != value)
                {
                    _dueDate = value;
                    OnPropertyChanged(nameof(DueDate));
                    OnPropertyChanged(nameof(HasDueDate));
                    OnPropertyChanged(nameof(DueDateDisplay));
                    OnPropertyChanged(nameof(IsOverdue));
                    OnPropertyChanged(nameof(DueDateColor));
                }
            }
        }

        [JsonIgnore] public bool HasDueDate => _dueDate.HasValue;
        [JsonIgnore]
        public string DueDateDisplay
        {
            get
            {
                if (!_dueDate.HasValue) return "";
                var d = _dueDate.Value.Date;
                if (d == DateTime.Today) return "Today";
                if (d == DateTime.Today.AddDays(1)) return "Tomorrow";
                if (d == DateTime.Today.AddDays(-1)) return "Yesterday";
                if (d.Year == DateTime.Today.Year) return d.ToString("MMM d", CultureInfo.InvariantCulture);
                return d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
            }
        }
        [JsonIgnore] public bool IsOverdue => _dueDate.HasValue && _dueDate.Value.Date < DateTime.Today && !IsDone;
        [JsonIgnore]
        public string DueDateColor => IsOverdue ? "#FF4444" :
            (_dueDate?.Date == DateTime.Today ? "#F59E0B" : "#8B8BA7");

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

        // ── Description / Notes ─────────────────────────────────
        private string _description = "";
        public string Description
        {
            get => _description;
            set
            {
                if (_description != value)
                {
                    _description = value ?? "";
                    OnPropertyChanged(nameof(Description));
                    OnPropertyChanged(nameof(HasDescription));
                    // Auto-expand if description gets content; don't auto-collapse (user controls that)
                    if (!string.IsNullOrWhiteSpace(_description) && !IsDescriptionVisible)
                    {
                        IsDescriptionVisible = true;
                    }
                }
            }
        }

        [JsonIgnore] public bool HasDescription => !string.IsNullOrWhiteSpace(_description);

        // Controls whether description panel is expanded (separate from whether content exists)
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isDescriptionVisible = false;

        // ── Subtasks ────────────────────────────────────────────
        private ObservableCollection<TodoItem> _subTasks = new();
        public ObservableCollection<TodoItem> SubTasks
        {
            get => _subTasks;
            set { _subTasks = value ?? new(); OnPropertyChanged(nameof(SubTasks)); OnPropertyChanged(nameof(HasSubTasks)); OnPropertyChanged(nameof(SubTaskProgress)); }
        }

        [JsonIgnore] public bool HasSubTasks => _subTasks.Count > 0;
        [JsonIgnore]
        public string SubTaskProgress
        {
            get
            {
                if (_subTasks.Count == 0) return "";
                int done = _subTasks.Count(s => s.IsDone);
                return $"{done}/{_subTasks.Count}";
            }
        }

        // ── Recurrence ──────────────────────────────────────────
        private TodoRecurrence _recurrence = TodoRecurrence.None;
        public TodoRecurrence Recurrence
        {
            get => _recurrence;
            set
            {
                if (_recurrence != value)
                {
                    _recurrence = value;
                    OnPropertyChanged(nameof(Recurrence));
                    OnPropertyChanged(nameof(HasRecurrence));
                    OnPropertyChanged(nameof(RecurrenceDisplay));
                }
            }
        }

        [JsonIgnore] public bool HasRecurrence => _recurrence != TodoRecurrence.None;
        [JsonIgnore]
        public string RecurrenceDisplay => _recurrence switch
        {
            TodoRecurrence.Daily => "Daily",
            TodoRecurrence.Weekly => "Weekly",
            TodoRecurrence.Monthly => "Monthly",
            _ => ""
        };

        // ── Sort Order (for manual drag-reorder) ────────────────────
        [ObservableProperty]
        private int _sortOrder;

        // ── Device Origin (tracks which device created/edited this item) ──
        public string? CreatedByDevice { get; set; }
        public string? LastEditedByDevice { get; set; }

        // ── Timer (existing) ────────────────────────────────────
        /// <summary>Timer duration in minutes for this task (null = no timer set)</summary>
        private int? _timerMinutes;
        public int? TimerMinutes
        {
            get => _timerMinutes;
            set { if (_timerMinutes != value) { _timerMinutes = value; OnPropertyChanged(nameof(TimerMinutes)); OnPropertyChanged(nameof(HasTimer)); OnPropertyChanged(nameof(TimerDisplay)); } }
        }

        /// <summary>Optional reminder due time for this task</summary>
        private DateTime? _reminderAt;
        public DateTime? ReminderAt
        {
            get => _reminderAt;
            set { if (_reminderAt != value) { _reminderAt = value; OnPropertyChanged(nameof(ReminderAt)); OnPropertyChanged(nameof(HasReminder)); OnPropertyChanged(nameof(ReminderDisplay)); } }
        }

        [JsonIgnore]
        public bool HasTimer => _timerMinutes.HasValue && _timerMinutes > 0;

        [JsonIgnore]
        public bool HasReminder => _reminderAt.HasValue;

        [JsonIgnore]
        public string TimerDisplay => HasTimer ? $"{_timerMinutes}m" : "";

        [JsonIgnore]
        public string ReminderDisplay => HasReminder ? _reminderAt!.Value.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";

        [JsonIgnore]
        public string CreatedAtDisplay => CreatedAt.ToString("h:mm tt", CultureInfo.InvariantCulture);

        private bool _isExpanded = false;
        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(CollapseIcon)); } }
        }

        [JsonIgnore]
        public string CollapseIcon => _isExpanded ? "▾" : "▸";

        // ── INotifyPropertyChanged ──────────────────────────────

        /// <summary>Refresh all computed display properties (call after bulk updates)</summary>
        public void RefreshDisplayProperties()
        {
            OnPropertyChanged(nameof(PriorityDisplay));
            OnPropertyChanged(nameof(PriorityColor));
            OnPropertyChanged(nameof(DueDateDisplay));
            OnPropertyChanged(nameof(DueDateColor));
            OnPropertyChanged(nameof(IsOverdue));
            OnPropertyChanged(nameof(SubTaskProgress));
            OnPropertyChanged(nameof(TagsDisplay));
        }
    }

    /// <summary>
    /// T-1: deletion tombstone - records a deleted item so the deletion
    /// propagates to paired devices instead of the item resurrecting on merge.
    /// </summary>
    public class TodoTombstone
    {
        public string Id { get; set; } = "";
        public long DeletedAt { get; set; } // Unix ms
    }

    public class TodoDay : INotifyPropertyChanged
    {
        public DateTime Date { get; set; } = DateTime.Today;

        /// <summary>T-1: deletion tombstones for this day. Purged after 30 days during merge.</summary>
        public List<TodoTombstone> DeletedItems { get; set; } = new();

        [JsonIgnore]
        public string DisplayDate => Date.ToString("dd, MMM", CultureInfo.InvariantCulture);

        [JsonIgnore]
        public string DayNumber => Date.Day.ToString(CultureInfo.InvariantCulture);

        [JsonIgnore]
        public string MonthName => Date.ToString("MMM", CultureInfo.InvariantCulture);

        [JsonIgnore]
        public string FullLabel => DisplayDate;

        private ObservableCollection<TodoItem> _items = new();
        public ObservableCollection<TodoItem> Items
        {
            get => _items;
            set { _items = value; OnPropertyChanged(nameof(Items)); }
        }

        [JsonIgnore]
        public bool IsToday => Date.Date == DateTime.Today;

        private bool _isSelected;
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public long? LastModified { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class TodoManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _todosPath = Path.Combine(_appDataDir, "todos.json");

        private static ObservableCollection<TodoDay> _days = new();
        private static List<TodoDay> _allDays = new(); // TM-3 FIX: Backing store preserves all data across _days swaps (mirrors NoteManager)
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static readonly object _fileLock = new(); // TM-1 FIX: separate lock for file I/O
        private static int _isDirty = 0;
        private static bool _isLoaded; // TM-3 FIX: Guard against saving before load completes

        // [FIX STABLE-1]: Consolidated into FileRetryHelper — tightened from bare catch to IOException/UnauthorizedAccessException only
        private static T RunWithRetry<T>(Func<T> action, int maxAttempts = 3, int delayMs = 50)
            => FileRetryHelper.RunWithRetry(action, maxAttempts, delayMs);

        public static ObservableCollection<TodoDay> Days
        {
            get { lock (_lock) { return _days; } }
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_todosPath))
                    {
                        _days = new ObservableCollection<TodoDay>();
                        _allDays = new List<TodoDay>();
                        _isLoaded = true;
                        return;
                    }

                    // TM-2 FIX: Use RunWithRetry so a brief file lock from concurrent .bak copy doesn't immediately fall to backup recovery
                    string json = RunWithRetry(() => File.ReadAllText(_todosPath));
                    var loaded = JsonSerializer.Deserialize<List<TodoDay>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null)
                    {
                        // TM-3 FIX: Normalize dates to local timezone (mirrors NoteManager)
                        foreach (var d in loaded)
                        {
                            d.Date = DateTime.SpecifyKind(d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date, DateTimeKind.Local);
                        }

                        _allDays = loaded.OrderByDescending(d => d.Date).ToList();
                        _days = new ObservableCollection<TodoDay>(_allDays);
                    }
                    else
                    {
                        _days = new ObservableCollection<TodoDay>();
                        _allDays = new List<TodoDay>();
                    }
                    _isLoaded = true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TODOS", $"Failed to load todos: {ex.Message}");
                    // Fallback: try loading from .bak file
                    try
                    {
                        string bakPath = _todosPath + ".bak";
                        if (File.Exists(bakPath))
                        {
                            Logger.LogAction("TODOS", "Attempting recovery from .bak file");
                            string bakJson = RunWithRetry(() => File.ReadAllText(bakPath));
                            var bakLoaded = JsonSerializer.Deserialize<List<TodoDay>>(bakJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (bakLoaded != null)
                            {
                                foreach (var d in bakLoaded)
                                {
                                    d.Date = DateTime.SpecifyKind(d.Date.Kind == DateTimeKind.Utc ? d.Date.ToLocalTime().Date : d.Date.Date, DateTimeKind.Local);
                                }
                                _allDays = bakLoaded.OrderByDescending(d => d.Date).ToList();
                                _days = new ObservableCollection<TodoDay>(_allDays);
                                _isLoaded = true;
                                Logger.LogAction("TODOS", "Successfully recovered todos from backup file (.bak)!");
                                return;
                            }
                        }
                    }
                    catch (Exception bakEx)
                    {
                        Logger.LogAction("TODOS", $"Backup recovery also failed: {bakEx.Message}");
                    }
                    _days = new ObservableCollection<TodoDay>();
                    _allDays = new List<TodoDay>();
                    _isLoaded = true;
                }
            }
        }

        public static TodoDay EnsureToday()
        {
            lock (_lock)
            {
                var today = DateTime.Today;
                var existing = _days.FirstOrDefault(d => d.Date.Date == today);
                if (existing != null) return existing;

                var newDay = new TodoDay { Date = today };
                _days.Insert(0, newDay);
                // TM-3 FIX: Also add to _allDays backing store
                _allDays.Add(newDay);
                _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                ScheduleSave();
                return newDay;
            }
        }

        public static bool HasDay(DateTime date)
        {
            lock (_lock) { return _days.Any(d => d.Date.Date == date.Date); }
        }

        public static TodoDay? GetDay(DateTime date)
        {
            lock (_lock) { return _days.FirstOrDefault(d => d.Date.Date == date.Date); }
        }

        public static TodoItem? AddItem(TodoDay day, string text = "")
        {
            // v7.2 FREE: Daily limit check bypassed — uncomment to re-enable Pro gate
            // int maxItems = LicenseManager.GetTodoDailyLimit();
            // if (maxItems < int.MaxValue && day.Items.Count >= maxItems)
            // {
            //     System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            //         UpgradePrompt.ShowTodoLimit());
            //     return null;
            // }

            var item = new TodoItem { Text = text, CreatedAt = DateTime.Now };
            day.Items.Add(item);
            ScheduleSave();
            return item;
        }

        public static TodoItem? InsertItem(TodoDay day, int index, string text = "")
        {
            // v7.2 FREE: Daily limit check bypassed — uncomment to re-enable Pro gate
            // int maxItems = LicenseManager.GetTodoDailyLimit();
            // if (maxItems < int.MaxValue && day.Items.Count >= maxItems)
            // {
            //     System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            //         UpgradePrompt.ShowTodoLimit());
            //     return null;
            // }

            var item = new TodoItem { Text = text, CreatedAt = DateTime.Now };
            if (index < 0 || index >= day.Items.Count)
            {
                day.Items.Add(item);
            }
            else
            {
                day.Items.Insert(index, item);
            }
            
            for (int i = 0; i < day.Items.Count; i++)
            {
                day.Items[i].SortOrder = i;
            }
            
            ScheduleSave();
            return item;
        }


        public static void RemoveItem(TodoDay day, TodoItem item)
        {
            day.Items.Remove(item);
            RecordTombstone(day, item);
            ScheduleSave();
        }

        /// <summary>
        /// T-1 fix: record a deletion tombstone so the delete propagates to
        /// paired devices instead of the item resurrecting on next sync.
        /// </summary>
        private static void RecordTombstone(TodoDay day, TodoItem item)
        {
            if (string.IsNullOrEmpty(item?.Id)) return;
            day.DeletedItems ??= new List<TodoTombstone>();
            day.DeletedItems.RemoveAll(t => t.Id == item.Id);
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            day.DeletedItems.Add(new TodoTombstone { Id = item.Id, DeletedAt = nowMs });
            day.LastModified = nowMs;
        }

        public static void MarkDirty()
        {
            Interlocked.Exchange(ref _isDirty, 1);
            ScheduleSave();
        }

        public static void ScheduleSave()
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

        public static Task? LastSaveTask;

        public static void SaveNow()
        {
            if (!_isLoaded) return;

            Action performSave = () =>
            {
                List<TodoDay> snapshot;
                string jsonStr;
                try
                {
                    snapshot = _days.ToList();
                    lock (_lock)
                    {
                        foreach (var snapDay in snapshot)
                        {
                            int idx = _allDays.FindIndex(d => d.Date.Date == snapDay.Date.Date);
                            if (idx >= 0) _allDays[idx] = snapDay;
                            else _allDays.Add(snapDay);
                        }
                        _allDays = _allDays.OrderByDescending(d => d.Date).ToList();

                        var serializableList = _allDays.ToList();
                        jsonStr = SerializeSnapshot(serializableList);
                    }
                }
                catch
                {
                    return;
                }
                LastSaveTask = Task.Run(() => SaveSnapshotJson(snapshot, jsonStr));
            };

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher?.CheckAccess() == false)
            {
                app.Dispatcher.InvokeAsync(performSave);
            }
            else
            {
                performSave();
            }
        }

        public static void SaveNowSync()
        {
            if (!_isLoaded) return;

            List<TodoDay> snapshot;
            string jsonStr;
            try
            {
                snapshot = _days.ToList();
                lock (_lock)
                {
                    foreach (var snapDay in snapshot)
                    {
                        int idx = _allDays.FindIndex(d => d.Date.Date == snapDay.Date.Date);
                        if (idx >= 0) _allDays[idx] = snapDay;
                        else _allDays.Add(snapDay);
                    }
                    _allDays = _allDays.OrderByDescending(d => d.Date).ToList();

                    var serializableList = _allDays.ToList();
                    jsonStr = SerializeSnapshot(serializableList);
                }
            }
            catch { return; }
            SaveSnapshotJson(snapshot, jsonStr);
        }

        /// <summary>Serialize a snapshot list to JSON string (called on the thread that owns the data).</summary>
        private static string SerializeSnapshot(List<TodoDay> snapshot)
        {
            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>Merge snapshot into _allDays, re-serialize under lock, and write to disk.
        /// Mirrors NoteManager.SaveSnapshotJson — ensures _allDays is always the complete truth.</summary>
        private static void SaveSnapshotJson(List<TodoDay> snapshot, string preSerializedJson)
        {
            if (snapshot.Count == 0) { Logger.LogAction("TODOS", "Skipping save — empty snapshot"); return; }
            string json = preSerializedJson;

            // TM-1 FIX: _fileLock ensures concurrent file writes don't race on .tmp/.bak files
            lock (_fileLock)
            {
                try
                {
                    if (!Directory.Exists(_appDataDir))
                        Directory.CreateDirectory(_appDataDir);

                    // Create backup before saving
                    if (File.Exists(_todosPath))
                    {
                        try { File.Copy(_todosPath, _todosPath + ".bak", overwrite: true); } catch { } // Best-effort: failure is acceptable
                    }

                    string tmpPath = _todosPath + ".tmp";
                    if (!DiskSpaceHelper.HasSufficientDiskSpace(_todosPath, json.Length * 2 + 1_000_000))
                    {
                        Logger.LogAction("TODOS", "Insufficient disk space to save todos — skipping write");
                        try { System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Cannot save — disk is full. Free up space to prevent data loss.")); } catch { }
                        return;
                    }
                    File.WriteAllText(tmpPath, json);
                    // AUDIT FIX: Retry loop for File.Move to handle transient file locks (AV, indexers)
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            File.Move(tmpPath, _todosPath, overwrite: true);
                            break;
                        }
                        catch (IOException) when (attempt < 2)
                        {
                            Thread.Sleep(50);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TODOS", $"Failed to write todos to disk: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SEARCH
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Search all todos for a query string. Returns matching items with their parent day.
        /// </summary>
        public static List<(TodoDay Day, TodoItem Item)> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();
            string q = query.Trim().ToLowerInvariant();

            var results = new List<(TodoDay, TodoItem)>();
            List<TodoDay> snapshot;
            lock (_lock) { snapshot = _days.ToList(); }
            foreach (var day in snapshot)
            {
                // Snapshot items too — they could be modified concurrently
                var items = day.Items.ToList();
                foreach (var item in items)
                {
                    bool matchText = !string.IsNullOrEmpty(item.Text) && item.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
                    bool matchDesc = !string.IsNullOrEmpty(item.Description) && item.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
                    bool matchTags = item.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
                    if (matchText || matchDesc || matchTags)
                    {
                        results.Add((day, item));
                    }
                    // Also search subtasks
                    foreach (var sub in item.SubTasks.ToList())
                    {
                        if (!string.IsNullOrEmpty(sub.Text) && sub.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add((day, sub));
                        }
                    }
                }
            }
            return results;
        }

        // ═══════════════════════════════════════════════════════════
        // SORT
        // ═══════════════════════════════════════════════════════════

        /// <summary>Sort items within a day by the given sort mode.</summary>
        public static void SortItems(TodoDay day, TodoSortMode mode)
        {
            if (day == null || day.Items.Count <= 1) return;

            // PM-8 FIX: Wrap clear+add in lock to prevent concurrent mutation
            lock (_lock)
            {
                List<TodoItem> sorted = mode switch
                {
                    TodoSortMode.Priority => day.Items.OrderByDescending(i => i.Priority).ThenBy(i => i.SortOrder).ToList(),
                    TodoSortMode.DueDate => day.Items.OrderBy(i => i.DueDate ?? DateTime.MaxValue).ThenBy(i => i.SortOrder).ToList(),
                    TodoSortMode.Alphabetical => day.Items.OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase).ToList(),
                    TodoSortMode.CreatedAt => day.Items.OrderBy(i => i.CreatedAt).ToList(),
                    _ => day.Items.OrderBy(i => i.SortOrder).ToList()
                };

                day.Items.Clear();
                foreach (var item in sorted) day.Items.Add(item);
                // Update sort order indices
                for (int i = 0; i < day.Items.Count; i++) day.Items[i].SortOrder = i;
            }
            ScheduleSave();
        }

        // ═══════════════════════════════════════════════════════════
        // AUTO-MIGRATE INCOMPLETE TASKS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Migrate incomplete (not done) tasks from yesterday to today.
        /// Returns the number of tasks migrated, or 0 if none.
        /// </summary>
        public static int MigrateIncompleteTasks()
        {
            lock (_lock)
            {
                var today = EnsureToday();
                int migrated = 0;

                var pastDays = _days.Where(d => d.Date.Date < DateTime.Today).ToList();

                foreach (var pastDay in pastDays)
                {
                    var incomplete = pastDay.Items.Where(i => !i.IsDone && !string.IsNullOrWhiteSpace(i.Text)).ToList();
                    if (incomplete.Count == 0) continue;

                    foreach (var item in incomplete)
                    {
                        // Don't migrate if an identical task already exists today
                        if (today.Items.Any(t => t.Text == item.Text)) continue;

                        var newItem = new TodoItem
                        {
                            Text = item.Text,
                            Priority = item.Priority,
                            DueDate = item.DueDate,
                            Tags = new List<string>(item.Tags),
                            Color = item.Color,
                            Description = item.Description,
                            Recurrence = item.Recurrence,
                            CreatedAt = DateTime.Now
                        };

                        if (item.SubTasks?.Count > 0)
                        {
                            foreach (var sub in item.SubTasks)
                            {
                                newItem.SubTasks.Add(new TodoItem { Text = sub.Text, IsDone = sub.IsDone });
                            }
                        }

                        today.Items.Add(newItem);
                        migrated++;
                    }
                }

                if (migrated > 0) ScheduleSave();
                return migrated;
            }
        }

        /// <summary>Permanently deletes an item from the given day.</summary>
        public static void DeleteItem(TodoDay day, TodoItem item)
        {
            // PM-7 FIX: Wrap mutation in lock to prevent concurrent access
            lock (_lock)
            {
                day.Items.Remove(item);
                RecordTombstone(day, item);
            }
            ScheduleSave();
        }

        // ═══════════════════════════════════════════════════════════
        // MOBILE SYNC
        // ═══════════════════════════════════════════════════════════

        public static string GetSyncPayload()
        {
            List<TodoDay> snapshot;
            lock (_lock)
            {
                try { snapshot = _days.ToList(); } catch (Exception ex) { Logger.LogAction("SYNC", $"GetSyncPayload error: {ex.Message}"); return "[]"; }
            }

            var payload = snapshot.Select(day => {
                long lastMod = 0;
                foreach (var item in day.Items)
                {
                    long iTs = new DateTimeOffset(item.LastEdited).ToUnixTimeMilliseconds();
                    if (iTs > lastMod) lastMod = iTs;
                }
                // T-1 fix: day.LastModified is bumped on deletions/merges - include it
                // so peers see deletion knowledge even when no item edit is newer.
                if (day.LastModified.HasValue && day.LastModified.Value > lastMod) lastMod = day.LastModified.Value;
                if (lastMod == 0) lastMod = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();

                return new {
                    Date = day.Date.ToString("o", CultureInfo.InvariantCulture),
                    Items = day.Items.Select(i => new {
                        i.Id, i.Text, i.IsDone,
                        CreatedAt = i.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                        LastEdited = i.LastEdited.ToString("o", CultureInfo.InvariantCulture),
                        Priority = (int)i.Priority,
                        DueDate = i.DueDate?.ToString("o", CultureInfo.InvariantCulture),
                        i.Tags, i.Color, i.Description,
                        SubTasks = i.SubTasks.Select(s => new {
                            s.Id, s.Text, s.IsDone,
                            CreatedAt = s.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                            LastEdited = s.LastEdited.ToString("o", CultureInfo.InvariantCulture),
                            Priority = (int)s.Priority,
                            DueDate = s.DueDate?.ToString("o", CultureInfo.InvariantCulture),
                            s.Tags, s.Color, s.Description,
                            SubTasks = new List<object>(),
                            Recurrence = (int)s.Recurrence,
                            s.SortOrder, s.TimerMinutes, s.ReminderAt
                        }).ToList(),
                        Recurrence = (int)i.Recurrence,
                        i.SortOrder, i.TimerMinutes,
                        i.CreatedByDevice, i.LastEditedByDevice,
                        ReminderAt = i.ReminderAt?.ToString("o", CultureInfo.InvariantCulture)
                    }).ToList(),
                    DeletedItems = day.DeletedItems ?? new List<TodoTombstone>(),
                    LastModified = lastMod
                };
            }).ToList();

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        }

        public static void MergeFromMobile(string json, string? deviceName = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var remoteDays = JsonSerializer.Deserialize<List<TodoDay>>(json, new JsonSerializerOptions
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
                        long remoteMod = remoteDay.LastModified ?? 0;

                        // Update _allDays (backing store) — mirrors NoteManager pattern
                        var localAllDay = _allDays.FirstOrDefault(d => d.Date.Date == remoteDay.Date.Date);
                        if (localAllDay == null)
                        {
                            _allDays.Add(remoteDay);
                            // Tag new day's items with device origin
                            if (!string.IsNullOrEmpty(deviceName))
                            {
                                foreach (var item in remoteDay.Items)
                                {
                                    item.LastEditedByDevice = deviceName;
                                    if (string.IsNullOrEmpty(item.CreatedByDevice))
                                        item.CreatedByDevice = deviceName;
                                }
                            }
                            changed = true;
                        }
                        else
                        {
                            // T-1 fix: per-item merge with deletion tombstones.
                            // Previously the entire day's Items collection was replaced when
                            // the remote day was newer - concurrent edits to OTHER tasks were
                            // silently lost. Items now merge by Id (newer LastEdited wins) and
                            // deletions propagate via day.DeletedItems tombstones. An item
                            // edited AFTER its deletion timestamp is revived (newer wins).
                            bool dayChanged = false;

                            var tombs = new Dictionary<string, long>(StringComparer.Ordinal);
                            foreach (var t in localAllDay.DeletedItems ?? new List<TodoTombstone>())
                                if (!string.IsNullOrEmpty(t.Id)) tombs[t.Id] = Math.Max(tombs.TryGetValue(t.Id, out var lv) ? lv : 0, t.DeletedAt);
                            foreach (var t in remoteDay.DeletedItems ?? new List<TodoTombstone>())
                                if (!string.IsNullOrEmpty(t.Id)) tombs[t.Id] = Math.Max(tombs.TryGetValue(t.Id, out var rv) ? rv : 0, t.DeletedAt);

                            var itemsById = new Dictionary<string, TodoItem>(StringComparer.Ordinal);
                            foreach (var li in localAllDay.Items)
                                if (!string.IsNullOrEmpty(li.Id)) itemsById[li.Id] = li;
                            foreach (var ri in remoteDay.Items)
                            {
                                if (string.IsNullOrEmpty(ri.Id)) continue;
                                bool exists = itemsById.TryGetValue(ri.Id, out var ex);
                                if (!exists || ri.LastEdited > ex!.LastEdited)
                                {
                                    if (!string.IsNullOrEmpty(deviceName))
                                    {
                                        ri.LastEditedByDevice = deviceName;
                                        if (string.IsNullOrEmpty(ri.CreatedByDevice))
                                            ri.CreatedByDevice = exists ? ex!.CreatedByDevice : deviceName;
                                    }
                                    itemsById[ri.Id] = ri;
                                    dayChanged = true;
                                }
                            }

                            var mergedItems = new List<TodoItem>();
                            foreach (var it in itemsById.Values)
                            {
                                long editedMs = new DateTimeOffset(it.LastEdited).ToUnixTimeMilliseconds();
                                if (tombs.TryGetValue(it.Id, out long deletedAt) && deletedAt >= editedMs)
                                {
                                    dayChanged = true; // Deleted on some device, not edited since
                                    continue;
                                }
                                if (tombs.Remove(it.Id)) dayChanged = true; // Edited after delete - revived
                                mergedItems.Add(it);
                            }

                            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            localAllDay.DeletedItems = tombs
                                .Where(kv => nowMs - kv.Value < 30L * 24 * 60 * 60 * 1000) // 30-day TTL
                                .Select(kv => new TodoTombstone { Id = kv.Key, DeletedAt = kv.Value })
                                .ToList();

                            if (dayChanged)
                            {
                                localAllDay.Items = new System.Collections.ObjectModel.ObservableCollection<TodoItem>(
                                    mergedItems.OrderBy(i => i.SortOrder).ToList());
                                changed = true;
                            }
                            localAllDay.LastModified = Math.Max(localAllDay.LastModified ?? 0, remoteMod);
                        }
                    }
                    if (changed)
                    {
                        _allDays = _allDays.OrderByDescending(d => d.Date).ToList();
                        // Rebuild _days from _allDays
                        _days.Clear();
                        foreach (var d in _allDays) _days.Add(d);
                    }
                }
                if (changed) ScheduleSave();
                Logger.LogAction("TODOS_SYNC", $"Merged {remoteDays.Count} days from mobile (changed={changed})");
            }
            catch (Exception ex)
            {
                Logger.LogAction("TODOS_SYNC", $"MergeFromMobile failed: {ex.Message}");
            }
        }
    }
}
