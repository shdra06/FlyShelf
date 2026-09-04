// ---------------------------------------------------------------
// TodoPanelControl.Items.cs — Todo item CRUD & text editing
// Handles: add/insert/delete items, focus management, text
// change/focus events, checkbox toggle, keyboard navigation
// (Enter/Up/Down/Back/Delete), subtask keyboard navigation,
// subtask text changes, description text changes, and
// finding parent todo items.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf.Controls
{
    public partial class TodoPanelControl : UserControl
    {
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
                TodoListItemsControl.UpdateLayout(); // Required: force container generation for newly added item
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
                TodoListItemsControl.UpdateLayout(); // Required: ensure container exists for target item
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
                var parentContainer = TodoListItemsControl.ItemContainerGenerator.ContainerFromItem(parent);
                if (parentContainer is ContentPresenter cp)
                {
                    var subtaskList = FindVisualChild<ItemsControl>(cp, "TodoSubtasksList");
                    if (subtaskList != null)
                    {
                        subtaskList.UpdateLayout(); // Required: single layout pass for subtask container generation
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
                item.LastEdited = DateTime.Now;
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
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
                item.LastEdited = DateTime.Now;
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
                    item.LastEdited = DateTime.Now;
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
                    subtask.LastEdited = DateTime.Now;
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
                parentItem.LastEdited = DateTime.Now;
                TodoManager.MarkDirty();
            }
        }

        private void TodoSubtaskCheck_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem subTask)
            {
                subTask.IsDone = !subTask.IsDone;
                subTask.LastEdited = DateTime.Now;

                // Walk up to find the parent TodoItem and refresh its SubTaskProgress
                var parent = fe;
                while (parent != null)
                {
                    parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                    if (parent?.DataContext is TodoItem parentItem && parentItem != subTask && parentItem.SubTasks.Contains(subTask))
                    {
                        parentItem.LastEdited = DateTime.Now;
                        parentItem.RefreshDisplayProperties();
                        break;
                    }
                }

                TodoManager.MarkDirty();
            }
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
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
                item.LastEdited = DateTime.Now;
            TodoManager.MarkDirty();
        }

        // ═══════════════════════════════════════════════════════════
        // DELETE (with confirmation)
        // ═══════════════════════════════════════════════════════════

        private void TodoItemTrash_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TodoItem item && _selectedTodoDay != null)
            {
                // Instant delete — no confirmation needed for todo items
                TodoManager.DeleteItem(_selectedTodoDay, item);
                UpdateTodoProgress(_selectedTodoDay);
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
                item.LastEdited = DateTime.Now;
                TodoManager.MarkDirty();
            }
        }

        private void TodoSubtaskText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TodoItem item)
            {
                // Text is already bound TwoWay, just mark dirty
                item.LastEdited = DateTime.Now;
                TodoManager.MarkDirty();
            }
        }
    }
}
