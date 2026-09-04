// ---------------------------------------------------------------
// TodoPanelControl — Self-contained Todo UserControl
// Extracted from MainWindow.Todo.cs (Decomposition Phase 4).
// Contains all todo business logic: task CRUD, date navigation,
// sidebar, search, templates, stopwatch, categories, timers.
// MainWindow.Todo.cs coordinates panel visibility via Open/Close.
//
// Partial class files:
//   TodoPanelControl.xaml.cs       — Constructor, fields, public API, initialization
//   TodoPanelControl.Sidebar.cs    — Day selection, sidebar toggle, progress
//   TodoPanelControl.Items.cs      — Item CRUD, focus, text editing, keyboard nav
//   TodoPanelControl.Timers.cs     — Stopwatch, item timer, reminder, preset cycling
//   TodoPanelControl.Menus.cs      — Context menus, dropdowns, property menus
//   TodoPanelControl.DragDrop.cs   — Drag-and-drop reordering, keyboard shortcuts
//   TodoPanelControl.Search.cs     — Fuzzy search across all days
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
        private static FlyShelf.Windows.TimerWindow? _activeTimerWindow;
        private static FlyShelf.Windows.ReminderCreateWindow? _activeTodoReminderWindow;
        private TodoDay? _selectedTodoDay = null;
        private TextBox? _lastFocusedTodoTextBox = null;
        private DateTime _lastTodoItemAddedTime = DateTime.MinValue;
        private bool _isTodoSidebarCollapsed = false;
        private ContextMenu? _activeTodoDropdownMenu = null; // Track open menu for toggle behavior

        // CA1861: Static readonly arrays for todo templates (avoid repeated heap allocations)
        private static readonly string[] s_groceryTemplate = { "Buy milk", "Buy eggs", "Buy veggies", "Buy bread", "Buy fruits" };
        private static readonly string[] s_choresTemplate = { "Clean room", "Do laundry", "Throw trash", "Vacuum floor" };
        private static readonly string[] s_workStandupTemplate = { "Check emails & Slack", "Update Jira tickets", "Team standup meeting", "Plan daily tasks" };
        private static readonly string[] s_travelPackingTemplate = { "Pack passport & documents", "Pack chargers & electronics", "Pack clothes & shoes", "Pack toiletries" };

        /// <summary>Fired when the user clicks the Back button to close the todo panel.</summary>
        public event EventHandler? CloseRequested;

#pragma warning disable CS0067
        /// <summary>Fired when the todo panel needs window activation without stealing focus.</summary>
        public event EventHandler? ActivateWithoutStealingFocusRequested;
#pragma warning restore CS0067

        public TodoPanelControl()
        {
            InitializeComponent();
        }

        /// <summary>Helper to find a named child in the visual tree.</summary>
        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && t.Name == name) return t;
                var result = FindVisualChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        private MainWindow? GetMainWindow() => Window.GetWindow(this) as MainWindow;

        // ═══════════════════════════════════════════════════════════
        // PUBLIC API — Called by MainWindow.Todo.cs coordinator
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize the todo panel for display. Called by OpenTodoPanel() in MainWindow.
        /// </summary>
        public void Initialize(TodoDay today)
        {
            ClearSearch();

            // Bind days list
            TodoDaySidebar.ItemsSource = TodoManager.Days;

            // Start with sidebar collapsed — user can expand via chevron
            if (!_isTodoSidebarCollapsed)
            {
                _isTodoSidebarCollapsed = true;
                TodoSidebarBorder.Visibility = Visibility.Collapsed;
                TodoSidebarColumn.Width = new GridLength(0);
                TodoSidebarCollapseIcon.Text = "▸";
                TodoSidebarExpandBtn.Visibility = Visibility.Visible;
            }

            SelectTodoDay(today);
        }

        /// <summary>
        /// Restores keyboard focus to the active text field inside the todo panel.
        /// </summary>
        public void FocusActiveTextBox()
        {
            FocusTodoActiveTextBox();
        }

        /// <summary>
        /// Apply todo search from the shared search box.
        /// </summary>
        public void ApplySearch(string query)
        {
            ApplyTodoSearch(query);
        }

        /// <summary>
        /// Update the sync status indicators in the todo header.
        /// </summary>
        public void UpdateSyncStatus(int count, bool isSynced)
        {
            var colorHex = isSynced ? "#10B981" : "#F59E0B";
            var text = isSynced ? $"Synced ({count})" : "Offline";
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(colorHex));

            TodoSyncDot.Fill = brush;
            TodoSyncText.Text = text;
            TodoSyncText.Foreground = brush;
        }

        // ═══════════════════════════════════════════════════════════
        // BACK BUTTON
        // ═══════════════════════════════════════════════════════════

        private void TodoBack_Click(object sender, MouseButtonEventArgs e)
        {
            ClearSearch();
            var mainWin = GetMainWindow();
            if (mainWin != null && mainWin.IsSearchActive)
            {
                mainWin.CloseSearch();
            }
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════════════════════
        // PANEL PREVIEW MOUSE DOWN — forwarded to MainWindow for activation
        // ═══════════════════════════════════════════════════════════

        // Note: TodoPanel_PreviewMouseDown is handled at the Grid wrapper level in MainWindow.
        // The UserControl raises ActivateWithoutStealingFocusRequested for internal needs.
    }
}
