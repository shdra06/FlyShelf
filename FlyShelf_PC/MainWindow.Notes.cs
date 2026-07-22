// ---------------------------------------------------------------
// MainWindow — Quick Notes Panel Coordinator (Thin Shell)
// Decomposition Phase 3: All notes UI logic moved to
// Controls/NotesPanelControl.xaml.cs. This file only handles
// panel open/close coordination, mutual exclusion, timers,
// window activation, and shared state flags.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlyShelf.Models;
using FlyShelf.Helpers;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isNotesActive = false;
        public bool IsNotesActive => _isNotesActive;
        private System.Windows.Threading.DispatcherTimer? _panelAutoRevertTimer;
        private bool _isNotesLoaded = false;
        private Brush? _originalHeaderBg = null;
        private static readonly SolidColorBrush _notesHeaderBrush = new(ThemeColors.DarkSurface);

        private System.Windows.Threading.DispatcherTimer? _notesSidebarAutoCollapseTimer;
        private System.Windows.Threading.DispatcherTimer? _notesSyncStatusTimer;

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
                    if (_isNotesActive && !NotesContent.IsSidebarCollapsed)
                    {
                        NotesContent.CollapseSidebarIfExpanded();
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

            // ─── Panel transition: animate exit of clipboard, then entrance of notes ───
            bool shelfWasVisible = ShelfListView.Visibility == Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            Action showNotesPanel = () =>
            {
                ShelfListView.Visibility = Visibility.Collapsed;
                NotesPanel.Visibility = Visibility.Visible;

                // Animate in
                var slideAnim = Classes.AnimationHelper.SlideIn(fromY: -12, durationMs: 200);
                var fadeAnim = Classes.AnimationHelper.FadeIn(durationMs: 200);
                if (NotesPanel.RenderTransform is TranslateTransform tt)
                    tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);
            };

            if (shelfWasVisible)
            {
                // Quick exit on the clipboard list before showing notes
                var exitFade = Classes.AnimationHelper.FadeOut(durationMs: 120);
                exitFade.Completed += (_, _) => showNotesPanel();
                ShelfListView.BeginAnimation(OpacityProperty, exitFade);
            }
            else
            {
                showNotesPanel();
            }

            // Initialize the UserControl with today's data
            NotesContent.Initialize(today);

            // Wire up UserControl events (once)
            WireUpNotesContentEvents();

            // Update sync status indicator
            UpdateNotesSyncStatus();

            // Start periodic sync status timer (every 5s)
            if (_notesSyncStatusTimer == null)
            {
                _notesSyncStatusTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
                };
                _notesSyncStatusTimer.Tick += (s, ev) =>
                {
                    if (_isNotesActive || _isTodoActive)
                        UpdateNotesSyncStatus();
                };
            }
            _notesSyncStatusTimer.Start();
        }

        private bool _notesEventsWired = false;
        private void WireUpNotesContentEvents()
        {
            if (_notesEventsWired) return;
            _notesEventsWired = true;

            NotesContent.CloseRequested += (s, e) => CloseNotesPanel();
            NotesContent.ActivateWithoutStealingFocusRequested += (s, e) => ActivateWindowWithoutStealingFocus();
            NotesContent.ActivateWindowRequested += (s, e) => ActivateNotesWindow();
        }

        /// <summary>
        /// Updates the sync status indicators in both Notes and Todo headers.
        /// Shows green "Synced (N)" when peers are connected, orange "Offline" otherwise.
        /// </summary>
        private void UpdateNotesSyncStatus()
        {
            var peerCount = Classes.PeerManager.Instance?.AliveCount ?? 0;
            // Also count directly-connected mobile devices polling via LAN
            var mobileCount = Classes.NetworkSyncServer.Instance?.GetDirectlyConnectedDeviceCount() ?? 0;
            var count = peerCount + mobileCount;
            var isSynced = count > 0;

            // Update Notes UserControl sync indicators
            NotesContent.UpdateSyncStatus(count, isSynced);

            // Update Todo UserControl sync indicators
            TodoContent.UpdateSyncStatus(count, isSynced);
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
        /// Delegates to the UserControl.
        /// </summary>
        private void FocusNotesActiveTextBox()
        {
            NotesContent.FocusActiveTextBox();
        }

        /// <summary>
        /// Apply notes search from the shared search box. Delegates to UserControl.
        /// </summary>
        private void ApplyNotesSearch(string query)
        {
            NotesContent.ApplySearch(query);
        }

        /// <summary>
        /// Stop the sidebar auto-collapse timer. Called from the UserControl when user manually toggles.
        /// </summary>
        internal void StopSidebarAutoCollapseTimer()
        {
            _notesSidebarAutoCollapseTimer?.Stop();
        }

        private void CloseNotesPanel(bool immediate = false)
        {
            _isNotesActive = false;

            // Close month picker popup if open
            NotesContent.CloseMonthPopup();

            // Restore taskbar/alt-tab title
            Title = "FlyShelf";

            // Restore non-activating window style
            UpdateWindowActivationStyle();

            // Clear last focused bullet textbox reference
            NotesContent.ClearFocusState();


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
                // BUGFIX: Clear the fade-out animation on ShelfListView — OpenNotesPanel animates
                // its opacity to 0 during the Notes entry transition. Without this reset,
                // the list is Visible but fully transparent on re-summon (empty box ghost).
                ShelfListView.BeginAnimation(OpacityProperty, null);
                ShelfListView.Opacity = 1;
                ShelfListView.Visibility = Visibility.Visible;
                // Let the XAML DataTrigger on DroppedItems.Count control visibility
                EmptyStatePanel.ClearValue(VisibilityProperty);

                // Debounced save — avoids blocking UI thread on panel close
                NoteManager.ScheduleSave();
                return;
            }

            // Debounced save — avoids blocking UI thread on panel close
            NoteManager.ScheduleSave();

            // Animate out: slide + fade for a smooth exit
            var slideAnim = Classes.AnimationHelper.SlideOut(toY: 8, durationMs: 120);
            var fadeAnim = Classes.AnimationHelper.FadeOut(durationMs: 120);
            fadeAnim.Completed += (s, a) =>
            {
                if (!_isNotesActive)
                {
                    NotesPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    // Reset opacity so the clipboard list is visible next time
                    ShelfListView.BeginAnimation(OpacityProperty, null);
                    ShelfListView.Opacity = 1;
                    // Restore empty state if needed
                    // Let the XAML DataTrigger on DroppedItems.Count control visibility
                    EmptyStatePanel.ClearValue(VisibilityProperty);

                    // Entrance animation on the returning clipboard list
                    AnimationHelper.PopIn(ShelfListView, fromScale: 0.98, durationMs: 180);
                }
            };
            if (NotesPanel.RenderTransform is TranslateTransform ttExit)
                ttExit.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);
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

    }


}
