using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class ReminderAlertWindow : Window
    {
        // ═══ Alert Stacking System ═══
        private static readonly List<ReminderAlertWindow> _activeAlerts = new();
        private static readonly object _alertLock = new();
        private const int ALERT_GAP = 10;

        private readonly ReminderItem _reminder;
        private System.Windows.Threading.DispatcherTimer? _autoDismissTimer;

        public ReminderAlertWindow(ReminderItem reminder)
        {
            InitializeComponent();
            _reminder = reminder;
        }

        // ═══ Window Loaded ═══
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateFields();
            PositionWindow();
            RunEntranceAnimation();
            StartAutoDismissTimer();
        }

        private void PopulateFields()
        {
            TitleText.Text = _reminder.Title;

            if (!string.IsNullOrWhiteSpace(_reminder.Notes))
            {
                NotesText.Text = _reminder.Notes;
                NotesText.Visibility = Visibility.Visible;
            }

            TimeText.Text = _reminder.DueAt.ToLocalTime().ToString("h:mm tt");
            DateText.Text = _reminder.DueAt.ToLocalTime().ToString("MMM dd, yyyy • h:mm tt");

            if (_reminder.Repeat != RepeatMode.None)
            {
                RepeatText.Text = _reminder.Repeat switch
                {
                    RepeatMode.Daily => "🔁 Daily",
                    RepeatMode.Weekly => "🔁 Weekly",
                    RepeatMode.Monthly => "🔁 Monthly",
                    _ => ""
                };
                RepeatText.Visibility = Visibility.Visible;
            }
        }

        // ═══ Positioning & Stacking ═══
        private void PositionWindow()
        {
            var workArea = SystemParameters.WorkArea;
            double baseBottom = workArea.Bottom - 80;

            lock (_alertLock)
            {
                double stackOffset = 0;
                foreach (var existing in _activeAlerts)
                {
                    if (existing != this)
                        stackOffset += existing.ActualHeight + ALERT_GAP;
                }

                this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double targetWidth = this.DesiredSize.Width > 0 ? this.DesiredSize.Width : 400;
                double targetHeight = this.DesiredSize.Height > 0 ? this.DesiredSize.Height : 200;

                this.Left = workArea.Left + (workArea.Width - targetWidth) / 2;
                this.Top = baseBottom - targetHeight - stackOffset;

                if (!_activeAlerts.Contains(this))
                    _activeAlerts.Add(this);
            }
        }

        // ═══ Entrance Animation ═══
        private void RunEntranceAnimation()
        {
            this.Opacity = 0;
            AlertTranslate.Y = 15;

            var sb = new Storyboard();

            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));

            var slideAnim = new DoubleAnimation(15.0, 0.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slideAnim, AlertBorder);
            Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

            sb.Children.Add(fadeAnim);
            sb.Children.Add(slideAnim);
            sb.Begin();
        }

        // ═══ Dismiss Animation ═══
        private void RunDismissAnimation(Action? onComplete = null)
        {
            _autoDismissTimer?.Stop();

            var sb = new Storyboard();

            var fadeAnim = new DoubleAnimation(this.Opacity, 0.0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeAnim, this);
            Storyboard.SetTargetProperty(fadeAnim, new PropertyPath(Window.OpacityProperty));

            var slideAnim = new DoubleAnimation(AlertTranslate.Y, 8.0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(slideAnim, AlertBorder);
            Storyboard.SetTargetProperty(slideAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

            sb.Children.Add(fadeAnim);
            sb.Children.Add(slideAnim);

            sb.Completed += (s, e) =>
            {
                lock (_alertLock)
                {
                    _activeAlerts.Remove(this);
                }
                onComplete?.Invoke();
                this.Close();
            };

            sb.Begin();
        }

        // ═══ Auto-Dismiss Timer ═══
        private void StartAutoDismissTimer()
        {
            _autoDismissTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _autoDismissTimer.Tick += (s, e) =>
            {
                _autoDismissTimer?.Stop();
                Logger.LogAction("REMINDER", $"Auto-snoozing (no interaction): {_reminder.Title}");
                SnoozeAndDismiss(TimeSpan.FromMinutes(5), "Auto-snoozed 5m ⏰");
            };
            _autoDismissTimer.Start();
        }

        private void ResetAutoDismissTimer()
        {
            _autoDismissTimer?.Stop();
            _autoDismissTimer?.Start();
        }

        // ═══ Button Actions ═══
        private void Snooze_Click(object sender, RoutedEventArgs e)
        {
            SnoozeAndDismiss(TimeSpan.FromMinutes(15), "Snoozed 15m ⏰");
        }

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReminderManager.DismissReminder(_reminder.Id);
                ReminderScheduler.ClearShownId(_reminder.Id);
                Logger.LogAction("REMINDER", $"Done: {_reminder.Title}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("REMINDER", $"Done error: {ex.Message}");
            }

            RunDismissAnimation(() =>
            {
                ToastWindow.ShowToast("Reminder done! ✅");
            });
        }

        // ═══ Snooze Helpers ═══
        private void SnoozeAndDismiss(TimeSpan duration, string toastMessage)
        {
            try
            {
                ReminderManager.SnoozeReminder(_reminder.Id, duration);
                ReminderScheduler.ClearShownId(_reminder.Id);
                Logger.LogAction("REMINDER", $"Snoozed {duration.TotalMinutes}m: {_reminder.Title}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("REMINDER", $"Snooze error: {ex.Message}");
            }

            RunDismissAnimation(() =>
            {
                ToastWindow.ShowToast(toastMessage);
            });
        }

        private void SnoozeToDuration(TimeSpan duration, string label)
        {
            SnoozePopup.IsOpen = false;
            SnoozeAndDismiss(duration, $"Snoozed {label} ⏰");
        }

        // ═══ Snooze Dropdown ═══
        private void SnoozeDropdown_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SnoozePopup.IsOpen = !SnoozePopup.IsOpen;
            ResetAutoDismissTimer();
        }

        // ═══ Snooze Preset Clicks ═══
        private void Snooze5_Click(object sender, MouseButtonEventArgs e)
        {
            SnoozeToDuration(TimeSpan.FromMinutes(5), "5m");
        }

        private void Snooze15_Click(object sender, MouseButtonEventArgs e)
        {
            SnoozeToDuration(TimeSpan.FromMinutes(15), "15m");
        }

        private void Snooze30_Click(object sender, MouseButtonEventArgs e)
        {
            SnoozeToDuration(TimeSpan.FromMinutes(30), "30m");
        }

        private void Snooze60_Click(object sender, MouseButtonEventArgs e)
        {
            SnoozeToDuration(TimeSpan.FromHours(1), "1h");
        }

        private void Snooze180_Click(object sender, MouseButtonEventArgs e)
        {
            SnoozeToDuration(TimeSpan.FromHours(3), "3h");
        }

        private void SnoozeTomorrow_Click(object sender, MouseButtonEventArgs e)
        {
            SnoozePopup.IsOpen = false;

            // Calculate tomorrow 9 AM local → duration from now
            var tomorrow9am = DateTime.Today.AddDays(1).AddHours(9);
            var duration = tomorrow9am.ToUniversalTime() - DateTime.UtcNow;

            // Guard against negative duration (shouldn't happen, but be safe)
            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromHours(12);

            try
            {
                ReminderManager.SnoozeReminder(_reminder.Id, duration);
                ReminderScheduler.ClearShownId(_reminder.Id);
                Logger.LogAction("REMINDER", $"Snoozed to tomorrow 9 AM: {_reminder.Title}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("REMINDER", $"Snooze error: {ex.Message}");
            }

            RunDismissAnimation(() =>
            {
                ToastWindow.ShowToast("Snoozed to tomorrow 9 AM 🌅");
            });
        }

        // ═══ Snooze Popup Hover Effects ═══
        private void SnoozeOption_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
                border.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        }

        private void SnoozeOption_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border)
                border.Background = Brushes.Transparent;
        }

        // ═══ Drag Move ═══
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                ResetAutoDismissTimer();
                try { DragMove(); } catch { }
            }
        }

        // ═══ Cleanup on Close ═══
        protected override void OnClosed(EventArgs e)
        {
            _autoDismissTimer?.Stop();
            _autoDismissTimer = null;

            lock (_alertLock)
            {
                _activeAlerts.Remove(this);
            }

            base.OnClosed(e);
        }
    }
}
