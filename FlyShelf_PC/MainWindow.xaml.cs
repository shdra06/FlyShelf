using FlyShelf.ViewModels;
using MicaWPF.Controls;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using FlyShelf.Classes;

namespace FlyShelf
{
    public partial class MainWindow : MicaWindow
    {
        private Point _dragStartPoint;
        private readonly FlyShelfViewModel _viewModel;
        public FlyShelfViewModel ViewModel => _viewModel;
        

        private int _spawnToken = 0;
        private bool _isDragHovering = false;
        private bool _shouldPreventDrag = false;
        private bool _justDeletedAnItem = false;

        public static readonly DependencyProperty IsDragHoveringProperty =
            DependencyProperty.Register("IsDragHovering", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool IsDragHovering
        {
            get => (bool)GetValue(IsDragHoveringProperty);
            set => SetValue(IsDragHoveringProperty, value);
        }

        private bool _didDragOut = false;
        public bool IsDeletingItem { get; set; } = false;
        private bool _isClosed = false;
        private double _lockedBottomEdge = 0;
        private bool _isEdgeLocked = false;
        private bool _elementsRegistered = false;
        private DateTime _showAnimEndTime = DateTime.MinValue; // Timestamp when show animation completed — used for post-animation cooldown
        private Windows.TaskbarWindow? _taskbarWidget;
        private Windows.MascotCompanionWindow? _mascotCompanion;
        private System.Windows.Threading.DispatcherTimer? _clipboardDebounceTimer = null;
        private System.Windows.Threading.DispatcherTimer? _scrollDecayTimer;
        private System.Windows.Threading.DispatcherTimer? _scrollHighQualityTimer;
        private System.Windows.Threading.DispatcherTimer? _scrollLiveLoadTimer; // PERF: Live thumbnail loading during active scroll
        private DateTime _lastScrollRenderTime = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer? _evictionBackgroundTimer;
        // Fix 5: Anchor for cheap per-pass visibility math (one TransformToAncestor per pass)
        private double _anchorY;
        private bool _anchorValid;
        private DateTime _lastMergeToggleTime = DateTime.MinValue;
        private IntPtr _lastActiveExternalWindow = IntPtr.Zero;
        private long _lastScrollTimeTick;
        private double _scrollVelocity = 0;
        private Point _lastPhysicalMousePosition = new Point(-999, -999);
        private string _currentLoadedWallpaperPath = "";
        private static string? _cachedDesktopWallpaperPath = null;
        private ScrollViewer? _shelfScrollViewer;
        private double _lastActualHeight = 0;
        private EventHandler<Classes.AnimationRequestEventArgs>? _mascotAnimationRequestedHandler;
        private Action<Classes.ThemePackage?>? _themeChangedHandler;
        private System.ComponentModel.PropertyChangedEventHandler? _settingsChangedHandler;
        private Action<bool>? _updateStatusChangedHandler = null; // Disabled — update notifications removed from clipboard
        private Action? _coastPrefetchHandler;
        private bool _isSuppressingSizeSync = false;
        private Guid _summonedDesktopId = Guid.Empty;
        private Guid _currentDesktopId = Guid.Empty; // Updated on every foreground change from the fg window's desktop GUID
        private bool _lastActiveExternalWindowWasOnCurrentAtSummon = false;
        internal bool _isFirstLaunchAfterOnboarding = false; // Set by App.xaml.cs after onboarding completes
        private volatile bool _isStartupReady = false; // Set true after theme init completes — guards hotkey spam during startup

        private SizeChangedEventHandler? _sizeChangedHandler;
        private EventHandler? _activatedHandler;
        private System.ComponentModel.PropertyChangedEventHandler? _viewModelPropertyChangedHandler;
        private NotifyCollectionChangedEventHandler? _droppedItemsCollectionChangedHandler;
        private EventHandler? _stateChangedHandler;
        private System.Windows.Controls.Primitives.ScrollBar? _verticalScrollBar;
        private MouseButtonEventHandler? _verticalScrollBarPreviewMouseLeftButtonDownHandler;

        private FlyShelf.Classes.NativeMethods.IVirtualDesktopManager? _vdm = null;
        private FlyShelf.Classes.NativeMethods.IVirtualDesktopManager? GetVirtualDesktopManager()
        {
            if (_vdm == null)
            {
                try
                {
                    _vdm = (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
                }
                catch { } // Best-effort: failure is acceptable
            }
            return _vdm;
        }

        private ScrollViewer? GetShelfScrollViewer()
        {
            if (_shelfScrollViewer == null)
            {
                _shelfScrollViewer = FindVisualChild<ScrollViewer>(ShelfListView);
            }
            return _shelfScrollViewer;
        }

        /// <summary>
        /// Scrolls the clipboard list to the top after a short delay.
        /// Used after async operations (e.g. PDF conversion) that insert new items at index 0
        /// via HandleDrop, which runs its insertion on a background thread + dispatcher callback.
        /// The delay ensures the item is already in the collection before we scroll.
        /// </summary>
        public void ScrollClipboardToTop()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(300);
                try
                {
                    Classes.SmoothScroll.ResetScrollState(GetShelfScrollViewer());
                    var sv = GetShelfScrollViewer();
                    if (sv != null)
                    {
                        sv.ScrollToVerticalOffset(0);
                        sv.ScrollToTop();
                    }
                }
                catch { } // Best-effort: failure is acceptable
            });
        }

        // ═══ Thin wrappers for NativeMethods — avoids adding NativeMethods. prefix to 20+ call sites across partial files ═══
        private static int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize)
            => NativeMethods.DwmSetWindowAttribute(hwnd, attr, ref attrValue, attrSize);

        // MainWindow-specific DWMWA_COLOR_NONE: returns context-aware border color (no border in clipboard mode, dark gray border in focus panel modes)
        public static int DWMWA_COLOR_NONE
        {
            get
            {
                try
                {
                    var mainWin = System.Windows.Application.Current.MainWindow as MainWindow;
                    if (mainWin != null)
                    {
                        bool isFocusPanel = mainWin.IsNotesActive || mainWin.IsTodoActive || mainWin.IsSearchActive;
                        if (!isFocusPanel)
                        {
                            return unchecked((int)0xFFFFFFFE); // Clipboard mode: no border at all
                        }
                    }
                    return Classes.SettingsManager.Current.ColorScheme == 1 ? 0x00D5D6D8 : 0x002D2D2D;
                }
                catch { return 0x002D2D2D; }
            }
        }
        private const int DWMWA_CLOAK = NativeMethods.DWMWA_CLOAK;
        private const int DWMWA_BORDER_COLOR = NativeMethods.DWMWA_BORDER_COLOR;
        private const int DWMWA_COLOR_DARK_GRAY = NativeMethods.DWMWA_COLOR_DARK_GRAY;
        private const int GWL_EXSTYLE = NativeMethods.GWL_EXSTYLE;
        private const int WS_EX_NOACTIVATE = NativeMethods.WS_EX_NOACTIVATE;
        private const int WS_EX_TOOLWINDOW = NativeMethods.WS_EX_TOOLWINDOW;
        private const int WS_EX_APPWINDOW = NativeMethods.WS_EX_APPWINDOW;
        private const int WS_EX_LAYERED = NativeMethods.WS_EX_LAYERED;
        private const uint LWA_ALPHA = NativeMethods.LWA_ALPHA;
        private const int KEYEVENTF_KEYUP = NativeMethods.KEYEVENTF_KEYUP;
        private const int VK_CONTROL = NativeMethods.VK_CONTROL;
        private const int VK_V = NativeMethods.VK_V;
        private const int VK_MENU = NativeMethods.VK_MENU;
        private const int WM_CLIPBOARDUPDATE = NativeMethods.WM_CLIPBOARDUPDATE;

        // Pointer-width safe Get/SetWindowLong wrappers for 32/64-bit compatibility
        private static int GetWindowLongSafe(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return (int)NativeMethods.GetWindowLongPtr(hWnd, nIndex);
            return NativeMethods.GetWindowLong(hWnd, nIndex);
        }
        private static IntPtr SetWindowLongSafe(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            if (IntPtr.Size == 8)
                return NativeMethods.SetWindowLongPtr(hWnd, nIndex, (IntPtr)dwNewLong);
            return (IntPtr)NativeMethods.SetWindowLong(hWnd, nIndex, dwNewLong);
        }

        private const int HOTKEY_ID = 9000;
        private const int HOTKEY_QUICKPASTE_BASE = 9001; // 9001-9009 for Alt+1 through Alt+9
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int WM_HOTKEY = 0x0312;

        // Hover preview popup state (DISABLED — replaced by expand/collapse chevron button)
#pragma warning disable CS0649
        private System.Windows.Threading.DispatcherTimer? _hoverPreviewTimer;
        private ClipboardItem? _hoveredItem;
        private Windows.PreviewPopup? _activePreviewPopup;
