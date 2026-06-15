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
                NotesSidebarColumn.Width = new GridLength(54);
                NotesSidebarCollapseIcon.Text = "◂";
            }

            // Auto-collapse sidebar after 10 seconds
            _notesSidebarAutoCollapseTimer?.Stop();
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
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Display);
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
            catch { }
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
                if (_isNotesActive || _isTodoActive || _isSearchActive)
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
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
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

                // PERF: Defer save to Background priority so it doesn't block the summon pipeline.
                Dispatcher.InvokeAsync(() => NoteManager.SaveNow(),
                    System.Windows.Threading.DispatcherPriority.Background);
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

            // Clear search if active
            if (_isSearchActive)
            {
                CloseSearch();
            }

            // Update sidebar selection highlight
            UpdateSidebarSelectionVisuals();

            // Bind content
            NotesBulletList.ItemsSource = day.Bullets;
            NotesFreeformBox.Text = day.FreeformContent ?? "";

            // Bind freeform images
            NotesFreeformImageList.ItemsSource = day.FreeformImages;

            // Show correct mode
            if (day.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";
                // Defer focus to freeform text box
                Dispatcher.InvokeAsync(() =>
                {
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
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
                NotesSidebarColumn.Width = new GridLength(54);
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

        private void NotesAddBullet_Click(object sender, MouseButtonEventArgs e)
        {
            if (GetTargetDayForAdd() == null) return;
            AddNewBulletAndFocus();
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
            if (sender is TextBox tb && tb.IsFocused && tb.DataContext is NoteBullet bullet)
            {
                bullet.LastEdited = DateTime.Now;
            }
            NoteManager.MarkDirty();
        }

        private void NoteBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                // Intercept Ctrl+V to handle image/file paste manually
                if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (HandleImagePasteForBullet(bullet))
                    {
                        e.Handled = true;
                        return;
                    }
                }

                // Shift+Enter → insert newline (AcceptsReturn handles this when true)
                // Enter without Shift → add new bullet below
                if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    AddNewBulletAndFocus();
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
                                string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
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
            // Intercept Ctrl+V to handle image/file paste manually
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (HandleImagePasteForFreeform())
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private bool HandleImagePasteForFreeform()
        {
            if (_selectedNoteDay == null) return false;
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
                        var freeformImg = new FreeformImage
                        {
                            ImagePath = path,
                            DisplayWidth = Math.Min(img.PixelWidth, 140)
                        };
                        _selectedNoteDay.FreeformImages.Add(freeformImg);
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
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                                File.Copy(f, destFile, overwrite: true);
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140
                                };
                                _selectedNoteDay.FreeformImages.Add(freeformImg);
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
                    if (fe.Name == "BulletDeleteBtn" || fe.Name == "BulletReminderBtn" || fe.Name == "BulletCollapseBtn")
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

                try { _activeReminderCreateWindow?.Close(); } catch { }
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
            if (NotesFreeformBox != null && !string.IsNullOrWhiteSpace(NotesFreeformBox.SelectedText))
            {
                noteText = NotesFreeformBox.SelectedText.Trim();
            }
            else if (NotesFreeformBox != null && !string.IsNullOrWhiteSpace(NotesFreeformBox.Text))
            {
                // Use the full freeform text, capped at a reasonable length for parsing
                noteText = NotesFreeformBox.Text.Trim();
                if (noteText.Length > 200) noteText = noteText[..200];
            }

            if (string.IsNullOrWhiteSpace(noteText))
            {
                // Nothing to parse — open with defaults
                var defaultDue = DateTime.Today.AddDays(1).AddHours(9);
                try { _activeReminderCreateWindow?.Close(); } catch { }
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

            try { _activeReminderCreateWindow?.Close(); } catch { }
            var window = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
            window.Show();
            window.Activate();
            _activeReminderCreateWindow = window;
        }

        private void NoteBulletDelete_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                var result = MessageBox.Show(
                    "Are you sure you want to delete this note?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    NoteManager.RemoveBullet(_selectedNoteDay, bullet);
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
                                catch { }
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
                    try { File.Delete(bullet.ImagePath); } catch { }
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
                    try { File.Delete(bullet.ImagePath2); } catch { }
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
            // If in Month View (no specific day selected), navigate to the most recent day in that month
            if (_selectedNoteDay == null)
            {
                if (_selectedMonth != -1 && _selectedYear != -1)
                {
                    var newestDay = NoteManager.Days
                        .Where(d => d.Date.Month == _selectedMonth && d.Date.Year == _selectedYear)
                        .OrderByDescending(d => d.Date)
                        .FirstOrDefault();
                    if (newestDay != null)
                    {
                        SelectNoteDay(newestDay);
                    }
                }
                return;
            }

            _selectedNoteDay.IsFreeformMode = !_selectedNoteDay.IsFreeformMode;
            NoteManager.MarkDirty();

            if (_selectedNoteDay.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";

                // ─── FOCUS FIX: Activate window, then focus freeform box ───
                ActivateNotesWindow();
                Dispatcher.InvokeAsync(() =>
                {
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
                    NotesFreeformBox.CaretIndex = NotesFreeformBox.Text.Length;
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformArea.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";

                // ─── FOCUS FIX: Activate window, then auto-create or focus bullet ───
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
        }

        private void NotesFreeformBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedNoteDay != null && sender is TextBox tb)
            {
                _selectedNoteDay.FreeformContent = tb.Text;
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

            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                e.CancelCommand();

                var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                if (img != null)
                {
                    string path = NoteManager.SaveImage(img);
                    var freeformImg = new FreeformImage
                    {
                        ImagePath = path,
                        DisplayWidth = Math.Min(img.PixelWidth, 140) // Nice and small default size
                    };
                    _selectedNoteDay.FreeformImages.Add(freeformImg);
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
                            string destDir = NoteManager.GetImagesDirectory();
                            string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                            try
                            {
                                File.Copy(f, destFile, overwrite: true);
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140 // Nice and small default size
                                };
                                _selectedNoteDay.FreeformImages.Add(freeformImg);
                                NoteManager.MarkDirty();
                            }
                            catch { }
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
                if (fi.HasImage) { try { File.Delete(fi.ImagePath); } catch { } }
                _selectedNoteDay.FreeformImages.Remove(fi);
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
                NotesFreeformBox.Text += sb.ToString();
                targetDay.FreeformContent = NotesFreeformBox.Text;
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
                NotesFreeformBox.Text += templateText;
                targetDay.FreeformContent = NotesFreeformBox.Text;
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
