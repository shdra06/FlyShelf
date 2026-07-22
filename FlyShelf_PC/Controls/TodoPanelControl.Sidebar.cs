// ---------------------------------------------------------------
// TodoPanelControl.Sidebar.cs — Day selection & sidebar management
// Handles: day selection, sidebar expand/collapse toggle,
// sidebar visual selection update, active textbox focus,
// and progress tracking.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Helpers;

namespace FlyShelf.Controls
{
    public partial class TodoPanelControl : UserControl
    {
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
                            var bg = new SolidColorBrush(ThemeColors.VioletAccentA2A);
                            bg.Freeze();
                            mainBorder.Background = bg;
                            var border = new SolidColorBrush(ThemeColors.VioletAccentA60);
                            border.Freeze();
                            mainBorder.BorderBrush = border;
                        }
                        else
                        {
                            var bg = new SolidColorBrush(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF));
                            bg.Freeze();
                            mainBorder.Background = bg;
                            var border = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
                            border.Freeze();
                            mainBorder.BorderBrush = border;
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
    }
}
