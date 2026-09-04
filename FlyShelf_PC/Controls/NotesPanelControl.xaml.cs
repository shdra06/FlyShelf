// ---------------------------------------------------------------
// NotesPanelControl.xaml.cs — Main partial: fields, constructor,
// helpers, events, and the public API called by MainWindow.Notes.cs.
// Business logic is split into partial class files:
//   • NotesPanelControl.Bullets.cs      — bullet CRUD, sub-bullets, images
//   • NotesPanelControl.Freeform.cs     — freeform sections, images, AI improve
//   • NotesPanelControl.Sidebar.cs      — sidebar nav, day/month selection
//   • NotesPanelControl.ContextMenus.cs — context menus, templates, AI, sort, export
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.Models;

namespace FlyShelf.Controls
{
    public partial class NotesPanelControl : UserControl
    {
        /// <summary>Creates a frozen (thread-safe, cheaper) SolidColorBrush.</summary>
        private static SolidColorBrush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static FlyShelf.Windows.ReminderCreateWindow? _activeReminderCreateWindow;
        private NoteDay? _selectedNoteDay = null;
        private int _selectedMonth = -1;
        private int _selectedYear = -1;
        private List<NotesSidebarItem> _sidebarItems = new();

        // CA1861: Static readonly array for notes tag presets
        private static readonly string[] s_notePresetTags = { "Work", "Personal", "Ideas", "Important", "Reference", "Project" };
        private TextBox? _lastFocusedBulletTextBox = null;
        private DateTime _lastBulletAddedTime = DateTime.MinValue;
        private bool _isNotesSidebarCollapsed = false;
        private bool _notesCharLimitWarned = false; // Prevents spamming 5K warning toast
        private const int NOTES_SOFT_LIMIT = 5000;  // Show warning at 5K chars
        private const int NOTES_HARD_LIMIT = 10000; // Hard cap at 10K chars
        private ContextMenu? _activeNoteDropdownMenu = null; // Track open menu for toggle behavior
        private DateTime _lastNoteDropdownCloseTime = DateTime.MinValue; // Guard against rapid re-open
        private ContextMenu? _cachedBulletMoreMenu; // [BTN-18/BTN-19]: Cached menu — Tag carries the active NoteBullet
        private ContextMenu? _activeNotesHeaderMenu = null;
        private DateTime _lastNotesHeaderMenuCloseTime = DateTime.MinValue;
        private string? _notesUndoText = null;  // Stores pre-AI text for undo
        private FreeformSection? _notesUndoSection = null; // Which section the undo applies to
        private bool _freeformBulletMode = false; // True while typing inline bullets in freeform
        private bool _isFolderViewMode = false; // True = Folder view, False = Journal view
        private FlyShelf.Classes.NoteFolder? _selectedFolder = null; // Currently selected folder in folder view

        /// <summary>Fired when the user clicks the Back button to close the notes panel.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>Fired when a text field receives focus and the window needs activation without stealing focus.</summary>
        public event EventHandler? ActivateWithoutStealingFocusRequested;

        /// <summary>Fired when the notes panel needs full window activation (mode toggle, open).</summary>
        public event EventHandler? ActivateWindowRequested;

        public NotesPanelControl()
        {
            InitializeComponent();
        }

        /// <summary>Helper to get the parent MainWindow instance.</summary>
        private MainWindow? GetMainWindow() => Window.GetWindow(this) as MainWindow;

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

        // ═══════════════════════════════════════════════════════════
        // PUBLIC API — Called by MainWindow.Notes.cs coordinator
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize the notes panel for display. Called by OpenNotesPanel() in MainWindow.
        /// </summary>
        public void Initialize(NoteDay today)
        {
            ClearSearch();
            _selectedNoteDay = today;

            // Bind days list
            RebuildSidebar();

            // Ensure sidebar is expanded when opening
            if (_isNotesSidebarCollapsed)
            {
                _isNotesSidebarCollapsed = false;
                NotesSidebarExpandBtn.Visibility = Visibility.Collapsed;
                NotesSidebarBorder.Visibility = Visibility.Visible;
                NotesSidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, null);
                NotesSidebarBorder.Width = double.NaN;
                NotesSidebarColumn.Width = new GridLength(42);
                NotesSidebarCollapseIcon.Text = "◂";
            }

            SelectNoteDay(today);
        }

        /// <summary>
        /// Collapse the sidebar. Called by the auto-collapse timer in MainWindow.Notes.cs.
        /// </summary>
        public void CollapseSidebarIfExpanded()
        {
            if (!_isNotesSidebarCollapsed)
            {
                CollapseNotesSidebar();
            }
        }

