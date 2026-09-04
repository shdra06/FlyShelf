// ---------------------------------------------------------------
// MainWindow.Todo.cs — Thin coordinator for the Todo panel
// (Decomposition Phase 4: business logic moved to Controls/TodoPanelControl)
// Keeps ONLY: open/close coordination, panel switching, shared state flags,
// button icon changes, window activation, animation orchestration.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
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
        public bool IsTodoActive => _isTodoActive;
        private bool _isTodoLoaded = false;

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
            if (_isResearchActive) CloseResearchPanel(immediate: true);
            if (_isAiSettingsActive) CloseAiSettingsPanel(immediate: true);
            if (_isSearchActive) CloseSearch(switchingPanel: true);
            try { TodoContent?.ClearSearch(); } catch { }
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

            _isTodoActive = true;
            // NOTE: No auto-revert timer — Todo panel should never auto-hide

            // Wire up UserControl events (once)
            WireUpTodoContentEvents();

            // Update taskbar/alt-tab title
            Title = "To-Do";

            // Update window activation style dynamically so clicking it works
            UpdateWindowActivationStyle();

            // Force-activate and topmost-cycle to grab OS focus
            ActivateTodoWindow();

            // Swap todo button to clipboard icon (acts as "go back" button)
            TodoToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24 };
            TodoToggleBtn.ToolTip = "Back to Clipboard";

            UpdateToolbarButtonsVisibility();

            // ─── Panel transition: animate exit of clipboard, then entrance of todo ───
            bool shelfWasVisible = ShelfListView.Visibility == Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            Action showTodoPanel = () =>
            {
                ShelfListView.Visibility = Visibility.Collapsed;
                TodoPanel.Visibility = Visibility.Visible;

                // Animate in
                var slideAnim = Classes.AnimationHelper.SlideIn(fromY: -12, durationMs: 200);
                var fadeAnim = Classes.AnimationHelper.FadeIn(durationMs: 200);
                if (TodoPanel.RenderTransform is TranslateTransform tt)
                    tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                TodoPanel.BeginAnimation(OpacityProperty, fadeAnim);
            };

            if (shelfWasVisible)
            {
                // Quick exit on the clipboard list before showing todo
                var exitFade = Classes.AnimationHelper.FadeOut(durationMs: 120);
                exitFade.Completed += (_, _) => showTodoPanel();
                ShelfListView.BeginAnimation(OpacityProperty, exitFade);
            }
            else
            {
                showTodoPanel();
            }

            // Delegate content initialization to the UserControl
            TodoContent.Initialize(today);
        }

        private bool _todoEventsWired = false;
        private void WireUpTodoContentEvents()
        {
            if (_todoEventsWired) return;
            _todoEventsWired = true;

            TodoContent.CloseRequested += (s, e) => CloseTodoPanel();
            TodoContent.ActivateWithoutStealingFocusRequested += (s, e) => ActivateWindowWithoutStealingFocus();
        }

        private void ActivateTodoWindow()
        {
            SuppressDwmBorder();
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

            // Clear search state inside Todo panel
            try { TodoContent?.ClearSearch(); } catch { }
            if (_isSearchActive)
            {
                CloseSearch();
            }

            Title = "FlyShelf";

            UpdateWindowActivationStyle();
            UpdateToolbarButtonsVisibility();

            if (immediate)
            {
                // Instant close — no animation (used when switching to another panel)
                TodoPanel.BeginAnimation(OpacityProperty, null);
                TodoPanel.Opacity = 0;
                TodoPanel.Visibility = Visibility.Collapsed;
                // BUGFIX: Clear the fade-out animation on ShelfListView — OpenTodoPanel animates
                // its opacity to 0 during the Todo entry transition. Without this reset,
                // the list is Visible but fully transparent on re-summon (empty box ghost).
                ShelfListView?.BeginAnimation(OpacityProperty, null);
                if (ShelfListView != null) ShelfListView.Opacity = 1;
                ShelfListView.Visibility = Visibility.Visible;
                // Let the XAML DataTrigger on DroppedItems.Count control visibility
                EmptyStatePanel.ClearValue(VisibilityProperty);

                // Debounced save — avoids blocking UI thread on panel close
                TodoManager.ScheduleSave();
                return;
            }

            // Debounced save — avoids blocking UI thread on panel close
            TodoManager.ScheduleSave();

            // Animate out: slide + fade for a smooth exit
            var slideAnim = Classes.AnimationHelper.SlideOut(toY: 8, durationMs: 120);
            var fadeAnim = Classes.AnimationHelper.FadeOut(durationMs: 120);

            if (TodoPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            fadeAnim.Completed += (s, ev) =>
            {
                if (!_isTodoActive)
                {
                    TodoPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    // Reset opacity so the clipboard list is visible next time
                    ShelfListView.BeginAnimation(OpacityProperty, null);
                    ShelfListView.Opacity = 1;
                    // Let the XAML DataTrigger on DroppedItems.Count control visibility
                    EmptyStatePanel.ClearValue(VisibilityProperty);

                    // Entrance animation on the returning clipboard list
                    AnimationHelper.PopIn(ShelfListView, fromScale: 0.98, durationMs: 180);
                }
            };
            TodoPanel.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private void TodoPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Skip activation when click targets a popup-triggering element (templates button).
            // PreviewMouseDown (tunnel) fires before MouseLeftButtonDown; calling Activate()
            // during the tunnel phase immediately closes ContextMenus/Popups that are about to open.
            if (e.OriginalSource is DependencyObject source)
            {
                var parent = source;
                while (parent != null)
                {
                    if (parent is FrameworkElement fe && (fe.Name == "TodoTemplatesBtn" || fe.Name == "TodoDropdownButton"))
                        return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
            ActivateWindowWithoutStealingFocus();
        }

        /// <summary>
        /// Restores keyboard focus to the active text field inside the todo panel.
        /// Delegates to the UserControl.
        /// </summary>
        private void FocusTodoActiveTextBox()
        {
            TodoContent.FocusActiveTextBox();
        }

        /// <summary>
        /// Apply todo search from the shared search box. Delegates to UserControl.
        /// </summary>
        private void ApplyTodoSearch(string query)
        {
            TodoContent.ApplySearch(query);
        }

        /// <summary>
        /// Stopwatch button click handler (toolbar button, stays on MainWindow).
        /// Forwards to a standalone TimerWindow.
        /// </summary>
        private static FlyShelf.Windows.TimerWindow? _activeTimerWindow;
        private void TodoStopwatch_Click(object sender, RoutedEventArgs e)
        {
            try { _activeTimerWindow?.Close(); } catch { } // Best-effort: failure is acceptable
            var tw = new FlyShelf.Windows.TimerWindow(null);
            WindowHelper.ShowInForeground(tw);
            _activeTimerWindow = tw;
        }
    }
}