#pragma warning restore CS0649

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                int exStyle = GetWindowLongSafe(helper.Handle, GWL_EXSTYLE);
                SetWindowLongSafe(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_LAYERED);

                // Force rounded corners on all devices (VMs, Win10-style DWM, etc.)
                int cornerPref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(helper.Handle, 33, ref cornerPref, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE

                // ═══ JITTER FIX: Disable DWM's own window transitions ═══
                // DWM applies its own show/hide/move animations on windows (slide-in, fade, etc.).
                // These DWM-level animations overlap with our WPF opacity+slide animation,
                // causing visible "bouncing" that the WPF profiler can't detect because DWM
                // operates at a lower composition layer.
                // DWMWA_TRANSITIONS_FORCEDISABLED (2) = TRUE suppresses all DWM transitions.
                int disableTransitions = 1; // TRUE
                DwmSetWindowAttribute(helper.Handle, 3, ref disableTransitions, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED

                // Pin the entire application to all virtual desktops natively!
                try
                {
                    string appId = "FlyShelf.Clipboard";
                    Classes.NativeMethods.SetCurrentProcessExplicitAppUserModelID(appId);
                    
                    var pinnedAppsType = Type.GetTypeFromCLSID(new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD"));
                    if (pinnedAppsType != null)
                    {
                        var pinnedApps = Activator.CreateInstance(pinnedAppsType) as Classes.NativeMethods.IVirtualDesktopPinnedApps;
                        if (pinnedApps != null)
                        {
                            int hr = pinnedApps.IsAppIdPinned(appId, out int isPinned);
                            if (hr == 0 && isPinned == 0)
                            {
                                pinnedApps.PinAppID(appId);
                                Classes.Logger.LogAction("DESKTOP", "Natively pinned FlyShelf AppID to all virtual desktops!");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("DESKTOP_ERR", $"Failed to pin AppID: {ex.Message}");
                }

                // Initialize _summonedDesktopId
                try
                {
                    var localVdm = (Classes.NativeMethods.IVirtualDesktopManager)new Classes.NativeMethods.VirtualDesktopManager();
                    localVdm.GetWindowDesktopId(helper.Handle, out _summonedDesktopId);
                    Classes.Logger.LogAction("DESKTOP", $"Initial virtual desktop GUID: {_summonedDesktopId}");
                }
                catch { } // Best-effort: failure is acceptable
            }
        }

        public MainWindow()
        {
            var vm = new FlyShelfViewModel();
            this.DataContext = vm;
            _viewModel = vm;
            InitializeComponent();

#if MSIX_STORE
            // Hide Research Mode in Store build — feature not ready for public release
            ResearchToggleBtn.Visibility = Visibility.Collapsed;
            AltResearchBtn.Visibility = Visibility.Collapsed;
#endif
            this.Width = _viewModel.CurrentFlyShelfWidth;

            // Load shortcuts at startup
            Classes.ShortcutManager.Load();

            this.PreviewKeyDown += Window_PreviewKeyDown;

            // Register global hotkeys EAGERLY in constructor — do NOT wait for Loaded event.
            // EnsureHandle() forces HWND creation so hotkeys work immediately on app start.
            var interop = new WindowInteropHelper(this);
            interop.EnsureHandle();
            var hwnd = interop.Handle;
            if (hwnd != IntPtr.Zero)
            {
                HwndSource.FromHwnd(hwnd)?.AddHook(HwndHook);
                AddClipboardFormatListener(hwnd);
                var settings = Classes.SettingsManager.Current;
                bool hotkeyRegistered = RegisterHotKey(hwnd, HOTKEY_ID, settings.HotkeyModifier | MOD_NOREPEAT, settings.HotkeyKey);
                if (!hotkeyRegistered)
                    Windows.ToastWindow.ShowToast($"Could not register {settings.HotkeyDisplayString} — another app may be using it. Change in Settings.");
                Classes.Logger.LogAction("HOTKEY", $"{settings.HotkeyDisplayString} registered: {hotkeyRegistered}");

                if (Classes.SettingsManager.Current.EnableQuickPasteHotkeys)
                {
                    for (int i = 1; i <= 9; i++)
                    {
                        bool ok = RegisterHotKey(hwnd, HOTKEY_QUICKPASTE_BASE + i, MOD_ALT | MOD_NOREPEAT, (uint)(0x30 + i));
                        if (!ok) Classes.Logger.LogAction("HOTKEY", $"Alt+{i} FAILED to register");
                    }
                    RegisterHotKey(hwnd, HOTKEY_QUICKPASTE_BASE + 10, MOD_ALT | MOD_NOREPEAT, 0x30); // Alt+0
                    Classes.Logger.LogAction("HOTKEY", $"Alt+1 through Alt+0 registered");
                }
            }


            // TODO: Store handler and unsubscribe in OnClosed
            _sizeChangedHandler = (s, e) =>
            {
                // CRITICAL: Don't reposition during spawn animation — causes visible bouncing
                // because ActualHeight fluctuates as content loads/generates, and each change
                // triggers a Top recalculation that fights the initial positioning.
                if (_isShowAnimating) return;

                // POST-ANIMATION COOLDOWN: Block repositioning for 500ms after animation ends.
                // RenderVisibleThumbnails fires at ~300ms (after animation ends at ~250ms),
                // calling UpdateLayout() which triggers SizeChanged. Without this cooldown,
                // the window bounces as content settles.
                if (_showAnimEndTime != DateTime.MinValue &&
                    (DateTime.UtcNow - _showAnimEndTime).TotalMilliseconds < 500)
                    return;

                if (_isEdgeLocked && _lockedBottomEdge > 0 && this.ActualWidth > 0 && this.ActualHeight > 0)
                {
                    var workArea = GetWorkAreaForPoint(Left, Top);

                    if (e.WidthChanged && e.PreviousSize.Width > 0)
                    {
                        double newLeft = this.Left + (e.PreviousSize.Width / 2.0) - (this.ActualWidth / 2.0);
                        
                        // Full bounds clamp: keep within visible work area
                        if (newLeft + this.ActualWidth > workArea.Left + workArea.Width - 16)
                            newLeft = workArea.Left + workArea.Width - this.ActualWidth - 16;
                        if (newLeft < workArea.Left + 16)
                            newLeft = workArea.Left + 16;

                        this.Left = newLeft;
                    }

                    if (e.HeightChanged && !IsDeletingItem)
                    {
                        double newTop = _lockedBottomEdge - this.ActualHeight - 20;
                        
                        // Full bounds clamp: keep within visible work area
                        if (newTop < workArea.Top + 16)
                            newTop = workArea.Top + 16;
                        if (newTop + this.ActualHeight > workArea.Top + workArea.Height - 16)
                            newTop = workArea.Top + workArea.Height - this.ActualHeight - 16;

                        double drift = Math.Abs(newTop - this.Top);
                        if (drift > 0.5)
                        {
                            Classes.Logger.LogAction("SIZE_BOUNCE", $"SizeChanged moving Top {this.Top:F1}→{newTop:F1} (Δ={drift:F1}px) H={this.ActualHeight:F0}→{e.NewSize.Height:F0} edge={_lockedBottomEdge:F1}");
                        }
                        this.Top = newTop;
                    }
                }
            };

            this.SizeChanged += _sizeChangedHandler;

            // Restore keyboard focus to ListView or Notes textbox after window is moved/repositioned
            // TODO: Store handler and unsubscribe in OnClosed
            _activatedHandler = (s, e) =>
            {
                // Skip re-focus during show animation or invisible pre-animation phase
                // to prevent heavy layout/container generation causing first-spawn flash
                if (_isShowAnimating || this.Opacity < 0.05) return;

                // Skip re-focus if a topmost child window (QuickLook) is active — prevents infinite activation loop
                if (System.Windows.Application.Current.Windows.OfType<Window>().Any(w => w.Topmost && w != this && w.IsActive)) return;

                if (_isNotesActive)
                {
                    FocusNotesActiveTextBox();
                    return;
                }
                if (_isTodoActive)
                {
                    FocusTodoActiveTextBox();
                    return;
                }

                // Debounce: only re-focus if the ListView isn't already keyboard-focused
                if (!ShelfListView.IsKeyboardFocusWithin)
                {
                    FocusFirstItemContainer();
                }
            };

            this.Activated += _activatedHandler;

            // TODO: Store handler and unsubscribe in OnClosed
            _viewModelPropertyChangedHandler = (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                if (_isSuppressingSizeSync) return;
                // Don't reposition during spawn animation or post-animation cooldown
                if (_isShowAnimating) return;
                if (_showAnimEndTime != DateTime.MinValue &&
                    (DateTime.UtcNow - _showAnimEndTime).TotalMilliseconds < 500) return;

                if (e.PropertyName == nameof(FlyShelfViewModel.CurrentFlyShelfMaxHeight))
                {
                    this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
                    if (_viewModel.CurrentMode != 0)
                    {
                        this.Height = _viewModel.CurrentFlyShelfMaxHeight;
                    }
                    Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.Render);
                    
                    if (_isEdgeLocked && this.ActualHeight > 0)
                    {
                        double newTop = _lockedBottomEdge - this.ActualHeight - 20;
                        var workArea = GetWorkAreaForPoint(Left, Top);
                        if (newTop < workArea.Top + 16)
                            newTop = workArea.Top + 16;
                        if (newTop + this.ActualHeight > workArea.Top + workArea.Height - 16)
                            newTop = workArea.Top + workArea.Height - this.ActualHeight - 16;
                        this.Top = newTop;
                    }
                }
                else if (e.PropertyName == nameof(FlyShelfViewModel.CurrentFlyShelfWidth))
                {
                    this.Width = _viewModel.CurrentFlyShelfWidth;
                }
                else if (e.PropertyName == nameof(FlyShelfViewModel.CurrentMode))
                {
                    if (_isSearchActive) CloseSearch();
                    UpdateToolbarButtonsVisibility();
                }
                });
            };

            _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;

            // Live-refresh wallpaper when user changes it in settings
            _settingsChangedHandler = (s, e) =>
            {
                // Live-update the summon hotkey when user changes it in settings
                if (e.PropertyName == nameof(Classes.AdvanceSettings.HotkeyModifier) ||
                    e.PropertyName == nameof(Classes.AdvanceSettings.HotkeyKey))
                {
                    ReRegisterSummonHotkey();
                }
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.ClipboardWallpaperPath))
                    Dispatcher.InvokeAsync(() => ApplyWallpaper());
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.ColorThemeName))
                {
                    string newTheme = Classes.SettingsManager.Current.ColorThemeName;
                    if (string.IsNullOrEmpty(newTheme) || newTheme.Equals("Default", System.StringComparison.OrdinalIgnoreCase))
                        Dispatcher.InvokeAsync(() => Classes.ThemeManager.Instance.RemoveColorTheme());
                    else
                        Dispatcher.InvokeAsync(() => Classes.ThemeManager.Instance.ApplyColorTheme(newTheme));
                }
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.EnableBlurBehind) ||
                         e.PropertyName == nameof(Classes.AdvanceSettings.ThemeDisplayMode))
                {
                    Dispatcher.InvokeAsync(() => _themeChangedHandler?.Invoke(Classes.ThemeManager.Instance.ActiveTheme));
                }
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.UseAlternateClipboardUI))
                {
                    Dispatcher.InvokeAsync(() => ApplyAlternateUIMode());
                }
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.EnableDesktopMascot))
                {
                    Dispatcher.InvokeAsync(() => UpdateMascotCompanionState());
                }
            };
            Classes.SettingsManager.Current.PropertyChanged += _settingsChangedHandler;

            // Auto-dismiss merge state when new items arrive on the shelf + Reapply active category/search filters to keep UI state robust
            // TODO: Store handler and unsubscribe in OnClosed
            _droppedItemsCollectionChangedHandler = (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add ||
                    e.Action == NotifyCollectionChangedAction.Reset)
                {
                    // CRITICAL: On Reset events (from AddRange/InsertRange), WPF's ListCollectionView
                    // internally rebuilds and DROPS the Filter delegate. Reapply SYNCHRONOUSLY here
                    // to prevent even a single frame of unfiltered items appearing.
                    // PERF: Skip if we're already applying a filter (e.g., FilterCategory_Click
                    // is setting view.Filter — the Reset from that is expected and handled).
                    if (!_isApplyingFilter &&
                        e.Action == NotifyCollectionChangedAction.Reset &&
                        (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox?.Text))))
                    {
                        ReapplyActiveFilters();
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (MergePdfToolbarBtn.Visibility == Visibility.Visible)
                        {
                            DismissMergeState();
                        }

                        // Hide Alt+C watermark once clipboard has enough items to fill the view
                        UpdateAltCWatermarkVisibility();
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                else if (e.Action == NotifyCollectionChangedAction.Remove)
                {
                    // PERF: Skip filter reapplication during animated deletion.
                    // AnimateAndRemoveItems sets IsDeletingItem=true and calls
                    // ReapplyActiveFilters() once in its completion callback.
                    // Without this guard, filters were being refreshed 6-7 times
                    // per delete (sync + deferred here + animation callback).
                    if (!IsDeletingItem)
                    {
                        if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox?.Text)))
                        {
                            ReapplyActiveFilters();
                        }
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (MergePdfToolbarBtn.Visibility == Visibility.Visible)
                        {
                            DismissMergeState();
                        }
                        UpdateAltCWatermarkVisibility();
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            };
            _viewModel.DroppedItems.CollectionChanged += _droppedItemsCollectionChangedHandler;

            // Calculate initial toolbar buttons visibility based on current mode
            UpdateToolbarButtonsVisibility();

            // Apply Aero UI mode if enabled in settings
            ApplyAlternateUIMode();

            // ═══ Update Available Badge — DISABLED ═══
            // Update notifications removed from clipboard per user request.
            // The UpdateBadge element exists in XAML but stays Collapsed.
            // _updateStatusChangedHandler and GlobalUpdateStatusChanged subscription are skipped.

            // ═══ POST-UPDATE HEALTH VERIFICATION ═══
            // If this is the first launch after an update, mark it as healthy
            // once the UI is fully rendered. If the app crashes before this fires,
            // the next startup will auto-rollback from the .bak backup.
            Dispatcher.InvokeAsync(() =>
            {
                Classes.UpdateManager.MarkUpdateVerified();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void DeleteContextMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi)
            {
                var cm = mi.Parent as ContextMenu ?? (mi.Parent is DependencyObject obj ? FindVisualParent<ContextMenu>(obj) : null);
                if (cm != null && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
                {
                    _justDeletedAnItem = true;
                    AnimateAndRemoveItems(new System.Collections.Generic.List<ClipboardItem> { item });
                }
            }
        }

        // ═══ CONTEXT MENU: Auto-close after 5 seconds + close on window deactivate ═══
        private System.Windows.Threading.DispatcherTimer? _contextMenuAutoCloseTimer;
        private ContextMenu _activeCardContextMenu;

        private System.Windows.Threading.DispatcherTimer EnsureContextMenuTimer()
        {
            if (_contextMenuAutoCloseTimer == null)
            {
                _contextMenuAutoCloseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
                };
                _contextMenuAutoCloseTimer.Tick += (s, args) =>
                {
                    _contextMenuAutoCloseTimer.Stop();
                    if (_activeCardContextMenu != null && _activeCardContextMenu.IsOpen)
                        _activeCardContextMenu.IsOpen = false;
                    _activeCardContextMenu = null;
                };
            }
            return _contextMenuAutoCloseTimer;
        }

        private void CardContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu cm)
            {
                _activeCardContextMenu = cm;

                // Start 5-second auto-close timer (reuse single instance)
                var timer = EnsureContextMenuTimer();
                timer.Stop();
                timer.Start();

                // When context menu closes (by any means), clean up timer
                cm.Closed += CardContextMenu_Closed;

                // ═══ Populate "Send to Device" submenu with connected peers ═══
                PopulateSendToDeviceMenu(cm);

                // ═══ Contextual Tip: First context menu ═══
                Windows.TipBadge.Show("context_menu_first_use", "Try Smart Actions for auto-detect features", cm.PlacementTarget as UIElement);
            }
        }

        private void CardContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            _contextMenuAutoCloseTimer?.Stop();
            if (sender is ContextMenu cm)
                cm.Closed -= CardContextMenu_Closed; // Unsubscribe to avoid leak
            _activeCardContextMenu = null;
        }

        private void RootContent_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Close any open card context menu when clicking anywhere in the app
            if (_activeCardContextMenu != null && _activeCardContextMenu.IsOpen)
            {
                // Check if the click is inside the context menu popup — if not, close it
                if (e.OriginalSource is DependencyObject source)
                {
                    DependencyObject parent = source;
                    while (parent != null)
                    {
                        if (parent is ContextMenu) return; // Click is inside context menu, don't close
                        if (parent is MenuItem) return; // Click is on a menu item
                        parent = VisualTreeHelper.GetParent(parent);
                    }
                }
                _activeCardContextMenu.IsOpen = false;
            }

            // Close OverflowPopup when clicking outside it.
            // StaysOpen="False" can fail when the popup HWND is forced topmost.
            if (OverflowPopup != null && OverflowPopup.IsOpen)
            {
                // Don't close if clicking the MoreBtn itself (the click handler will toggle it)
                if (e.OriginalSource is DependencyObject src)
                {
                    DependencyObject parent = src;
                    while (parent != null)
                    {
                        if (parent == MoreBtn) return;
                        if (parent == OverflowPopup.Child) return; // Click inside the popup
                        parent = VisualTreeHelper.GetParent(parent);
                    }
                }
                OverflowPopup.IsOpen = false;
            }
        }

        private void PasteFilePath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item 
                && !string.IsNullOrEmpty(item.FilePath))
            {
                _ = CopyItemAndPaste(item, hideWindow: true, pastePathOnly: true);
            }
        }

        private void CopyFilePath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string? path = !string.IsNullOrEmpty(item.FilePath) ? item.FilePath : item.RawContent;
                if (!string.IsNullOrEmpty(path))
                {
                    FlyShelf.Classes.ClipboardHelper.SafeSetTextAllowCapture(path);
                    FlyShelf.Windows.ToastWindow.ShowToast("Path copied!");
                }
            }
        }

        private void CopyFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                if (!string.IsNullOrEmpty(item.FilePath) && (System.IO.File.Exists(item.FilePath) || System.IO.Directory.Exists(item.FilePath)))
                {
                    try
                    {
                        var dataObj = new DataObject();
                        var dropList = new System.Collections.Specialized.StringCollection { item.FilePath };
                        dataObj.SetFileDropList(dropList);
                        dataObj.SetData("FileNameW", new string[] { item.FilePath });
                        dataObj.SetData("FileName", new string[] { item.FilePath });
                        dataObj.SetData(DataFormats.UnicodeText, item.FilePath);
                        dataObj.SetData(DataFormats.Text, item.FilePath);

                        byte[] moveEffect = new byte[] { 5, 0, 0, 0 }; // DragDropEffects.Copy
                        using var dropEffect = new System.IO.MemoryStream();
                        dropEffect.Write(moveEffect, 0, moveEffect.Length);
                        dataObj.SetData("Preferred DropEffect", dropEffect);

                        Classes.ClipboardHelper.SafeSetDataObject(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast("File copied!");
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("COPY_FILE_ERROR", ex.Message);
                        FlyShelf.Windows.ToastWindow.ShowToast("Failed to copy file.");
                    }
                }
            }
        }

        private void PrettyPrintJson_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string raw = item.RawContent ?? item.FileName ?? "";
                string pretty = FlyShelf.Classes.SmartContentDetector.PrettyPrintJson(raw);
                try { Clipboard.SetText(pretty); } catch { try { Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Clipboard access failed — please try again.")); } catch { } }
                FlyShelf.Windows.ToastWindow.ShowToast("Formatted JSON copied!");
            }
        }

        private void ComposeEmail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string email = FlyShelf.Classes.SmartContentDetector.ExtractFirstEmail(item.RawContent ?? item.FileName ?? "");
                if (!string.IsNullOrEmpty(email))
                {
                    _ = System.Threading.Tasks.Task.Run(() => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"mailto:{email}") { UseShellExecute = true }); } catch { } });
                }
            }
        }

        private void EvaluateMath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string expr = (item.RawContent ?? item.FileName ?? "").Trim();
                string result = FlyShelf.Classes.SmartContentDetector.EvaluateMath(expr);
                try { Clipboard.SetText(result); } catch { try { Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Clipboard access failed — please try again.")); } catch { } }
                FlyShelf.Windows.ToastWindow.ShowToast($"Result: {result} (copied!)");
            }
        }

        private void DecodeBase64_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string raw = (item.RawContent ?? item.FileName ?? "").Trim();
                string decoded = FlyShelf.Classes.SmartContentDetector.DecodeBase64(raw);
                try { Clipboard.SetText(decoded); } catch { try { Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Clipboard access failed — please try again.")); } catch { } }
                FlyShelf.Windows.ToastWindow.ShowToast("Decoded Base64 copied!");
            }
        }

        private void ConvertEpoch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string raw = (item.RawContent ?? item.FileName ?? "").Trim();
                string dateStr = FlyShelf.Classes.SmartContentDetector.EpochToDateTime(raw);
                try { Clipboard.SetText(dateStr); } catch { try { Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Clipboard access failed — please try again.")); } catch { } }
                FlyShelf.Windows.ToastWindow.ShowToast($"{dateStr} (copied!)");
            }
        }

        // ═══ AI ACTIONS (Clipboard Items) ═══

        private void AISummarize_Click(object sender, RoutedEventArgs e)
        {
            RunClipboardAIAction(sender, "Summarize");
        }

        private void AIFixGrammar_Click(object sender, RoutedEventArgs e)
        {
            RunClipboardAIAction(sender, "Rewrite"); // Rewrite includes grammar fix
        }

        private void AIRewriteFormal_Click(object sender, RoutedEventArgs e)
        {
            RunClipboardAIActionCustom(sender, "Make this text formal and professional. Return only the rewritten text.");
        }

        private void AIRewriteCasual_Click(object sender, RoutedEventArgs e)
        {
            RunClipboardAIActionCustom(sender, "Make this text casual and friendly. Return only the rewritten text.");
        }

        private void AIExplainCode_Click(object sender, RoutedEventArgs e)
        {
            RunClipboardAIAction(sender, "Explain");
        }

        private void RunClipboardAIAction(object sender, string actionType)
        {
            if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                string text = item.RawContent ?? item.FileName ?? "";
                if (string.IsNullOrWhiteSpace(text))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No text content to process.");
                    return;
                }

                // Check AI availability
                if (!FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey &&
                    !FlyShelf.Classes.WindowsAIService.Instance.IsAvailable)
                {
                    FlyShelf.Classes.AiProviderService.Instance.EnsureApiKeyOrPrompt(this);
                    if (!FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey) return;
                }

                var aiWindow = new FlyShelf.Windows.NotesAIWindow(text, actionType);
                aiWindow.Owner = this;
                if (WindowHelper.ShowDialogInForeground(aiWindow, this) == true && aiWindow.IsApplied)
                {
                    try { Clipboard.SetText(aiWindow.ResultText); } catch { try { Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Clipboard access failed — please try again.")); } catch { } }
                    FlyShelf.Windows.ToastWindow.ShowToast("AI result copied to clipboard!");
                }
            }
        }

        private async void RunClipboardAIActionCustom(object sender, string systemPrompt)
        {
            try
            {
                if (sender is MenuItem mi && mi.Tag is FlyShelf.ViewModels.ClipboardItem item)
                {
                    string text = item.RawContent ?? item.FileName ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("No text content to process.");
                        return;
                    }

                    // Check AI availability
                    if (!FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey &&
                        !FlyShelf.Classes.WindowsAIService.Instance.IsAvailable)
                    {
                        FlyShelf.Classes.AiProviderService.Instance.EnsureApiKeyOrPrompt(this);
                        if (!FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey) return;
                    }

                    try
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("AI working...");
                        string result = await System.Threading.Tasks.Task.Run(async () =>
                            await FlyShelf.Classes.AiProviderService.Instance.GenerateAsync(text, systemPrompt));

                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            try { Clipboard.SetText(result); } catch { try { Application.Current?.Dispatcher?.InvokeAsync(() => Windows.ToastWindow.ShowToast("⚠ Clipboard access failed — please try again.")); } catch { } }
                            FlyShelf.Windows.ToastWindow.ShowToast("AI result copied to clipboard!");
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"AI error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("AI_CUSTOM_ERROR", $"RunClipboardAIActionCustom failed: {ex.Message}");
            }
        }

        // ═══ Thin wrappers for NativeMethods — used across many MainWindow partial files ═══
        private static IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, NativeMethods.WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags)
            => NativeMethods.SetWinEventHook(eventMin, eventMax, hmodWinEventProc, lpfnWinEventProc, idProcess, idThread, dwFlags);
        private static bool UnhookWinEvent(IntPtr hWinEventHook) => NativeMethods.UnhookWinEvent(hWinEventHook);
        private static bool AddClipboardFormatListener(IntPtr hwnd) => NativeMethods.AddClipboardFormatListener(hwnd);
        private static bool RemoveClipboardFormatListener(IntPtr hwnd) => NativeMethods.RemoveClipboardFormatListener(hwnd);
        private static uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId) => NativeMethods.GetWindowThreadProcessId(hWnd, out processId);
        private static uint GetCurrentProcessId() => NativeMethods.GetCurrentProcessId();
        private static bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) => NativeMethods.AttachThreadInput(idAttach, idAttachTo, fAttach);
        private static uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();
        private static bool EnumWindows(NativeMethods.EnumWindowsProc lpEnumFunc, IntPtr lParam) => NativeMethods.EnumWindows(lpEnumFunc, lParam);
        private static int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount) => NativeMethods.GetClassName(hWnd, lpClassName, nMaxCount);
        private static bool IsWindow(IntPtr hWnd) => NativeMethods.IsWindow(hWnd);
        private static bool IsWindowVisible(IntPtr hWnd) => NativeMethods.IsWindowVisible(hWnd);
        private static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();
        private static bool SetForegroundWindow(IntPtr hWnd) => NativeMethods.SetForegroundWindow(hWnd);
        private static IntPtr WindowFromPhysicalPoint(Classes.NativeMethods.POINT Point) => NativeMethods.WindowFromPhysicalPoint(Point);
        private static IntPtr GetAncestor(IntPtr hwnd, uint gaFlags) => NativeMethods.GetAncestor(hwnd, gaFlags);
        private static void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo) => NativeMethods.mouse_event(dwFlags, dx, dy, dwData, dwExtraInfo);
        private static bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags) => NativeMethods.SetLayeredWindowAttributes(hwnd, crKey, bAlpha, dwFlags);
        private static bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) => NativeMethods.RegisterHotKey(hWnd, id, fsModifiers, vk);
        private static bool UnregisterHotKey(IntPtr hWnd, int id) => NativeMethods.UnregisterHotKey(hWnd, id);
        private static int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount) => NativeMethods.GetWindowText(hWnd, lpString, nMaxCount);
        private static int GetWindowLong(IntPtr hWnd, int nIndex) => GetWindowLongSafe(hWnd, nIndex);
        private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong) => SetWindowLongSafe(hWnd, nIndex, dwNewLong);
        private static bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags) => NativeMethods.SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
