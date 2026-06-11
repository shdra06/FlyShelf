using System;
using System.Collections.Generic;
using System.Windows;

namespace FlyShelf.Classes
{
    public static class ReminderScheduler
    {
        private static System.Threading.Timer? _pollTimer;
        private static readonly HashSet<string> _shownIds = new();
        private static readonly object _lock = new();
        private static int _pollCount;

        public static void Start()
        {
            _pollTimer = new System.Threading.Timer(Poll, null, 5000, 15000);
            Logger.LogAction("REMINDER", "Scheduler started");
        }

        public static void Stop()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            Logger.LogAction("REMINDER", "Scheduler stopped");
        }

        private static void Poll(object? state)
        {
            try
            {
                var dueReminders = ReminderManager.GetDueReminders();

                foreach (var reminder in dueReminders)
                {
                    lock (_lock)
                    {
                        if (_shownIds.Contains(reminder.Id))
                            continue;

                        _shownIds.Add(reminder.Id);
                    }

                    Logger.LogAction("REMINDER", $"⏰ Firing: {reminder.Title} (due {reminder.DueAt.ToLocalTime():h:mm tt})");

                    Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        System.Media.SystemSounds.Exclamation.Play();
                        new FlyShelf.Windows.ReminderAlertWindow(reminder).Show();
                    });
                }

                _pollCount++;
                if (_pollCount % 100 == 0)
                {
                    ReminderManager.CleanupOldReminders();
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("REMINDER", $"Poll error: {ex.Message}");
            }
        }

        public static void ClearShownId(string id)
        {
            lock (_lock)
            {
                _shownIds.Remove(id);
            }
        }

        public static void MarkShown(string id)
        {
            lock (_lock)
            {
                _shownIds.Add(id);
            }
        }
    }
}
