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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
        private static volatile bool _isDirty;

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

        public static void RemoveItem(TodoDay day, TodoItem item)
        {
            day.Items.Remove(item);
            ScheduleSave();
        }

        public static void MarkDirty() => ScheduleSave();

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

        public static void SaveNow()
        {
            List<TodoDay> snapshot;
            lock (_lock)
            {
                // Snapshot under the same _lock — no nested locking needed
                snapshot = _days.ToList();
            }

            // Run serialization and file IO on a background thread so it doesn't block the UI thread
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        if (!Directory.Exists(_appDataDir))
                            Directory.CreateDirectory(_appDataDir);

                        // Create backup before saving
                        try { if (File.Exists(_todosPath)) File.Copy(_todosPath, _todosPath + ".bak", overwrite: true); } catch { }

                        string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                        {
                            WriteIndented = false,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                        });

                        string tmpPath = _todosPath + ".tmp";
                        File.WriteAllText(tmpPath, json);
                        File.Move(tmpPath, _todosPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("TODOS", $"Failed to save todos: {ex.Message}");
                    }
                }
            });
        }
    }
}
