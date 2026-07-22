// ---------------------------------------------------------------
// TodoPanelControl.Timers.cs — Timer & reminder functionality
// Handles: stopwatch launch, per-item timer launch with
// completion reminder, reminder creation from todo items,
// timer preset cycling (5/10/15/25/30/60m), and custom
// timer parsing (minutes or mm:ss format).
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlyShelf.Controls
{
    public partial class TodoPanelControl : UserControl
    {
        private void TodoStopwatch_Click(object sender, RoutedEventArgs e)
        {
            try { _activeTimerWindow?.Close(); } catch { } // Best-effort: failure is acceptable
            var tw = new FlyShelf.Windows.TimerWindow(null);
            WindowHelper.ShowInForeground(tw);
            _activeTimerWindow = tw;
        }

        private void TodoItemTimer_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.TodoItem item)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                // If the item already has a timer duration set, launch with that duration
                string context = item.HasTimer ? $"{item.TimerMinutes}m" : null;
                try { _activeTimerWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var tw = new FlyShelf.Windows.TimerWindow(context, item.Text);
                tw.TimerCompleted += (taskName) =>
                {
                    // When timer finishes, create an instant reminder notification
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            string reminderTitle = string.IsNullOrEmpty(taskName) ? "Timer finished!" : $"Timer done: {taskName}";
                            var reminder = Classes.ReminderManager.AddReminder(
                                reminderTitle, "", DateTime.UtcNow, "Timer", Classes.RepeatMode.None);
                            // Also fire an alert window immediately
                            var alertWindow = new FlyShelf.Windows.ReminderAlertWindow(reminder);
                            WindowHelper.ShowInForeground(alertWindow);
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("TODO_TIMER", $"Failed to create completion reminder: {ex.Message}");
                        }
                    });
                };
                WindowHelper.ShowInForeground(tw);
                _activeTimerWindow = tw;
                }));
            }
        }

        private void TodoItemReminder_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.TodoItem item)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                string title = !string.IsNullOrEmpty(item.Text) ? item.Text : "To-Do Reminder";
                DateTime defaultDue = DateTime.Today.AddDays(1).AddHours(9); // Tomorrow 9 AM

                try { _activeTodoReminderWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(title, defaultDue);
                WindowHelper.ShowInForeground(reminderWindow);
                _activeTodoReminderWindow = reminderWindow;
                }));
            }
        }

        private void TodoItemSetTimer_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.TodoItem item)
            {
                // Cycle through common timer presets: none → 5m → 10m → 15m → 25m → 30m → 60m → none
                int[] presets = { 5, 10, 15, 25, 30, 60 };
                if (!item.HasTimer)
                {
                    item.TimerMinutes = presets[0];
                }
                else
                {
                    int currentIndex = Array.IndexOf(presets, item.TimerMinutes ?? 0);
                    if (currentIndex >= 0 && currentIndex < presets.Length - 1)
                    {
                        item.TimerMinutes = presets[currentIndex + 1];
                    }
                    else
                    {
                        item.TimerMinutes = null; // Reset
                    }
                }
                item.LastEdited = DateTime.Now;
                Classes.TodoManager.MarkDirty();
            }
        }

        private void LaunchCustomTimer(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            string trimmed = input.Trim();

            // Support mm:ss format (e.g. "3:30")
            if (trimmed.Contains(':'))
            {
                try { _activeTimerWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var tw = new FlyShelf.Windows.TimerWindow(trimmed);
                WindowHelper.ShowInForeground(tw);
                _activeTimerWindow = tw;
                return;
            }

            // Try parse as number → treat as minutes
            if (int.TryParse(trimmed, out int mins) && mins > 0)
            {
                try { _activeTimerWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var tw = new FlyShelf.Windows.TimerWindow($"{mins}m");
                WindowHelper.ShowInForeground(tw);
                _activeTimerWindow = tw;
            }
            else
            {
                // Fallback: pass as-is and let TimerWindow.ParseContext handle it
                try { _activeTimerWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var tw = new FlyShelf.Windows.TimerWindow(trimmed);
                WindowHelper.ShowInForeground(tw);
                _activeTimerWindow = tw;
            }
        }
    }
}
