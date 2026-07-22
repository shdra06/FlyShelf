// ---------------------------------------------------------------
// TodoPanelControl.Menus.cs — Context menus, dropdowns, and
// property editing menus
// Handles: templates menu, priority cycling, due date menu
// (with calendar popup), tag management (preset + custom),
// color picker, sort menu, auto-migrate, recurrence cycling,
// "More" dropdown (consolidated menu for all item properties),
// collapse toggle, and all RoutedEventArgs overloads for
// context menu items (priority setters, due date, tags, color,
// subtask, description, recurrence, trash).
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Helpers;

namespace FlyShelf.Controls
{
    public partial class TodoPanelControl : UserControl
    {
        private DateTime _lastTodoDropdownCloseTime = DateTime.MinValue;

        private void TodoTemplates_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // Prevent event from bubbling further
            if (sender is FrameworkElement fe)
            {
                var menu = new ContextMenu();

                // Helper: colorful emoji menu item using Emoji.Wpf
                MenuItem EmojiMenuItem(string emoji, string label, string[] template)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var emojiBlock = new Emoji.Wpf.TextBlock
                    {
                        Text = emoji, FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    var labelBlock = new TextBlock
                    {
                        Text = label, FontSize = 13,
                        Foreground = new SolidColorBrush(ThemeColors.CatppuccinText),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    sp.Children.Add(emojiBlock);
                    sp.Children.Add(labelBlock);
                    var mi = new MenuItem { Header = sp };
                    mi.Click += (s, ev) => ApplyTodoTemplate(template);
                    return mi;
                }

                menu.Items.Add(EmojiMenuItem("🛒", "Grocery Shopping", s_groceryTemplate));
                menu.Items.Add(EmojiMenuItem("🧹", "Weekly Chores", s_choresTemplate));
                menu.Items.Add(EmojiMenuItem("💼", "Work Standup Routine", s_workStandupTemplate));
                menu.Items.Add(EmojiMenuItem("✈️", "Travel Packing", s_travelPackingTemplate));

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
                item.LastEdited = DateTime.Now;
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
                today.Click += (s, ev) => { item.DueDate = DateTime.Today; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

                var tomorrow = new MenuItem { Header = "📅 Tomorrow" };
                tomorrow.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(1); item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

                var nextWeek = new MenuItem { Header = "📅 Next Week" };
                nextWeek.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(7); item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

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
                        Background = new SolidColorBrush(ThemeColors.CatppuccinSurface),
                        Foreground = new SolidColorBrush(Colors.White),
                        BorderBrush = new SolidColorBrush(ThemeColors.VioletAccentA40),
                        BorderThickness = new Thickness(1)
                    };

                    calendar.SelectedDatesChanged += (cs, cev) =>
                    {
                        if (calendar.SelectedDate.HasValue)
                        {
                            item.DueDate = calendar.SelectedDate.Value.Date;
                            item.LastEdited = DateTime.Now;
                            TodoManager.MarkDirty();
                            popup.IsOpen = false;
                        }
                    };

                    popup.Child = new Border
                    {
                        Background = new SolidColorBrush(ThemeColors.CatppuccinSurface),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(4),
                        BorderBrush = new SolidColorBrush(ThemeColors.VioletAccentA40),
                        BorderThickness = new Thickness(1),
                        Child = calendar
                    };

                    popup.IsOpen = true;
                };

                var clear = new MenuItem { Header = "✕ Clear" };
                clear.Click += (s, ev) => { item.DueDate = null; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

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
                        if (!item.Tags.Remove(capturedTag))
                            item.Tags.Add(capturedTag);
                        // Force property change notification
                        item.Tags = new List<string>(item.Tags);
                        item.LastEdited = DateTime.Now;
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
                        Background = new SolidColorBrush(ThemeColors.CatppuccinSurface),
                        Foreground = new SolidColorBrush(Colors.White),
                        BorderBrush = new SolidColorBrush(ThemeColors.VioletAccentA60),
                        CaretBrush = new SolidColorBrush(Colors.White)
                    };

                    textBox.KeyDown += (ts, te) =>
                    {
                        if (te.Key == Key.Enter && !string.IsNullOrWhiteSpace(textBox.Text))
                        {
                            te.Handled = true;
                            string newTag = textBox.Text.Trim();
                            if (!item.Tags.Remove(newTag))
                                item.Tags.Add(newTag);
                            item.Tags = new List<string>(item.Tags);
                            item.LastEdited = DateTime.Now;
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
                        Background = new SolidColorBrush(ThemeColors.CatppuccinSurface),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(4),
                        BorderBrush = new SolidColorBrush(ThemeColors.VioletAccentA40),
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
                    catch { } // Best-effort: failure is acceptable
                    string capturedHex = hex;
                    mi.Click += (s, ev) =>
                    {
                        item.Color = capturedHex;
                        item.LastEdited = DateTime.Now;
                        TodoManager.MarkDirty();
                    };
                    menu.Items.Add(mi);
                }

                menu.Items.Add(new Separator());

                var clearItem = new MenuItem { Header = "✕ Clear" };
                clearItem.Click += (s, ev) =>
                {
                    item.Color = "";
                    item.LastEdited = DateTime.Now;
                    TodoManager.MarkDirty();
                };
                menu.Items.Add(clearItem);

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
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

                var sortOptions = new (string Label, Wpf.Ui.Controls.SymbolRegular Symbol, TodoSortMode Mode)[]
                {
                    ("Manual",   Wpf.Ui.Controls.SymbolRegular.ArrowSort24,   TodoSortMode.Manual),
                    ("Priority", Wpf.Ui.Controls.SymbolRegular.Flag16,        TodoSortMode.Priority),
                    ("Due Date", Wpf.Ui.Controls.SymbolRegular.CalendarLtr16, TodoSortMode.DueDate),
                    ("A-Z",      Wpf.Ui.Controls.SymbolRegular.ArrowSort24, TodoSortMode.Alphabetical),
                    ("Created",  Wpf.Ui.Controls.SymbolRegular.Timer24,       TodoSortMode.CreatedAt)
                };

                foreach (var (label, symbol, mode) in sortOptions)
                {
                    var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    sp.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = symbol, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
                    sp.Children.Add(new System.Windows.Controls.TextBlock { Text = label });
                    var mi = new MenuItem { Header = sp };
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
                item.LastEdited = DateTime.Now;
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
                    pHigh.Click += (s, ev) => { item.Priority = TodoPriority.High; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };
                    var pMed = new MenuItem { Header = "Medium" };
                    pMed.Icon = Dot("#F59E0B");
                    pMed.Click += (s, ev) => { item.Priority = TodoPriority.Medium; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };
                    var pLow = new MenuItem { Header = "Low" };
                    pLow.Icon = Dot("#22C55E");
                    pLow.Click += (s, ev) => { item.Priority = TodoPriority.Low; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };
                    var pNone = new MenuItem { Header = "Clear Priority" };
                    pNone.Icon = MI("✕", "#6B7280");
                    pNone.Click += (s, ev) => { item.Priority = TodoPriority.None; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };
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
                        item.LastEdited = DateTime.Now;
                        TodoManager.MarkDirty();
                        FocusSubtaskWithCaret(item, subTask, true);
                    };
                    menu.Items.Add(addSub);


                    // Description toggle  —— indigo notes icon
                    bool hasDesc = item.IsDescriptionVisible || !string.IsNullOrWhiteSpace(item.Description);
                    var desc = new MenuItem { Header = hasDesc ? "Hide Description" : "Show Description" };
                    desc.Icon = MI("📝", "#6366F1");
                    desc.Click += (s, ev) =>
                    {
                        item.IsDescriptionVisible = !item.IsDescriptionVisible;
                        item.LastEdited = DateTime.Now;
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
                        item.LastEdited = DateTime.Now;
                        TodoManager.MarkDirty();
                    };
                    menu.Items.Add(rec);

                    menu.Items.Add(new Separator());

                    // Delete  —— red trash icon
                    var deleteItem = new MenuItem { Header = "Delete" };
                    deleteItem.Icon = MI("🗑", "#EF4444");
                    deleteItem.Foreground = new SolidColorBrush(ThemeColors.ErrorRed);
                    deleteItem.Click += (s, ev) =>
                    {
                        if (_selectedTodoDay != null)
                        {
                            // Instant delete — no confirmation needed for todo items
                            TodoManager.DeleteItem(_selectedTodoDay, item);
                            UpdateTodoProgress(_selectedTodoDay);
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
            if (item != null) { item.Priority = TodoPriority.High; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); }
        }

        private void TodoItemPriorityMedium_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.Medium; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); }
        }

