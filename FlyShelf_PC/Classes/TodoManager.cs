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

    public class TodoItem : INotifyPropertyChanged
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

        public DateTime CreatedAt { get; set; } = DateTime.Now;

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
                if (d.Year == DateTime.Today.Year) return d.ToString("MMM d");
                return d.ToString("MMM d, yyyy");
            }
        }
        [JsonIgnore] public bool IsOverdue => _dueDate.HasValue && _dueDate.Value.Date < DateTime.Today && !_isDone;
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
                    if (!string.IsNullOrWhiteSpace(_description) && !_descriptionVisible)
                    {
                        _descriptionVisible = true;
                        OnPropertyChanged(nameof(IsDescriptionVisible));
                    }
                }
            }
        }

        [JsonIgnore] public bool HasDescription => !string.IsNullOrWhiteSpace(_description);

        // Controls whether description panel is expanded (separate from whether content exists)
        private bool _descriptionVisible = false;
        [JsonIgnore] public bool IsDescriptionVisible
        {
            get => _descriptionVisible;
            set { if (_descriptionVisible != value) { _descriptionVisible = value; OnPropertyChanged(nameof(IsDescriptionVisible)); } }
        }

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
            TodoRecurrence.Daily => "🔄 Daily",
            TodoRecurrence.Weekly => "🔄 Weekly",
            TodoRecurrence.Monthly => "🔄 Monthly",
            _ => ""
        };

        // ── Sort Order (for manual drag-reorder) ────────────────
        private int _sortOrder;
        public int SortOrder
        {
            get => _sortOrder;
            set { if (_sortOrder != value) { _sortOrder = value; OnPropertyChanged(nameof(SortOrder)); } }
        }

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
        public string ReminderDisplay => HasReminder ? _reminderAt!.Value.ToString("h:mm tt") : "";

        [JsonIgnore]
        public string CreatedAtDisplay => CreatedAt.ToString("h:mm tt");

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
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

    public class TodoDay : INotifyPropertyChanged
    {
        public DateTime Date { get; set; } = DateTime.Today;

        [JsonIgnore]
        public string DisplayDate => Date.ToString("dd, MMM");

        [JsonIgnore]
        public string DayNumber => Date.Day.ToString();

        [JsonIgnore]
        public string MonthName => Date.ToString("MMM");

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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class TodoManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _todosPath = Path.Combine(_appDataDir, "todos.json");

        private static ObservableCollection<TodoDay> _days = new();
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static int _isDirty = 0;

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
                        return;
                    }

                    string json = File.ReadAllText(_todosPath);
                    var loaded = JsonSerializer.Deserialize<List<TodoDay>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null)
                    {
                        var sorted = loaded.OrderByDescending(d => d.Date).ToList();
                        _days = new ObservableCollection<TodoDay>(sorted);
                    }
                    else
                    {
                        _days = new ObservableCollection<TodoDay>();
                    }
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
                            string bakJson = File.ReadAllText(bakPath);
                            var bakLoaded = JsonSerializer.Deserialize<List<TodoDay>>(bakJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (bakLoaded != null)
                            {
                                var sorted = bakLoaded.OrderByDescending(d => d.Date).ToList();
                                _days = new ObservableCollection<TodoDay>(sorted);
                                return;
                            }
                        }
                    }
                    catch (Exception bakEx)
                    {
                        Logger.LogAction("TODOS", $"Backup recovery also failed: {bakEx.Message}");
                    }
                    _days = new ObservableCollection<TodoDay>();
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
            int maxItems = LicenseManager.GetTodoDailyLimit();
            if (maxItems < int.MaxValue && day.Items.Count >= maxItems)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    UpgradePrompt.ShowTodoLimit());
                return null;
            }

            var item = new TodoItem { Text = text, CreatedAt = DateTime.Now };
            day.Items.Add(item);
            ScheduleSave();
            return item;
        }

        public static TodoItem? InsertItem(TodoDay day, int index, string text = "")
        {
            int maxItems = LicenseManager.GetTodoDailyLimit();
            if (maxItems < int.MaxValue && day.Items.Count >= maxItems)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    UpgradePrompt.ShowTodoLimit());
                return null;
            }

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
            ScheduleSave();
        }

        public static void MarkDirty()
        {
            Interlocked.Exchange(ref _isDirty, 1);
            ScheduleSave();
        }

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

        public static void SaveNow()
        {
            List<TodoDay> snapshot = null;
            try
            {
                // Must read ObservableCollection on UI thread if it was created there
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher?.CheckAccess() == false)
                {
                    // Async snapshot — don't block the timer thread
                    app.Dispatcher.InvokeAsync(() =>
                    {
                        List<TodoDay> snap;
                        try { snap = _days.ToList(); } catch { return; }
                        Task.Run(() => SaveSnapshot(snap));
                    });
                    return;
                }
                else
                {
                    try { snapshot = _days.ToList(); } catch { return; }
                }
            }
            catch
            {
                try { snapshot = _days.ToList(); } catch { return; }
            }
            if (snapshot == null) return;

            // Run serialization and file IO on a background thread so it doesn't block the UI thread
            Task.Run(() => SaveSnapshot(snapshot));
        }

        private static void SaveSnapshot(List<TodoDay> snapshot)
        {
            string json;
            lock (_lock)
            {
                try
                {
                    json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TODOS", $"Failed to serialize todos: {ex.Message}");
                    return;
                }
            }

            try
            {
                if (!Directory.Exists(_appDataDir))
                    Directory.CreateDirectory(_appDataDir);

                // Create backup before saving
                try { if (File.Exists(_todosPath)) File.Copy(_todosPath, _todosPath + ".bak", overwrite: true); } catch { }

                string tmpPath = _todosPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _todosPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("TODOS", $"Failed to write todos to disk: {ex.Message}");
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
            foreach (var day in _days)
            {
                foreach (var item in day.Items)
                {
                    bool matchText = !string.IsNullOrEmpty(item.Text) && item.Text.ToLowerInvariant().Contains(q);
                    bool matchDesc = !string.IsNullOrEmpty(item.Description) && item.Description.ToLowerInvariant().Contains(q);
                    bool matchTags = item.Tags.Any(t => t.ToLowerInvariant().Contains(q));
                    if (matchText || matchDesc || matchTags)
                    {
                        results.Add((day, item));
                    }
                    // Also search subtasks
                    foreach (var sub in item.SubTasks)
                    {
                        if (!string.IsNullOrEmpty(sub.Text) && sub.Text.ToLowerInvariant().Contains(q))
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
                var yesterday = DateTime.Today.AddDays(-1);
                var yesterdayDay = _days.FirstOrDefault(d => d.Date.Date == yesterday);
                if (yesterdayDay == null) return 0;

                var incomplete = yesterdayDay.Items.Where(i => !i.IsDone && !string.IsNullOrWhiteSpace(i.Text)).ToList();
                if (incomplete.Count == 0) return 0;

                var today = EnsureToday();
                int migrated = 0;

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
                    today.Items.Add(newItem);
                    migrated++;
                }

                if (migrated > 0) ScheduleSave();
                return migrated;
            }
        }

        /// <summary>Permanently deletes an item from the given day.</summary>
        public static void DeleteItem(TodoDay day, TodoItem item)
        {
            day.Items.Remove(item);
            ScheduleSave();
        }
    }
}
