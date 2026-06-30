// ---------------------------------------------------------------
// MainWindow — Quick Notes Panel
// Toggle, navigation, bullet CRUD, freeform mode, search, images.
// Split from MainWindow.Search.cs for modularity.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private static FlyShelf.Windows.ReminderCreateWindow? _activeReminderCreateWindow;
        private bool _isNotesActive = false;
        public bool IsNotesActive => _isNotesActive;
        private System.Windows.Threading.DispatcherTimer? _panelAutoRevertTimer;
        private bool _isNotesLoaded = false;
        private NoteDay? _selectedNoteDay = null;
        private int _selectedMonth = -1;
        private int _selectedYear = -1;
        private List<NotesSidebarItem> _sidebarItems = new();
        private Brush? _originalHeaderBg = null;
        private static readonly SolidColorBrush _notesHeaderBrush = new(Color.FromRgb(0x1A, 0x1A, 0x2E));
        private TextBox? _lastFocusedBulletTextBox = null;
        private DateTime _lastBulletAddedTime = DateTime.MinValue;
        private bool _isNotesSidebarCollapsed = false;
        private System.Windows.Threading.DispatcherTimer? _notesSidebarAutoCollapseTimer;
        private bool _notesCharLimitWarned = false; // Prevents spamming 5K warning toast
        private const int NOTES_SOFT_LIMIT = 5000;  // Show warning at 5K chars
        private const int NOTES_HARD_LIMIT = 10000; // Hard cap at 10K chars
        private ContextMenu? _activeNoteDropdownMenu = null; // Track open menu for toggle behavior
        private DateTime _lastNoteDropdownCloseTime = DateTime.MinValue; // Guard against rapid re-open
        private ContextMenu? _activeNotesHeaderMenu = null;
        private DateTime _lastNotesHeaderMenuCloseTime = DateTime.MinValue;
        private string? _notesUndoText = null;  // Stores pre-AI text for undo
        private FreeformSection? _notesUndoSection = null; // Which section the undo applies to
        private bool _freeformBulletMode = false; // True while typing inline bullets in freeform

        // ═══════════════════════════════════════════════════════════
        // TOGGLE NOTES PANEL
        // ═══════════════════════════════════════════════════════════

        private void NotesToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isNotesActive)
                CloseNotesPanel();
            else
                OpenNotesPanel();
        }

        private void OpenNotesPanel()
        {

            // Close other modes
            if (_isTodoActive) CloseTodoPanel(immediate: true);
            if (_isResearchActive) CloseResearchPanel(immediate: true);
            if (_isAiSettingsActive) CloseAiSettingsPanel(immediate: true);
            if (_isSearchActive) CloseSearch(switchingPanel: true);
            if (_isFilterBarActive) ToggleFilterBar(false);
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            // Lazy-load notes data on first open
            if (!_isNotesLoaded)
            {
                NoteManager.Load();
                _isNotesLoaded = true;
            }


            // Ensure today exists and select it
            var today = NoteManager.EnsureToday();
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

            // Auto-collapse sidebar after 10 seconds
            if (_notesSidebarAutoCollapseTimer != null)
            {
                _notesSidebarAutoCollapseTimer.Stop();
            }
            else
            {
                _notesSidebarAutoCollapseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(10)
                };
                _notesSidebarAutoCollapseTimer.Tick += (s, ev) =>
                {
                    _notesSidebarAutoCollapseTimer.Stop();
                    if (_isNotesActive && !_isNotesSidebarCollapsed)
                    {
                        CollapseNotesSidebar();
                    }
                };
            }
            _notesSidebarAutoCollapseTimer.Start();

            _isNotesActive = true;
            // NOTE: No auto-revert timer — Notes panel should never auto-hide

            // Update taskbar/alt-tab title
            Title = "Notes";

            // Update window activation style dynamically so clicking it works
            UpdateWindowActivationStyle();

            // ─── FOCUS FIX: Force-activate and topmost-cycle to grab OS focus ───
            ActivateNotesWindow();

            // Hide clipboard, show notes
            ShelfListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            NotesPanel.Visibility = Visibility.Visible;

            // ─── HEADER: Match the opaque notes dark theme ───
            if (_originalHeaderBg == null)
                _originalHeaderBg = HeaderAndFiltersStack.Background;
            HeaderAndFiltersStack.Background = _notesHeaderBrush;
            // Also apply ClearType hints to the header while notes are active
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(HeaderAndFiltersStack, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(HeaderAndFiltersStack, ClearTypeHint.Enabled);

            // Swap notes button to clipboard icon (acts as "go back" button)
            NotesToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24 };
            NotesToggleBtn.ToolTip = "Back to Clipboard";

            // Swap filter button → reminders button in Notes mode
            if (SortFilterBtn != null)
            {
                SortFilterBtn.Icon = null;
                SortFilterBtn.Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Alert24, FontSize = 15 };
                SortFilterBtn.ToolTip = "Reminders";
            }

            // Animate in
            var slideAnim = Classes.AnimationHelper.SlideIn(fromY: -12, durationMs: 200);
            var fadeAnim = Classes.AnimationHelper.FadeIn(durationMs: 200);
            if (NotesPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);

            SelectNoteDay(today);
        }

        /// <summary>
        /// Force the MainWindow to become the active foreground window.
        /// This is critical because FlyShelf uses ShowActivated="False" and is normally
        /// a non-activating overlay. Without this, typing may go to the previously focused app.
        /// </summary>
        private void ActivateNotesWindow()
        {
            // Step 1: Suppress DWM accent border before Activate() triggers it
            SuppressDwmBorder();

            // Step 2: Activate the WPF window (requests OS focus)
            this.Activate();

            // Step 3: Temporarily toggle Topmost to force Win32 SetForegroundWindow
            if (!this.Topmost)
            {
                this.Topmost = true;
                this.Topmost = false;
            }

            // Step 4: Set keyboard focus to the notes panel itself
            this.Focus();
        }

        /// <summary>
        /// Activates the WPF window and brings it to the foreground without stealing keyboard
        /// focus from child text elements that the user is trying to click on.
        /// </summary>
        private void ActivateWindowWithoutStealingFocus()
        {
            // Suppress DWM accent border before Activate() triggers it
            SuppressDwmBorder();

            if (!this.IsActive)
            {
                this.Activate();
                if (!this.Topmost)
                {
                    this.Topmost = true;
                    this.Topmost = false;
                }
            }
        }

        /// <summary>
        /// Removes the DWM-drawn accent border (appears red/accent-colored) that Windows
        /// applies when a window is activated via Activate(). Clipboard mode never calls
        /// Activate() so it never gets this border.
        /// </summary>
        private void SuppressDwmBorder()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int cn = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Updates the WS_EX_NOACTIVATE style dynamically based on the notes panel state.
        /// When in notes mode, we remove WS_EX_NOACTIVATE so clicking the window activates it.
        /// When not in notes mode, we add it back so it stays a non-activating overlay.
        /// </summary>
        private void UpdateWindowActivationStyle()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                if (_isNotesActive || _isTodoActive || _isSearchActive || _isResearchActive)
                {
                    // Remove WS_EX_NOACTIVATE so the window can receive keyboard focus
                    // DO NOT add WS_EX_APPWINDOW — it unpins the window from all virtual
                    // desktops and causes cross-desktop spawning failures.
                    exStyle = exStyle & ~WS_EX_NOACTIVATE;
                    SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
                }
                else
                {
                    // Restore WS_EX_NOACTIVATE for clipboard overlay mode
                    exStyle = exStyle & ~WS_EX_APPWINDOW; // Ensure APPWINDOW is never left on
                    exStyle = exStyle | WS_EX_NOACTIVATE;
                    SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
                }

                // Force frame to update style changes immediately
                Classes.NativeMethods.SetWindowPos(
                    helper.Handle,
                    0, 0, 0, 0, 0,
                    Classes.NativeMethods.SWP_NOMOVE |
                    Classes.NativeMethods.SWP_NOSIZE |
                    Classes.NativeMethods.SWP_NOZORDER |
                    Classes.NativeMethods.SWP_NOACTIVATE |
                    0x0020 // SWP_FRAMECHANGED
                );

                // No need to re-pin — we never unpin because WS_EX_APPWINDOW is never set
            }
        }

        /// <summary>
        /// Restores keyboard focus to the active text field inside the notes panel.
        /// </summary>
        private void FocusNotesActiveTextBox()
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
                        NotesBulletList.UpdateLayout(); // Force container generation!
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
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CloseNotesPanel(bool immediate = false)
        {
            _isNotesActive = false;

            // Close month picker popup if open
            NotesMonthPopup.IsOpen = false;

            // Restore taskbar/alt-tab title
            Title = "FlyShelf";

            // Restore non-activating window style
            UpdateWindowActivationStyle();

            // Clear last focused bullet textbox reference
            _lastFocusedBulletTextBox = null;


            // Restore notes button icon and tooltip
            NotesToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.List24 };
            NotesToggleBtn.ToolTip = "Quick Notes";
            NotesToggleBtn.ClearValue(ForegroundProperty);

            // Restore filter button from reminder mode
            if (SortFilterBtn != null)
            {
                SortFilterBtn.Content = null;
                SortFilterBtn.Icon = new Wpf.Ui.Controls.FontIcon { Glyph = "\uE71C", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
                SortFilterBtn.ToolTip = "Filter by Category";
            }

            // ─── HEADER: Restore original transparent/Mica background ───
            HeaderAndFiltersStack.Background = _originalHeaderBg ?? Brushes.Transparent;
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(HeaderAndFiltersStack, TextRenderingMode.Auto);
            RenderOptions.SetClearTypeHint(HeaderAndFiltersStack, ClearTypeHint.Auto);

            if (immediate)
            {
                // Instant close — no animation (used when switching to another panel)
                NotesPanel.BeginAnimation(OpacityProperty, null);
                NotesPanel.Opacity = 0;
                NotesPanel.Visibility = Visibility.Collapsed;
                ShelfListView.Visibility = Visibility.Visible;
                // Let the XAML DataTrigger on DroppedItems.Count control visibility
                EmptyStatePanel.ClearValue(VisibilityProperty);

                // NM-FIX: Save synchronously — deferred async saves were being dropped
                NoteManager.SaveNow();
                return;
            }

            // Normal close path: save synchronously (no spawn pipeline follows)
            NoteManager.SaveNow();

            // Animate out
            var fadeAnim = Classes.AnimationHelper.FadeOut();
            fadeAnim.Completed += (s, a) =>
            {
                if (!_isNotesActive)
                {
                    NotesPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    // Restore empty state if needed
                    // Let the XAML DataTrigger on DroppedItems.Count control visibility
                    EmptyStatePanel.ClearValue(VisibilityProperty);
                }
            };
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private void NotesBack_Click(object sender, MouseButtonEventArgs e)
        {
            CloseNotesPanel();
        }

        // ═══════════════════════════════════════════════════════════
        // FOCUS CAPTURE: Clicking anywhere in notes panel activates window
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// PreviewMouseDown on the entire NotesPanel grid.
        /// Ensures the window captures OS focus when user clicks ANYWHERE inside notes.
        /// Without this, keyboard input may still go to the previously focused app.
        /// </summary>
        private void NotesPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Skip activation when click targets popup-triggering elements (month picker, templates).
            // PreviewMouseDown (tunnel) fires before MouseLeftButtonDown; calling Activate()
            // during the tunnel phase immediately closes Popups/ContextMenus that are about to open.
            if (e.OriginalSource is DependencyObject source)
            {
                var parent = source;
                while (parent != null)
                {
                    if (parent is FrameworkElement fe && 
                        (fe.Name == "NotesMonthPickerBtn" || fe.Name == "NotesTemplatesBtn"))
                        return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
            ActivateWindowWithoutStealingFocus();
        }

        // ═══════════════════════════════════════════════════════════
        // DAY SELECTION (SIDEBAR)
        // ═══════════════════════════════════════════════════════════

        private void SelectNoteDay(NoteDay day)
        {
            // Auto-determine mode based on existing content
            bool hasBullets = day.Bullets.Any(b => !string.IsNullOrWhiteSpace(b.Header) || !string.IsNullOrWhiteSpace(b.Content) || b.HasImage);
            bool hasFreeform = !string.IsNullOrWhiteSpace(day.FreeformContent) || day.FreeformImages.Count > 0;

            if (hasBullets && !hasFreeform)
            {
                day.IsFreeformMode = false;
            }
            else if (hasFreeform && !hasBullets)
            {
                day.IsFreeformMode = true;
            }
            else if (!hasBullets && !hasFreeform)
            {
                // New/empty notes: always open in freeform mode by default
                day.IsFreeformMode = true;
            }

            _selectedNoteDay = day;
            _selectedMonth = -1;
            _selectedYear = -1;
            _notesCharLimitWarned = false; // Reset warning flag for the new note
            _freeformBulletMode = false;   // Reset inline-bullet mode for new note

            // Clear search if active
            if (_isSearchActive)
            {
                CloseSearch();
            }

            // Update sidebar selection highlight
            UpdateSidebarSelectionVisuals();

            // Bind content
            NotesBulletList.ItemsSource = day.Bullets;
            day.MigrateFreeformIfNeeded(); // Ensure at least one section exists
            NotesFreeformSectionsList.ItemsSource = day.FreeformSections;

            // Show correct mode
            if (day.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";
                // Defer focus to last freeform section text box
                Dispatcher.InvokeAsync(() =>
                {
                    FocusFreeformLastSection();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformArea.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";

                // Auto-create a first bullet if the day is empty so user can start typing immediately
                if (day.Bullets.Count == 0)
                {
                    _lastBulletAddedTime = DateTime.MinValue; // Reset cooldown
                    AddNewBulletAndFocus();
                }
                else
                {
                    // Auto-focus the last bullet's content text box
                    FocusNotesActiveTextBox();
                }
            }

            // Update day label
            NotesCurrentDayLabel.Text = "Notes · " + day.DisplayDate;
            UpdateNoteBulletCount();
        }

        private void RebuildSidebar()
        {
            // Direct-bind to NoteManager.Days — same pattern as Todo sidebar
            NotesDaySidebar.ItemsSource = NoteManager.Days;
        }

        private void UpdateSidebarSelectionVisuals()
        {
            if (NotesDaySidebar == null) return;

            for (int i = 0; i < NotesDaySidebar.Items.Count; i++)
            {
                var item = NotesDaySidebar.Items[i];
                var container = NotesDaySidebar.ItemContainerGenerator.ContainerFromItem(item);
                if (container is ContentPresenter cp)
                {
                    var mainBorder = FindVisualChild<Border>(cp, "NotesDayBorder");
                    if (mainBorder != null)
                    {
                        bool isSelected = item == _selectedNoteDay;
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

        private void SelectNoteMonth(int month, int year)
        {
            _selectedNoteDay = null;
            _selectedMonth = month;
            _selectedYear = year;

            if (_isSearchActive)
            {
                CloseSearch();
            }

            var monthDate = new DateTime(year, month, 1);
            NotesCurrentDayLabel.Text = "Notes · " + monthDate.ToString("MMMM yyyy");

            UpdateSidebarSelectionVisuals();

            var monthDays = NoteManager.Days.Where(d => d.Date.Month == month && d.Date.Year == year).ToList();
            var combinedBullets = new ObservableCollection<NoteBullet>();
            foreach (var d in monthDays)
            {
                foreach (var b in d.Bullets)
                {
                    combinedBullets.Add(b);
                }
            }

            NotesBulletList.ItemsSource = combinedBullets;

            NotesBulletList.Visibility = Visibility.Visible;
            NotesFreeformArea.Visibility = Visibility.Collapsed;
            NotesModeToggleText.Text = "📄 Month View";
        }

        private void CurrentDayLabel_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay != null)
            {
                // Currently viewing a specific day — switch to month view
                SelectNoteMonth(_selectedNoteDay.Date.Month, _selectedNoteDay.Date.Year);
            }
            else if (_selectedMonth != -1 && _selectedYear != -1)
            {
                // Currently in month view — navigate to the most recent day
                var newestDay = NoteManager.Days
                    .Where(d => d.Date.Month == _selectedMonth && d.Date.Year == _selectedYear)
                    .OrderByDescending(d => d.Date)
                    .FirstOrDefault();
                if (newestDay != null)
                {
                    SelectNoteDay(newestDay);
                }
            }
        }

        private void CollapseNotesSidebar()
        {
            _isNotesSidebarCollapsed = true;
            NotesSidebarBorder.Visibility = Visibility.Collapsed;
            NotesSidebarColumn.Width = new GridLength(0);
            NotesSidebarCollapseIcon.Text = "▸";
            NotesSidebarExpandBtn.Visibility = Visibility.Visible;
        }

        private void NotesSidebarToggle_Click(object sender, MouseButtonEventArgs e)
        {
            // Cancel auto-collapse timer on manual interaction
            _notesSidebarAutoCollapseTimer?.Stop();

            _isNotesSidebarCollapsed = !_isNotesSidebarCollapsed;

            if (_isNotesSidebarCollapsed)
            {
                CollapseNotesSidebar();
            }
            else
            {
                // Expand: show sidebar border and restore column width
                NotesSidebarExpandBtn.Visibility = Visibility.Collapsed;
                NotesSidebarBorder.Visibility = Visibility.Visible;
                NotesSidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, null); // Clear any leftover animation
                NotesSidebarBorder.Width = double.NaN;
                NotesSidebarColumn.Width = new GridLength(42);
                NotesSidebarCollapseIcon.Text = "◂";
            }
        }

        private void NotesDayItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteDay day)
            {
                SelectNoteDay(day);
            }
        }

        private NoteDay? GetTargetDayForAdd()
        {
            if (_selectedNoteDay != null) return _selectedNoteDay;

            if (_selectedMonth != -1 && _selectedYear != -1)
            {
                var today = DateTime.Today;
                if (today.Month == _selectedMonth && today.Year == _selectedYear)
                {
                    return NoteManager.GetOrCreateDay(today);
                }

                var newest = NoteManager.Days
                    .Where(d => d.Date.Month == _selectedMonth && d.Date.Year == _selectedYear)
                    .OrderByDescending(d => d.Date)
                    .FirstOrDefault();

                if (newest != null) return newest;

                return NoteManager.GetOrCreateDay(new DateTime(_selectedYear, _selectedMonth, 1));
            }
            return null;
        }

        private void AddNewBulletAndFocus()
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            // ── Empty-card guard ────────────────────────────────────
            // If the last bullet is already completely empty, just focus it
            // instead of stacking another blank card on top of it.
            if (targetDay.Bullets.Count > 0)
            {
                var last = targetDay.Bullets[^1];
                bool lastIsEmpty = string.IsNullOrWhiteSpace(last.Header)
                                && string.IsNullOrWhiteSpace(last.Content)
                                && last.SubBullets.Count == 0
                                && !last.HasImage && !last.HasImage2;
                if (lastIsEmpty)
                {
                    // Focus that existing empty card's content box
                    Dispatcher.InvokeAsync(() =>
                    {
                        NotesBulletList.UpdateLayout();
                        var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(last);
                        if (container is ContentPresenter cp)
                        {
                            var tb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                            tb?.Focus();
                            if (tb != null) Keyboard.Focus(tb);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
            }

            // Spam proof check: enforce 1 second cooldown
            if ((DateTime.Now - _lastBulletAddedTime).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastBulletAddedTime = DateTime.Now;

            var bullet = NoteManager.AddBullet(targetDay);

            if (_selectedNoteDay == null && _selectedMonth != -1)
            {
                RebuildSidebar();
                SelectNoteMonth(_selectedMonth, _selectedYear);
            }

            // Focus the new bullet's TextBox after render
            Dispatcher.InvokeAsync(() =>
            {
                NotesBulletList.UpdateLayout(); // Force container generation!
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(bullet);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Adds a new SubBulletItem to the parent NoteBullet that currently has keyboard focus,
        /// then focuses the new sub-bullet's TextBox.
        /// </summary>
        private void AddSubBulletAndFocus(NoteBullet parentBullet)
        {
            if (parentBullet == null) return;

            // Ensure the card is expanded so sub-bullets are visible
            parentBullet.IsCollapsed = false;

            var sub = new FlyShelf.Classes.SubBulletItem();
            parentBullet.SubBullets.Add(sub);
            parentBullet.OnSubBulletsChanged(); // notify HasSubBullets
            NoteManager.MarkDirty();

            // Focus the new sub-bullet TextBox after the ItemsControl renders it
            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                if (container is ContentPresenter cp)
                {
                    var ic = FindVisualChild<ItemsControl>(cp, "SubBulletsItemsControl");
                    if (ic != null)
                    {
                        ic.UpdateLayout();
                        var subContainer = ic.ItemContainerGenerator.ContainerFromItem(sub);
                        if (subContainer is ContentPresenter subCp)
                        {
                            var tb = FindVisualChild<TextBox>(subCp, "SubBulletTextBox");
                            tb?.Focus();
                            if (tb != null) Keyboard.Focus(tb);
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Key handler for sub-bullet TextBoxes:
        ///   Enter            → create next sub-bullet in same parent
        ///   Shift+Enter      → dismantle (collapse) sub-bullets, return focus to card body
        ///   Backspace(empty) → remove this sub-bullet, focus previous or parent
        /// </summary>
        private void SubBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (tb.Tag is not FlyShelf.Classes.SubBulletItem sub) return;

            // Walk up the visual tree to find the parent NoteBullet via DataContext
            NoteBullet? parentBullet = null;
            DependencyObject? walk = VisualTreeHelper.GetParent(tb);
            while (walk != null)
            {
                if (walk is FrameworkElement fe && fe.DataContext is NoteBullet nb)
                {
                    parentBullet = nb;
                    break;
                }
                walk = VisualTreeHelper.GetParent(walk);
            }
            if (parentBullet == null) return;

            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift+Enter → remove current sub-bullet if empty, return focus to card body
                e.Handled = true;

                // Remove the current sub-bullet if it's empty
                if (string.IsNullOrWhiteSpace(tb.Text))
                {
                    parentBullet.SubBullets.Remove(sub);
                    NoteManager.MarkDirty();
                }

                Dispatcher.InvokeAsync(() =>
                {
                    var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                    if (container is ContentPresenter cp)
                    {
                        var bodyTb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                        if (bodyTb != null)
                        {
                            bodyTb.Focus();
                            Keyboard.Focus(bodyTb);
                            bodyTb.CaretIndex = bodyTb.Text.Length;
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (e.Key == Key.Enter)
            {
                // Enter → create next sub-bullet below
                e.Handled = true;
                AddSubBulletAndFocus(parentBullet);
            }
            else if (e.Key == Key.Back && string.IsNullOrEmpty(tb.Text))
            {
                e.Handled = true;
                int idx = parentBullet.SubBullets.IndexOf(sub);
                parentBullet.SubBullets.RemoveAt(idx);
                parentBullet.OnSubBulletsChanged();
                NoteManager.MarkDirty();

                // Focus the previous sub-bullet or the parent content box
                Dispatcher.InvokeAsync(() =>
                {
                    var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                    if (container is ContentPresenter cp)
                    {
                        if (idx > 0)
                        {
                            var ic = FindVisualChild<ItemsControl>(cp, "SubBulletsItemsControl");
                            if (ic != null)
                            {
                                var prevSub = parentBullet.SubBullets[idx - 1];
                                var subContainer = ic.ItemContainerGenerator.ContainerFromItem(prevSub);
                                if (subContainer is ContentPresenter subCp)
                                {
                                    var prevTb = FindVisualChild<TextBox>(subCp, "SubBulletTextBox");
                                    prevTb?.Focus();
                                    if (prevTb != null) Keyboard.Focus(prevTb);
                                }
                            }
                        }
                        else
                        {
                            // No more sub-bullets — focus parent content box
                            var parentTb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                            parentTb?.Focus();
                            if (parentTb != null) Keyboard.Focus(parentTb);
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Focuses the last sub-bullet TextBox of a parent bullet card.
        /// </summary>
        private void FocusLastSubBullet(NoteBullet parentBullet)
        {
            if (parentBullet.SubBullets.Count == 0) return;
            var lastSub = parentBullet.SubBullets[^1];

            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                if (container is ContentPresenter cp)
                {
                    var ic = FindVisualChild<ItemsControl>(cp, "SubBulletsItemsControl");
                    if (ic != null)
                    {
                        ic.UpdateLayout();
                        var subContainer = ic.ItemContainerGenerator.ContainerFromItem(lastSub);
                        if (subContainer is ContentPresenter subCp)
                        {
                            var tb = FindVisualChild<TextBox>(subCp, "SubBulletTextBox");
                            tb?.Focus();
                            if (tb != null) Keyboard.Focus(tb);
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NotesAddBullet_Click(object sender, MouseButtonEventArgs e)
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            // If currently in freeform mode, add a new freeform section card
            if (_selectedNoteDay != null && _selectedNoteDay.IsFreeformMode)
            {
                AddNewFreeformSection();
                return;
            }

            AddNewBulletAndFocus();
        }

        /// <summary>
        /// Add a new freeform section card and focus it.
        /// </summary>
        private void AddNewFreeformSection()
        {
            if (_selectedNoteDay == null) return;

            var section = new FreeformSection();
            _selectedNoteDay.FreeformSections.Add(section);
            NoteManager.MarkDirty();

            // Focus the new section after layout update
            Dispatcher.InvokeAsync(() =>
            {
                NotesFreeformSectionsList.UpdateLayout();
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(section);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Remove a freeform section card. Prevents removing the last section.
        /// </summary>
        private void FreeformSectionRemove_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            e.Handled = true;

            if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
            {
                // Don't allow removing the last section
                if (_selectedNoteDay.FreeformSections.Count <= 1)
                {
                    Windows.ToastWindow.ShowToast("Cannot remove the only section");
                    return;
                }

                _selectedNoteDay.FreeformSections.Remove(section);
                NoteManager.MarkDirty();
            }
        }

        /// <summary>
        /// Focus the last freeform section's TextBox.
        /// </summary>
        private void FocusFreeformLastSection()
        {
            if (_selectedNoteDay == null || _selectedNoteDay.FreeformSections.Count == 0) return;

            NotesFreeformSectionsList.UpdateLayout();
            var lastSection = _selectedNoteDay.FreeformSections.Last();
            var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(lastSection);
            if (container is ContentPresenter cp)
            {
                var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                if (tb != null)
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                }
            }
        }

        /// <summary>
        /// Get the currently focused freeform section TextBox, or the last one if none focused.
        /// </summary>
        private TextBox? GetActiveFreeformTextBox()
        {
            // Check if any section TextBox currently has focus
            if (_selectedNoteDay == null) return null;
            foreach (var section in _selectedNoteDay.FreeformSections)
            {
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(section);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null && tb.IsFocused) return tb;
                }
            }
            // Fallback: return the last section's TextBox
            if (_selectedNoteDay.FreeformSections.Count > 0)
            {
                var lastSection = _selectedNoteDay.FreeformSections.Last();
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(lastSection);
                if (container is ContentPresenter cp)
                {
                    return FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                }
            }
            return null;
        }

        /// <summary>
        /// Get the FreeformSection whose TextBox currently has keyboard focus, or the last section as fallback.
        /// </summary>
        private FreeformSection? GetActiveFreeformSection()
        {
            if (_selectedNoteDay == null) return null;
            foreach (var section in _selectedNoteDay.FreeformSections)
            {
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(section);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null && tb.IsFocused) return section;
                }
            }
            // Fallback: return the last section
            return _selectedNoteDay.FreeformSections.LastOrDefault();
        }

        /// <summary>
        /// Check if the given section can accept another image (respects Free/Pro limits).
        /// Shows a toast if the limit is reached.
        /// </summary>
        private bool CanAddImageToSection(FreeformSection section)
        {
            int maxImages = LicenseManager.IsPro
                ? LicenseManager.PRO_NOTE_IMAGES_PER_CARD
                : LicenseManager.FREE_NOTE_IMAGES_PER_CARD;

            if (section.Images.Count >= maxImages)
            {
                if (!LicenseManager.IsPro)
                    UpgradePrompt.ShowNoteImageLimit();
                else
                    Windows.ToastWindow.ShowToast($"Max {LicenseManager.PRO_NOTE_IMAGES_PER_CARD} images per card");
                return false;
            }
            return true;
        }

        private void NoteBulletHeader_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.IsFocused && tb.DataContext is NoteBullet bullet)
            {
                bullet.LastEdited = DateTime.Now;
            }
            NoteManager.MarkDirty();
        }

        private void NoteBulletHeader_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    // Move focus to the content textbox of the same bullet card
                    tb.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                }
            }
        }

        private void NoteBulletText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                // Hard cap: truncate beyond 10K characters
                if (tb.Text.Length > NOTES_HARD_LIMIT)
                {
                    int caretPos = tb.CaretIndex;
                    tb.Text = tb.Text.Substring(0, NOTES_HARD_LIMIT);
                    tb.CaretIndex = Math.Min(caretPos, NOTES_HARD_LIMIT);
                    Windows.ToastWindow.ShowToast("⚠️ Note limit reached (10,000 chars max)");
                }
                // Soft warning at 5K characters
                else if (tb.Text.Length > NOTES_SOFT_LIMIT && !_notesCharLimitWarned)
                {
                    _notesCharLimitWarned = true;
                    Windows.ToastWindow.ShowToast("📝 Note is getting long (5,000+ chars) — limit is 10,000");
                }

                if (tb.IsFocused && tb.DataContext is NoteBullet bullet)
                {
                    bullet.LastEdited = DateTime.Now;
                }
            }
            NoteManager.MarkDirty();
        }

        private void NoteBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                // Ctrl+V → image/file paste
                if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (HandleImagePasteForBullet(bullet))
                    {
                        e.Handled = true;
                        return;
                    }
                }

                // Shift+Enter → always add a new sub-bullet below (predictable, no toggle surprises)
                // Plain Enter  → AcceptsReturn=True inserts a newline (native WPF)
                if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    bullet.IsCollapsed = false; // ensure sub-bullets area is visible
                    NoteManager.MarkDirty();
                    AddSubBulletAndFocus(bullet);
                }
            }
        }

        private bool AssignImageToBullet(NoteBullet bullet, string path, double width)
        {
            if (string.IsNullOrEmpty(bullet.ImagePath))
            {
                bullet.ImagePath = path;
                bullet.ImageDisplayWidth = width;
                NoteManager.MarkDirty();
                return true;
            }
            else if (string.IsNullOrEmpty(bullet.ImagePath2))
            {
                if (!LicenseManager.IsPro)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        Windows.ToastWindow.ShowToast("⚠️ Embedding 2 images per bullet is a Pro feature."));
                    return false;
                }

                bullet.ImagePath2 = path;
                bullet.ImageDisplayWidth2 = width;
                NoteManager.MarkDirty();
                return true;
            }
            return false;
        }

        private bool HandleImagePasteForBullet(NoteBullet bullet)
        {
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return false;

                if (data.GetDataPresent(DataFormats.Bitmap) || 
                    data.GetDataPresent(typeof(BitmapSource)) ||
                    data.GetDataPresent("DeviceIndependentBitmap"))
                {
                    BitmapSource? img = null;
                    if (data.GetDataPresent(DataFormats.Bitmap))
                        img = data.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (img == null && data.GetDataPresent(typeof(BitmapSource)))
                        img = data.GetData(typeof(BitmapSource)) as BitmapSource;
                    if (img == null && data.GetDataPresent("DeviceIndependentBitmap"))
                        img = Clipboard.GetImage();

                    if (img != null)
                    {
                        string path = NoteManager.SaveImage(img);
                        double width = Math.Min(img.PixelWidth, 140);
                        return AssignImageToBullet(bullet, path, width);
                    }
                }
                else if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        foreach (string? f in files)
                        {
                            if (f != null && IsImageFile(f))
                            {
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_img_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 6)}_{Path.GetFileName(f)}");
                                File.Copy(f, destFile, overwrite: true);
                                return AssignImageToBullet(bullet, destFile, 140);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"HandleImagePasteForBullet error: {ex.Message}");
            }
            return false;
        }

        private void NotesFreeformBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V → image/file paste
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (HandleImagePasteForFreeform())
                {
                    e.Handled = true;
                    return;
                }
            }

            // ── Inline bullet list mode (Shift+Enter to start/stop) ─────────────────────────
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                if (!_freeformBulletMode)
                {
                    // ─ Enable inline bullet mode ─
                    _freeformBulletMode = true;
                    if (sender is TextBox tb)
                    {
                        // If not at start of an empty line, break to a new line first
                        int caret = tb.CaretIndex;
                        string prefix = (caret > 0 && tb.Text.Length > 0 && tb.Text[caret - 1] != '\n')
                                        ? "\n\u2022 " : "\u2022 ";
                        tb.SelectedText = prefix;
                        tb.CaretIndex = caret + prefix.Length;
                    }
                }
                else
                {
                    // ─ Disable inline bullet mode: remove • from current line, cursor stays ─
                    _freeformBulletMode = false;
                    if (sender is TextBox tb)
                    {
                        int caret = tb.CaretIndex;
                        string text = tb.Text;

                        // Find the start of the current line
                        int lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1));
                        lineStart = (lineStart < 0) ? 0 : lineStart + 1;

                        // Check if this line starts with "• " and remove it
                        if (lineStart + 2 <= text.Length && text.Substring(lineStart, 2) == "\u2022 ")
                        {
                            tb.Text = text.Remove(lineStart, 2);
                            tb.CaretIndex = Math.Max(lineStart, caret - 2);
                        }
                    }
                }
                return;
            }

            // While in bullet mode, Enter continues the list with a new bullet
            if (e.Key == Key.Enter && _freeformBulletMode)
            {
                e.Handled = true;
                if (sender is TextBox tb)
                {
                    int caret = tb.CaretIndex;
                    const string bullet = "\n\u2022 ";
                    tb.SelectedText = bullet;
                    tb.CaretIndex = caret + bullet.Length;
                }
            }
        }

        private bool HandleImagePasteForFreeform()
        {
            if (_selectedNoteDay == null) return false;
            var section = GetActiveFreeformSection();
            if (section == null) return false;
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return false;

                if (data.GetDataPresent(DataFormats.Bitmap) || 
                    data.GetDataPresent(typeof(BitmapSource)) ||
                    data.GetDataPresent("DeviceIndependentBitmap"))
                {
                    BitmapSource? img = null;
                    if (data.GetDataPresent(DataFormats.Bitmap))
                        img = data.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (img == null && data.GetDataPresent(typeof(BitmapSource)))
                        img = data.GetData(typeof(BitmapSource)) as BitmapSource;
                    if (img == null && data.GetDataPresent("DeviceIndependentBitmap"))
                        img = Clipboard.GetImage();

                    if (img != null)
                    {
                        if (!CanAddImageToSection(section)) return true; // block paste
                        string path = NoteManager.SaveImage(img);
                        var freeformImg = new FreeformImage
                        {
                            ImagePath = path,
                            DisplayWidth = Math.Min(img.PixelWidth, 140)
                        };
                        section.Images.Add(freeformImg);
                        NoteManager.MarkDirty();
                        return true;
                    }
                }
                else if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        foreach (string? f in files)
                        {
                            if (f != null && IsImageFile(f))
                            {
                                if (!CanAddImageToSection(section)) return true; // block paste
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_img_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 6)}_{Path.GetFileName(f)}");
                                File.Copy(f, destFile, overwrite: true);
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140
                                };
                                section.Images.Add(freeformImg);
                                NoteManager.MarkDirty();
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"HandleImagePasteForFreeform error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// When a bullet TextBox gets focus, make sure the window is activated.
        /// This fixes the ghost-typing issue where text goes to external app.
        /// </summary>
        private void NoteBulletText_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _lastFocusedBulletTextBox = tb;
            }
            ActivateWindowWithoutStealingFocus();
        }

        private void NoteBulletCollapse_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                bullet.IsCollapsed = !bullet.IsCollapsed;
                NoteManager.MarkDirty();
            }
        }

        /// <summary>
        /// Auto-expand a collapsed bullet card when clicked anywhere on it.
        /// Only expands — does not re-collapse (use the collapse toggle for that).
        /// </summary>
        private void BulletCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Traverse up the visual tree from the original source to see if we clicked an interactive action button
            DependencyObject dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != sender)
            {
                if (dep is FrameworkElement fe)
                {
                    if (fe.Name == "BulletDeleteBtn" || fe.Name == "BulletReminderBtn" || fe.Name == "BulletMoreBtn" || fe.Name == "BulletCollapseBtn")
                    {
                        // Let the specific button handler deal with it
                        return;
                    }
                }
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (sender is FrameworkElement cardFe && cardFe.DataContext is NoteBullet bullet && bullet.IsCollapsed)
            {
                bullet.IsCollapsed = false;
                NoteManager.MarkDirty();
            }
        }

        private void NoteBulletPin_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.NoteBullet bullet)
            {
                bullet.IsPinned = !bullet.IsPinned;
                Classes.NoteManager.MarkDirty();
            }
        }

        private void NoteBulletReminder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                // Build the raw text from the bullet's header and/or content
                string noteText = !string.IsNullOrEmpty(bullet.Header) ? bullet.Header :
                                   (!string.IsNullOrEmpty(bullet.Content) ? (bullet.Content.Length > 120 ? bullet.Content[..120] : bullet.Content) : "");

                // Use the NLP parser to extract a clean title and calculated due date
                var (parsedTitle, calculatedDue) = Classes.NaturalLanguageReminderParser.Parse(noteText, DateTime.Now);

                // If the note belongs to a future date, use that date's 9 AM as minimum
                if (_selectedNoteDay != null && _selectedNoteDay.Date.Date > DateTime.Today && calculatedDue < _selectedNoteDay.Date.Date.AddHours(9))
                {
                    calculatedDue = _selectedNoteDay.Date.Date.AddHours(9);
                }

                try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
                reminderWindow.Show();
                reminderWindow.Activate();
                _activeReminderCreateWindow = reminderWindow;
            }
        }


        /// <summary>
        /// Freeform notes reminder button — parses the selected text (or full freeform content)
        /// using NLP to extract a clean title and auto-calculated due date.
        /// </summary>
        private void NotesFreeformReminder_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;

            // Prefer selected text if the user highlighted a specific line/phrase; otherwise use entire content
            string noteText = "";
            var activeFreeformTb = GetActiveFreeformTextBox();
            if (activeFreeformTb != null && !string.IsNullOrWhiteSpace(activeFreeformTb.SelectedText))
            {
                noteText = activeFreeformTb.SelectedText.Trim();
            }
            else if (activeFreeformTb != null && !string.IsNullOrWhiteSpace(activeFreeformTb.Text))
            {
                // Use the full freeform text, capped at a reasonable length for parsing
                noteText = activeFreeformTb.Text.Trim();
                if (noteText.Length > 200) noteText = noteText[..200];
            }

            if (string.IsNullOrWhiteSpace(noteText))
            {
                // Nothing to parse — open with defaults
                var defaultDue = DateTime.Today.AddDays(1).AddHours(9);
                try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow("Note Reminder", defaultDue);
                reminderWindow.Show();
                reminderWindow.Activate();
                _activeReminderCreateWindow = reminderWindow;
                return;
            }

            // Use the NLP parser to extract a clean title and calculated due date
            var (parsedTitle, calculatedDue) = Classes.NaturalLanguageReminderParser.Parse(noteText, DateTime.Now);

            // If the note belongs to a future date, use that date's 9 AM as minimum
            if (_selectedNoteDay.Date.Date > DateTime.Today && calculatedDue < _selectedNoteDay.Date.Date.AddHours(9))
            {
                calculatedDue = _selectedNoteDay.Date.Date.AddHours(9);
            }

            try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
            var window = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
            window.Show();
            window.Activate();
            _activeReminderCreateWindow = window;
        }

        private void NotesFreeformExpand_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_selectedNoteDay == null) return;
            try
            {
                // Get the FreeformSection from the clicked button's DataContext
                if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
                {
                    string dayLabel = $"📝 {_selectedNoteDay.DisplayDate}";
                    var expandWindow = new FlyShelf.Windows.NoteExpandWindow(section, dayLabel);
                    expandWindow.Show();
                    expandWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("NOTES", $"Failed to open expand window: {ex.Message}");
            }
        }

        private void NoteBulletDelete_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                // The very first bullet card is permanent — it cannot be deleted.
                if (_selectedNoteDay.Bullets.Count > 0 && _selectedNoteDay.Bullets[0] == bullet)
                    return;

                var result = MessageBox.Show("Are you sure you want to delete this bullet?", "Delete Bullet",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    NoteManager.DeleteBullet(_selectedNoteDay, bullet);
                    UpdateNoteBulletCount();
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // IMAGE PASTE & DROP ON BULLETS
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletText_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                var dataObject = e.DataObject;
                if (dataObject == null) return;

                // Check for image data on clipboard
                if (dataObject.GetDataPresent(DataFormats.Bitmap))
                {
                    var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (img != null)
                    {
                        string path = NoteManager.SaveImage(img);
                        double width = Math.Min(img.PixelWidth, 140);
                        if (AssignImageToBullet(bullet, path, width))
                        {
                            e.CancelCommand(); // Cancel text paste
                        }
                    }
                }
                // Check for image file path
                else if (dataObject.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        foreach (string? f in files)
                        {
                            if (f != null && IsImageFile(f))
                            {
                                // Copy image to notes directory
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                                try
                                {
                                    File.Copy(f, destFile, overwrite: true);
                                    if (AssignImageToBullet(bullet, destFile, 140))
                                    {
                                        e.CancelCommand(); // Cancel text paste
                                    }
                                }
                                catch { } // Best-effort: failure is acceptable
                                break; // Only first image
                            }
                        }
                    }
                }
            }
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
        }

        private void NoteImageResize_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                double newWidth = Math.Clamp(bullet.ImageDisplayWidth + delta, 60, 600);
                bullet.ImageDisplayWidth = newWidth;
                NoteManager.MarkDirty();
                e.Handled = true;
            }
        }

        private void NoteImageResize2_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                double newWidth = Math.Clamp(bullet.ImageDisplayWidth2 + delta, 60, 600);
                bullet.ImageDisplayWidth2 = newWidth;
                NoteManager.MarkDirty();
                e.Handled = true;
            }
        }

        private void NoteImageRemove_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                if (bullet.HasImage)
                {
                    try { File.Delete(bullet.ImagePath); } catch { } // Best-effort: failure is acceptable
                }
                bullet.ImagePath = "";
                NoteManager.MarkDirty();
            }
        }

        private void NoteImageRemove2_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                if (bullet.HasImage2)
                {
                    try { File.Delete(bullet.ImagePath2); } catch { } // Best-effort: failure is acceptable
                }
                bullet.ImagePath2 = "";
                NoteManager.MarkDirty();
            }
        }

        private void NoteImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet && bullet.HasImage)
            {
                var virtualItem = new FlyShelf.ViewModels.ClipboardItem
                {
                    FilePath = bullet.ImagePath,
                    ItemType = FlyShelf.ViewModels.ClipboardItemType.Image
                };
                ShowQuickLookForItem(virtualItem);
                e.Handled = true;
            }
        }

        private void NoteImage2_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet && bullet.HasImage2)
            {
                var virtualItem = new FlyShelf.ViewModels.ClipboardItem
                {
                    FilePath = bullet.ImagePath2,
                    ItemType = FlyShelf.ViewModels.ClipboardItemType.Image
                };
                ShowQuickLookForItem(virtualItem);
                e.Handled = true;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MODE TOGGLE (BULLETS ↔ FREEFORM)
        // ═══════════════════════════════════════════════════════════

        private void NotesModeToggle_Click(object sender, MouseButtonEventArgs e)
        {
            // If in Month View (no specific day selected), navigate to the most recent day
            if (_selectedNoteDay == null)
            {
                if (_selectedMonth != -1 && _selectedYear != -1)
                {
                    var newestDay = NoteManager.Days
                        .Where(d => d.Date.Month == _selectedMonth && d.Date.Year == _selectedYear)
                        .OrderByDescending(d => d.Date)
                        .FirstOrDefault();
                    if (newestDay != null) SelectNoteDay(newestDay);
                }
                return;
            }
            ToggleNotesMode();
        }

        /// <summary>
        /// Flips the current note day between Bullet mode and Freeform mode.
        /// Called by the mode-toggle button AND by Shift+Enter from any notes TextBox.
        /// </summary>
        private void ToggleNotesMode()
        {
            if (_selectedNoteDay == null) return;
            _freeformBulletMode = false; // Reset inline-bullet mode on any mode switch

            _selectedNoteDay.IsFreeformMode = !_selectedNoteDay.IsFreeformMode;
            NoteManager.MarkDirty();

            if (_selectedNoteDay.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";

                ActivateNotesWindow();
                Dispatcher.InvokeAsync(() =>
                {
                    FocusFreeformLastSection();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformArea.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";

                ActivateNotesWindow();
                if (_selectedNoteDay.Bullets.Count == 0)
                {
                    _lastBulletAddedTime = DateTime.MinValue;
                    AddNewBulletAndFocus();
                }
                else
                {
                    FocusNotesActiveTextBox();
                }
            }

            UpdateNoteBulletCount();
        }

        private void NotesFreeformBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedNoteDay != null && sender is TextBox tb)
            {
                // Hard cap: truncate beyond 10K characters per section
                if (tb.Text.Length > NOTES_HARD_LIMIT)
                {
                    int caretPos = tb.CaretIndex;
                    tb.Text = tb.Text.Substring(0, NOTES_HARD_LIMIT);
                    tb.CaretIndex = Math.Min(caretPos, NOTES_HARD_LIMIT);
                    Windows.ToastWindow.ShowToast("⚠️ Section limit reached (10,000 chars max)");
                }
                // Soft warning at 5K characters (once per session per note)
                else if (tb.Text.Length > NOTES_SOFT_LIMIT && !_notesCharLimitWarned)
                {
                    _notesCharLimitWarned = true;
                    Windows.ToastWindow.ShowToast("📝 Section is getting long (5,000+ chars) — limit is 10,000");
                }

                // Content is synced via TwoWay binding to FreeformSection.Content
                NoteManager.MarkDirty();
            }
        }

        /// <summary>
        /// When freeform TextBox gets focus, force-activate the window.
        /// </summary>
        private void NotesFreeformBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ActivateWindowWithoutStealingFocus();
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM IMAGE PASTE
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Intercept paste in freeform TextBox — if clipboard has an image, save it and add
        /// to the day's FreeformImages list instead of pasting text.
        /// </summary>
        private void NotesFreeformBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            var dataObject = e.DataObject;
            if (dataObject == null) return;

            // Find the FreeformSection that owns this TextBox
            FreeformSection? section = null;
            if (sender is TextBox tb)
            {
                DependencyObject? walk = VisualTreeHelper.GetParent(tb);
                while (walk != null)
                {
                    if (walk is FrameworkElement fe && fe.DataContext is FreeformSection fs)
                    {
                        section = fs;
                        break;
                    }
                    walk = VisualTreeHelper.GetParent(walk);
                }
            }
            if (section == null) section = GetActiveFreeformSection();
            if (section == null) return;

            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                e.CancelCommand();

                if (!CanAddImageToSection(section)) return;

                var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                if (img != null)
                {
                    string path = NoteManager.SaveImage(img);
                    var freeformImg = new FreeformImage
                    {
                        ImagePath = path,
                        DisplayWidth = Math.Min(img.PixelWidth, 140)
                    };
                    section.Images.Add(freeformImg);
                    NoteManager.MarkDirty();
                }
            }
            else if (dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    foreach (string? f in files)
                    {
                        if (f != null && IsImageFile(f))
                        {
                            e.CancelCommand();
                            if (!CanAddImageToSection(section)) break;
                            string destDir = NoteManager.GetImagesDirectory();
                            string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                            try
                            {
                                File.Copy(f, destFile, overwrite: true);
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140
                                };
                                section.Images.Add(freeformImg);
                                NoteManager.MarkDirty();
                            }
                            catch { } // Best-effort: failure is acceptable
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Click on a freeform image → open in default system viewer.
        /// </summary>
        private void FreeformImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FreeformImage fi && fi.HasImage)
            {
                var virtualItem = new FlyShelf.ViewModels.ClipboardItem
                {
                    FilePath = fi.ImagePath,
                    ItemType = FlyShelf.ViewModels.ClipboardItemType.Image
                };
                ShowQuickLookForItem(virtualItem);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Mouse wheel on freeform image → resize.
        /// </summary>
        private void FreeformImageResize_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FreeformImage fi)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                fi.DisplayWidth = Math.Clamp(fi.DisplayWidth + delta, 60, 600);
                NoteManager.MarkDirty();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Remove a freeform image.
        /// </summary>
        private void FreeformImageRemove_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is FreeformImage fi)
            {
                // Walk up the visual tree to find the parent FreeformSection
                FreeformSection? section = null;
                DependencyObject? walk = VisualTreeHelper.GetParent(fe);
                while (walk != null)
                {
                    if (walk is FrameworkElement parent && parent.DataContext is FreeformSection fs)
                    {
                        section = fs;
                        break;
                    }
                    walk = VisualTreeHelper.GetParent(walk);
                }

                if (fi.HasImage) { try { File.Delete(fi.ImagePath); } catch { } /* Best-effort: failure is acceptable */ }

                if (section != null)
                    section.Images.Remove(fi);
                else
                    _selectedNoteDay.FreeformImages.Remove(fi); // Fallback for legacy day-level images

                NoteManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES SEARCH
        // ═══════════════════════════════════════════════════════════

        private void ApplyNotesSearch(string query)
        {
            string queryClean = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(queryClean))
            {
                NotesSearchResults.Visibility = Visibility.Collapsed;
                NotesContentArea.Visibility = Visibility.Visible;
                return;
            }

            var results = NoteManager.Search(queryClean);

            // Build display items
            var displayItems = results.Select(r => new NoteSearchResult
            {
                DateLabel = r.Day.DisplayDate,
                Content = !string.IsNullOrEmpty(r.Bullet.Header) ? $"[{r.Bullet.Header}] {r.Bullet.Content}" : r.Bullet.Content,
                Day = r.Day,
                Bullet = r.Bullet
            }).ToList();

            NotesSearchResultsList.ItemsSource = displayItems;
            NotesSearchResults.Visibility = Visibility.Visible;
            NotesContentArea.Visibility = Visibility.Collapsed;
        }

        private void NotesSearchResult_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteSearchResult result)
            {
                CloseSearch();
                SelectNoteDay(result.Day);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MONTH PICKER — Navigate notes by month
        // ═══════════════════════════════════════════════════════════

        private void NotesMonthPicker_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            // Build month list for popup
            var monthsWithContent = NoteManager.Days
                .Where(d => {
                    bool hasBullets = d.Bullets.Any(b => !string.IsNullOrWhiteSpace(b.Header) || !string.IsNullOrWhiteSpace(b.Content) || b.HasImage);
                    bool hasFreeform = !string.IsNullOrWhiteSpace(d.FreeformContent) || d.FreeformImages.Count > 0;
                    return hasBullets || hasFreeform;
                })
                .Select(d => new { d.Date.Month, d.Date.Year })
                .Distinct()
                .OrderByDescending(m => m.Year)
                .ThenByDescending(m => m.Month)
                .ToList();

            var today = DateTime.Today;
            if (!monthsWithContent.Any(m => m.Month == today.Month && m.Year == today.Year))
            {
                monthsWithContent.Insert(0, new { Month = today.Month, Year = today.Year });
            }

            var items = monthsWithContent.Select(m => new NotesMonthPickerItem
            {
                MonthName = new DateTime(m.Year, m.Month, 1).ToString("MMMM"),
                YearText = m.Year.ToString(),
                DayCount = NoteManager.Days.Count(d => d.Date.Month == m.Month && d.Date.Year == m.Year) + " days",
                Month = m.Month,
                Year = m.Year
            }).ToList();

            NotesMonthList.ItemsSource = items;
            NotesMonthPopup.IsOpen = !NotesMonthPopup.IsOpen;
        }

        private void NotesMonthItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NotesMonthPickerItem item)
            {
                NotesMonthPopup.IsOpen = false;

                // Navigate to the first (most recent) day in that month
                var firstDay = NoteManager.Days
                    .Where(d => d.Date.Month == item.Month && d.Date.Year == item.Year)
                    .OrderByDescending(d => d.Date)
                    .FirstOrDefault();

                if (firstDay != null)
                {
                    SelectNoteDay(firstDay);
                }
                else
                {
                    SelectNoteMonth(item.Month, item.Year);
                }
            }
        }

        private void NotesTemplates_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe)
            {
                var menu = new ContextMenu();

                var item1 = new MenuItem { Header = "🛒 Grocery List" };
                item1.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Dairy", "Milk, Eggs, Cheese, Yogurt"),
                    ("Produce", "Veggies, Fruits, Herbs"),
                    ("Pantry", "Bread, Rice, Pasta, Cereal"),
                    ("Frozen & Snacks", "")
                });

                var item2 = new MenuItem { Header = "💼 Daily Standup" };
                item2.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Yesterday", ""),
                    ("Today", ""),
                    ("Blockers", ""),
                    ("Notes", "")
                });

                var item3 = new MenuItem { Header = "📝 Meeting Notes" };
                item3.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Attendees", ""),
                    ("Agenda", ""),
                    ("Discussion", ""),
                    ("Action Items", ""),
                    ("Follow-up", "")
                });

                var item4 = new MenuItem { Header = "🏋️ Workout Planner" };
                item4.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Warmup", "5 min cardio"),
                    ("Main Set", ""),
                    ("Cooldown", "Stretching & foam roll")
                });

                var sep1 = new Separator();

                var item5 = new MenuItem { Header = "🎯 Project Planning" };
                item5.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Goal", ""),
                    ("Tasks", ""),
                    ("Timeline", ""),
                    ("Risks & Mitigations", "")
                });

                var item6 = new MenuItem { Header = "📊 Weekly Review" };
                item6.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Wins", ""),
                    ("Challenges", ""),
                    ("Lessons Learned", ""),
                    ("Next Week Priorities", "")
                });

                var item7 = new MenuItem { Header = "🧠 Brain Dump" };
                item7.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Ideas", ""),
                    ("To Research", ""),
                    ("Questions", "")
                });

                var item8 = new MenuItem { Header = "📚 Reading Notes" };
                item8.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                    ("Key Takeaways", ""),
                    ("Quotes", ""),
                    ("Reflections", "")
                });

                menu.Items.Add(item1);
                menu.Items.Add(item2);
                menu.Items.Add(item3);
                menu.Items.Add(item4);
                menu.Items.Add(sep1);
                menu.Items.Add(item5);
                menu.Items.Add(item6);
                menu.Items.Add(item7);
                menu.Items.Add(item8);

                menu.PlacementTarget = fe;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        private void ApplyNotesTemplateWithHeaders((string header, string content)[] items)
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            if (targetDay.IsFreeformMode)
            {
                // In freeform mode, format as structured text
                var sb = new System.Text.StringBuilder();
                foreach (var (header, content) in items)
                {
                    sb.AppendLine($"## {header}");
                    if (!string.IsNullOrEmpty(content))
                        sb.AppendLine($"  {content}");
                    sb.AppendLine();
                }
                // Append to last freeform section
                var lastSec = targetDay.FreeformSections.LastOrDefault();
                if (lastSec != null) lastSec.Content += sb.ToString();
                NoteManager.MarkDirty();
            }
            else
            {
                foreach (var (header, content) in items)
                {
                    var bullet = NoteManager.AddBullet(targetDay);
                    bullet.Header = header;
                    bullet.Content = content;
                    bullet.IsCollapsed = false; // Templates should start expanded
                }

                if (_selectedNoteDay == null && _selectedMonth != -1)
                {
                    RebuildSidebar();
                    SelectNoteMonth(_selectedMonth, _selectedYear);
                }
                else
                {
                    NotesBulletList.ItemsSource = null;
                    NotesBulletList.ItemsSource = targetDay.Bullets;
                }
            }
        }

        private void ApplyNotesTemplate(string[] lines)
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            if (targetDay.IsFreeformMode)
            {
                string templateText = string.Join(Environment.NewLine, lines.Select(l => "• " + l)) + Environment.NewLine;
                // Append to last freeform section
                var lastSection = targetDay.FreeformSections.LastOrDefault();
                if (lastSection != null) lastSection.Content += templateText;
                NoteManager.MarkDirty();
            }
            else
            {
                foreach (var line in lines)
                {
                    var bullet = NoteManager.AddBullet(targetDay);
                    bullet.Content = line;
                }
                
                if (_selectedNoteDay == null && _selectedMonth != -1)
                {
                    RebuildSidebar();
                    SelectNoteMonth(_selectedMonth, _selectedYear);
                }
                else
                {
                    NotesBulletList.ItemsSource = null;
                    NotesBulletList.ItemsSource = targetDay.Bullets;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MORE MENU (consolidated dropdown for bullet cards)
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletMore_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                // Close any existing menu first
                if (_activeNoteDropdownMenu != null)
                {
                    var wasForSameTarget = _activeNoteDropdownMenu.IsOpen && _activeNoteDropdownMenu.PlacementTarget == fe;
                    _activeNoteDropdownMenu.IsOpen = false;
                    _activeNoteDropdownMenu = null;
                    if (wasForSameTarget) return; // toggle OFF
                }

                // Guard against rapid re-open: StaysOpen=False closes the menu async
                // BEFORE this click handler fires, so the toggle above never triggers.
                // This timestamp guard catches that case.
                if ((DateTime.Now - _lastNoteDropdownCloseTime).TotalMilliseconds < 300)
                    return;

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    var menu = new ContextMenu();

                    // ── Helper: make a colored TextBlock icon ──────────
                    TextBlock MakeIcon(string glyph, string hexColor) => new TextBlock
                    {
                        Text = glyph, FontFamily = new FontFamily("Segoe UI Emoji"),
                        FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor))
                    };

                    // Pin / Unpin  ── amber pin icon
                    var pin = new MenuItem { Header = bullet.IsPinned ? "Unpin" : "Pin to Top" };
                    pin.Icon = MakeIcon(bullet.IsPinned ? "📌" : "📍", "#F59E0B");
                    pin.Click += (s, ev) => { bullet.IsPinned = !bullet.IsPinned; NoteManager.MarkDirty(); };
                    menu.Items.Add(pin);

                    // Color submenu  ── palette icon
                    var colorMenu = new MenuItem { Header = "Color" };
                    colorMenu.Icon = MakeIcon("🎨", "#EC4899");
                    var noteColors = new (string Hex, string Name)[]
                    {
                        ("#FF4444", "Red"), ("#F59E0B", "Amber"), ("#22C55E", "Green"),
                        ("#3B82F6", "Blue"), ("#8B5CF6", "Purple"), ("#EC4899", "Pink")
                    };
                    foreach (var (hex, name) in noteColors)
                    {
                        var mi = new MenuItem { Header = name };
                        mi.Icon = new Border
                        {
                            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))
                        };
                        string ch = hex;
                        mi.Click += (s, ev) => { bullet.Color = ch; NoteManager.MarkDirty(); };
                        colorMenu.Items.Add(mi);
                    }
                    colorMenu.Items.Add(new Separator());
                    var clearColor = new MenuItem { Header = "Clear Color" };
                    clearColor.Icon = MakeIcon("✕", "#6B7280");
                    clearColor.Click += (s, ev) => { bullet.Color = ""; NoteManager.MarkDirty(); };
                    colorMenu.Items.Add(clearColor);
                    menu.Items.Add(colorMenu);

                    // Tags submenu  ── cyan tag icon
                    var tagMenu = new MenuItem { Header = "Tags" };
                    tagMenu.Icon = MakeIcon("🏷", "#00D2FF");
                    string[] presetTags = { "Work", "Personal", "Ideas", "Important", "Reference", "Project" };
                    foreach (var tag in presetTags)
                    {
                        bool hasTag = bullet.Tags.Contains(tag);
                        var mi = new MenuItem { Header = tag, IsChecked = hasTag };
                        mi.Icon = hasTag
                            ? MakeIcon("✓", "#22C55E")
                            : MakeIcon("○", "#6B7280");
                        string ct = tag;
                        mi.Click += (s, ev) =>
                        {
                            if (bullet.Tags.Contains(ct)) bullet.Tags.Remove(ct);
                            else bullet.Tags.Add(ct);
                            bullet.Tags = new List<string>(bullet.Tags);
                            NoteManager.MarkDirty();
                        };
                        tagMenu.Items.Add(mi);
                    }
                    tagMenu.Items.Add(new Separator());
                    var customTag = new MenuItem { Header = "Custom Tag..." };
                    customTag.Icon = MakeIcon("✏", "#8B5CF6");
                    customTag.Click += (s, ev) =>
                    {
                        var popup = new System.Windows.Controls.Primitives.Popup
                        {
                            PlacementTarget = fe,
                            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                            StaysOpen = false, AllowsTransparency = true
                        };
                        var textBox = new TextBox
                        {
                            Width = 160, FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
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
                                if (bullet.Tags.Contains(newTag)) bullet.Tags.Remove(newTag);
                                else bullet.Tags.Add(newTag);
                                bullet.Tags = new List<string>(bullet.Tags);
                                NoteManager.MarkDirty();
                                popup.IsOpen = false;
                            }
                            else if (te.Key == Key.Escape) { te.Handled = true; popup.IsOpen = false; }
                        };
                        popup.Child = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                            CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6)),
                            BorderThickness = new Thickness(1), Child = textBox
                        };
                        popup.IsOpen = true;
                        Dispatcher.InvokeAsync(() => { textBox.Focus(); Keyboard.Focus(textBox); },
                            System.Windows.Threading.DispatcherPriority.Input);
                    };
                    tagMenu.Items.Add(customTag);
                    menu.Items.Add(tagMenu);

                    menu.Items.Add(new Separator());

                    // Copy as Text  ── blue clipboard icon
                    var copyText = new MenuItem { Header = "Copy as Text" };
                    copyText.Icon = MakeIcon("📋", "#3B82F6");
                    copyText.Click += (s, ev) =>
                    {
                        string text = "";
                        if (!string.IsNullOrEmpty(bullet.Header)) text += bullet.Header + "\n";
                        if (!string.IsNullOrEmpty(bullet.Content)) text += bullet.Content;
                        if (!string.IsNullOrWhiteSpace(text)) Classes.ClipboardHelper.SafeSetText(text.Trim());
                    };
                    menu.Items.Add(copyText);

                    // Copy as Markdown  ── indigo markdown icon
                    var copyMd = new MenuItem { Header = "Copy as Markdown" };
                    copyMd.Icon = MakeIcon("📝", "#6366F1");
                    copyMd.Click += (s, ev) =>
                    {
                        string md = "";
                        if (!string.IsNullOrEmpty(bullet.Header)) md += $"## {bullet.Header}\n\n";
                        if (!string.IsNullOrEmpty(bullet.Content)) md += bullet.Content;
                        if (!string.IsNullOrWhiteSpace(md)) Classes.ClipboardHelper.SafeSetText(md.Trim());
                    };
                    menu.Items.Add(copyMd);

                    menu.Items.Add(new Separator());

                    // Set Reminder  ── amber bell icon
                    var reminderItem = new MenuItem { Header = "Set Reminder" };
                    reminderItem.Icon = MakeIcon("⏰", "#F59E0B");
                    reminderItem.Click += (s, ev) =>
                    {
                        string noteText = !string.IsNullOrEmpty(bullet.Header) ? bullet.Header :
                                           (!string.IsNullOrEmpty(bullet.Content) ? (bullet.Content.Length > 120 ? bullet.Content[..120] : bullet.Content) : "");

                        var (parsedTitle, calculatedDue) = Classes.NaturalLanguageReminderParser.Parse(noteText, DateTime.Now);

                        if (_selectedNoteDay != null && _selectedNoteDay.Date.Date > DateTime.Today && calculatedDue < _selectedNoteDay.Date.Date.AddHours(9))
                        {
                            calculatedDue = _selectedNoteDay.Date.Date.AddHours(9);
                        }

                        try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                        var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
                        reminderWindow.Show();
                        reminderWindow.Activate();
                        _activeReminderCreateWindow = reminderWindow;
                    };
                    menu.Items.Add(reminderItem);

                    // Delete  ── red trash icon
                    var deleteItem = new MenuItem { Header = "Delete" };
                    deleteItem.Icon = MakeIcon("🗑", "#EF4444");
                    deleteItem.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    deleteItem.Click += (s, ev) =>
                    {
                        if (_selectedNoteDay != null)
                        {
                            var result = MessageBox.Show("Are you sure you want to delete this bullet?", "Delete Bullet",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning);
                            if (result == MessageBoxResult.Yes)
                            {
                                NoteManager.DeleteBullet(_selectedNoteDay, bullet);
                                UpdateNoteBulletCount();
                            }
                        }
                    };
                    menu.Items.Add(deleteItem);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.Closed += (s, ev) => { _lastNoteDropdownCloseTime = DateTime.Now; if (_activeNoteDropdownMenu == menu) _activeNoteDropdownMenu = null; };
                    _activeNoteDropdownMenu = menu;
                    menu.IsOpen = true;
                }));
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES AI ASSISTANT (Summarize / Rewrite / Organize)
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletAI_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                string textToProcess = !string.IsNullOrEmpty(bullet.Content) ? bullet.Content : bullet.Header;
                OpenNotesAIDropdown(fe, textToProcess, (newText) =>
                {
                    if (!string.IsNullOrEmpty(bullet.Content))
                    {
                        bullet.Content = newText;
                    }
                    else
                    {
                        bullet.Header = newText;
                    }
                    NoteManager.MarkDirty();
                });
            }
        }

        private void NotesFreeformAI_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
            {
                // Snapshot for undo before AI modifies the text
                _notesUndoText = section.Content;
                _notesUndoSection = section;

                OpenNotesAIDropdown(fe, section.Content, (newText) =>
                {
                    section.Content = newText;
                    NoteManager.MarkDirty();

                    // Show the undo button now that AI has modified text
                    NotesUndoBtn.Visibility = Visibility.Visible;
                });
            }
        }

        private void NotesFreeformImprove_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
            {
                if (string.IsNullOrWhiteSpace(section.Content))
                {
                    Windows.ToastWindow.ShowToast("⚠️ Note is empty. Type something first!");
                    return;
                }

                bool hasCloudKey = AiProviderService.Instance.HasCloudApiKey;
                if (!LicenseManager.IsPro && !hasCloudKey)
                {
                    UpgradePrompt.ShowNotesAILimit(this);
                    return;
                }

                // Snapshot for undo before AI modifies the text
                _notesUndoText = section.Content;
                _notesUndoSection = section;

                var aiWindow = new FlyShelf.Windows.NotesAIDiffWindow(section.Content);
                aiWindow.Owner = this;
                if (aiWindow.ShowDialog() == true && aiWindow.IsApplied)
                {
                    section.Content = aiWindow.ImprovedText;
                    NoteManager.MarkDirty();

                    // Show the undo button now that AI has modified text
                    NotesUndoBtn.Visibility = Visibility.Visible;
                }
            }
        }

        private void NotesUndo_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_notesUndoSection != null && _notesUndoText != null)
            {
                _notesUndoSection.Content = _notesUndoText;
                NoteManager.MarkDirty();
                _notesUndoText = null;
                _notesUndoSection = null;
                NotesUndoBtn.Visibility = Visibility.Collapsed;
                Windows.ToastWindow.ShowToast("↩️ Undo applied");
            }
        }


        private void OpenNotesAIDropdown(FrameworkElement target, string originalText, Action<string> onApplyText)
        {
            if (string.IsNullOrWhiteSpace(originalText))
            {
                Windows.ToastWindow.ShowToast("⚠️ Note is empty. Type something first!");
                return;
            }

            var menu = new ContextMenu();

            var summarize = new MenuItem { Header = "✨ Summarize" };
            summarize.Click += (s, ev) => RunNotesAIAction("Summarize", originalText, onApplyText);
            menu.Items.Add(summarize);

            var rewrite = new MenuItem { Header = "✍️ Rewrite" };
            rewrite.Click += (s, ev) => RunNotesAIAction("Rewrite", originalText, onApplyText);
            menu.Items.Add(rewrite);

            var organize = new MenuItem { Header = "🪄 Organize" };
            organize.Click += (s, ev) => RunNotesAIAction("Organize", originalText, onApplyText);
            menu.Items.Add(organize);

            menu.Items.Add(new Separator());

            // Translate submenu with language options
            var translate = new MenuItem { Header = "🌐 Translate" };
            var languages = new[] { "English", "Spanish", "French", "German", "Japanese", "Chinese", "Hindi", "Arabic", "Korean", "Portuguese" };
            foreach (var lang in languages)
            {
                var langItem = new MenuItem { Header = lang, Tag = $"Translate:{lang}" };
                langItem.Click += (s, ev) => RunNotesAIAction($"Translate:{lang}", originalText, onApplyText);
                translate.Items.Add(langItem);
            }
            menu.Items.Add(translate);

            var expand = new MenuItem { Header = "💡 Expand" };
            expand.Click += (s, ev) => RunNotesAIAction("Expand", originalText, onApplyText);
            menu.Items.Add(expand);

            var explain = new MenuItem { Header = "🔍 Explain Simply" };
            explain.Click += (s, ev) => RunNotesAIAction("Explain", originalText, onApplyText);
            menu.Items.Add(explain);

            menu.Items.Add(new Separator());

            var actions = new MenuItem { Header = "✅ Extract Actions" };
            actions.Click += (s, ev) => RunNotesAIAction("Actions", originalText, onApplyText);
            menu.Items.Add(actions);

            var autoTag = new MenuItem { Header = "🏷️ Auto-Tag" };
            autoTag.Click += (s, ev) => RunNotesAIAction("AutoTag", originalText, onApplyText);
            menu.Items.Add(autoTag);

            menu.PlacementTarget = target;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void RunNotesAIAction(string actionType, string originalText, Action<string> onApplyText)
        {
            bool hasCloudKey = AiProviderService.Instance.HasCloudApiKey;

            // Allow if Pro OR if user has their own cloud API key
            if (!LicenseManager.IsPro && !hasCloudKey)
            {
                UpgradePrompt.ShowNotesAILimit(this);
                return;
            }

            // Cloud-only actions require an API key (no offline fallback)
            bool isCloudOnly = actionType.StartsWith("Translate:", StringComparison.OrdinalIgnoreCase)
                || actionType == "Expand" || actionType == "Explain"
                || actionType == "Actions" || actionType == "AutoTag";

            if (isCloudOnly && !hasCloudKey && !WindowsAIService.Instance.IsAvailable)
            {
                Windows.ToastWindow.ShowToast("⚠️ This feature requires an AI API key. Click ⚡ in Settings to configure.");
                return;
            }

            var aiWindow = new FlyShelf.Windows.NotesAIWindow(originalText, actionType);
            aiWindow.Owner = this;
            if (aiWindow.ShowDialog() == true && aiWindow.IsApplied)
            {
                onApplyText(aiWindow.ResultText);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES HEADER DROPDOWN MENU (Sort / Export)
        // ═══════════════════════════════════════════════════════════

        private void NotesHeaderMenu_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe)
            {
                // Close existing header menu (toggle OFF)
                if (_activeNotesHeaderMenu != null)
                {
                    var wasForSameTarget = _activeNotesHeaderMenu.IsOpen && _activeNotesHeaderMenu.PlacementTarget == fe;
                    _activeNotesHeaderMenu.IsOpen = false;
                    _activeNotesHeaderMenu = null;
                    if (wasForSameTarget) return;
                }

                // Guard against rapid re-open
                if ((DateTime.Now - _lastNotesHeaderMenuCloseTime).TotalMilliseconds < 300)
                    return;

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

                    // ── Sort submenu ── cyan chart icon
                    if (_selectedNoteDay != null)
                    {
                        var sortMenu = new MenuItem { Header = "Sort Bullets" };
                        sortMenu.Icon = MI("📊", "#00D2FF");

                        var sortPinned = new MenuItem { Header = "Pinned First" };
                        sortPinned.Icon = MI("📌", "#F59E0B");
                        sortPinned.Click += (s, ev) =>
                        {
                            var sorted = _selectedNoteDay.Bullets.OrderByDescending(b => b.IsPinned).ThenBy(b => b.SortOrder).ToList();
                            _selectedNoteDay.Bullets.Clear();
                            foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortPinned);

                        var sortAZ = new MenuItem { Header = "Header A-Z" };
                        sortAZ.Icon = MI("🔤", "#3B82F6");
                        sortAZ.Click += (s, ev) =>
                        {
                            var sorted = _selectedNoteDay.Bullets.OrderBy(b => b.Header ?? "").ToList();
                            _selectedNoteDay.Bullets.Clear();
                            foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortAZ);

                        var sortEdited = new MenuItem { Header = "Last Edited" };
                        sortEdited.Icon = MI("🕐", "#8B5CF6");
                        sortEdited.Click += (s, ev) =>
                        {
                            var sorted = _selectedNoteDay.Bullets.OrderByDescending(b => b.LastEdited).ToList();
                            _selectedNoteDay.Bullets.Clear();
                            foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortEdited);

                        var sortCreated = new MenuItem { Header = "Created" };
                        sortCreated.Icon = MI("📅", "#22C55E");
                        sortCreated.Click += (s, ev) =>
                        {
                            var sorted = _selectedNoteDay.Bullets.OrderByDescending(b => b.CreatedAt).ToList();
                            _selectedNoteDay.Bullets.Clear();
                            foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                            NoteManager.MarkDirty();
                        };
                        sortMenu.Items.Add(sortCreated);

                        menu.Items.Add(sortMenu);
                    }

                    // ── Export submenu ── blue clipboard icon
                    if (_selectedNoteDay != null)
                    {
                        var exportMenu = new MenuItem { Header = "Export" };
                        exportMenu.Icon = MI("📋", "#3B82F6");

                        var copyMd = new MenuItem { Header = "Copy as Markdown" };
                        copyMd.Icon = MI("📝", "#6366F1");
                        copyMd.Click += (s, ev) =>
                        {
                            string md = NoteManager.ExportToMarkdown(_selectedNoteDay);
                            if (!string.IsNullOrWhiteSpace(md)) Classes.ClipboardHelper.SafeSetText(md);
                        };
                        exportMenu.Items.Add(copyMd);

                        var copyTxt = new MenuItem { Header = "Copy as Text" };
                        copyTxt.Icon = MI("📋", "#3B82F6");
                        copyTxt.Click += (s, ev) =>
                        {
                            string txt = NoteManager.ExportToText(_selectedNoteDay);
                            if (!string.IsNullOrWhiteSpace(txt)) Classes.ClipboardHelper.SafeSetText(txt);
                        };
                        exportMenu.Items.Add(copyTxt);

                        menu.Items.Add(exportMenu);
                    }

                    menu.Items.Add(new Separator());

                    // ── Templates submenu ── amber document icon
                    var templatesMenu = new MenuItem { Header = "Templates" };
                    templatesMenu.Icon = MI("📄", "#F59E0B");

                    var tGrocery = new MenuItem { Header = "Grocery List" };
                    tGrocery.Icon = MI("🛒", "#22C55E");
                    tGrocery.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Dairy", "Milk, Eggs, Cheese, Yogurt"),
                        ("Produce", "Veggies, Fruits, Herbs"),
                        ("Pantry", "Bread, Rice, Pasta, Cereal"),
                        ("Frozen & Snacks", "")
                    });
                    templatesMenu.Items.Add(tGrocery);

                    var tStandup = new MenuItem { Header = "Daily Standup" };
                    tStandup.Icon = MI("💼", "#3B82F6");
                    tStandup.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Yesterday", ""),
                        ("Today", ""),
                        ("Blockers", ""),
                        ("Notes", "")
                    });
                    templatesMenu.Items.Add(tStandup);

                    var tMeeting = new MenuItem { Header = "Meeting Notes" };
                    tMeeting.Icon = MI("📝", "#6366F1");
                    tMeeting.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Attendees", ""),
                        ("Agenda", ""),
                        ("Discussion", ""),
                        ("Action Items", ""),
                        ("Follow-up", "")
                    });
                    templatesMenu.Items.Add(tMeeting);

                    var tWorkout = new MenuItem { Header = "Workout Planner" };
                    tWorkout.Icon = MI("🏋", "#EF4444");
                    tWorkout.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Warmup", "5 min cardio"),
                        ("Main Set", ""),
                        ("Cooldown", "Stretching & foam roll")
                    });
                    templatesMenu.Items.Add(tWorkout);

                    var tProject = new MenuItem { Header = "Project Planning" };
                    tProject.Icon = MI("📋", "#00D2FF");
                    tProject.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Goals", ""),
                        ("Tasks", ""),
                        ("Timeline", ""),
                        ("Resources", "")
                    });
                    templatesMenu.Items.Add(tProject);

                    var tBrainDump = new MenuItem { Header = "Brain Dump" };
                    tBrainDump.Icon = MI("🧠", "#EC4899");
                    tBrainDump.Click += (s, ev) => ApplyNotesTemplateWithHeaders(new[] {
                        ("Ideas", ""),
                        ("To Process", ""),
                        ("Follow Up", "")
                    });
                    templatesMenu.Items.Add(tBrainDump);

                    menu.Items.Add(templatesMenu);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.Closed += (s, ev) => { _lastNotesHeaderMenuCloseTime = DateTime.Now; if (_activeNotesHeaderMenu == menu) _activeNotesHeaderMenu = null; };
                    _activeNotesHeaderMenu = menu;
                    menu.IsOpen = true;
                }));
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTE SORT (legacy — now integrated into header dropdown)
        // ═══════════════════════════════════════════════════════════

        private void NoteSort_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && _selectedNoteDay != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var menu = new ContextMenu();

                    var sortPinned = new MenuItem { Header = "📌 Pinned First" };
                    sortPinned.Click += (s, ev) =>
                    {
                        var sorted = _selectedNoteDay.Bullets.OrderByDescending(b => b.IsPinned).ThenBy(b => b.SortOrder).ToList();
                        _selectedNoteDay.Bullets.Clear();
                        foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortPinned);

                    var sortAZ = new MenuItem { Header = "🔤 Header A-Z" };
                    sortAZ.Click += (s, ev) =>
                    {
                        var sorted = _selectedNoteDay.Bullets.OrderBy(b => b.Header ?? "").ToList();
                        _selectedNoteDay.Bullets.Clear();
                        foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortAZ);

                    var sortEdited = new MenuItem { Header = "🕐 Last Edited" };
                    sortEdited.Click += (s, ev) =>
                    {
                        var sorted = _selectedNoteDay.Bullets.OrderByDescending(b => b.LastEdited).ToList();
                        _selectedNoteDay.Bullets.Clear();
                        foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortEdited);

                    var sortCreated = new MenuItem { Header = "📅 Created" };
                    sortCreated.Click += (s, ev) =>
                    {
                        var sorted = _selectedNoteDay.Bullets.OrderByDescending(b => b.CreatedAt).ToList();
                        _selectedNoteDay.Bullets.Clear();
                        foreach (var b in sorted) _selectedNoteDay.Bullets.Add(b);
                        NoteManager.MarkDirty();
                    };
                    menu.Items.Add(sortCreated);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }));
            }
        }



        // ═══════════════════════════════════════════════════════════
        // NOTE EXPORT
        // ═══════════════════════════════════════════════════════════

        private void NoteExport_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && _selectedNoteDay != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var menu = new ContextMenu();

                    var copyMd = new MenuItem { Header = "📋 Copy as Markdown" };
                    copyMd.Click += (s, ev) =>
                    {
                        string md = NoteManager.ExportToMarkdown(_selectedNoteDay);
                        if (!string.IsNullOrWhiteSpace(md)) Classes.ClipboardHelper.SafeSetText(md);
                    };
                    menu.Items.Add(copyMd);

                    var copyTxt = new MenuItem { Header = "📋 Copy as Text" };
                    copyTxt.Click += (s, ev) =>
                    {
                        string txt = NoteManager.ExportToText(_selectedNoteDay);
                        if (!string.IsNullOrWhiteSpace(txt)) Classes.ClipboardHelper.SafeSetText(txt);
                    };
                    menu.Items.Add(copyTxt);

                    menu.PlacementTarget = fe;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                }));
            }
        }

        // ═══════════════════════════════════════════════════════════
        // BULLET COUNT DISPLAY
        // ═══════════════════════════════════════════════════════════

        private void UpdateNoteBulletCount()
        {
            // Bullet count badge was removed from UI — method kept as no-op for callers
        }

    }

    /// <summary>ViewModel for search results display.</summary>
    public class NoteSearchResult
    {
        public string DateLabel { get; set; } = "";
        public string Content { get; set; } = "";
        public NoteDay Day { get; set; } = null!;
        public NoteBullet Bullet { get; set; } = null!;
    }

    /// <summary>ViewModel for sidebar display representing day or month box.</summary>
    public class NotesSidebarItem : System.ComponentModel.INotifyPropertyChanged
    {
        public bool IsMonthHeader { get; set; }
        public string Label { get; set; } = "";
        public string MonthLabel { get; set; } = "";
        public string FullLabel { get; set; } = "";
        public bool IsToday { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public NoteDay Day { get; set; } = null!;
        public int MonthValue { get; set; }
        public int YearValue { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    /// <summary>ViewModel for the month picker popup items.</summary>
    public class NotesMonthPickerItem
    {
        public string MonthName { get; set; } = "";
        public string YearText { get; set; } = "";
        public string DayCount { get; set; } = "";
        public int Month { get; set; }
        public int Year { get; set; }
    }
}