        private void TodoItemPriorityLow_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.Low; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); }
        }

        private void TodoItemPriorityNone_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item != null) { item.Priority = TodoPriority.None; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); }
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
            today.Click += (s, ev) => { item.DueDate = DateTime.Today; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

            var tomorrow = new MenuItem { Header = "📅 Tomorrow" };
            tomorrow.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(1); item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

            var nextWeek = new MenuItem { Header = "📅 Next Week" };
            nextWeek.Click += (s, ev) => { item.DueDate = DateTime.Today.AddDays(7); item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

            var clear = new MenuItem { Header = "Clear" };
            clear.Click += (s, ev) => { item.DueDate = null; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };

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
                    if (!tags.Remove(capturedTag)) tags.Add(capturedTag);
                    item.Tags = tags;
                    item.LastEdited = DateTime.Now;
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
                try { mi.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); } catch { } // Best-effort: failure is acceptable
                mi.Click += (s, ev) => { item.Color = capturedHex; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var clearMi = new MenuItem { Header = "Clear" };
            clearMi.Click += (s, ev) => { item.Color = null; item.LastEdited = DateTime.Now; TodoManager.MarkDirty(); };
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
            item.LastEdited = DateTime.Now;
            TodoManager.MarkDirty();
            // Auto-focus the new subtask so user can type immediately
            FocusSubtaskWithCaret(item, newSubtask, true);
        }


        private void TodoItemDescription_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null) return;
            item.IsDescriptionVisible = !item.IsDescriptionVisible;
            item.LastEdited = DateTime.Now;
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
            item.LastEdited = DateTime.Now;
            TodoManager.MarkDirty();
        }

        private void TodoItemTrash_Click(object sender, RoutedEventArgs e)
        {
            var item = GetTodoItemFromMenuContext(sender);
            if (item == null || _selectedTodoDay == null) return;
            // Instant delete — no confirmation needed for todo items
            TodoManager.DeleteItem(_selectedTodoDay, item);
            UpdateTodoProgress(_selectedTodoDay);
        }
    }
}
