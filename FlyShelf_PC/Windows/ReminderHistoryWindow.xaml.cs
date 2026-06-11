using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Classes;
using MicaWPF.Controls;

namespace FlyShelf.Windows
{
    public partial class ReminderHistoryWindow : MicaWindow
    {
        private string _currentFilter = "Upcoming";
        private readonly ObservableCollection<ReminderItem> _filteredReminders = new();

        public ReminderHistoryWindow()
        {
            InitializeComponent();
            NativeMethods.ApplyWindowBackdropAndBackground(this);

            RemindersList.ItemsSource = _filteredReminders;

            // Refresh when the source collection changes
            ReminderManager.Reminders.CollectionChanged += (s, e) => RefreshList();

            // Esc to close
            this.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };

            RefreshList();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int colorNone = NativeMethods.DWMWA_COLOR_DARK_GRAY;
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref colorNone, sizeof(int));
                }
            }
            catch { }
            NativeMethods.ApplyWindowBackdropAndBackground(this);
        }

        // ═══════════════════════════════════════════════════════
        // FILTERING
        // ═══════════════════════════════════════════════════════

        private void Filter_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string tag) return;
            _currentFilter = tag;
            UpdateFilterVisuals();
            RefreshList();
        }

        private void RefreshList()
        {
            _filteredReminders.Clear();

            List<ReminderItem> source;

            lock (ReminderManager.Reminders)
            {
                source = _currentFilter switch
                {
                    "Upcoming" => ReminderManager.Reminders
                        .Where(r => !r.IsDone)
                        .OrderBy(r => r.DueAt)
                        .ToList(),
                    "Done" => ReminderManager.Reminders
                        .Where(r => r.IsDone)
                        .OrderByDescending(r => r.DueAt)
                        .ToList(),
                    _ => ReminderManager.Reminders
                        .OrderByDescending(r => r.DueAt)
                        .ToList()
                };
            }

            foreach (var item in source)
                _filteredReminders.Add(item);

            // Update counts
            int activeCount;
            lock (ReminderManager.Reminders)
            {
                activeCount = ReminderManager.Reminders.Count(r => !r.IsDone);
            }
            ReminderCountLabel.Text = $"{activeCount} active";
            EmptyState.Visibility = _filteredReminders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFilterVisuals()
        {
            var tabs = new[] { FilterUpcoming, FilterDone, FilterAll };
            var tags = new[] { "Upcoming", "Done", "All" };

            var selectedBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#20F59E0B"));
            var selectedBorder = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#40F59E0B"));
            var normalBg = Brushes.Transparent;
            var normalBorder = Brushes.Transparent;

            for (int i = 0; i < tabs.Length; i++)
            {
                if (tags[i] == _currentFilter)
                {
                    tabs[i].Background = selectedBg;
                    tabs[i].BorderBrush = selectedBorder;
                }
                else
                {
                    tabs[i].Background = normalBg;
                    tabs[i].BorderBrush = normalBorder;
                }
            }
        }

        // ═══════════════════════════════════════════════════════
        // ACTIONS
        // ═══════════════════════════════════════════════════════

        private void NewReminder_Click(object sender, MouseButtonEventArgs e)
        {
            var createWin = new ReminderCreateWindow();
            createWin.Owner = this;
            createWin.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            createWin.Closed += (s, ev) => RefreshList();
            createWin.Show();
        }

        private void ToggleDone_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is ReminderItem reminder)
            {
                if (reminder.IsDone)
                {
                    // Undo done — reactivate
                    reminder.IsDone = false;
                    ReminderManager.ScheduleSave();
                    ReminderScheduler.ClearShownId(reminder.Id);
                    ToastWindow.ShowToast("Reminder reactivated 🔔");
                }
                else
                {
                    ReminderManager.DismissReminder(reminder.Id);
                    ToastWindow.ShowToast("Reminder done! ✅");
                }
                RefreshList();
            }
        }

        private void DeleteReminder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is ReminderItem reminder)
            {
                ReminderManager.DeleteReminder(reminder.Id);
                ToastWindow.ShowToast($"Deleted: {reminder.Title}");
                RefreshList();
            }
        }
    }
}
