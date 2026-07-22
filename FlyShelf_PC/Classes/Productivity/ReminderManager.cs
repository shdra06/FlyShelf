using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Classes
{
    public enum RepeatMode
    {
        None,
        Daily,
        Weekly,
        Monthly
    }

    public partial class ReminderItem : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [ObservableProperty]
        private string _title = "";

        [ObservableProperty]
        private string _notes = "";

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

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ObservableProperty]
        private string _category = "";

        private RepeatMode _repeat = RepeatMode.None;
        public RepeatMode Repeat
        {
            get => _repeat;
            set { if (_repeat != value) { _repeat = value; OnPropertyChanged(nameof(Repeat)); OnPropertyChanged(nameof(RepeatDisplay)); } }
        }

        public DateTime? LastFiredAt { get; set; }

        [JsonIgnore]
        public string DueAtDisplay => DueAt.ToLocalTime().ToString("MMM dd, yyyy • h:mm tt", CultureInfo.InvariantCulture);

        [JsonIgnore]
        public string TimeDisplay => DueAt.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

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
    }

    public static class ReminderManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _remindersPath = Path.Combine(_appDataDir, "reminders.json");

        private static ObservableCollection<ReminderItem> _reminders = new();
        private static Timer? _saveTimer;
        private static readonly object _lock = new();
        private static volatile int _isDirty = 0; // RM-1 FIX: int for Interlocked atomic ops (0=clean, 1=dirty)
        private static bool _isLoaded = false;

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
                        _isLoaded = true;
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
                        _isLoaded = true;
                    }
                    else
                    {
                        _reminders = new ObservableCollection<ReminderItem>();
                        _isLoaded = true;
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
                                _isLoaded = true;
                                return;
                            }
                        }
                    }
                    catch (Exception bakEx)
                    {
                        Logger.LogAction("REMINDERS", $"Backup recovery also failed: {bakEx.Message}");
                    }
                    _reminders = new ObservableCollection<ReminderItem>();
                    _isLoaded = true;
                }
            }
        }

        private const int MAX_REMINDERS = 500;

        public static ReminderItem AddReminder(string title, string notes, DateTime dueAtUtc, string category, RepeatMode repeat)
        {
            var item = new ReminderItem
            {
                Title = title,
                Notes = notes,
                DueAt = dueAtUtc,
                Category = category,
                Repeat = repeat,
                CreatedAt = DateTime.UtcNow
            };

            lock (_lock)
            {
                // Enforce hard cap — evict oldest completed reminders first
                if (_reminders.Count >= MAX_REMINDERS)
                {
                    var completedOldest = _reminders
                        .Where(r => r.IsDone)
                        .OrderBy(r => r.CreatedAt)
                        .ToList();

                    if (completedOldest.Count > 0)
                    {
                        // Remove enough completed reminders to make room
                        int toRemove = Math.Min(completedOldest.Count, _reminders.Count - MAX_REMINDERS + 1);
                        for (int i = 0; i < toRemove; i++)
                            _reminders.Remove(completedOldest[i]);

                        Logger.LogAction("REMINDERS", $"Evicted {toRemove} oldest completed reminders to stay under {MAX_REMINDERS} cap.");
                    }

                    // If still at cap (all active), reject the add
                    if (_reminders.Count >= MAX_REMINDERS)
                    {
                        Logger.LogAction("REMINDERS", $"Cannot add reminder — hard cap of {MAX_REMINDERS} reached with no completed reminders to evict.");
                        return null!;
                    }
                }

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

            // Safety guard: if mode is None, next == current, so the while loop below would be infinite
            if (mode == RepeatMode.None) return next;

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
                var cutoff = DateTime.UtcNow.AddDays(-30);
                var old = _reminders.Where(r => r.IsDone && r.CreatedAt < cutoff).ToList();
                foreach (var item in old)
                    _reminders.Remove(item);
            }

            if (_reminders.Count > 0)
                ScheduleSave();
        }

        public static void ScheduleSave()
        {
            Interlocked.Exchange(ref _isDirty, 1); // RM-1 FIX: atomic set
            lock (_lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ =>
                {
                    if (Interlocked.CompareExchange(ref _isDirty, 0, 1) == 0) return; // already clean
                    SaveNow();
                }, null, 2000, Timeout.Infinite);
            }
        }

        public static void SaveNow()
        {
            if (!_isLoaded) return;
            List<ReminderItem> snapshot;
            lock (_lock)
            {
                snapshot = _reminders.ToList();
            }

            // Run serialization and file IO on a background thread so it doesn't block the UI thread
            System.Threading.Tasks.Task.Run(() =>
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
                        Logger.LogAction("REMINDERS", $"Failed to serialize reminders: {ex.Message}");
                        return;
                    }
                }

                try
                {
                    if (!Directory.Exists(_appDataDir))
                        Directory.CreateDirectory(_appDataDir);

                    // Create backup before saving
                    try { if (File.Exists(_remindersPath)) File.Copy(_remindersPath, _remindersPath + ".bak", overwrite: true); } catch { } // Best-effort: failure is acceptable

                    string tmpPath = _remindersPath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _remindersPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("REMINDERS", $"Failed to write reminders to disk: {ex.Message}");
                }
            });
        }
    }
}
