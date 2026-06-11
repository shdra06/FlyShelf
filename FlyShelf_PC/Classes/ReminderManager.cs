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
    public enum RepeatMode
    {
        None,
        Daily,
        Weekly,
        Monthly
    }

    public class ReminderItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        private string _title = "";
        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(nameof(Title)); } }
        }

        private string _notes = "";
        public string Notes
        {
            get => _notes;
            set { if (_notes != value) { _notes = value; OnPropertyChanged(nameof(Notes)); } }
        }

        private DateTime _dueAt;
        public DateTime DueAt
        {
            get => _dueAt;
            set { if (_dueAt != value) { _dueAt = value; OnPropertyChanged(nameof(DueAt)); OnPropertyChanged(nameof(DueAtDisplay)); OnPropertyChanged(nameof(TimeDisplay)); OnPropertyChanged(nameof(IsOverdue)); } }
        }

        private bool _isDone;
        public bool IsDone
        {
            get => _isDone;
            set { if (_isDone != value) { _isDone = value; OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(IsOverdue)); } }
        }

        private bool _isSnoozed;
        public bool IsSnoozed
        {
            get => _isSnoozed;
            set { if (_isSnoozed != value) { _isSnoozed = value; OnPropertyChanged(nameof(IsSnoozed)); OnPropertyChanged(nameof(IsOverdue)); } }
        }

        private DateTime? _snoozedUntil;
        public DateTime? SnoozedUntil
        {
            get => _snoozedUntil;
            set { if (_snoozedUntil != value) { _snoozedUntil = value; OnPropertyChanged(nameof(SnoozedUntil)); OnPropertyChanged(nameof(IsOverdue)); } }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        private string _category = "";
        public string Category
        {
            get => _category;
            set { if (_category != value) { _category = value; OnPropertyChanged(nameof(Category)); } }
        }

        private RepeatMode _repeat = RepeatMode.None;
        public RepeatMode Repeat
        {
            get => _repeat;
            set { if (_repeat != value) { _repeat = value; OnPropertyChanged(nameof(Repeat)); OnPropertyChanged(nameof(RepeatDisplay)); } }
        }

        public DateTime? LastFiredAt { get; set; }

        [JsonIgnore]
        public string DueAtDisplay => DueAt.ToLocalTime().ToString("MMM dd, yyyy • h:mm tt");

        [JsonIgnore]
        public string TimeDisplay => DueAt.ToLocalTime().ToString("h:mm tt");

        [JsonIgnore]
        public bool IsOverdue => !IsDone && DueAt <= DateTime.UtcNow && (!IsSnoozed || SnoozedUntil <= DateTime.UtcNow);

        [JsonIgnore]
        public string RepeatDisplay => Repeat switch
        {
            RepeatMode.Daily => "Daily",
            RepeatMode.Weekly => "Weekly",
            RepeatMode.Monthly => "Monthly",
            _ => ""
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class ReminderManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _remindersPath = Path.Combine(_appDataDir, "reminders.json");

        private static ObservableCollection<ReminderItem> _reminders = new();
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static volatile bool _isDirty;

        public static ObservableCollection<ReminderItem> Reminders
        {
            get { lock (_lock) { return _reminders; } }
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_remindersPath))
                    {
                        _reminders = new ObservableCollection<ReminderItem>();
                        return;
                    }

                    string json = File.ReadAllText(_remindersPath);
                    var loaded = JsonSerializer.Deserialize<List<ReminderItem>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null)
                    {
                        var sorted = loaded.OrderBy(r => r.DueAt).ToList();
                        _reminders = new ObservableCollection<ReminderItem>(sorted);
                    }
                    else
                    {
                        _reminders = new ObservableCollection<ReminderItem>();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("REMINDERS", $"Failed to load reminders: {ex.Message}");
                    // Fallback: try loading from .bak file
                    try
                    {
                        string bakPath = _remindersPath + ".bak";
                        if (File.Exists(bakPath))
                        {
                            Logger.LogAction("REMINDERS", "Attempting recovery from .bak file");
                            string bakJson = File.ReadAllText(bakPath);
                            var bakLoaded = JsonSerializer.Deserialize<List<ReminderItem>>(bakJson, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (bakLoaded != null)
                            {
                                var sorted = bakLoaded.OrderBy(r => r.DueAt).ToList();
                                _reminders = new ObservableCollection<ReminderItem>(sorted);
                                return;
                            }
                        }
                    }
                    catch (Exception bakEx)
                    {
                        Logger.LogAction("REMINDERS", $"Backup recovery also failed: {bakEx.Message}");
                    }
                    _reminders = new ObservableCollection<ReminderItem>();
                }
            }
        }

        public static ReminderItem AddReminder(string title, string notes, DateTime dueAtUtc, string category, RepeatMode repeat)
        {
            var item = new ReminderItem
            {
                Title = title,
                Notes = notes,
                DueAt = dueAtUtc,
                Category = category,
                Repeat = repeat,
                CreatedAt = DateTime.Now
            };

            lock (_lock)
            {
                _reminders.Add(item);
            }

            ScheduleSave();
            return item;
        }

        public static void DismissReminder(string id)
        {
            lock (_lock)
            {
                var item = _reminders.FirstOrDefault(r => r.Id == id);
                if (item == null) return;

                if (item.Repeat != RepeatMode.None)
                {
                    // Recurring: advance to next occurrence
                    item.DueAt = GetNextOccurrence(item.DueAt, item.Repeat);
                    item.IsSnoozed = false;
                    item.SnoozedUntil = null;
                    item.LastFiredAt = DateTime.UtcNow;
                }
                else
                {
                    item.IsDone = true;
                }
            }

            ScheduleSave();
        }

        public static void SnoozeReminder(string id, TimeSpan duration)
        {
            lock (_lock)
            {
                var item = _reminders.FirstOrDefault(r => r.Id == id);
                if (item == null) return;

                item.IsSnoozed = true;
                item.SnoozedUntil = DateTime.UtcNow + duration;
            }

            ScheduleSave();
        }

        public static void DeleteReminder(string id)
        {
            lock (_lock)
            {
                var item = _reminders.FirstOrDefault(r => r.Id == id);
                if (item != null)
                    _reminders.Remove(item);
            }

            ScheduleSave();
        }

        public static List<ReminderItem> GetDueReminders()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                return _reminders
                    .Where(r => r.DueAt <= now && !r.IsDone && (!r.IsSnoozed || r.SnoozedUntil <= now))
                    .ToList();
            }
        }

        public static DateTime GetNextOccurrence(DateTime current, RepeatMode mode)
        {
            var next = mode switch
            {
                RepeatMode.Daily => current.AddDays(1),
                RepeatMode.Weekly => current.AddDays(7),
                RepeatMode.Monthly => current.AddMonths(1),
                _ => current
            };

            // Skip past dates until future
            var now = DateTime.UtcNow;
            while (next <= now)
            {
                next = mode switch
                {
                    RepeatMode.Daily => next.AddDays(1),
                    RepeatMode.Weekly => next.AddDays(7),
                    RepeatMode.Monthly => next.AddMonths(1),
                    _ => next
                };
            }

            return next;
        }

        public static void CleanupOldReminders()
        {
            lock (_lock)
            {
                var cutoff = DateTime.Now.AddDays(-30);
                var old = _reminders.Where(r => r.IsDone && r.CreatedAt < cutoff).ToList();
                foreach (var item in old)
                    _reminders.Remove(item);
            }

            if (_reminders.Count > 0)
                ScheduleSave();
        }

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
            List<ReminderItem> snapshot;
            lock (_lock)
            {
                snapshot = _reminders.ToList();
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
                        try { if (File.Exists(_remindersPath)) File.Copy(_remindersPath, _remindersPath + ".bak", overwrite: true); } catch { }

                        string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                        {
                            WriteIndented = false,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                        });

                        string tmpPath = _remindersPath + ".tmp";
                        File.WriteAllText(tmpPath, json);
                        File.Move(tmpPath, _remindersPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("REMINDERS", $"Failed to save reminders: {ex.Message}");
                    }
                }
            });
        }
    }
}