#pragma warning disable CA2020
        private static void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo) => NativeMethods.keybd_event(bVk, bScan, dwFlags, (IntPtr)(long)dwExtraInfo);
#pragma warning restore CA2020
        private static void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo) => NativeMethods.keybd_event(bVk, bScan, (uint)dwFlags, (IntPtr)dwExtraInfo);

        private const int HWND_TOPMOST = NativeMethods.HWND_TOPMOST;
        private const uint SWP_NOSIZE = NativeMethods.SWP_NOSIZE;
        private const uint SWP_NOMOVE = NativeMethods.SWP_NOMOVE;
        private const uint SWP_NOACTIVATE = NativeMethods.SWP_NOACTIVATE;

        private const uint WINEVENT_OUTOFCONTEXT = NativeMethods.WINEVENT_OUTOFCONTEXT;
        private const uint EVENT_SYSTEM_FOREGROUND = NativeMethods.EVENT_SYSTEM_FOREGROUND;
        private const uint MOUSEEVENTF_LEFTDOWN = NativeMethods.MOUSEEVENTF_LEFTDOWN;
        private const uint MOUSEEVENTF_LEFTUP = NativeMethods.MOUSEEVENTF_LEFTUP;

        private IntPtr _foregroundHook = IntPtr.Zero;
        private NativeMethods.WinEventDelegate _foregroundDelegate = null!;

        /// <summary>
        /// Simulates a mouse click at the specified screen coordinates.
        /// Used by drag-and-drop fallback to click into a text field before Ctrl+V.
        /// </summary>
        private static void SendClickAt(int screenX, int screenY)
        {
            NativeMethods.SetCursorPos(screenX, screenY);
            NativeMethods.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private bool _isLoadedInitialized = false;

        private void MicaWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoadedInitialized) return;
            _isLoadedInitialized = true;

            // Setup global foreground window change listener to dismiss when clicking elsewhere (handles non-activated summons)
            try
            {
                _foregroundDelegate = new NativeMethods.WinEventDelegate(ForegroundChangedCallback);
                _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("HOOK_FAIL", $"Failed to setup foreground win event hook: {ex.Message}");
            }

            // DWM border styling — set synchronously, no deferred callback
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int borderColor = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }

            // Hook state changes to prevent DWM border leakage on minimize/maximize/restore/etc.
            // TODO: Store handler and unsubscribe in OnClosed
            _stateChangedHandler = (s, ev) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int cn = DWMWA_COLOR_NONE;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { } // Best-effort: failure is acceptable
            };
            this.StateChanged += _stateChangedHandler;


            // Launch the taskbar-embedded widget
            try
            {
                _taskbarWidget = new Windows.TaskbarWindow();
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET_FAIL", $"Failed to create taskbar widget: {ex.Message}");
            }

            // Initialize Incognito Mode (loads persisted state, wires events)
            try { InitializeIncognitoMode(); } catch { }

            // ═══ FIRST-LAUNCH: Auto-summon clipboard AND open Hub (Settings tab) after onboarding ═══
            if (_isFirstLaunchAfterOnboarding)
            {
                _isFirstLaunchAfterOnboarding = false;
                Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(2500);
                    // Summon the clipboard popup so user sees it
                    try { ToggleMainClipboard(); } catch { }

                    // Also open the Hub window directed to the Settings tab
                    await System.Threading.Tasks.Task.Delay(800);
                    try
                    {
                        OpenApp_Click_Internal();
                        // Navigate to Settings tab after Hub opens
                        if (_hubWindowInstance != null)
                        {
                            _hubWindowInstance.NavigateToTab("Settings");
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                }, System.Windows.Threading.DispatcherPriority.Background);
            }

            // Launch desktop mascot companion if enabled
            UpdateMascotCompanionState();

            // Attach window-level smooth scrolling with specialized snappy ClipboardProfile
            Classes.SmoothScroll.AttachToWindow(this, Classes.SmoothScroll.ClipboardProfile);

            // Track scrolling to optimize hover button summoning (prevent during scroll)
            ShelfListView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ShelfListView_ScrollChanged));
            ShelfListView.MouseLeave += ShelfListView_MouseLeave;

            // Hook coast-phase prefetch: during touchpad deceleration, SmoothScroll fires
            // this event every ~200ms so we can preload images in the ±800px prefetch zone
            // before they enter the viewport — premium "images always loaded" experience.
            _coastPrefetchHandler = () =>
            {
                Dispatcher.InvokeAsync(() => RenderVisibleThumbnails(onlyFirstTen: false),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            Classes.SmoothScroll.CoastPrefetchNeeded += _coastPrefetchHandler;

            // Apply wallpaper is now handled by the deferred theme block at ApplicationIdle
            // (no more redundant early load that gets overwritten by theme init)

            // ═══ BACKDROP STRATEGY: Set once, never toggle ═══
            // On Win11+ with blur enabled, use Mica glass. Otherwise fallback to solid background.
            // ROOT CAUSE RESOLVED: The jitter was caused by DWM redirection surface reallocation
            // from the -20000 coordinate jump, not Mica composition. Mica is safe to enable.
            if (!Classes.NativeMethods.ShouldUseBlur())
            {
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                ApplyPopupBackground();
            }


            // HubWindow is created on-demand in OpenApp_Click_Internal() to save ~15-30MB idle RAM.
            // The 286KB XAML visual tree is only materialized when the user actually opens settings.

            // ═══ SCROLL-TO-HERE: Click anywhere on scrollbar track → jump to that position ═══
            // Default WPF behavior fires PageUp/PageDown RepeatButtons which oscillate and glitch.
            // This intercepts track clicks and calculates the exact offset from the click position.
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var sv = GetShelfScrollViewer();
                    if (sv == null) return;

                    var verticalScrollBar = FindVisualChild<System.Windows.Controls.Primitives.ScrollBar>(sv);
                    if (verticalScrollBar == null) return;

                    // TODO: Store handler and unsubscribe in OnClosed
                    _verticalScrollBar = verticalScrollBar;
                    _verticalScrollBarPreviewMouseLeftButtonDownHandler = (s, args) =>
                    {
                        // Let thumb dragging work normally — only intercept track area clicks
                        var thumb = FindVisualChild<System.Windows.Controls.Primitives.Thumb>(verticalScrollBar);
                        if (thumb != null && thumb.IsMouseOver) return;

                        var track = FindVisualChild<System.Windows.Controls.Primitives.Track>(verticalScrollBar);
                        if (track == null) return;

                        // Use WPF Track's built-in geometry math to convert click position → scroll value
                        Point clickPoint = args.GetPosition(track);
                        double newValue = track.ValueFromPoint(clickPoint);

                        // PERF: Set IsScrolling BEFORE the jump so any synchronous layout
                        // callbacks during virtualization realization can check this flag
                        // and skip expensive work (e.g., RenderVisibleThumbnails eviction).
                        if (_viewModel != null) _viewModel.IsScrolling = true;

                        sv.ScrollToVerticalOffset(newValue);
                        Classes.SmoothScroll.ResetScrollState(sv);

                        // PERF: Throttle thumbnail loading to 300ms after track jumps.
                        // The default 30ms timer fires before the virtualizer has settled,
                        // causing a layout storm. 300ms gives containers time to materialize.
                        if (_scrollHighQualityTimer != null)
                        {
                            _scrollHighQualityTimer.Stop();
                            _scrollHighQualityTimer.Interval = TimeSpan.FromMilliseconds(300);
                            _scrollHighQualityTimer.Start();
                        }

                        args.Handled = true;
                    };
                    _verticalScrollBar.PreviewMouseLeftButtonDown += _verticalScrollBarPreviewMouseLeftButtonDownHandler;
                }
                catch { } // Best-effort: failure is acceptable
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            // ═══ MASCOT THEME ENGINE INIT ═══
            // Deferred initialization so it doesn't block main UI render
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Enforce free tier restrictions at startup
                    if (!Classes.LicenseManager.IsPro)
                    {
                        bool changed = false;
                        if (Classes.SettingsManager.Current.ThemeDisplayMode == "glass")
                        {
                            Classes.SettingsManager.Current.ThemeDisplayMode = "mica";
                            Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                            changed = true;
                        }
                        
                        string activeThemeName = Classes.SettingsManager.Current.ActiveThemeName ?? "";
                        if (!string.IsNullOrEmpty(activeThemeName) && !Classes.LicenseManager.CanUseTheme(activeThemeName))
                        {
                            Classes.SettingsManager.Current.ActiveThemeName = "";
                            if (Classes.SettingsManager.Current.ThemeDisplayMode == "theme")
                            {
                                Classes.SettingsManager.Current.ThemeDisplayMode = "mica";
                                Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                            }
                            changed = true;
                        }
                        if (changed)
                        {
                            Classes.SettingsManager.Save();
                        }
                    }

                    Classes.ThemeManager.Instance.Initialize();
                    Classes.ThemeManager.Instance.RestoreColorTheme();
                    Classes.AnimationTriggerService.Instance.Initialize();

                    // ═══ Unified Header Mascot Event Routing ═══
                    // Route all mascot triggers directly to the header mascot control MascotIdle
                    _mascotAnimationRequestedHandler = (s, e) =>
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            // If the current display mode is NOT custom "theme", we should absolutely ignore all mascot animations and ensure the mascot is stopped/hidden!
                            string displayMode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica";
                            if (displayMode != "theme")
                            {
                                MascotIdle.StopAnimation();
                                return;
                            }

                            if (e.IsStop)
                            {
                                // Return to idle state when a looping action stops (e.g. search ended)
                                Classes.AnimationTriggerService.Instance.StartIdleAnimation();
                                return;
                            }

                            Classes.ThemeAnimation? anim = null;
                            if (e.TriggerName == "idle")
                            {
                                anim = e.Animation;
                            }
                            else if (e.TriggerName == "delete")
                            {
                                anim = Classes.ThemeManager.Instance.GetAnimation("header_reaction") 
                                       ?? Classes.ThemeManager.Instance.GetAnimation("delete");
                            }
                            else if (e.TriggerName == "copy")
                            {
                                anim = Classes.ThemeManager.Instance.GetAnimation("insert") 
                                       ?? Classes.ThemeManager.Instance.GetAnimation("copy");
                            }
                            else if (e.TriggerName == "search")
                            {
                                anim = Classes.ThemeManager.Instance.GetAnimation("search");
                            }
                            else if (e.TriggerName == "running")
                            {
                                anim = Classes.ThemeManager.Instance.GetAnimation("running");
                            }

                            if (anim != null)
                            {
                                Classes.Logger.LogAction("MASCOT", $"Handler routing trigger='{e.TriggerName}' → file='{anim.ResolvedFilePath}'");
                                MascotIdle.PlayAnimation(anim);
                            }
                            else
                            {
                                Classes.Logger.LogAction("MASCOT", $"Handler: no animation resolved for trigger='{e.TriggerName}'");
                            }
                        });
                    };
                    Classes.AnimationTriggerService.Instance.AnimationRequested += _mascotAnimationRequestedHandler;

                    // ═══ Theme Display Mode Handler ═══
                    // Handles three modes: "mica" (system blur), "desktop" (Windows wallpaper), "theme" (custom theme)
                    _themeChangedHandler = (theme) =>
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (_isApplyingTheme) return; // Reentrancy guard — prevent overlapping theme applications
                            _isApplyingTheme = true;
                            try
                            {
                                // STEP 1: Always stop/clear the old mascot + wallpaper first
                                MascotIdle.StopAnimation();
                                try
                                {
                                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                                    animator?.Dispose();
                                }
                                catch { } // Best-effort: failure is acceptable
                                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                                WallpaperBg.Source = null;
                                WallpaperBg.Visibility = Visibility.Collapsed;
                                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                                _currentLoadedWallpaperPath = "";

                                string displayMode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica";

                                // Always remove Glass UI theme unless glass mode is active
                                if (displayMode != "glass")
                                    Classes.ThemeManager.Instance.RemoveGlassTheme();

                                if (displayMode == "mica")
                                {
                                    // ═══ MICA BLUR MODE ═══
                                    // Pure system Acrylic/Mica blur — no wallpaper, no mascot
                                    // (manual wallpaper is NOT applied in mica mode — mica is intentionally wallpaper-free)
                                    Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                    RestoreMicaBlur();
                                    Classes.Logger.LogAction("THEME", "Mode: Mica Blur — pure system backdrop");
                                }
                                else if (displayMode == "glass")
                                {
                                    // ═══ GLASS MODE ═══
                                    // Glassmorphism UI — frosted buttons, translucent cards, and optional Acrylic blur
                                    // (manual wallpaper is NOT applied in glass mode — glass is intentionally wallpaper-free)
                                    Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                    RestoreAcrylicBlur();
                                    Classes.ThemeManager.Instance.ApplyGlassTheme();
                                    Classes.ThemeManager.Instance.ApplyAeroThemeOverrides("__glass__");
                                    Classes.Logger.LogAction("THEME", "Mode: Glass (Acrylic Blur) — glassmorphism UI applied");
                                }
                                else if (displayMode == "desktop")
                                {
                                    // ═══ FLYSHELF (DESKTOP WALLPAPER) MODE ═══
                                    // Priority: Manual wallpaper > Color theme wallpaper > Desktop wallpaper
                                    ApplyNonMicaBackground();
                                    string manualWp = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                                    if (!string.IsNullOrEmpty(manualWp) && System.IO.File.Exists(manualWp))
                                    {
                                        Classes.SettingsManager.Current.ClipboardWallpaperPath = manualWp;
                                        ApplyWallpaper();
                                        Classes.Logger.LogAction("THEME", $"Mode: FlyShelf — manual wallpaper (priority): {manualWp}");
                                    }
                                    else
                                    {
                                        // Check if a color theme with its own wallpaper is active
                                        string colorThemeWp = Classes.SettingsManager.Current.ClipboardWallpaperPath ?? "";
                                        bool hasColorThemeWp = colorThemeWp.Contains("ColorThemeWallpapers", System.StringComparison.OrdinalIgnoreCase)
                                                              && System.IO.File.Exists(colorThemeWp);
                                        if (!hasColorThemeWp)
                                        {
                                            // Re-apply color theme wallpaper if a color theme is active
                                            string activeColorTheme = Classes.SettingsManager.Current.ColorThemeName ?? "Default";
                                            if (!string.IsNullOrEmpty(activeColorTheme) && !activeColorTheme.Equals("ArcticSnow", System.StringComparison.OrdinalIgnoreCase))
                                            {
                                                Classes.ThemeManager.Instance.ApplyColorTheme(activeColorTheme);
                                                colorThemeWp = Classes.SettingsManager.Current.ClipboardWallpaperPath ?? "";
                                                hasColorThemeWp = colorThemeWp.Contains("ColorThemeWallpapers", System.StringComparison.OrdinalIgnoreCase)
                                                                  && System.IO.File.Exists(colorThemeWp);
                                            }
                                        }

                                        if (hasColorThemeWp)
                                        {
                                            // Color theme wallpaper takes priority over desktop wallpaper
                                            ApplyWallpaper();
                                            Classes.Logger.LogAction("THEME", $"Mode: FlyShelf — color theme wallpaper: {colorThemeWp}");
                                        }
                                        else
                                        {
                                            // Fallback to desktop wallpaper
                                            string desktopWp = GetDesktopWallpaperPath();
                                            if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                                            {
                                                Classes.SettingsManager.Current.ClipboardWallpaperPath = desktopWp;
                                                ApplyWallpaper();
                                                Classes.Logger.LogAction("THEME", $"Mode: FlyShelf — desktop wallpaper: {desktopWp}");
                                            }
                                            else
                                            {
                                                Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                                Classes.Logger.LogAction("THEME", "Mode: FlyShelf — no wallpaper found, solid dark bg");
                                            }
                                        }
                                    }
                                }
                                else // displayMode == "theme"
                                {
                                    // ═══ CUSTOM THEME MODE ═══
                                    if (theme == null)
                                    {
                                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                        RestoreMicaBlur(); // Safe fallback to Acrylic blur
                                        Classes.Logger.LogAction("THEME", "Mode: Theme — but no theme active, falling back to Acrylic blur");
                                        return;
                                    }

                                    // Manual wallpaper takes priority over theme wallpaper
                                    string manualWpTheme = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                                    if (!string.IsNullOrEmpty(manualWpTheme) && System.IO.File.Exists(manualWpTheme))
                                    {
                                        ApplyNonMicaBackground();
                                        Classes.SettingsManager.Current.ClipboardWallpaperPath = manualWpTheme;
                                        ApplyWallpaper();
                                        Classes.Logger.LogAction("THEME", $"Mode: Theme '{theme.Name}' — manual wallpaper (priority): {manualWpTheme}");
                                    }
                                    else
                                    {
                                        string? themeWp = Classes.ThemeManager.Instance.GetWallpaperPath();
                                        if (!string.IsNullOrEmpty(themeWp) && System.IO.File.Exists(themeWp))
                                        {
                                            ApplyNonMicaBackground(); // Disable system blur for custom wallpaper
                                            Classes.SettingsManager.Current.ClipboardWallpaperPath = themeWp;
                                            ApplyWallpaper();
                                            Classes.Logger.LogAction("THEME", $"Mode: Theme '{theme.Name}' — wallpaper: {themeWp}");
                                        }
                                        else
                                        {
                                            Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                            RestoreMicaBlur(); // No custom wallpaper: preserve system Acrylic blur!
                                            Classes.Logger.LogAction("THEME", $"Mode: Theme '{theme.Name}' — no custom wallpaper, keeping Acrylic blur active");
                                        }
                                    }

                                    // Start mascot idle animation
                                    Classes.AnimationTriggerService.Instance.StartIdleAnimation();
                                }
                            }
                            catch (Exception ex) { Classes.Logger.LogAction("THEME", $"Theme switch error: {ex.Message}"); }
                            finally
                            {
                                _isApplyingTheme = false;

                                // Re-apply Aero UI color overrides after any theme/mode change
                                // to prevent the alternate clipboard from losing its color theme
                                try
                                {
                                    string currentDisplayMode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica";
                                    string aeroThemeKey = currentDisplayMode == "glass"
                                        ? "__glass__"
                                        : (Classes.SettingsManager.Current.ColorThemeName ?? "Default");
                                    Classes.ThemeManager.Instance.ApplyAeroThemeOverrides(aeroThemeKey);
                                }
                                catch { }
                                // Re-apply DWM border color override after backdrop/theme changes to prevent system accent color leakage
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

                                // Sync PremiumHeaderButtonStyle's internal resources with the
                                // now-loaded theme Color values (fixes startup flash + theme switch)
                                SyncHeaderButtonStyleResources();
                            }
                        });
                    };
                    Classes.ThemeManager.Instance.ActiveThemeChanged += _themeChangedHandler;

                    // ═══ Startup: Apply correct mode ═══
                    string startupMode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica";
                    if (startupMode == "glass")
                    {
                        // Glass mode — optional system blur, glassmorphism UI
                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                        RestoreAcrylicBlur();
                        Classes.ThemeManager.Instance.ApplyGlassTheme();
                        Classes.ThemeManager.Instance.ApplyAeroThemeOverrides("__glass__");
                    }
                    else if (startupMode == "desktop")
                    {
                        // Desktop wallpaper mode — Priority: Manual > Color theme > Desktop
                        ApplyNonMicaBackground();
                        string manualWp = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                        if (!string.IsNullOrEmpty(manualWp) && System.IO.File.Exists(manualWp))
                        {
                            Classes.SettingsManager.Current.ClipboardWallpaperPath = manualWp;
                            ApplyWallpaper();
                        }
                        else
                        {
                            // Check for active color theme wallpaper
                            string activeColorTheme = Classes.SettingsManager.Current.ColorThemeName ?? "Default";
                            bool hasColorThemeWp = false;
                            if (!string.IsNullOrEmpty(activeColorTheme) && !activeColorTheme.Equals("ArcticSnow", System.StringComparison.OrdinalIgnoreCase))
                            {
                                // Apply the color theme (which sets ClipboardWallpaperPath to theme wallpaper)
                                Classes.ThemeManager.Instance.ApplyColorTheme(activeColorTheme);
                                string colorThemeWp = Classes.SettingsManager.Current.ClipboardWallpaperPath ?? "";
                                hasColorThemeWp = colorThemeWp.Contains("ColorThemeWallpapers", System.StringComparison.OrdinalIgnoreCase)
                                                  && System.IO.File.Exists(colorThemeWp);
                            }

                            if (hasColorThemeWp)
                            {
                                ApplyWallpaper();
                            }
                            else
                            {
                                // Fallback to desktop wallpaper
                                _cachedDesktopWallpaperPath = null; // Force re-read
                                string desktopWp = GetDesktopWallpaperPath();
                                if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                                {
                                    Classes.SettingsManager.Current.ClipboardWallpaperPath = desktopWp;
                                    ApplyWallpaper();
                                }
                            }
                        }
                    }
                    else if (startupMode == "theme")
                    {
                        // Custom theme mode — manual wallpaper takes priority
                        string manualWpTheme = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                        if (!string.IsNullOrEmpty(manualWpTheme) && System.IO.File.Exists(manualWpTheme))
                        {
                            ApplyNonMicaBackground();
                            Classes.SettingsManager.Current.ClipboardWallpaperPath = manualWpTheme;
                            ApplyWallpaper();
                        }
                        else
                        {
                            string? startupWp = Classes.ThemeManager.Instance.GetWallpaperPath();
                            if (!string.IsNullOrEmpty(startupWp) && System.IO.File.Exists(startupWp))
                            {
                                ApplyNonMicaBackground();
                                Classes.SettingsManager.Current.ClipboardWallpaperPath = startupWp;
                                ApplyWallpaper();
                            }
                            else
                            {
                                Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                RestoreMicaBlur(); // No custom wallpaper: keep system Acrylic blur active!
                            }
                        }
                    }
                    else
                    {
                        // "mica" mode — ensure clean slate: no wallpaper, just system blur
                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                        RestoreMicaBlur();
                    }

                    // Re-apply DWM border color override after startup theme setup
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

                    // ═══ START MASCOT IDLE ANIMATION ═══
                    // Must happen AFTER _mascotAnimationRequestedHandler is wired (line above).
                    // AnimationTriggerService.Initialize() fires StartIdleAnimation() too early
                    // (before the handler exists), so the event is lost. This is the real startup trigger.
                    if (startupMode == "theme" && Classes.ThemeManager.Instance.ActiveTheme != null && Classes.SettingsManager.Current.ThemeAnimationsEnabled)
                    {
                        Classes.AnimationTriggerService.Instance.StartIdleAnimation();
                    }

                    Classes.Logger.LogAction("THEME", $"Mascot overlays wired. Startup mode: {startupMode}");

                    // ═══ START WALLPAPER FILE WATCHER ═══
                    // Monitors %APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper for changes.
                    // Catches Spotlight, slideshow, and Bing wallpaper changes that WM_SETTINGCHANGE misses.
                    StartWallpaperFileWatcher();

                    // ═══ STARTUP GUARD: Mark app as ready for hotkey summons ═══
                    // Without this, spamming Alt+C before theme init completes
                    // shows the clipboard with wrong/default colors (bluish tint on buttons).
                    _isStartupReady = true;
                    Classes.Logger.LogAction("STARTUP", "Theme init complete — hotkey summons now allowed");
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("THEME", $"Theme init failed (non-fatal): {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Pre-cache/hardcode actual height on startup so spawning is instant and doesn't jump
            Dispatcher.InvokeAsync(() =>
            {
                if (this.ActualHeight > 0)
                {
                    _lastActualHeight = this.ActualHeight;
                    Classes.Logger.LogAction("TELEMETRY", $"Startup height hardcoded to cache: {_lastActualHeight}");
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            // ONNX model pre-loading removed. Models will be lazy-loaded on-demand and auto-unloaded.

            // Trim memory footprint immediately after the app is fully loaded and idle on startup
            Dispatcher.InvokeAsync(() =>
            {
                OptimizeMemoryUsage();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // ═══ STARTUP VERSION CHECK — DISABLED ═══
            // Update notification banner removed from clipboard per user request.
            // The UpdateNotificationBanner element exists in XAML but stays Collapsed.
            // No version check is performed; no banner is ever shown.
            // Classes.UpdateManager.GlobalUpdateStatusChanged += OnGlobalUpdateStatusChanged;
            // Classes.UpdateManager.CheckForNewVersionNotificationAsync() — skipped.
        }

        // ═══ Theme/Wallpaper/Backdrop methods moved to MainWindow.Theme.cs ═══

        // ═══ UPDATE NOTIFICATION BANNER HANDLERS ═══
        private void OnGlobalUpdateStatusChanged(bool updateAvailable)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (updateAvailable && !_updateBannerDismissed)
                {
                    string ver = Classes.UpdateManager.GlobalLatestVersion;
                    UpdateBannerText.Text = $"New version v{ver} available — tap to update";
                    UpdateNotificationBanner.Visibility = Visibility.Visible;
                }
            });
        }
        private bool _updateBannerDismissed = false;

        private void UpdateBanner_Click(object sender, MouseButtonEventArgs e)
        {
            // Open the Microsoft Store page for FlyShelf (Store-compliant — just redirects to Store)
            try
            {
                if (Classes.StartupHelper.IsPackaged())
                {
                    // MSIX/Store install — open the Store app directly
                    _ = System.Threading.Tasks.Task.Run(() => { try {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ms-windows-store://pdp/?ProductId=9PM37CMM3T72",
                        UseShellExecute = true
                    });
                    } catch { } });
                }
                else
                {
                    // Non-packaged (sideload) — open the Hub with update section
                    OpenApp_Click_Internal();
                    if (_hubWindowInstance != null)
                    {
                        _hubWindowInstance.NavigateToTab("Settings");
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("UPDATE_BANNER", $"Failed to open store/hub: {ex.Message}");
            }
        }

        private void UpdateBannerDismiss_Click(object sender, RoutedEventArgs e)
        {
            _updateBannerDismissed = true;
            UpdateNotificationBanner.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Intercepts close requests (e.g., user clicking "Close window" on the taskbar thumbnail
        /// when Notes/Todo is showing as an app). Instead of destroying the window, cancel the
        /// close, dismiss the Notes/Todo panel, and return to normal overlay mode.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isNotesActive || _isTodoActive)
            {
                // Cancel the close — don't destroy the window, just dismiss
                e.Cancel = true;

                // Dismiss via the same path as Alt+C (preserves panel state for resummon)
                AnimateAndHide();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            try
            {
                if (_mascotCompanion != null)
                {
                    _mascotCompanion.Close();
                    _mascotCompanion = null;
                }
            }
            catch { } // Best-effort: failure is acceptable
            try { _taskbarWidget?.Close(); } catch { } // Best-effort: failure is acceptable
            try
            {
                if (_foregroundHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWinEvent(_foregroundHook);
                    _foregroundHook = IntPtr.Zero;
                }

                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    RemoveClipboardFormatListener(handle);
                    UnregisterHotKey(handle, HOTKEY_ID);
                    for (int i = 1; i <= 9; i++)
                        UnregisterHotKey(handle, HOTKEY_QUICKPASTE_BASE + i);
                    UnregisterHotKey(handle, HOTKEY_QUICKPASTE_BASE + 10); // Alt+0
                    HwndSource.FromHwnd(handle)?.RemoveHook(HwndHook);
                }

                // Clean up static event subscriptions to prevent memory leaks
                if (_mascotAnimationRequestedHandler != null)
                    Classes.AnimationTriggerService.Instance.AnimationRequested -= _mascotAnimationRequestedHandler;
                if (_themeChangedHandler != null)
                    Classes.ThemeManager.Instance.ActiveThemeChanged -= _themeChangedHandler;
                if (_settingsChangedHandler != null)
                    Classes.SettingsManager.Current.PropertyChanged -= _settingsChangedHandler;

                // Detach smooth scroll window hooks
                Classes.SmoothScroll.DetachFromWindow(this);

                // Unsubscribe static event handlers to prevent memory leaks
                if (_updateStatusChangedHandler != null)
                    Classes.UpdateManager.GlobalUpdateStatusChanged -= _updateStatusChangedHandler;
                Classes.UpdateManager.GlobalUpdateStatusChanged -= OnGlobalUpdateStatusChanged;
                if (_incognitoStateChangedHandler != null)
                    Classes.IncognitoManager.IncognitoStateChanged -= _incognitoStateChangedHandler;
                if (_coastPrefetchHandler != null)
                    Classes.SmoothScroll.CoastPrefetchNeeded -= _coastPrefetchHandler;

                if (_sizeChangedHandler != null)
                    this.SizeChanged -= _sizeChangedHandler;
                if (_activatedHandler != null)
                    this.Activated -= _activatedHandler;
                if (_viewModelPropertyChangedHandler != null)
                    _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;
                if (_droppedItemsCollectionChangedHandler != null)
                    _viewModel.DroppedItems.CollectionChanged -= _droppedItemsCollectionChangedHandler;
                if (_stateChangedHandler != null)
                    this.StateChanged -= _stateChangedHandler;
                if (_verticalScrollBar != null && _verticalScrollBarPreviewMouseLeftButtonDownHandler != null)
                {
                    _verticalScrollBar.PreviewMouseLeftButtonDown -= _verticalScrollBarPreviewMouseLeftButtonDownHandler;
                    _verticalScrollBar = null;
                }

                // Detach ScrollChanged handler
                ShelfListView.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ShelfListView_ScrollChanged));

                // Safety net: release keyboard hook if app exits while clipboard is visible
                try { UninstallKeyboardHook(); } catch { }

                // Stop all timers to prevent post-close ticks
                _evictionBackgroundTimer?.Stop();
                _evictionBackgroundTimer = null;
                _searchDebounceTimer?.Stop();
                _clipboardDebounceTimer?.Stop();
                _scrollDecayTimer?.Stop();
                _scrollHighQualityTimer?.Stop();
                _mascotDelayTimer?.Stop();
                if (_showAnimRenderHandler != null) { CompositionTarget.Rendering -= _showAnimRenderHandler; _showAnimRenderHandler = null; }
                _dragActiveDismissTimer?.Stop();
                _hoverPreviewTimer?.Stop();
                _altScrollTimer?.Stop();
                _altThumbnailTimer?.Stop();
                _incognitoRefreshTimer?.Stop();
                _transferRefreshTimer?.Stop();
                _notesSyncStatusTimer?.Stop();
                _notesSidebarAutoCollapseTimer?.Stop();
                _panelAutoRevertTimer?.Stop();
                _wallpaperDebounceTimer?.Stop();

                // Dispose wallpaper file watchers
                try { StopWallpaperFileWatcher(); } catch { } // Best-effort: failure is acceptable
            }
            catch { /* Window already destroyed — nothing to clean up */ }
            base.OnClosed(e);
        }

        public bool ReRegisterSummonHotkey()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return false;
            UnregisterHotKey(handle, HOTKEY_ID);
            var s = Classes.SettingsManager.Current;
            bool ok = RegisterHotKey(handle, HOTKEY_ID, s.HotkeyModifier | MOD_NOREPEAT, s.HotkeyKey);
            if (!ok)
                Windows.ToastWindow.ShowToast($"Could not register {s.HotkeyDisplayString} — another app may be using it.");
            Classes.Logger.LogAction("HOTKEY", $"ReRegister {s.HotkeyDisplayString}: {ok}");
            return ok;
        }

        // ═══ HwndHook (Hotkeys, Clipboard, Settings) moved to MainWindow.WndProc.cs ═══

        private bool _isCurrentlySummoned = false;
        private bool _isProgrammaticMinimize = false;
        public bool IsSummoned => _isCurrentlySummoned;

        public void HideWindowInternal()
        {
            int capturedToken = _spawnToken;

            // Guard: If a new show happened between AnimateAndHide and this deferred call,
            // abort — we'd clobber the new show by moving the window offscreen.
            if (_isCurrentlySummoned)
            {
                Classes.Logger.LogAction("VD_HIDE", "HideWindowInternal ABORTED — window was re-summoned");
                return;
            }

            _isCurrentlySummoned = false;
            _isEdgeLocked = false;
            UninstallKeyboardHook(); // Safety net: ensure hook is released on deferred hide

            if (this.WindowState == WindowState.Minimized)
            {
                _isProgrammaticMinimize = true;
                this.WindowState = WindowState.Normal;
                _isProgrammaticMinimize = false;
            }

            // JITTER FIX: Use native ShowWindow(SW_HIDE) instead of moving to -20000.
            // Moving the window far offscreen forces DWM to invalidate its redirection surface.
            // When the window is re-shown, DWM must reallocate and re-rasterize the entire
            // surface (~45ms stall), causing visible jitter on the first animation frames.
            // SW_HIDE keeps the DWM surface warm at the last visible position.
            Dispatcher.InvokeAsync(() => 
            {
                if (_spawnToken != capturedToken) return;

                // Safety: clear any stale DWM cloak from aborted previous animation
                var safetyHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (safetyHwnd != IntPtr.Zero)
                {
                    int uncloakVal = 0;
                    Classes.NativeMethods.DwmSetWindowAttribute(safetyHwnd, Classes.NativeMethods.DWMWA_CLOAK, ref uncloakVal, sizeof(int));
                }

                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                        Classes.NativeMethods.ShowWindow(hwnd, 0 /*SW_HIDE*/);
                }
                catch { }

                // Actively optimize and release memory whenever the window is hidden/unsummoned
                if (!_hasOptimizedThisHide) OptimizeMemoryUsage();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Ensures the window is in clipboard mode by closing any active Notes or Todo panel.
        /// Called by shake-to-open to guarantee only the clipboard overlay appears.
        /// </summary>
        public void EnsureClipboardMode()
        {
            if (_isNotesActive)
                CloseNotesPanel(immediate: true);
            if (_isTodoActive)
                CloseTodoPanel(immediate: true);
            if (_isResearchActive)
                CloseResearchPanel(immediate: true);
        }

        /// <summary>
        /// Verifies the app is still pinned to all virtual desktops and re-pins if not.
        /// Call this before any spawn to guard against accidental unpinning from
        /// style changes, OS updates, or other edge cases.
        /// </summary>
        private void EnsureVirtualDesktopPinned()
        {
            try
            {
                string appId = "FlyShelf.Clipboard";
                var pinnedAppsType = Type.GetTypeFromCLSID(new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD"));
                if (pinnedAppsType == null) return;

                var pinnedApps = Activator.CreateInstance(pinnedAppsType) as Classes.NativeMethods.IVirtualDesktopPinnedApps;
                if (pinnedApps == null) return;

                int hr = pinnedApps.IsAppIdPinned(appId, out int isPinned);
                if (hr == 0 && isPinned == 0)
                {
                    pinnedApps.PinAppID(appId);
                    Classes.Logger.LogAction("DESKTOP", "Re-pinned FlyShelf AppID to all virtual desktops (was unpinned!)");
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("DESKTOP_ERR", $"EnsureVirtualDesktopPinned failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Query IsWindowOnCurrentVirtualDesktop on a background ThreadPool thread (MTA)
        /// with a strict timeout. Prevents Explorer.exe COM congestion
        /// from freezing or lagging the UI thread during virtual desktop switches.
        /// PERF: Uses a cached COM singleton — avoids leaking abandoned COM objects that
        /// congest Explorer's COM apartment and cause progressive desktop-switch lag.
        /// </summary>
        [ThreadStatic] private static FlyShelf.Classes.NativeMethods.IVirtualDesktopManager? _threadLocalVdm;

        public bool IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, int timeoutMs = 30)
        {
            try
            {
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // PERF: Reuse a thread-local COM instance instead of creating new ones.
                        // This prevents COM object leaks when tasks timeout and get abandoned.
                        _threadLocalVdm ??= (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
                        int hr = _threadLocalVdm.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrent);
                        if (hr != 0)
                        {
                            // COM call failed — recreate instance on next call
                            _threadLocalVdm = null;
                            return false;
                        }
                        return onCurrent != 0;
                    }
                    catch
                    {
                        _threadLocalVdm = null; // Reset on error so next call creates fresh instance
                        return false;
                    }
                });

                if (task.Wait(timeoutMs))
                {
                    return task.Result;
                }
                else
                {
                    Classes.Logger.LogAction("VD_TIMEOUT", $"IsWindowOnCurrentVirtualDesktop timed out (>{timeoutMs}ms) for HWND 0x{hwnd:X}");
                    return false; // Treat as on another desktop if it times out
                }
            }
            catch
            {
                return false;
            }
        }

        private void UpdateMascotCompanionState()
        {
            try
            {
                bool enabled = Classes.SettingsManager.Current.EnableDesktopMascot;
                if (enabled)
                {
                    if (_mascotCompanion == null)
                    {
                        _mascotCompanion = new Windows.MascotCompanionWindow();
                        _mascotCompanion.Closed += (s, ev) => _mascotCompanion = null;
                        _mascotCompanion.Show();
                    }
                }
                else
                {
                    if (_mascotCompanion != null)
                    {
                        _mascotCompanion.Close();
                        _mascotCompanion = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("COMPANION_STATE_ERR", ex.ToString());
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // HEADER BUTTON STYLE — Sync internal resources with active theme
        // ═══════════════════════════════════════════════════════════════
        /// <summary>
        /// WPF-UI's Button template reads ButtonBackground/ButtonBackgroundPointerOver etc. from
        /// Style.Resources for hover/pressed visual states. DynamicResource on Color inside
        /// Style.Resources is unreliable during early startup (causes accent-color flash).
        /// This method reads the now-loaded theme Color values and patches the style resources.
        /// </summary>
        private void SyncHeaderButtonStyleResources()
        {
            try
            {
                if (Resources["PremiumHeaderButtonStyle"] is not Style style) return;

                var colorMap = new (string resourceKey, string colorKey)[]
                {
                    ("ButtonBackground",              "ThemeHeaderBtnBgColor"),
                    ("ButtonBorderBrush",              "ThemeHeaderBtnBorderColor"),
                    ("ButtonBackgroundPointerOver",    "ThemeHeaderBtnBgHoverColor"),
                    ("ButtonBorderBrushPointerOver",   "ThemeHeaderBtnBorderHoverColor"),
                    ("ButtonForegroundPointerOver",    "ThemeHeaderBtnFgHoverColor"),
                    ("ButtonBackgroundPressed",        "ThemeHeaderBtnBgPressedColor"),
                    ("ButtonBorderBrushPressed",       "ThemeHeaderBtnBorderPressedColor"),
                    ("ButtonForegroundPressed",        "ThemeHeaderBtnFgPressedColor"),
                };

                foreach (var (resourceKey, colorKey) in colorMap)
                {
                    if (TryFindResource(colorKey) is Color color)
                    {
                        var brush = new SolidColorBrush(color);
                        brush.Freeze();
                        style.Resources[resourceKey] = brush;
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable
        }
    }
}
