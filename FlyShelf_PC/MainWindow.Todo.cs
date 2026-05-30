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
            TodoToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ClipboardTextLtr24 };
            TodoToggleBtn.ToolTip = "Back to Clipboard";
            TodoToggleBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));

            // Animate in
            var slideAnim = new DoubleAnimation(-12, 0, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)));
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
        }

        private void CloseTodoPanel(bool immediate = false)
        {
            if (!_isTodoActive) return;

            // Restore todo button icon and tooltip
            TodoToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.CheckboxChecked24 };
            TodoToggleBtn.ToolTip = "To-Do List";
            TodoToggleBtn.ClearValue(ForegroundProperty);

            _isTodoActive = false;
            Title = "FlyShelf";

            UpdateWindowActivationStyle();

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
            var slideAnim = new DoubleAnimation(0, -12, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var fadeAnim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(180)));

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
    }
}
