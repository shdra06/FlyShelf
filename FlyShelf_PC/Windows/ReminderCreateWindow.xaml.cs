using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class ReminderCreateWindow : Window
    {
        private RepeatMode _selectedRepeat = RepeatMode.None;
        private DateTime _selectedDate = DateTime.Today;
        private DateTime _selectedTime;

        public ReminderCreateWindow()
        {
            InitializeComponent();
            BuildTimeSlots();

            _selectedDate = DateTime.Today;
            SetTimeToNearestSlot();

            CalendarControl.SelectedDate = _selectedDate;
            UpdateDateDisplay();
            UpdateTimeDisplay();
        }

        /// <summary>
        /// Creates a reminder window pre-filled with title and default due date/time.
        /// Used by Notes and Todo panels for contextual reminder creation.
        /// </summary>
        public ReminderCreateWindow(string prefillTitle, DateTime defaultDue) : this()
        {
            // Pre-fill the title
            if (!string.IsNullOrEmpty(prefillTitle))
            {
                TitleInput.Text = prefillTitle;
            }

            // Set the default due date/time
            _selectedDate = defaultDue.Date;
            _selectedTime = defaultDue;
            CalendarControl.SelectedDate = _selectedDate;
            UpdateDateDisplay();
            UpdateTimeDisplay();
        }

        // ─── Initialization ───────────────────────────────────────

        private void BuildTimeSlots()
        {
            TimeSlotPanel.Children.Clear();
            var baseDate = DateTime.Today;

            for (int i = 0; i < 96; i++)
            {
                var slot = baseDate.AddMinutes(i * 15);
                string label = slot.ToString("h:mm tt");

                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 7, 12, 7),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    Tag = slot
                };

                var text = new TextBlock
                {
                    Text = label,
                    FontSize = 13,
                    Foreground = (Brush)FindResource("ThemeTextPrimary")
                };

                border.Child = text;
                border.MouseEnter += (s, e) => border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#18FFFFFF"));
                border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;
                border.MouseLeftButtonDown += TimeSlot_Click;

                TimeSlotPanel.Children.Add(border);
            }
        }

        private void SetTimeToNearestSlot()
        {
            var now = DateTime.Now;
            int minutes = now.Minute;
            int remainder = minutes % 15;
            var nextSlot = remainder == 0
                ? now.AddMinutes(15)
                : now.AddMinutes(15 - remainder);

            _selectedTime = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day,
                nextSlot.Hour, nextSlot.Minute, 0);

            if (nextSlot.Date > now.Date)
            {
                _selectedDate = nextSlot.Date;
                CalendarControl.SelectedDate = _selectedDate;
                UpdateDateDisplay();
            }
        }

        private void UpdateDateDisplay()
        {
            DateDisplay.Text = _selectedDate.ToString("MMM dd, yyyy");
        }

        private void UpdateTimeDisplay()
        {
            TimeDisplay.Text = _selectedTime.ToString("h:mm tt");
        }

        // ─── Window events ────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TitleInput.Focus();
            // Wire event AFTER initialization to prevent premature popup closure
            // when SelectedDate is set in the constructor
            CalendarControl.SelectedDatesChanged += Calendar_SelectedDatesChanged;
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ─── Date Picker ─────────────────────────────────────────

        private void DatePicker_Click(object sender, MouseButtonEventArgs e)
        {
            // Detach handler before opening to prevent spurious close
            CalendarControl.SelectedDatesChanged -= Calendar_SelectedDatesChanged;
            CalendarPopup.IsOpen = !CalendarPopup.IsOpen;
            TimePopup.IsOpen = false;

            if (CalendarPopup.IsOpen)
            {
                // Re-attach handler after WPF finishes its initialization events.
                // DispatcherPriority.Input ensures all layout/render events have settled.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
                {
                    CalendarControl.SelectedDatesChanged += Calendar_SelectedDatesChanged;
                });
            }
        }

        private void Calendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CalendarControl.SelectedDate.HasValue)
            {
                _selectedDate = CalendarControl.SelectedDate.Value;
                UpdateDateDisplay();
                CalendarPopup.IsOpen = false;
            }
        }

        // ─── Time Picker ─────────────────────────────────────────

        private void TimePicker_Click(object sender, MouseButtonEventArgs e)
        {
            TimePopup.IsOpen = !TimePopup.IsOpen;
            CalendarPopup.IsOpen = false;
            ScrollToSelectedTime();
        }

        private void TimeSlot_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is DateTime slot)
            {
                _selectedTime = slot;
                UpdateTimeDisplay();
                HighlightSelectedTimeSlot();
                TimePopup.IsOpen = false;
            }
        }

        private void ScrollToSelectedTime()
        {
            // Find and scroll to the matching time slot
            string target = _selectedTime.ToString("h:mm tt");
            for (int i = 0; i < TimeSlotPanel.Children.Count; i++)
            {
                if (TimeSlotPanel.Children[i] is Border b && b.Tag is DateTime slot)
                {
                    if (slot.ToString("h:mm tt") == target)
                    {
                        // Scroll the slot into view after layout
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                        {
                            b.BringIntoView();
                        });
                        break;
                    }
                }
            }
            HighlightSelectedTimeSlot();
        }

        private void HighlightSelectedTimeSlot()
        {
            string target = _selectedTime.ToString("h:mm tt");
            foreach (var child in TimeSlotPanel.Children)
            {
                if (child is Border b && b.Tag is DateTime slot)
                {
                    if (slot.ToString("h:mm tt") == target)
                    {
                        b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28F59E0B"));
                        if (b.Child is TextBlock tb)
                        {
                            tb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FDE68A"));
                            tb.FontWeight = FontWeights.SemiBold;
                        }
                    }
                    else
                    {
                        b.Background = Brushes.Transparent;
                        if (b.Child is TextBlock tb)
                        {
                            tb.Foreground = (Brush)FindResource("ThemeTextPrimary");
                            tb.FontWeight = FontWeights.Normal;
                        }
                    }
                }
            }
        }

        // ─── Repeat mode selection ───────────────────────────────

        private void Repeat_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border clicked || clicked.Tag is not string tag)
                return;

            _selectedRepeat = tag switch
            {
                "Daily" => RepeatMode.Daily,
                "Weekly" => RepeatMode.Weekly,
                "Monthly" => RepeatMode.Monthly,
                _ => RepeatMode.None
            };

            UpdateRepeatVisuals();
        }

        private void UpdateRepeatVisuals()
        {
            var pills = new[] { RepeatNone, RepeatDaily, RepeatWeekly, RepeatMonthly };
            var modes = new[] { RepeatMode.None, RepeatMode.Daily, RepeatMode.Weekly, RepeatMode.Monthly };

            for (int i = 0; i < pills.Length; i++)
            {
                if (modes[i] == _selectedRepeat)
                {
                    pills[i].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#30F59E0B"));
                    pills[i].BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#50F59E0B"));
                }
                else
                {
                    pills[i].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10FFFFFF"));
                    pills[i].BorderBrush = Brushes.Transparent;
                }
            }
        }

        // ─── Quick-set buttons ────────────────────────────────────

        private void QuickSet_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not string tag)
                return;

            DateTime target;

            if (tag == "tomorrow9")
            {
                target = DateTime.Today.AddDays(1).AddHours(9);
            }
            else if (int.TryParse(tag, out int minutes))
            {
                target = DateTime.Now.AddMinutes(minutes);
            }
            else
            {
                return;
            }

            _selectedDate = target.Date;
            _selectedTime = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day,
                target.Hour, target.Minute, 0);
            CalendarControl.SelectedDate = _selectedDate;
            UpdateDateDisplay();
            UpdateTimeDisplay();
        }

        // ─── Submit ───────────────────────────────────────────────

        private void Submit_Click(object sender, MouseButtonEventArgs e)
        {
            string title = TitleInput.Text?.Trim() ?? "";

            // Validate title
            if (string.IsNullOrEmpty(title))
            {
                FlashTitleError();
                return;
            }

            // Combine date + time
            var combinedLocal = new DateTime(
                _selectedDate.Year, _selectedDate.Month, _selectedDate.Day,
                _selectedTime.Hour, _selectedTime.Minute, 0,
                DateTimeKind.Local);

            var dueAtUtc = combinedLocal.ToUniversalTime();

            // Must be in the future
            if (dueAtUtc <= DateTime.UtcNow)
            {
                ToastWindow.ShowToast("Reminder must be in the future");
                return;
            }

            string notes = NotesInput.Text?.Trim() ?? "";

            // Add the reminder
            ReminderManager.AddReminder(title, notes, dueAtUtc, "", _selectedRepeat);

            Logger.LogAction("REMINDER", $"Created: \"{title}\" due {combinedLocal:MMM dd h:mm tt} repeat={_selectedRepeat}");
            ToastWindow.ShowToast("Reminder set! 🔔");
            Close();
        }

        private void FlashTitleError()
        {
            var original = TitleInput.BorderBrush;
            TitleInput.BorderBrush = new SolidColorBrush(Colors.Red);

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            timer.Tick += (s, e) =>
            {
                TitleInput.BorderBrush = original;
                timer.Stop();
            };
            timer.Start();
        }
    }
}
