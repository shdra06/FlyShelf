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
        private Windows.TaskbarWindow? _taskbarWidget;
        private System.Windows.Threading.DispatcherTimer? _clipboardDebounceTimer;
        private System.Windows.Threading.DispatcherTimer? _scrollDecayTimer;
        private System.Windows.Threading.DispatcherTimer? _scrollHighQualityTimer;
        private DateTime _lastScrollRenderTime = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer? _evictionBackgroundTimer;
        private DateTime _lastMergeToggleTime = DateTime.MinValue;
        private IntPtr _lastActiveExternalWindow = IntPtr.Zero;
        private DateTime _lastScrollTime = DateTime.MinValue;
        private double _scrollVelocity = 0;
        private Point _lastPhysicalMousePosition = new Point(-999, -999);
        private string _currentLoadedWallpaperPath = "";
        private static string? _cachedDesktopWallpaperPath = null;
        private ScrollViewer? _shelfScrollViewer;
        private double _lastActualHeight = 0;
        private EventHandler<Classes.AnimationRequestEventArgs>? _mascotAnimationRequestedHandler;
        private Action<Classes.ThemePackage?>? _themeChangedHandler;
        private System.ComponentModel.PropertyChangedEventHandler? _settingsChangedHandler;
        private bool _isSuppressingSizeSync = false;

        private ScrollViewer? GetShelfScrollViewer()
        {
            if (_shelfScrollViewer == null)
            {
                _shelfScrollViewer = FindVisualChild<ScrollViewer>(ShelfListView);
            }
            return _shelfScrollViewer;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int VK_CONTROL = 0x11;
        private const int VK_V = 0x56;
        private const int VK_MENU = 0x12; // Alt key

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        public const int DWMWA_COLOR_DARK_GRAY = 0x002D2D2D;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x02;

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);


        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const int HOTKEY_QUICKPASTE_BASE = 9001; // 9001-9009 for Alt+1 through Alt+9
        private const uint MOD_ALT = 0x0001;
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
                int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_LAYERED);

                // Force rounded corners on all devices (VMs, Win10-style DWM, etc.)
                int cornerPref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(helper.Handle, 33, ref cornerPref, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            }
        }

        public MainWindow()
        {
            var vm = new FlyShelfViewModel();
            this.DataContext = vm;
            _viewModel = vm;
            InitializeComponent();
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
                RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, 0x43); // Alt+C
                Classes.Logger.LogAction("HOTKEY", $"Alt+C registered");

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

            this.SizeChanged += (s, e) =>
            {
                if (_isEdgeLocked && this.ActualWidth > 0 && this.ActualHeight > 0)
                {
                    var workArea = SystemParameters.WorkArea;

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
                        
                        this.Top = newTop;
                    }
                }
            };

            // Restore keyboard focus to ListView or Notes textbox after window is moved/repositioned
            this.Activated += (s, e) =>
            {
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

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (_isSuppressingSizeSync) return;

                if (e.PropertyName == nameof(FlyShelfViewModel.CurrentFlyShelfMaxHeight))
                {
                    this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
                    if (_viewModel.CurrentMode != 0)
                    {
                        this.Height = _viewModel.CurrentFlyShelfMaxHeight;
                    }
                    this.UpdateLayout();
                    
                    if (_isEdgeLocked && this.ActualHeight > 0)
                    {
                        double newTop = _lockedBottomEdge - this.ActualHeight - 20;
                        var workArea = SystemParameters.WorkArea;
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
                    UpdateToolbarButtonsVisibility();
                }
            };

            // Live-refresh wallpaper when user changes it in settings
            _settingsChangedHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(Classes.AdvanceSettings.ClipboardWallpaperPath))
                    Dispatcher.InvokeAsync(() => ApplyWallpaper());
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.ColorThemeName))
                {
                    string newTheme = Classes.SettingsManager.Current.ColorThemeName;
                    if (string.IsNullOrEmpty(newTheme) || newTheme.Equals("Default", System.StringComparison.OrdinalIgnoreCase))
                        return; // Default is handled by RemoveColorTheme, not ApplyColorTheme
                    Dispatcher.InvokeAsync(() => Classes.ThemeManager.Instance.ApplyColorTheme(newTheme));
                }
                else if (e.PropertyName == nameof(Classes.AdvanceSettings.EnableBlurBehind) ||
                         e.PropertyName == nameof(Classes.AdvanceSettings.ThemeDisplayMode))
                {
                    Dispatcher.InvokeAsync(() => _themeChangedHandler?.Invoke(Classes.ThemeManager.Instance.ActiveTheme));
                }
            };
            Classes.SettingsManager.Current.PropertyChanged += _settingsChangedHandler;

            // Auto-dismiss merge state when new items arrive on the shelf + Reapply active category/search filters to keep UI state robust
            _viewModel.DroppedItems.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add ||
                    e.Action == NotifyCollectionChangedAction.Reset)
                {
                    // CRITICAL: On Reset events (from AddRange/InsertRange), WPF's ListCollectionView
                    // internally rebuilds and DROPS the Filter delegate. Reapply SYNCHRONOUSLY here
                    // to prevent even a single frame of unfiltered items appearing.
                    if (e.Action == NotifyCollectionChangedAction.Reset &&
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

                        // Safety net: reapply filters deferred as well
                        ReapplyActiveFilters();
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                else if (e.Action == NotifyCollectionChangedAction.Remove)
                {
                    // For removals, reapply filters synchronously to prevent filter loss
                    if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox?.Text)))
                    {
                        ReapplyActiveFilters();
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (MergePdfToolbarBtn.Visibility == Visibility.Visible)
                        {
                            DismissMergeState();
                        }

                        // Reapply active category/search filters to keep the filtered view persistent after deleting an item
                        ReapplyActiveFilters();
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            };

            // Calculate initial toolbar buttons visibility based on current mode
            UpdateToolbarButtonsVisibility();

            // ═══ Update Available Badge ═══
            // Subscribe to the static cross-window event from UpdateManager
            Classes.UpdateManager.GlobalUpdateStatusChanged += (hasUpdate) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (UpdateBadge != null)
                    {
                        UpdateBadge.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
                        if (hasUpdate && UpdateBadgeText != null)
                        {
                            UpdateBadgeText.Text = $"v{Classes.UpdateManager.GlobalLatestVersion}";
                        }
                    }
                });
            };

            // Check if an update was already detected before this window loaded
            if (Classes.UpdateManager.GlobalUpdateAvailable && UpdateBadge != null)
            {
                UpdateBadge.Visibility = Visibility.Visible;
                if (UpdateBadgeText != null)
                    UpdateBadgeText.Text = $"v{Classes.UpdateManager.GlobalLatestVersion}";
            }
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

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

        private IntPtr _foregroundHook = IntPtr.Zero;
        private WinEventDelegate _foregroundDelegate = null!;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        private bool _isLoadedInitialized = false;

        private void MicaWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoadedInitialized) return;
            _isLoadedInitialized = true;

            // Setup global foreground window change listener to dismiss when clicking elsewhere (handles non-activated summons)
            try
            {
                _foregroundDelegate = new WinEventDelegate(ForegroundChangedCallback);
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
                int borderColor = DWMWA_COLOR_DARK_GRAY;
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }

            // Hook state changes to prevent DWM border leakage on minimize/maximize/restore/etc.
            this.StateChanged += (s, ev) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int cn = DWMWA_COLOR_DARK_GRAY;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { }

                // Notes/Todo Mode: Handle USER-INITIATED minimize (taskbar click, alt+tab dismiss)
                // Close the Notes/Todo panel and open the normal clipboard — same as Alt+C.
                // Skip if the minimize was triggered programmatically by HideWindowInternal (unsummon flow).
                if (this.WindowState == WindowState.Minimized && (_isNotesActive || _isTodoActive) && !_isProgrammaticMinimize)
                {
                    // Defer to avoid issues during the state change event
                    Dispatcher.InvokeAsync(() =>
                    {
                        // Close whichever panel is active
                        if (_isNotesActive)
                            CloseNotesPanel(immediate: true);
                        if (_isTodoActive)
                            CloseTodoPanel(immediate: true);

                        // Restore normal window state, reset, then open clipboard
                        this.WindowState = WindowState.Normal;
                        _isCurrentlySummoned = false;
                        this.Opacity = 0;
                        this.BeginAnimation(OpacityProperty, null);
                        this.Left = -20000;
                        this.Top = -20000;

                        // Open the clipboard — same as pressing Alt+C
                        ToggleMainClipboard();
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }

                // Notes/Todo Mode Alt+Tab/Restore un-minimize handling
                if (this.WindowState == WindowState.Normal && (_isNotesActive || _isTodoActive))
                {
                    this.Opacity = 1;
                    _isCurrentlySummoned = true;
                    this.Activate();
                    this.Topmost = true;
                    
                    if (_isNotesActive)
                        FocusNotesActiveTextBox();
                    else if (_isTodoActive)
                        FocusTodoActiveTextBox();
                }
            };

            // Launch the taskbar-embedded widget
            try
            {
                _taskbarWidget = new Windows.TaskbarWindow();
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET_FAIL", $"Failed to create taskbar widget: {ex.Message}");
            }

            // Attach window-level smooth scrolling with specialized snappy ClipboardProfile
            Classes.SmoothScroll.AttachToWindow(this, Classes.SmoothScroll.ClipboardProfile);

            // Track scrolling to optimize hover button summoning (prevent during scroll)
            ShelfListView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ShelfListView_ScrollChanged));
            ShelfListView.MouseLeave += ShelfListView_MouseLeave;

            // Apply wallpaper is now handled by the deferred theme block at ApplicationIdle
            // (no more redundant early load that gets overwritten by theme init)

            // ═══ BACKDROP STRATEGY: Set once, never toggle ═══
            // SystemBackdropType is set to Mica via XAML attribute.
            // On Win10 (Build < 22000), Mica doesn't exist — fall back to solid background once.
            // This is the ONLY place SystemBackdropType is ever modified at runtime.
            if (Environment.OSVersion.Version.Build < 22000)
            {
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                ApplyPopupBackground();
            }

            // Pre-initialize the heavy Hub Window in the background when the system is truly idle
            // Priority: SystemIdle (lowest) — runs AFTER theme init to avoid competing for UI thread
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_hubWindowInstance == null)
                    {
                        _hubWindowInstance = new Windows.HubWindow(_viewModel);
                        _hubWindowInstance.Closed += (s, args) => _hubWindowInstance = null;
                    }
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("PRE_INIT_HUB_FAIL", ex.ToString());
                }
            }, System.Windows.Threading.DispatcherPriority.SystemIdle);

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

                    verticalScrollBar.PreviewMouseLeftButtonDown += (s, args) =>
                    {
                        // Let thumb dragging work normally — only intercept track area clicks
                        var thumb = FindVisualChild<System.Windows.Controls.Primitives.Thumb>(verticalScrollBar);
                        if (thumb != null && thumb.IsMouseOver) return;

                        var track = FindVisualChild<System.Windows.Controls.Primitives.Track>(verticalScrollBar);
                        if (track == null) return;

                        // Use WPF Track's built-in geometry math to convert click position → scroll value
                        Point clickPoint = args.GetPosition(track);
                        double newValue = track.ValueFromPoint(clickPoint);

                        sv.ScrollToVerticalOffset(newValue);
                        Classes.SmoothScroll.ResetScrollState(sv);
                        args.Handled = true;
                    };
                }
                catch { }
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
                                catch { }
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
                                    Classes.Logger.LogAction("THEME", "Mode: Glass (Acrylic Blur) — glassmorphism UI applied");
                                }
                                else if (displayMode == "desktop")
                                {
                                    // ═══ FLYSHELF (DESKTOP WALLPAPER) MODE ═══
                                    // Manual wallpaper takes top priority until explicitly removed
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
                                            Classes.Logger.LogAction("THEME", "Mode: FlyShelf — no desktop wallpaper found, solid dark bg");
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
                                // Re-apply DWM border color override after backdrop/theme changes to prevent system accent color leakage
                                try
                                {
                                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                                    if (hwnd != IntPtr.Zero)
                                    {
                                        int cn = DWMWA_COLOR_DARK_GRAY;
                                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                                    }
                                }
                                catch { }
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
                    }
                    else if (startupMode == "desktop")
                    {
                        // Desktop wallpaper mode — manual wallpaper takes priority
                        ApplyNonMicaBackground();
                        string manualWp = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                        if (!string.IsNullOrEmpty(manualWp) && System.IO.File.Exists(manualWp))
                        {
                            Classes.SettingsManager.Current.ClipboardWallpaperPath = manualWp;
                            ApplyWallpaper();
                        }
                        else
                        {
                            _cachedDesktopWallpaperPath = null; // Force re-read
                            string desktopWp = GetDesktopWallpaperPath();
                            if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                            {
                                Classes.SettingsManager.Current.ClipboardWallpaperPath = desktopWp;
                                ApplyWallpaper();
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
                            int cn = DWMWA_COLOR_DARK_GRAY;
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

            // Pre-load ONNX models in the background after the app is fully idle
            Dispatcher.InvokeAsync(() =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        var tempDetector = new Classes.OnnxTableDetector();
                        var tempRecognizer = new Classes.OnnxTextRecognizer();
                        tempDetector.Initialize();
                        tempRecognizer.Initialize();
                        Classes.Logger.LogAction("ONNX_INIT", "ONNX Models pre-loaded successfully in background thread.");
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("ONNX_INIT_FAIL", $"Lazy loading failed: {ex.Message}");
                    }
                });
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // ═══ Theme/Wallpaper/Backdrop methods moved to MainWindow.Theme.cs ═══

        /// <summary>
        /// Intercepts close requests (e.g., user clicking "Close window" on the taskbar thumbnail
        /// when Notes/Todo is showing as an app). Instead of destroying the window, cancel the
        /// close, dismiss the Notes/Todo panel, and return to normal overlay mode.
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isNotesActive || _isTodoActive)
            {
                // Cancel the close — don't destroy the window
                e.Cancel = true;

                // Close whichever panel is active
                if (_isNotesActive)
                    CloseNotesPanel(immediate: true);
                if (_isTodoActive)
                    CloseTodoPanel(immediate: true);

                // Reset to normal overlay state
                _isCurrentlySummoned = false;
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                this.WindowState = WindowState.Normal;
                this.Left = -20000;
                this.Top = -20000;

                // Open the clipboard — same as pressing Alt+C
                ToggleMainClipboard();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            try
            {
                if (_foregroundHook != IntPtr.Zero)
                {
                    UnhookWinEvent(_foregroundHook);
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

                _evictionBackgroundTimer?.Stop();
                _evictionBackgroundTimer = null;
            }
            catch { /* Window already destroyed — nothing to clean up */ }
            base.OnClosed(e);
        }

        // ═══ HwndHook (Hotkeys, Clipboard, Settings) moved to MainWindow.WndProc.cs ═══

        private bool _isCurrentlySummoned = false;
        private bool _isProgrammaticMinimize = false;
        public bool IsSummoned => _isCurrentlySummoned;

        public void HideWindowInternal()
        {
            _isCurrentlySummoned = false;
            _isEdgeLocked = false;

            if (_isNotesActive || _isTodoActive)
            {
                // Instead of moving offscreen, minimize the window so it remains in Alt+Tab list as requested.
                // Set guard flag so StateChanged handler knows NOT to close the panel.
                _isProgrammaticMinimize = true;
                this.Opacity = 1;
                this.WindowState = WindowState.Minimized;
                _isProgrammaticMinimize = false;
            }
            else
            {
                this.Left = -20000;
                this.Top = -20000;
            }

            // Actively optimize and release memory whenever the window is hidden/unsummoned
            OptimizeMemoryUsage();
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
        }
    }
}
