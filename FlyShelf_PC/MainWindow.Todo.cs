using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isTodoActive = false;
        private bool _isTodoLoaded = false;
        private TodoDay? _selectedTodoDay = null;
        private TextBox? _lastFocusedTodoTextBox = null;
        private DateTime _lastTodoItemAddedTime = DateTime.MinValue;
        private bool _isTodoSidebarCollapsed = false;



        private void TodoToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isTodoActive)
                CloseTodoPanel();
            else
                OpenTodoPanel();
        }

        private void OpenTodoPanel()
        {
            // Close other modes
            if (_isNotesActive) CloseNotesPanel(immediate: true);
            if (_isSearchActive) CloseSearch(switchingPanel: true);
            if (_isFilterBarActive) ToggleFilterBar(false);
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            // Lazy-load todos data on first open
            if (!_isTodoLoaded)
            {
                TodoManager.Load();
                _isTodoLoaded = true;
            }

            // Ensure today exists and select it
            var today = TodoManager.EnsureToday();

            // Bind days list
            TodoDaySidebar.ItemsSource = TodoManager.Days;

            _isTodoActive = true;
            // NOTE: No auto-revert timer — Todo panel should never auto-hide

            // Update taskbar/alt-tab title
            Title = "To-Do";

            // Update window activation style dynamically so clicking it works
            UpdateWindowActivationStyle();

            // Force-activate and topmost-cycle to grab OS focus
            ActivateTodoWindow();

            // Hide clipboard, show todo panel
            ShelfListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            TodoPanel.Visibility = Visibility.Visible;

            // Swap todo button to clipboard icon (acts as "go back" button)
            TodoToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24 };
            TodoToggleBtn.ToolTip = "Back to Clipboard";

            UpdateToolbarButtonsVisibility();

            // Animate in
            var slideAnim = Classes.AnimationHelper.SlideIn(fromY: -12, durationMs: 200);
            var fadeAnim = Classes.AnimationHelper.FadeIn(durationMs: 200);
            if (TodoPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            TodoPanel.BeginAnimation(OpacityProperty, fadeAnim);

            SelectTodoDay(today);
        }

        private void ActivateTodoWindow()
        {
            this.Activate();
            if (!this.Topmost)
            {
                this.Topmost = true;
                this.Topmost = false;
            }
            this.Focus();
            SuppressDwmBorder();
        }

        private void TodoSidebarToggle_Click(object sender, MouseButtonEventArgs e)
        {
            _isTodoSidebarCollapsed = !_isTodoSidebarCollapsed;

            if (_isTodoSidebarCollapsed)
            {
                // Collapse: hide sidebar border and set column width to 0
                TodoSidebarBorder.Visibility = Visibility.Collapsed;
                TodoSidebarColumn.Width = new GridLength(0);
                TodoSidebarCollapseIcon.Text = "▸";
                TodoSidebarExpandBtn.Visibility = Visibility.Visible;
            }
            else
            {
                // Expand: show sidebar border and restore column width
                TodoSidebarExpandBtn.Visibility = Visibility.Collapsed;
                TodoSidebarBorder.Visibility = Visibility.Visible;
                TodoSidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, null); // Clear any leftover animation
                TodoSidebarBorder.Width = double.NaN;
                TodoSidebarColumn.Width = new GridLength(54);
                TodoSidebarCollapseIcon.Text = "◂";
            }
        }

        private void CloseTodoPanel(bool immediate = false)
        {
            if (!_isTodoActive) return;

            // Restore todo button icon and tooltip
            TodoToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.TaskListSquareLtr24 };
            TodoToggleBtn.ToolTip = "To-Do List";
            TodoToggleBtn.ClearValue(ForegroundProperty);

            _isTodoActive = false;
            Title = "FlyShelf";

            UpdateWindowActivationStyle();
            UpdateToolbarButtonsVisibility();

            if (immediate)
            {
                // Instant close — no animation (used when switching to another panel)
                TodoPanel.BeginAnimation(OpacityProperty, null);
                TodoPanel.Opacity = 0;
                TodoPanel.Visibility = Visibility.Collapsed;
                ShelfListView.Visibility = Visibility.Visible;
                // Let the XAML DataTrigger on DroppedItems.Count control visibility
                EmptyStatePanel.ClearValue(VisibilityProperty);
                return;
            }

            // Animate out
            var slideAnim = Classes.AnimationHelper.SlideOut(toY: -12, durationMs: 180);
            var fadeAnim = Classes.AnimationHelper.FadeOut(durationMs: 180);

            if (TodoPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            fadeAnim.Completed += (s, ev) =>
            {
                if (!_isTodoActive)
                {
                    TodoPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    // Let the XAML DataTrigger on DroppedItems.Count control visibility
                    EmptyStatePanel.ClearValue(VisibilityProperty);
                }
            };
            TodoPanel.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private void TodoBack_Click(object sender, MouseButtonEventArgs e)
        {
            CloseTodoPanel();
        }

        private void TodoPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Skip activation when click targets a popup-triggering element (templates button).
            // PreviewMouseDown (tunnel) fires before MouseLeftButtonDown; calling Activate()
            // during the tunnel phase immediately closes ContextMenus/Popups that are about to open.
            if (e.OriginalSource is DependencyObject source)
            {
                var parent = source;
                while (parent != null)
                {
                    if (parent is FrameworkElement fe && fe.Name == "TodoTemplatesBtn")
                        return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
            ActivateWindowWithoutStealingFocus();
        }

        // ═══════════════════════════════════════════════════════════
        // DAY SELECTION (SIDEBAR)
        // ═══════════════════════════════════════════════════════════

        private void SelectTodoDay(TodoDay day)
        {
            _selectedTodoDay = day;

            // Update sidebar selection highlight
            UpdateTodoDaySidebarSelection();

            // Bind content
            TodoListItemsControl.ItemsSource = day.Items;

            // Update day label in header
            TodoCurrentDayLabel.Text = day.DisplayDate;

            // Update progress label
            UpdateTodoProgress(day);

            // Auto-create a first item if empty, so the user can start typing immediately
            if (day.Items.Count == 0)
            {
                _lastTodoItemAddedTime = DateTime.MinValue; // Reset cooldown
                AddNewTodoItemAndFocus();
            }
            else
            {
                FocusTodoItem(day.Items.Last());
            }
        }

        private void TodoDayItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TodoDay day)
            {
                SelectTodoDay(day);
            }
        }

        private void UpdateTodoDaySidebarSelection()
        {
            if (TodoDaySidebar == null) return;

            for (int i = 0; i < TodoDaySidebar.Items.Count; i++)
            {
                var item = TodoDaySidebar.Items[i];
                var container = TodoDaySidebar.ItemContainerGenerator.ContainerFromItem(item);
                if (container is ContentPresenter cp)
                {
                    var mainBorder = FindVisualChild<Border>(cp, "TodoDayBorder");
                    if (mainBorder != null)
                    {
                        bool isSelected = item == _selectedTodoDay;
                        if (isSelected)
                        {
                            mainBorder.Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x8B, 0x5C, 0xF6));
                            mainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x8B, 0x5C, 0xF6));
                        }
                        else
                        {
                            mainBorder.Background = new SolidColorBrush(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF));
                            mainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
                        }
                    }
                }
            }
        }

        private void FocusTodoActiveTextBox()
        {
            if (_selectedTodoDay == null) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (_lastFocusedTodoTextBox != null && _lastFocusedTodoTextBox.IsLoaded && _lastFocusedTodoTextBox.IsVisible)
                {
                    _lastFocusedTodoTextBox.Focus();
                    Keyboard.Focus(_lastFocusedTodoTextBox);
                }
                else if (_selectedTodoDay.Items.Count > 0)
                {
                    var firstItem = _selectedTodoDay.Items.First();
                    var container = TodoListItemsControl.ItemContainerGenerator.ContainerFromItem(firstItem);
                    if (container is ContentPresenter cp)
                    {
                        var tb = FindVisualChild<TextBox>(cp, "TodoItemTextBox");
                        if (tb != null)
                        {
                            tb.Focus();
                            Keyboard.Focus(tb);
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void UpdateTodoProgress(TodoDay day)
        {
            if (day == null) return;
            int total = day.Items.Count;
            int done = day.Items.Count(i => i.IsDone);

            if (total == 0)
            {
                TodoProgressText.Text = "🌱 0%";
                return;
            }

            int pct = (int)Math.Round(100.0 * done / total);
            string emoji = pct switch
            {
                100 => "🔥",
                >= 75 => "⚡",
                >= 50 => "✨",
                >= 25 => "💪",
                _ => "🌱"
            };
            TodoProgressText.Text = $"{emoji} {pct}% · {done}/{total}";
        }

        // ═══════════════════════════════════════════════════════════
        // TODO CARD ACTIONS
        // ═══════════════════════════════════════════════════════════

        private void TodoAddBtn_Click(object sender, MouseButtonEventArgs e)
        {
            AddNewTodoItemAndFocus();
        }

        private void AddNewTodoItemAndFocus()
        {
            if (_selectedTodoDay == null) return;

            // Cooldown check for safety (e.g. rapid multiple clicks/enters)
            if ((DateTime.Now - _lastTodoItemAddedTime).TotalMilliseconds < 250)
            {
                return;
            }

            // If the last item is already empty, just focus it instead of adding a new one
            var lastItem = _selectedTodoDay.Items.LastOrDefault();
            if (lastItem != null && string.IsNullOrWhiteSpace(lastItem.Text))
            {
                FocusTodoItem(lastItem);
                return;
            }

            _lastTodoItemAddedTime = DateTime.Now;
            var newItem = TodoManager.AddItem(_selectedTodoDay);
            if (newItem == null) return;
            UpdateTodoProgress(_selectedTodoDay);

            FocusTodoItem(newItem);
        }

        private void FocusTodoItem(TodoItem item)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TodoListItemsControl.UpdateLayout(); // Force container generation!
                var container = TodoListItemsControl.ItemContainerGenerator.ContainerFromItem(item);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "TodoItemTextBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                        tb.CaretIndex = tb.Text.Length;
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void TodoItemCheck_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                item.IsDone = !item.IsDone;
                TodoManager.MarkDirty();

                if (_selectedTodoDay != null)
                {
                    UpdateTodoProgress(_selectedTodoDay);
                }
            }
        }

        private void TodoItemDelete_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item && _selectedTodoDay != null)
            {
                TodoManager.RemoveItem(_selectedTodoDay, item);
                UpdateTodoProgress(_selectedTodoDay);
            }
        }

        private void TodoItemDeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            // Context menu: walk up from MenuItem → ContextMenu → PlacementTarget (the TodoCard Border)
            if (sender is MenuItem mi
                && mi.Parent is ContextMenu cm
                && cm.PlacementTarget is FrameworkElement card
                && card.DataContext is TodoItem item
                && _selectedTodoDay != null)
            {
                TodoManager.RemoveItem(_selectedTodoDay, item);
                UpdateTodoProgress(_selectedTodoDay);
            }
        }

        private void TodoItemText_TextChanged(object sender, TextChangedEventArgs e)
        {
            TodoManager.MarkDirty();
        }

        private void TodoItemText_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _lastFocusedTodoTextBox = tb;
            }
        }

        private void TodoItemText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
            {
                if (e.Key == Key.Enter)
                {
                    // Shift+Enter → insert newline (multi-line support)
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        // Let the TextBox handle the newline insertion naturally
                        // (AcceptsReturn must be True on the TextBox for this to work)
                        return;
                    }

                    e.Handled = true;

                    // If key is repeating (held down), ignore it to prevent holding enter from spamming
                    if (e.IsRepeat)
                    {
                        return;
                    }

                    // If the current item is empty/whitespace, don't create a new one to prevent blank spam
                    if (string.IsNullOrWhiteSpace(tb.Text))
                    {
                        return;
                    }

                    // Enter key creates a new item below
                    AddNewTodoItemAndFocus();
                }
            }
        }

        private void TodoStopwatch_Click(object sender, RoutedEventArgs e)
        {
            var tw = new FlyShelf.Windows.TimerWindow(null);
            tw.Show();
        }

        private void TodoItemTimer_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.TodoItem item)
            {
                // If the item already has a timer duration set, launch with that duration
                string context = item.HasTimer ? $"{item.TimerMinutes}m" : null;
                var tw = new FlyShelf.Windows.TimerWindow(context, item.Text);
                tw.TimerCompleted += (taskName) =>
                {
                    // When timer finishes, create an instant reminder notification
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            string reminderTitle = string.IsNullOrEmpty(taskName) ? "Timer finished!" : $"Timer done: {taskName}";
                            var reminder = Classes.ReminderManager.AddReminder(
                                reminderTitle, "", DateTime.UtcNow, "Timer", Classes.RepeatMode.None);
                            // Also fire an alert window immediately
                            var alertWindow = new FlyShelf.Windows.ReminderAlertWindow(reminder);
                            alertWindow.Show();
                            alertWindow.Activate();
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("TODO_TIMER", $"Failed to create completion reminder: {ex.Message}");
                        }
                    });
                };
                tw.Show();
            }
        }

        private void TodoItemReminder_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.TodoItem item)
            {
                string title = !string.IsNullOrEmpty(item.Text) ? item.Text : "To-Do Reminder";
                DateTime defaultDue = DateTime.Today.AddDays(1).AddHours(9); // Tomorrow 9 AM

                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(title, defaultDue);
                reminderWindow.Show();
                reminderWindow.Activate();
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
                Classes.TodoManager.MarkDirty();
            }
        }

        private void LaunchCustomTimer(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            string trimmed = input.Trim();

            // Support mm:ss format (e.g. "3:30")
            if (trimmed.Contains(":"))
            {
                var tw = new FlyShelf.Windows.TimerWindow(trimmed);
                tw.Show();
                return;
            }

            // Try parse as number → treat as minutes
            if (int.TryParse(trimmed, out int mins) && mins > 0)
            {
                var tw = new FlyShelf.Windows.TimerWindow($"{mins}m");
                tw.Show();
            }
            else
            {
                // Fallback: pass as-is and let TimerWindow.ParseContext handle it
                var tw = new FlyShelf.Windows.TimerWindow(trimmed);
                tw.Show();
            }
        }

        private void TodoTemplates_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // Prevent event from bubbling further
            if (sender is FrameworkElement fe)
            {
                var menu = new ContextMenu();
                
                var item1 = new MenuItem { Header = "🛒 Grocery Shopping" };
                item1.Click += (s, ev) => ApplyTodoTemplate(new[] { "Buy milk", "Buy eggs", "Buy veggies", "Buy bread", "Buy fruits" });
                
                var item2 = new MenuItem { Header = "🧹 Weekly Chores" };
                item2.Click += (s, ev) => ApplyTodoTemplate(new[] { "Clean room", "Do laundry", "Throw trash", "Vacuum floor" });
                
                var item3 = new MenuItem { Header = "💼 Work Standup Routine" };
                item3.Click += (s, ev) => ApplyTodoTemplate(new[] { "Check emails & Slack", "Update Jira tickets", "Team standup meeting", "Plan daily tasks" });
                
                var item4 = new MenuItem { Header = "✈️ Travel Packing" };
                item4.Click += (s, ev) => ApplyTodoTemplate(new[] { "Pack passport & documents", "Pack chargers & electronics", "Pack clothes & shoes", "Pack toiletries" });

                menu.Items.Add(item1);
                menu.Items.Add(item2);
                menu.Items.Add(item3);
                menu.Items.Add(item4);

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        private void ApplyTodoTemplate(string[] tasks)
        {
            if (_selectedTodoDay == null) return;
            
            // If the last item is empty and it's the only one, remove it so we can insert the template cleanly
            if (_selectedTodoDay.Items.Count == 1 && string.IsNullOrWhiteSpace(_selectedTodoDay.Items[0].Text))
            {
                TodoManager.RemoveItem(_selectedTodoDay, _selectedTodoDay.Items[0]);
            }

            foreach (var task in tasks)
            {
                TodoManager.AddItem(_selectedTodoDay, task);
            }
            UpdateTodoProgress(_selectedTodoDay);
        }
    }
}
