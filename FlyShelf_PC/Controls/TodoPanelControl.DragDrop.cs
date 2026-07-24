// ---------------------------------------------------------------
// TodoPanelControl.DragDrop.cs — Drag-and-drop reordering &
// keyboard shortcuts for item movement
// Handles: mouse-initiated drag start, drag-over visual feedback,
// drop handling with index recalculation, sort order update,
// and Ctrl+Shift+↑/↓ keyboard shortcuts for reordering.
// Also handles Ctrl+D keyboard delete and the click-to-expand
// logic that gates drag initiation.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf.Controls
{
    public partial class TodoPanelControl : UserControl
    {
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
                if (skipToggle)
                {
                    _todoDragItem = null;
                    return;
                }

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

        private void TodoItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _todoDragItem = null;
        }

        private void TodoItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_todoDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPos = e.GetPosition(null);
            Vector diff = _todoDragStartPoint - currentPos;

            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
            {
                if (sender is FrameworkElement fe && _todoDragItem != null)
                {
                    var itemToDrag = _todoDragItem;
                    _todoDragItem = null; // Clear BEFORE starting DoDragDrop to avoid re-entrancy state issues

                    try
                    {
                        var data = new DataObject(DataFormats.Serializable, itemToDrag);
                        DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("TODO_DRAG_ERR", $"DoDragDrop failed safely: {ex.Message}");
                    }
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
            _todoDragItem = null;

            try
            {
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
            }
            catch (Exception ex)
            {
                Logger.LogAction("TODO_DROP_ERR", $"Drop failed safely: {ex.Message}");
            }
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
                // Instant delete — no confirmation needed for todo items
                if (Keyboard.FocusedElement is FrameworkElement focused && focused.DataContext is TodoItem item)
                {
                    TodoManager.DeleteItem(_selectedTodoDay, item);
                    UpdateTodoProgress(_selectedTodoDay);
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
    }
}