        /// <summary>
        /// Update the sync status indicators in the Notes header.
        /// </summary>
        public void UpdateSyncStatus(int peerCount, bool isSynced)
        {
            var colorHex = isSynced ? "#10B981" : "#F59E0B";
            var text = isSynced ? $"Synced ({peerCount})" : "Offline";
            var brush = FrozenBrush(
                (Color)ColorConverter.ConvertFromString(colorHex));

            NotesSyncDot.Fill = brush;
            NotesSyncText.Text = text;
            NotesSyncText.Foreground = brush;
        }

        /// <summary>
        /// Restores keyboard focus to the active text field inside the notes panel.
        /// </summary>
        public void FocusActiveTextBox()
        {
            if (_selectedNoteDay == null) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (_selectedNoteDay.IsFreeformMode)
                {
                    FocusFreeformLastSection();
                }
                else
                {
                    // Focus last focused bullet TextBox if it's still valid
                    if (_lastFocusedBulletTextBox != null && _lastFocusedBulletTextBox.IsLoaded && _lastFocusedBulletTextBox.IsVisible)
                    {
                        _lastFocusedBulletTextBox.Focus();
                        Keyboard.Focus(_lastFocusedBulletTextBox);
                    }
                    else if (_selectedNoteDay.Bullets.Count > 0)
                    {
                        // Fallback: focus first bullet's TextBox
                        var firstBullet = _selectedNoteDay.Bullets.First();
                        var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(firstBullet);
                        if (container is ContentPresenter cp)
                        {
                            var tb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                            if (tb != null)
                            {
                                tb.Focus();
                                Keyboard.Focus(tb);
                            }
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded); // Loaded priority: layout pass has completed, containers are realized
        }

        /// <summary>
        /// Clears notes search results and restores normal note content visibility.
        /// </summary>
        public void ClearSearch()
        {
            NotesSearchResultsList.ItemsSource = null;
            NotesSearchResults.Visibility = Visibility.Collapsed;
            NotesContentArea.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Apply notes search from the shared search box. Called by MainWindow.Search.cs.
        /// </summary>
        public void ApplySearch(string query)
        {
            string queryClean = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(queryClean))
            {
                ClearSearch();
                return;
            }

            var results = NoteManager.Search(queryClean);

            // Build display items
            var displayItems = results.Select(r =>
            {
                string pageLabel = "";
                string? sectionId = null;

                if (r.Bullet?.Id != null && r.Bullet.Id.StartsWith("section_", StringComparison.Ordinal))
                {
                    sectionId = r.Bullet.Id.Substring("section_".Length);
                }

                // Check if Header has a "Page X" prefix
                string header = r.Bullet?.Header ?? "";
                string contentText;

                if (header.StartsWith("Page ", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIdx = header.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        pageLabel = header.Substring(0, colonIdx).Trim();
                        string titlePart = header.Substring(colonIdx + 1).Trim();
                        contentText = string.IsNullOrEmpty(titlePart)
                            ? (r.Bullet?.Content ?? "")
                            : $"[{titlePart}] {r.Bullet?.Content}";
                    }
                    else
                    {
                        pageLabel = header.Trim();
                        contentText = r.Bullet?.Content ?? "";
                    }
                }
                else if (!string.IsNullOrEmpty(header))
                {
                    contentText = $"[{header}] {r.Bullet?.Content}";
                }
                else
                {
                    contentText = r.Bullet?.Content ?? "";
                }

                return new NoteSearchResult
                {
                    DateLabel = r.Day.DisplayDate,
                    PageLabel = pageLabel,
                    Content = contentText,
                    Day = r.Day,
                    Bullet = r.Bullet,
                    SectionId = sectionId
                };
            }).ToList();

            NotesSearchResultsList.ItemsSource = displayItems;
            NotesSearchResults.Visibility = Visibility.Visible;
            NotesContentArea.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Select a specific day and optionally focus a specific section.
        /// </summary>
        public void SelectDay(NoteDay day, string? targetSectionId = null)
        {
            SelectNoteDay(day, targetSectionId);
        }

        /// <summary>
        /// Close month picker popup if open. Called by CloseNotesPanel().
        /// </summary>
        public void CloseMonthPopup()
        {
            NotesMonthPopup.IsOpen = false;
        }

        /// <summary>
        /// Clear last focused bullet textbox reference. Called by CloseNotesPanel().
        /// </summary>
        public void ClearFocusState()
        {
            _lastFocusedBulletTextBox = null;
        }

        /// <summary>Whether the sidebar is currently collapsed.</summary>
        public bool IsSidebarCollapsed => _isNotesSidebarCollapsed;
    }
}
