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
            StartPanelAutoRevertTimer();

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
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = TodoStopwatchBtn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = true,
                HorizontalOffset = -40
            };

            // Common time presets
            var presets = new[]
            {
                ("1 min",    "1m"),
                ("2 min",    "2m"),
                ("5 min",    "5m"),
                ("10 min",   "10m"),
                ("15 min",   "15m"),
                ("25 min  🍅", "25m"),   // Pomodoro
                ("30 min",   "30m"),
                ("45 min",   "45m"),
                ("1 hour",   "1h"),
                ("2 hours",  "2h"),
            };

            foreach (var (label, code) in presets)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = label,
                    Tag = code,
                    FontSize = 12.5,
                    Padding = new Thickness(10, 6, 10, 6)
                };
                item.Click += (s, ev) =>
                {
                    var tw = new FlyShelf.Windows.TimerWindow((string)((System.Windows.Controls.MenuItem)s).Tag);
                    tw.Show();
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new System.Windows.Controls.Separator());

            // Custom time input
            var customPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10, 4, 10, 6)
            };

            var customInput = new TextBox
            {
                Width = 70,
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 4, 6, 4),
                ToolTip = "e.g. 12, 90, 3:30",
                Foreground = (Brush)(TryFindResource("MicaWPF.Brushes.TextFillColorPrimary") ?? Brushes.White),
                Background = (Brush)(TryFindResource("MicaWPF.Brushes.SubtleFillColorSecondary") ?? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))),
                BorderBrush = (Brush)(TryFindResource("MicaWPF.Brushes.ControlStrokeColorDefault") ?? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))),
                BorderThickness = new Thickness(1),
                Text = ""
            };

            var unitLabel = new TextBlock
            {
                Text = "min",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)(TryFindResource("MicaWPF.Brushes.TextFillColorTertiary") ?? Brushes.Gray),
                Margin = new Thickness(4, 0, 6, 0)
            };

            var goBtn = new System.Windows.Controls.Button
            {
                Content = "▶",
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Start Timer"
            };

            goBtn.Click += (s, ev) =>
            {
                LaunchCustomTimer(customInput.Text);
                menu.IsOpen = false;
            };

            customInput.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter)
                {
                    LaunchCustomTimer(customInput.Text);
                    menu.IsOpen = false;
                    ev.Handled = true;
                }
            };

            customPanel.Children.Add(customInput);
            customPanel.Children.Add(unitLabel);
            customPanel.Children.Add(goBtn);

            var customMenuItem = new System.Windows.Controls.MenuItem
            {
                Header = customPanel,
                StaysOpenOnClick = true
            };
            menu.Items.Add(customMenuItem);

            menu.IsOpen = true;

            // Focus the custom input after the menu opens
            menu.Opened += (s, ev) =>
            {
                customInput.Dispatcher.InvokeAsync(() =>
                {
                    customInput.Focus();
                    Keyboard.Focus(customInput);
                }, System.Windows.Threading.DispatcherPriority.Input);
            };
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
    }
}
