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
        private static FlyShelf.Windows.TimerWindow? _activeTimerWindow;
        private static FlyShelf.Windows.ReminderCreateWindow? _activeTodoReminderWindow;
        private bool _isTodoActive = false;
        public bool IsTodoActive => _isTodoActive;
        private bool _isTodoLoaded = false;
        private TodoDay? _selectedTodoDay = null;
        private TextBox? _lastFocusedTodoTextBox = null;
        private DateTime _lastTodoItemAddedTime = DateTime.MinValue;
        private bool _isTodoSidebarCollapsed = false;
        private ContextMenu? _activeTodoDropdownMenu = null; // Track open menu for toggle behavior




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

            // Start with sidebar collapsed — user can expand via chevron
            if (!_isTodoSidebarCollapsed)
            {
                _isTodoSidebarCollapsed = true;
                TodoSidebarBorder.Visibility = Visibility.Collapsed;
                TodoSidebarColumn.Width = new GridLength(0);
                TodoSidebarCollapseIcon.Text = "▸";
                TodoSidebarExpandBtn.Visibility = Visibility.Visible;
            }

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
            SuppressDwmBorder();
            this.Activate();
            if (!this.Topmost)
            {
                this.Topmost = true;
                this.Topmost = false;
            }
            this.Focus();
        }

        private void CollapseTodoSidebar()
        {
            _isTodoSidebarCollapsed = true;
            TodoSidebarBorder.Visibility = Visibility.Collapsed;
            TodoSidebarColumn.Width = new GridLength(0);
            TodoSidebarCollapseIcon.Text = "▸";
            TodoSidebarExpandBtn.Visibility = Visibility.Visible;
        }

        private void TodoSidebarToggle_Click(object sender, MouseButtonEventArgs e)
        {
            _isTodoSidebarCollapsed = !_isTodoSidebarCollapsed;

            if (_isTodoSidebarCollapsed)
            {
                CollapseTodoSidebar();
            }
            else
            {
                // Expand: show sidebar border and restore column width
                TodoSidebarExpandBtn.Visibility = Visibility.Collapsed;
                TodoSidebarBorder.Visibility = Visibility.Visible;
                TodoSidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
                TodoSidebarBorder.Width = double.NaN;
                TodoSidebarColumn.Width = new GridLength(42);
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

                // PERF: Defer save to Background priority so it doesn't block the summon pipeline.
                Dispatcher.InvokeAsync(() => TodoManager.SaveNow(),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            // Animate out
            var slideAnim = Classes.AnimationHelper.SlideOut(toY: -12, durationMs: 180);
            var fadeAnim = Classes.AnimationHelper.FadeOut(durationMs: 180);

            if (TodoPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            // Normal close path: defer save to background priority
            Dispatcher.InvokeAsync(() => TodoManager.SaveNow(), System.Windows.Threading.DispatcherPriority.Background);

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
                    if (parent is FrameworkElement fe && (fe.Name == "TodoTemplatesBtn" || fe.Name == "TodoDropdownButton"))
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
            // Update IsSelected on all days for data-driven binding
            foreach (var d in TodoManager.Days)
                d.IsSelected = d == day;

            _selectedTodoDay = day;

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
            // Removed auto-focus jumping
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
            // Progress pill was removed from the UI (was UI bloat).
            // This method is kept as a no-op so all existing call sites compile.
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

        private void FocusTodoItemWithCaret(TodoItem item, bool atStart, int exactCaretIndex = -1)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TodoListItemsControl.UpdateLayout();
                var container = TodoListItemsControl.ItemContainerGenerator.ContainerFromItem(item);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "TodoItemTextBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                        if (exactCaretIndex >= 0)
                            tb.CaretIndex = exactCaretIndex;
                        else
                            tb.CaretIndex = atStart ? 0 : tb.Text.Length;
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void FocusSubtaskWithCaret(TodoItem parent, TodoItem subtask, bool atStart, int exactCaretIndex = -1)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TodoListItemsControl.UpdateLayout();
                var parentContainer = TodoListItemsControl.ItemContainerGenerator.ContainerFromItem(parent);
                if (parentContainer is ContentPresenter cp)
                {
                    var subtaskList = FindVisualChild<ItemsControl>(cp, "TodoSubtasksList");
                    if (subtaskList != null)
                    {
                        subtaskList.UpdateLayout();
                        var subContainer = subtaskList.ItemContainerGenerator.ContainerFromItem(subtask);
                        if (subContainer is ContentPresenter subCp)
                        {
                            var tb = FindVisualChild<TextBox>(subCp, "TodoSubtaskTextBox");
                            if (tb != null)
                            {
                                tb.Focus();
                                Keyboard.Focus(tb);
                                if (exactCaretIndex >= 0)
                                    tb.CaretIndex = exactCaretIndex;
                                else
                                    tb.CaretIndex = atStart ? 0 : tb.Text.Length;
                            }
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private TodoItem FindParentTodoItem(TodoItem subtask)
        {
            if (_selectedTodoDay == null) return null;
            foreach (var item in _selectedTodoDay.Items)
            {
                if (item.SubTasks.Contains(subtask)) return item;
            }
            return null;
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
                if (tb.DataContext is TodoItem item)
                {
                    if (!item.IsExpanded)
                    {
                        CollapseAllTodosExcept(item);
                        item.IsExpanded = true;
                    }
                }
            }
        }

        private void TodoItemText_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
            {
                string trimmed = tb.Text.Trim();
                if (tb.Text != trimmed)
                {
                    tb.Text = trimmed;
                    item.Text = trimmed;
                    TodoManager.MarkDirty();
                }
            }
        }

        private void TodoSubtaskText_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem subtask)
            {
                string trimmed = tb.Text.Trim();
                if (tb.Text != trimmed)
                {
                    tb.Text = trimmed;
                    subtask.Text = trimmed;
                    TodoManager.MarkDirty();
                }
            }
        }

        private void TodoItemText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
            {
                if (e.Key == Key.Enter)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        e.Handled = true;
                        int caret = tb.CaretIndex;
                        tb.SelectedText = "\r\n";
                        tb.CaretIndex = caret + 2;
                        return;
                    }
                    e.Handled = true;
                    if (e.IsRepeat) return;

                    if (_selectedTodoDay == null) return;
                    int currentIndex = _selectedTodoDay.Items.IndexOf(item);
                    if (currentIndex >= 0)
                    {
                        var newItem = TodoManager.InsertItem(_selectedTodoDay, currentIndex + 1, "");
                        if (newItem != null)
                        {
                            UpdateTodoProgress(_selectedTodoDay);
                            FocusTodoItemWithCaret(newItem, true);
                        }
                    }
                }
                else if (e.Key == Key.Up)
                {
                    if (tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) == 0)
                    {
                        e.Handled = true;
                        int currentIndex = _selectedTodoDay?.Items.IndexOf(item) ?? -1;
                        if (currentIndex > 0 && _selectedTodoDay != null)
                        {
                            FocusTodoItemWithCaret(_selectedTodoDay.Items[currentIndex - 1], false);
                        }
                    }
                }
                else if (e.Key == Key.Down)
                {
                    if (tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) == tb.LineCount - 1)
                    {
                        e.Handled = true;
                        int currentIndex = _selectedTodoDay?.Items.IndexOf(item) ?? -1;
                        if (_selectedTodoDay != null && currentIndex >= 0 && currentIndex < _selectedTodoDay.Items.Count - 1)
                        {
                            FocusTodoItemWithCaret(_selectedTodoDay.Items[currentIndex + 1], true);
                        }
                    }
                }
                else if (e.Key == Key.Back)
                {
                    if (tb.CaretIndex == 0 && tb.SelectionLength == 0)
                    {
                        e.Handled = true;
                        int currentIndex = _selectedTodoDay?.Items.IndexOf(item) ?? -1;
                        if (currentIndex > 0 && _selectedTodoDay != null)
                        {
                            var prevItem = _selectedTodoDay.Items[currentIndex - 1];
                            int prevLen = prevItem.Text?.Length ?? 0;
                            if (!string.IsNullOrEmpty(item.Text))
                            {
                                prevItem.Text += item.Text;
                            }
                            TodoManager.RemoveItem(_selectedTodoDay, item);
                            FocusTodoItemWithCaret(prevItem, false, prevLen);
                        }
                    }
                }
                else if (e.Key == Key.Delete)
                {
                    if (tb.CaretIndex == tb.Text.Length && tb.SelectionLength == 0)
                    {
                        e.Handled = true;
                        int currentIndex = _selectedTodoDay?.Items.IndexOf(item) ?? -1;
                        if (_selectedTodoDay != null && currentIndex >= 0 && currentIndex < _selectedTodoDay.Items.Count - 1)
                        {
                            var nextItem = _selectedTodoDay.Items[currentIndex + 1];
                            int prevLen = item.Text?.Length ?? 0;
                            if (!string.IsNullOrEmpty(nextItem.Text))
                            {
                                item.Text += nextItem.Text;
                            }
                            TodoManager.RemoveItem(_selectedTodoDay, nextItem);
                            FocusTodoItemWithCaret(item, false, prevLen);
                        }
                    }
                }
            }
        }

        private void TodoStopwatch_Click(object sender, RoutedEventArgs e)
        {
            try { _activeTimerWindow?.Close(); } catch { }
            var tw = new FlyShelf.Windows.TimerWindow(null);
            tw.Show();
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
                try { _activeTimerWindow?.Close(); } catch { }
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

                try { _activeTodoReminderWindow?.Close(); } catch { }
                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(title, defaultDue);
                reminderWindow.Show();
                reminderWindow.Activate();
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
                try { _activeTimerWindow?.Close(); } catch { }
                var tw = new FlyShelf.Windows.TimerWindow(trimmed);
                tw.Show();
                _activeTimerWindow = tw;
                return;
            }

            // Try parse as number → treat as minutes
            if (int.TryParse(trimmed, out int mins) && mins > 0)
            {
                try { _activeTimerWindow?.Close(); } catch { }
                var tw = new FlyShelf.Windows.TimerWindow($"{mins}m");
                tw.Show();
                _activeTimerWindow = tw;
            }
            else
            {
                // Fallback: pass as-is and let TimerWindow.ParseContext handle it
                try { _activeTimerWindow?.Close(); } catch { }
                var tw = new FlyShelf.Windows.TimerWindow(trimmed);
                tw.Show();
                _activeTimerWindow = tw;
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

        // ═══════════════════════════════════════════════════════════
        // PRIORITY CYCLING
        // ═══════════════════════════════════════════════════════════

        private void TodoItemPriority_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                item.Priority = item.Priority switch
                {
                    TodoPriority.None => TodoPriority.Low,
                    TodoPriority.Low => TodoPriority.Medium,
                    TodoPriority.Medium => TodoPriority.High,
                    TodoPriority.High => TodoPriority.None,
                    _ => TodoPriority.None
                };
                TodoManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // DUE DATE
        // ═══════════════════════════════════════════════════════════

        private void TodoItemDueDate_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                Dispatcher.BeginInvoke(new Action(() => OpenDueDateMenu(fe, item)));
            }
        }

        private void OpenDueDateMenu(FrameworkElement fe, TodoItem item)
        {
                var menu = new ContextMenu();

                var today = new MenuItem { Header = "📅 Today" };
                today.Click += (s, ev) => { item.DueDate = DateTime.Today; TodoManager.MarkDirty(); };

                var tomorrow = new MenuItem { Header = "📅 Tomorrow" };
                tomorrow.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(1); TodoManager.MarkDirty(); };

                var nextWeek = new MenuItem { Header = "📅 Next Week" };
                nextWeek.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(7); TodoManager.MarkDirty(); };

                var pickDate = new MenuItem { Header = "📅 Pick Date..." };
                pickDate.Click += (s, ev) =>
                {
                    var popup = new System.Windows.Controls.Primitives.Popup
                    {
                        PlacementTarget = fe,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                        StaysOpen = false,
                        AllowsTransparency = true
                    };

                    var calendar = new System.Windows.Controls.Calendar
                    {
                        SelectedDate = item.DueDate ?? DateTime.Today,
                        DisplayDate = item.DueDate ?? DateTime.Today,
                        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                        Foreground = new SolidColorBrush(Colors.White),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6)),
                        BorderThickness = new Thickness(1)
                    };

                    calendar.SelectedDatesChanged += (cs, cev) =>
                    {
                        if (calendar.SelectedDate.HasValue)
                        {
                            item.DueDate = calendar.SelectedDate.Value.Date;
                            TodoManager.MarkDirty();
                            popup.IsOpen = false;
                        }
                    };

                    popup.Child = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(4),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6)),
                        BorderThickness = new Thickness(1),
                        Child = calendar
                    };

                    popup.IsOpen = true;
                };

                var clear = new MenuItem { Header = "✕ Clear" };
                clear.Click += (s, ev) => { item.DueDate = null; TodoManager.MarkDirty(); };

                menu.Items.Add(today);
                menu.Items.Add(tomorrow);
                menu.Items.Add(nextWeek);
                menu.Items.Add(new Separator());
                menu.Items.Add(pickDate);
                menu.Items.Add(new Separator());
                menu.Items.Add(clear);

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
        }

        // ═══════════════════════════════════════════════════════════
        // SUBTASK ADD / CHECK
        // ═══════════════════════════════════════════════════════════

        private void TodoItemAddSubtask_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem parentItem)
            {
                var subTask = new TodoItem { Text = "", CreatedAt = DateTime.Now };
                parentItem.SubTasks.Add(subTask);
                TodoManager.MarkDirty();
            }
        }

        private void TodoSubtaskCheck_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem subTask)
            {
                subTask.IsDone = !subTask.IsDone;

                // Walk up to find the parent TodoItem and refresh its SubTaskProgress
                var parent = fe;
                while (parent != null)
                {
                    parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                    if (parent?.DataContext is TodoItem parentItem && parentItem != subTask && parentItem.SubTasks.Contains(subTask))
                    {
                        parentItem.RefreshDisplayProperties();
                        break;
                    }
                }

                TodoManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // TAG MANAGEMENT
        // ═══════════════════════════════════════════════════════════

        private void TodoItemTag_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                Dispatcher.BeginInvoke(new Action(() => OpenTagMenu(fe, item)));
            }
        }

        private void OpenTagMenu(FrameworkElement fe, TodoItem item)
        {
                var menu = new ContextMenu();
                string[] presetTags = { "Work", "Personal", "Urgent", "Ideas", "Health", "Finance" };

                foreach (var tag in presetTags)
                {
                    bool hasTag = item.Tags.Contains(tag);
                    var mi = new MenuItem
                    {
                        Header = hasTag ? $"✓ {tag}" : $"  {tag}",
                        IsChecked = hasTag
                    };
                    string capturedTag = tag;
                    mi.Click += (s, ev) =>
                    {
                        if (item.Tags.Contains(capturedTag))
                            item.Tags.Remove(capturedTag);
                        else
                            item.Tags.Add(capturedTag);
                        // Force property change notification
                        item.Tags = new List<string>(item.Tags);
                        TodoManager.MarkDirty();
                    };
                    menu.Items.Add(mi);
                }

                menu.Items.Add(new Separator());

                var customItem = new MenuItem { Header = "✏️ Custom..." };
                customItem.Click += (s, ev) =>
                {
                    var popup = new System.Windows.Controls.Primitives.Popup
                    {
                        PlacementTarget = fe,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                        StaysOpen = false,
                        AllowsTransparency = true
                    };

                    var textBox = new TextBox
                    {
                        Width = 160,
                        FontSize = 13,
                        Padding = new Thickness(6, 4, 6, 4),
                        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                        Foreground = new SolidColorBrush(Colors.White),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x8B, 0x5C, 0xF6)),
                        CaretBrush = new SolidColorBrush(Colors.White)
                    };

                    textBox.KeyDown += (ts, te) =>
                    {
                        if (te.Key == Key.Enter && !string.IsNullOrWhiteSpace(textBox.Text))
                        {
                            te.Handled = true;
                            string newTag = textBox.Text.Trim();
                            if (item.Tags.Contains(newTag))
                                item.Tags.Remove(newTag);
                            else
                                item.Tags.Add(newTag);
                            item.Tags = new List<string>(item.Tags);
                            TodoManager.MarkDirty();
                            popup.IsOpen = false;
                        }
                        else if (te.Key == Key.Escape)
                        {
                            te.Handled = true;
                            popup.IsOpen = false;
                        }
                    };

                    popup.Child = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(4),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6)),
                        BorderThickness = new Thickness(1),
                        Child = textBox
                    };

                    popup.IsOpen = true;
                    Dispatcher.InvokeAsync(() => { textBox.Focus(); Keyboard.Focus(textBox); },
                        System.Windows.Threading.DispatcherPriority.Input);
                };
                menu.Items.Add(customItem);

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
        }

        // ═══════════════════════════════════════════════════════════
        // COLOR PICKER
        // ═══════════════════════════════════════════════════════════

        private void TodoItemColor_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                Dispatcher.BeginInvoke(new Action(() => OpenColorMenu(fe, item)));
            }
        }

        private void OpenColorMenu(FrameworkElement fe, TodoItem item)
        {
                var menu = new ContextMenu();

                var colors = new (string Hex, string Name)[]
                {
                    ("#FF4444", "Red"),
                    ("#F59E0B", "Amber"),
                    ("#22C55E", "Green"),
                    ("#3B82F6", "Blue"),
                    ("#8B5CF6", "Purple"),
                    ("#EC4899", "Pink")
                };

                foreach (var (hex, name) in colors)
                {
                    var mi = new MenuItem { Header = $"● {name}" };
                    try
                    {
                        mi.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    }
                    catch { }
                    string capturedHex = hex;
                    mi.Click += (s, ev) =>
                    {
                        item.Color = capturedHex;
                        TodoManager.MarkDirty();
                    };
                    menu.Items.Add(mi);
                }

                menu.Items.Add(new Separator());

                var clearItem = new MenuItem { Header = "✕ Clear" };
                clearItem.Click += (s, ev) =>
                {
                    item.Color = "";
                    TodoManager.MarkDirty();
                };
                menu.Items.Add(clearItem);

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
        }

        // ═══════════════════════════════════════════════════════════
        // DESCRIPTION TOGGLE
        // ═══════════════════════════════════════════════════════════

        private void TodoItemDescription_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                item.IsDescriptionVisible = !item.IsDescriptionVisible;
                if (item.IsDescriptionVisible)
                {
                    // Focus the description textbox after the UI has updated
                    Dispatcher.InvokeAsync(() =>
                    {
                        var container = ItemsControl.ContainerFromElement(TodoListItemsControl, fe) as FrameworkElement
                            ?? fe;
                        var descTextBox = FindVisualChild<TextBox>(container, "TodoDescriptionBox");
                        if (descTextBox != null)
                        {
                            descTextBox.Focus();
                            Keyboard.Focus(descTextBox);
                            descTextBox.CaretIndex = descTextBox.Text.Length;
                        }
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        private void DescriptionText_TextChanged(object sender, TextChangedEventArgs e)
        {
            TodoManager.MarkDirty();
        }

        // ═══════════════════════════════════════════════════════════
        // SORT
        // ═══════════════════════════════════════════════════════════

        private void TodoSort_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && _selectedTodoDay != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                var menu = new ContextMenu();

                var sortOptions = new (string Label, TodoSortMode Mode)[]
                {
                    ("↕ Manual", TodoSortMode.Manual),
                    ("🔴 Priority", TodoSortMode.Priority),
                    ("📅 Due Date", TodoSortMode.DueDate),
                    ("🔤 A-Z", TodoSortMode.Alphabetical),
                    ("🕐 Created", TodoSortMode.CreatedAt)
                };

                foreach (var (label, mode) in sortOptions)
                {
                    var mi = new MenuItem { Header = label };
                    var capturedMode = mode;
                    mi.Click += (s, ev) =>
                    {
                        if (_selectedTodoDay == null) return;
                        TodoManager.SortItems(_selectedTodoDay, capturedMode);
                        // Refresh the binding so the ItemsControl picks up the new order
                        TodoListItemsControl.ItemsSource = null;
                        TodoListItemsControl.ItemsSource = _selectedTodoDay.Items;
                    };
                    menu.Items.Add(mi);
                }

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
                }));
            }
        }

        // ═══════════════════════════════════════════════════════════
        // AUTO-MIGRATE
        // ═══════════════════════════════════════════════════════════

        private void TodoMigrate_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            int count = TodoManager.MigrateIncompleteTasks();

            if (_selectedTodoDay != null)
            {
                UpdateTodoProgress(_selectedTodoDay);
                // Refresh binding in case today is selected and new items were added
                TodoListItemsControl.ItemsSource = null;
                TodoListItemsControl.ItemsSource = _selectedTodoDay.Items;
            }

            // Progress pill was removed; migration result is reflected in the refreshed list.

        }

        // ═══════════════════════════════════════════════════════════
        // DELETE (with confirmation)
        // ═══════════════════════════════════════════════════════════

        private void TodoItemTrash_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item && _selectedTodoDay != null)
            {
                var result = MessageBox.Show("Are you sure you want to delete this task?", "Delete Task",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    TodoManager.DeleteItem(_selectedTodoDay, item);
                    UpdateTodoProgress(_selectedTodoDay);
                }
            }
        }



        // ═══════════════════════════════════════════════════════════
        // RECURRENCE CYCLING
        // ═══════════════════════════════════════════════════════════

        private void TodoItemRecurrence_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                item.Recurrence = item.Recurrence switch
                {
                    TodoRecurrence.None => TodoRecurrence.Daily,
                    TodoRecurrence.Daily => TodoRecurrence.Weekly,
                    TodoRecurrence.Weekly => TodoRecurrence.Monthly,
                    TodoRecurrence.Monthly => TodoRecurrence.None,
                    _ => TodoRecurrence.None
                };
                TodoManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MORE MENU (consolidated dropdown for Priority/Due/Tags/Color/Desc/Recurrence/Subtask)
        // ═══════════════════════════════════════════════════════════

        private void TodoItemCollapse_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                item.IsExpanded = !item.IsExpanded;
            }
        }

        private DateTime _lastTodoDropdownCloseTime = DateTime.MinValue;

        private void TodoItemMore_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                // Close any existing menu first (whether for this button or another)
                if (_activeTodoDropdownMenu != null)
                {
                    var wasForSameTarget = _activeTodoDropdownMenu.IsOpen && _activeTodoDropdownMenu.PlacementTarget == fe;
                    _activeTodoDropdownMenu.IsOpen = false;
                    _activeTodoDropdownMenu = null;

                    // If it was a toggle-close for the same button, just return
                    if (wasForSameTarget) return;
                }

                // Guard against rapid re-open flickering (menu Closed event fires async)
                if ((DateTime.Now - _lastTodoDropdownCloseTime).TotalMilliseconds < 150) return;

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    var menu = new ContextMenu();

                    // Helper: colored emoji icon
                    TextBlock MI(string g, string c) => new TextBlock
                    {
                        Text = g, FontFamily = new FontFamily("Segoe UI Emoji"),
                        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c))
                    };
                    Border Dot(string c) => new Border
                    {
                        Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c))
                    };

                    // Priority submenu  —— tag icon
                    var priorityMenu = new MenuItem { Header = "Priority" };
                    priorityMenu.Icon = MI("🏷", "#F59E0B");
                    var pHigh = new MenuItem { Header = "High" };
                    pHigh.Icon = Dot("#EF4444");
                    pHigh.Click += (s, ev) => { item.Priority = TodoPriority.High; TodoManager.MarkDirty(); };
                    var pMed = new MenuItem { Header = "Medium" };
                    pMed.Icon = Dot("#F59E0B");
                    pMed.Click += (s, ev) => { item.Priority = TodoPriority.Medium; TodoManager.MarkDirty(); };
                    var pLow = new MenuItem { Header = "Low" };
                    pLow.Icon = Dot("#22C55E");
                    pLow.Click += (s, ev) => { item.Priority = TodoPriority.Low; TodoManager.MarkDirty(); };
                    var pNone = new MenuItem { Header = "Clear Priority" };
                    pNone.Icon = MI("✕", "#6B7280");
                    pNone.Click += (s, ev) => { item.Priority = TodoPriority.None; TodoManager.MarkDirty(); };
                    priorityMenu.Items.Add(pHigh);
                    priorityMenu.Items.Add(pMed);
                    priorityMenu.Items.Add(pLow);
                    priorityMenu.Items.Add(pNone);
                    menu.Items.Add(priorityMenu);

                    // Due Date  —— green calendar icon
                    var dueDate = new MenuItem { Header = "Due Date" };
                    dueDate.Icon = MI("📅", "#22C55E");
                    dueDate.Click += (s, ev) => Dispatcher.BeginInvoke(new Action(() => OpenDueDateMenu(fe, item)));
                    menu.Items.Add(dueDate);

                    // Tags  —— cyan tag icon
                    var tags = new MenuItem { Header = "Tags" };
                    tags.Icon = MI("🏷", "#00D2FF");
                    tags.Click += (s, ev) => Dispatcher.BeginInvoke(new Action(() => OpenTagMenu(fe, item)));
                    menu.Items.Add(tags);

                    // Color  —— pink palette icon
                    var color = new MenuItem { Header = "Color" };
                    color.Icon = MI("🎨", "#EC4899");
                    color.Click += (s, ev) => Dispatcher.BeginInvoke(new Action(() => OpenColorMenu(fe, item)));
                    menu.Items.Add(color);

                    menu.Items.Add(new Separator());

                    // Add Subtask  —— green plus icon
                    var addSub = new MenuItem { Header = "Add Subtask" };
                    addSub.Icon = MI("➕", "#22C55E");
                    addSub.Click += (s, ev) =>
                    {
                        var subTask = new TodoItem { Text = "", CreatedAt = DateTime.Now };
                        item.SubTasks.Add(subTask);
                        item.IsExpanded = true;
                        item.RefreshDisplayProperties();
                        TodoManager.MarkDirty();
                        FocusSubtaskWithCaret(item, subTask, true);
                    };
                    menu.Items.Add(addSub);


                    // Description toggle  —— indigo notes icon
                    bool hasDesc = !string.IsNullOrWhiteSpace(item.Description);
                    var desc = new MenuItem { Header = hasDesc ? "Hide Description" : "Show Description" };
                    desc.Icon = MI("📝", "#6366F1");
                    desc.Click += (s, ev) =>
                    {
                        if (string.IsNullOrEmpty(item.Description))
                            item.Description = " ";
                        else
                            item.Description = null;
                        TodoManager.MarkDirty();
                    };
                    menu.Items.Add(desc);

                    // Recurrence  —— purple recycle icon
                    string recLabel = item.Recurrence switch
                    {
                        TodoRecurrence.Daily => "Recurrence (Daily → Weekly)",
                        TodoRecurrence.Weekly => "Recurrence (Weekly → Monthly)",
                        TodoRecurrence.Monthly => "Recurrence (Monthly → None)",
                        _ => "Set Recurrence (→ Daily)"
                    };
                    var rec = new MenuItem { Header = recLabel };
                    rec.Icon = MI("🔄", "#8B5CF6");
                    rec.Click += (s, ev) =>
                    {
                        item.Recurrence = item.Recurrence switch
                        {
                            TodoRecurrence.None => TodoRecurrence.Daily,
                            TodoRecurrence.Daily => TodoRecurrence.Weekly,
                            TodoRecurrence.Weekly => TodoRecurrence.Monthly,
                            TodoRecurrence.Monthly => TodoRecurrence.None,
                            _ => TodoRecurrence.None
                        };
                        TodoManager.MarkDirty();
                    };
                    menu.Items.Add(rec);

                    menu.Items.Add(new Separator());

                    // Delete  —— red trash icon
                    var deleteItem = new MenuItem { Header = "Delete" };
                    deleteItem.Icon = MI("🗑", "#EF4444");
                    deleteItem.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    deleteItem.Click += (s, ev) =>
                    {
                        if (_selectedTodoDay != null)
                        {
                            var result = MessageBox.Show("Are you sure you want to delete this task?", "Delete Task",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning);
                            if (result == MessageBoxResult.Yes)
                            {
                                TodoManager.DeleteItem(_selectedTodoDay, item);
                                UpdateTodoProgress(_selectedTodoDay);
                            }
                        }
                    };
                    menu.Items.Add(deleteItem);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.Closed += (s, ev) =>
                    {
                        _lastTodoDropdownCloseTime = DateTime.Now;
                        if (_activeTodoDropdownMenu == menu) _activeTodoDropdownMenu = null;
                    };
                    _activeTodoDropdownMenu = menu;
                    menu.IsOpen = true;
                }));
            }
        }

        private void CollapseAllTodosExcept(TodoItem item)
        {
            if (_selectedTodoDay != null)
            {
                foreach (var otherItem in _selectedTodoDay.Items)
                {
                    if (otherItem != item)
                    {
                        otherItem.IsExpanded = false;
                    }
                }
            }
        }

        private TodoItem? _todoDragItem;
        private Point _todoDragStartPoint;

        private void TodoItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item)
            {
                // Check if the click target is inside a TextBox or an action button
                // (More, Collapse, DueDate, Checkbox, etc.) — these handle their own events
                bool skipToggle = false;
                if (e.OriginalSource is DependencyObject focusSource)
                {
                    var parent = focusSource;
                    while (parent != null)
                    {
                        if (parent is TextBox) { skipToggle = true; break; }
                        if (parent is FrameworkElement actionFe && !string.IsNullOrEmpty(actionFe.Name))
                        {
                            // Skip expand toggle for named action buttons that handle their own clicks
                            if (actionFe.Name == "TodoDropdownButton" ||
                                actionFe.Name == "TodoCollapseBtn" ||
                                actionFe.Name == "TodoDueDateBadge" ||
                                actionFe.Name == "TodoCheckboxBorder" ||
                                actionFe.Name == "TodoRecurrenceBadge")
                            {
                                skipToggle = true;
                                break;
                            }
                        }
                        parent = VisualTreeHelper.GetParent(parent);
                    }
                }

                // If clicking on a TextBox or action button, let it handle focus naturally — don't toggle or re-layout
                if (skipToggle) return;

                // Clicking on non-TextBox area toggles expansion
                bool targetState = !item.IsExpanded;
                if (targetState)
                {
                    CollapseAllTodosExcept(item);
                }
                item.IsExpanded = targetState;

                _todoDragItem = item;
                _todoDragStartPoint = e.GetPosition(null);
            }
        }

        private void TodoItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_todoDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPos = e.GetPosition(null);
            Vector diff = _todoDragStartPoint - currentPos;

            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
            {
                if (sender is FrameworkElement fe)
                {
                    var data = new DataObject(DataFormats.Serializable, _todoDragItem);
                    DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
                    _todoDragItem = null;
                }
            }
        }

        private void TodoItem_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.Serializable))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void TodoItem_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_selectedTodoDay == null) return;

            if (e.Data.GetData(DataFormats.Serializable) is TodoItem sourceItem
                && sender is FrameworkElement fe
                && fe.DataContext is TodoItem targetItem
                && sourceItem != targetItem)
            {
                int sourceIndex = _selectedTodoDay.Items.IndexOf(sourceItem);
                int targetIndex = _selectedTodoDay.Items.IndexOf(targetItem);

                if (sourceIndex < 0 || targetIndex < 0) return;

                _selectedTodoDay.Items.RemoveAt(sourceIndex);
                // Recalculate target index after removal
                targetIndex = _selectedTodoDay.Items.IndexOf(targetItem);
                if (targetIndex < 0)
                    _selectedTodoDay.Items.Add(sourceItem);
                else
                    _selectedTodoDay.Items.Insert(targetIndex, sourceItem);

                // Update SortOrder indices for all items
                for (int i = 0; i < _selectedTodoDay.Items.Count; i++)
                {
                    _selectedTodoDay.Items[i].SortOrder = i;
                }

                TodoManager.MarkDirty();
            }

            _todoDragItem = null;
        }

        // ═══════════════════════════════════════════════════════════
        // ENHANCED KEYBOARD SHORTCUTS
        // ═══════════════════════════════════════════════════════════

        private void TodoPanel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_selectedTodoDay == null) return;

            // Ctrl+D: Delete focused item
            if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (Keyboard.FocusedElement is FrameworkElement focused && focused.DataContext is TodoItem item)
                {
                    var result = MessageBox.Show("Are you sure you want to delete this task?", "Delete Task",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                    {
                        TodoManager.DeleteItem(_selectedTodoDay, item);
                        UpdateTodoProgress(_selectedTodoDay);
                    }
                }
                return;
            }

            // Ctrl+Shift+↑: Move item up
            if (e.Key == Key.Up && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                if (Keyboard.FocusedElement is FrameworkElement focused && focused.DataContext is TodoItem item)
                {
                    int index = _selectedTodoDay.Items.IndexOf(item);
                    if (index > 0)
                    {
                        _selectedTodoDay.Items.RemoveAt(index);
                        _selectedTodoDay.Items.Insert(index - 1, item);
                        for (int i = 0; i < _selectedTodoDay.Items.Count; i++)
                            _selectedTodoDay.Items[i].SortOrder = i;
                        TodoManager.MarkDirty();
                        FocusTodoItem(item);
                    }
                }
                return;
            }

            // Ctrl+Shift+↓: Move item down
            if (e.Key == Key.Down && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                if (Keyboard.FocusedElement is FrameworkElement focused && focused.DataContext is TodoItem item)
                {
                    int index = _selectedTodoDay.Items.IndexOf(item);
                    if (index >= 0 && index < _selectedTodoDay.Items.Count - 1)
                    {
                        _selectedTodoDay.Items.RemoveAt(index);
                        _selectedTodoDay.Items.Insert(index + 1, item);
                        for (int i = 0; i < _selectedTodoDay.Items.Count; i++)
                            _selectedTodoDay.Items[i].SortOrder = i;
                        TodoManager.MarkDirty();
                        FocusTodoItem(item);
                    }
                }
                return;
            }
        }
        // ═══════════════════════════════════════════════════════════
        // CONTEXT MENU ROUTED EVENT OVERLOADS
        // (MenuItem.Click sends RoutedEventArgs, not MouseButtonEventArgs)
        // ═══════════════════════════════════════════════════════════

        private TodoItem GetTodoItemFromMenuContext(object sender)
        {
            // MenuItem in a ContextMenu: walk up to find the DataContext
            if (sender is MenuItem mi)
            {
                // Direct DataContext
                if (mi.DataContext is TodoItem directItem) return directItem;

                // Walk up through parent MenuItems
                var parent = mi.Parent;
                while (parent != null)
                {
                    if (parent is ContextMenu ctx)
                    {
                        if (ctx.PlacementTarget is FrameworkElement target && target.DataContext is TodoItem item)
                            return item;
                        break;
                    }
                    if (parent is MenuItem parentMi)
                    {
                        if (parentMi.DataContext is TodoItem pItem) return pItem;
                        parent = parentMi.Parent;
                    }
                    else break;
                }
            }
            return null;
        }

        // --- Priority direct setters (from context menu submenu) ---

        private void TodoItemPriorityHigh_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.High; TodoManager.MarkDirty(); }
        }

        private void TodoItemPriorityMedium_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.Medium; TodoManager.MarkDirty(); }
        }

        private void TodoItemPriorityLow_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.Low; TodoManager.MarkDirty(); }
        }

        private void TodoItemPriorityNone_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.None; TodoManager.MarkDirty(); }
        }

        // --- RoutedEventArgs overloads for context menu items ---

        private void TodoItemDueDate_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            // Show the same due date context menu
            var fe = sender as FrameworkElement ?? this;
            ShowDueDateMenu(item, fe);
        }

        private void ShowDueDateMenu(TodoItem item, FrameworkElement placementTarget)
        {
            var menu = new ContextMenu();

            var today = new MenuItem { Header = "📅 Today" };
            today.Click += (s, ev) => { item.DueDate = DateTime.Today; TodoManager.MarkDirty(); };

            var tomorrow = new MenuItem { Header = "📅 Tomorrow" };
            tomorrow.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(1); TodoManager.MarkDirty(); };

            var nextWeek = new MenuItem { Header = "📅 Next Week" };
            nextWeek.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(7); TodoManager.MarkDirty(); };

            var clear = new MenuItem { Header = "Clear" };
            clear.Click += (s, ev) => { item.DueDate = null; TodoManager.MarkDirty(); };

            menu.Items.Add(today);
            menu.Items.Add(tomorrow);
            menu.Items.Add(nextWeek);
            menu.Items.Add(new Separator());
            menu.Items.Add(clear);

            menu.PlacementTarget = placementTarget;
            menu.IsOpen = true;
        }

        private void TodoItemTag_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            var fe = sender as FrameworkElement ?? this;
            ShowTagMenu(item, fe);
        }

        private void ShowTagMenu(TodoItem item, FrameworkElement placementTarget)
        {
            var menu = new ContextMenu();
            string[] presets = { "Work", "Personal", "Urgent", "Ideas", "Health", "Finance" };
            foreach (var tag in presets)
            {
                bool hasTag = item.Tags?.Contains(tag) == true;
                var mi = new MenuItem { Header = (hasTag ? "✓ " : "") + tag };
                string capturedTag = tag;
                mi.Click += (s, ev) =>
                {
                    var tags = item.Tags != null ? new System.Collections.Generic.List<string>(item.Tags) : new System.Collections.Generic.List<string>();
                    if (tags.Contains(capturedTag)) tags.Remove(capturedTag);
                    else tags.Add(capturedTag);
                    item.Tags = tags;
                    TodoManager.MarkDirty();
                };
                menu.Items.Add(mi);
            }
            menu.PlacementTarget = placementTarget;
            menu.IsOpen = true;
        }

        private void TodoItemColor_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            var fe = sender as FrameworkElement ?? this;
            ShowColorMenu(item, fe);
        }

        private void ShowColorMenu(TodoItem item, FrameworkElement placementTarget)
        {
            var menu = new ContextMenu();
            var colors = new[] {
                ("#FF4444", "Red"), ("#F59E0B", "Amber"), ("#22C55E", "Green"),
                ("#3B82F6", "Blue"), ("#8B5CF6", "Purple"), ("#EC4899", "Pink")
            };
            foreach (var (hex, name) in colors)
            {
                string capturedHex = hex;
                var mi = new MenuItem { Header = $"● {name}" };
                try { mi.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); } catch { }
                mi.Click += (s, ev) => { item.Color = capturedHex; TodoManager.MarkDirty(); };
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var clearMi = new MenuItem { Header = "Clear" };
            clearMi.Click += (s, ev) => { item.Color = null; TodoManager.MarkDirty(); };
            menu.Items.Add(clearMi);

            menu.PlacementTarget = placementTarget;
            menu.IsOpen = true;
        }

        private void TodoItemAddSubtask_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            if (item.SubTasks == null)
                item.SubTasks = new System.Collections.ObjectModel.ObservableCollection<TodoItem>();
            var newSubtask = new TodoItem { Text = "", CreatedAt = DateTime.Now };
            item.SubTasks.Add(newSubtask);
            item.IsExpanded = true;   // expand card so subtask row is visible
            item.RefreshDisplayProperties();
            TodoManager.MarkDirty();
            // Auto-focus the new subtask so user can type immediately
            FocusSubtaskWithCaret(item, newSubtask, true);
        }


        private void TodoItemDescription_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            item.IsDescriptionVisible = !item.IsDescriptionVisible;
            TodoManager.MarkDirty();
        }

        private void TodoItemRecurrence_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            item.Recurrence = item.Recurrence switch
            {
                TodoRecurrence.None => TodoRecurrence.Daily,
                TodoRecurrence.Daily => TodoRecurrence.Weekly,
                TodoRecurrence.Weekly => TodoRecurrence.Monthly,
                TodoRecurrence.Monthly => TodoRecurrence.None,
                _ => TodoRecurrence.None
            };
            TodoManager.MarkDirty();
        }

        private void TodoItemTrash_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null || _selectedTodoDay == null) return;
            var result = MessageBox.Show("Are you sure you want to delete this task?", "Delete Task",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                TodoManager.DeleteItem(_selectedTodoDay, item);
                UpdateTodoProgress(_selectedTodoDay);
            }
        }

        // --- Missing TextChanged handlers ---

        private void TodoSubtask_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem subtask)
            {
                var parentItem = FindParentTodoItem(subtask);
                if (parentItem == null) return;

                if (e.Key == Key.Enter)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
                    e.Handled = true;
                    if (e.IsRepeat) return;

                    int currentIndex = parentItem.SubTasks.IndexOf(subtask);
                    if (currentIndex >= 0)
                    {
                        var newSubtask = new TodoItem { Text = "", CreatedAt = DateTime.Now };
                        parentItem.SubTasks.Insert(currentIndex + 1, newSubtask);
                        TodoManager.MarkDirty();
                        FocusSubtaskWithCaret(parentItem, newSubtask, true);
                    }
                }
                else if (e.Key == Key.Up)
                {
                    if (tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) == 0)
                    {
                        e.Handled = true;
                        int currentIndex = parentItem.SubTasks.IndexOf(subtask);
                        if (currentIndex > 0)
                        {
                            FocusSubtaskWithCaret(parentItem, parentItem.SubTasks[currentIndex - 1], false);
                        }
                        else
                        {
                            FocusTodoItemWithCaret(parentItem, false);
                        }
                    }
                }
                else if (e.Key == Key.Down)
                {
                    if (tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) == tb.LineCount - 1)
                    {
                        e.Handled = true;
                        int currentIndex = parentItem.SubTasks.IndexOf(subtask);
                        if (currentIndex >= 0 && currentIndex < parentItem.SubTasks.Count - 1)
                        {
                            FocusSubtaskWithCaret(parentItem, parentItem.SubTasks[currentIndex + 1], true);
                        }
                        else
                        {
                            int parentIndex = _selectedTodoDay?.Items.IndexOf(parentItem) ?? -1;
                            if (_selectedTodoDay != null && parentIndex >= 0 && parentIndex < _selectedTodoDay.Items.Count - 1)
                            {
                                FocusTodoItemWithCaret(_selectedTodoDay.Items[parentIndex + 1], true);
                            }
                        }
                    }
                }
                else if (e.Key == Key.Back)
                {
                    if (tb.CaretIndex == 0 && tb.SelectionLength == 0)
                    {
                        e.Handled = true;
                        int currentIndex = parentItem.SubTasks.IndexOf(subtask);
                        if (currentIndex > 0)
                        {
                            var prevItem = parentItem.SubTasks[currentIndex - 1];
                            int prevLen = prevItem.Text?.Length ?? 0;
                            if (!string.IsNullOrEmpty(subtask.Text))
                            {
                                prevItem.Text += subtask.Text;
                            }
                            parentItem.SubTasks.Remove(subtask);
                            TodoManager.MarkDirty();
                            FocusSubtaskWithCaret(parentItem, prevItem, false, prevLen);
                        }
                        else
                        {
                            int prevLen = parentItem.Text?.Length ?? 0;
                            if (!string.IsNullOrEmpty(subtask.Text))
                            {
                                parentItem.Text += subtask.Text;
                            }
                            parentItem.SubTasks.Remove(subtask);
                            TodoManager.MarkDirty();
                            FocusTodoItemWithCaret(parentItem, false, prevLen);
                        }
                    }
                }
            }
        }

        private void TodoDescriptionText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
            {
                TodoManager.MarkDirty();
            }
        }

        private void TodoSubtaskText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
            {
                // Text is already bound TwoWay, just mark dirty
                TodoManager.MarkDirty();
            }
        }
        // ═══════════════════════════════════════════════════════════
        // TODO SEARCH — Fuzzy search across all days
        // ═══════════════════════════════════════════════════════════

        private ObservableCollection<TodoItem>? _todoSearchResults;

        private void ApplyTodoSearch(string query)
        {
            string queryClean = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(queryClean))
            {
                // Restore normal view — show selected day's items
                if (_selectedTodoDay != null)
                {
                    TodoListItemsControl.ItemsSource = _selectedTodoDay.Items;
                }
                _todoSearchResults = null;
                return;
            }

            // Search across ALL days for matching items
            var results = new ObservableCollection<TodoItem>();
            foreach (var day in TodoManager.Days)
            {
                foreach (var item in day.Items)
                {
                    if (IsTodoItemMatch(queryClean, item))
                    {
                        results.Add(item);
                    }
                    // Also search subtasks
                    foreach (var sub in item.SubTasks)
                    {
                        if (IsTodoItemMatch(queryClean, sub) && !results.Contains(sub))
                        {
                            results.Add(sub);
                        }
                    }
                }
            }

            _todoSearchResults = results;
            TodoListItemsControl.ItemsSource = results;
        }

        private static bool IsTodoItemMatch(string query, TodoItem item)
        {
            return FuzzyMatcher.IsMatchAny(query, item.Text, item.Description)
                || item.Tags.Any(t => FuzzyMatcher.IsMatch(query, t));
        }
    }
}
