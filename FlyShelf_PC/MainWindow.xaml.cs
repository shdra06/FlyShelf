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
        private int _spawnToken = 0;
        private bool _isDragHovering = false;

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

        // Hover preview popup state (DISABLED â€” replaced by expand/collapse chevron button)
#pragma warning disable CS0649
        private System.Windows.Threading.DispatcherTimer? _hoverPreviewTimer;
        private ClipboardItem? _hoveredItem;
        private Windows.PreviewPopup? _activePreviewPopup;
#pragma warning restore CS0649

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, GetWindowLong(helper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        }

        public MainWindow()
        {
            var vm = new FlyShelfViewModel();
            this.DataContext = vm;
            _viewModel = vm;
            InitializeComponent();

            // Register global hotkeys EAGERLY in constructor â€” do NOT wait for Loaded event.
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
                if (_isEdgeLocked && e.HeightChanged && this.ActualHeight > 0)
                {
                    this.Top = _lockedBottomEdge - this.ActualHeight;
                    
                    var workArea = SystemParameters.WorkArea;
                    if (this.Top < workArea.Top)
                    {
                        this.Top = workArea.Top + 16;
                    }
                }
            };

            // Restore keyboard focus to ListView after window is moved/repositioned
            this.Activated += (s, e) =>
            {
                // Skip re-focus if a topmost child window (QuickLook) is active â€” prevents infinite activation loop
                if (System.Windows.Application.Current.Windows.OfType<Window>().Any(w => w.Topmost && w != this && w.IsActive)) return;
                // Debounce: only re-focus if the ListView isn't already keyboard-focused
                if (!ShelfListView.IsKeyboardFocusWithin)
                {
                    FocusFirstItemContainer();
                }
            };

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FlyShelfViewModel.CurrentFlyShelfMaxHeight))
                {
                    this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
                    this.UpdateLayout();
                    
                    if (_isEdgeLocked && this.ActualHeight > 0)
                    {
                        this.Top = _lockedBottomEdge - this.ActualHeight;
                        var workArea = SystemParameters.WorkArea;
                        if (this.Top < workArea.Top) this.Top = workArea.Top + 16;
                    }
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
            };
            Classes.SettingsManager.Current.PropertyChanged += _settingsChangedHandler;

            // Auto-dismiss merge state when new items arrive on the shelf
            _viewModel.DroppedItems.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add ||
                    e.Action == NotifyCollectionChangedAction.Reset ||
                    e.Action == NotifyCollectionChangedAction.Remove)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (MergePdfToolbarBtn.Visibility == Visibility.Visible)
                        {
                            DismissMergeState();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            };

            // Calculate initial toolbar buttons visibility based on current mode
            UpdateToolbarButtonsVisibility();
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

            // DWM border styling â€” must happen after window is shown
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
                        {
                            int colorNone = DWMWA_COLOR_DARK_GRAY;
                            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
                        }
                    });
                });
            }

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

            // Blur-off or system transparency disabled: solid dark gradient fallback
            if (!Classes.SettingsManager.Current.EnableBlurBehind || !Classes.NativeMethods.ShouldUseBlur())
            {
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                System.Threading.Tasks.Task.Delay(150).ContinueWith(_ =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        ApplyPopupBackground();
                    });
                });
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

            // Wire up paginated scroll loading on-demand
            try
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500); // Wait for visual templates to generate completely
                    var sv = GetShelfScrollViewer();
                    if (sv != null)
                    {
                        sv.ScrollChanged += async (s, args) =>
                        {
                            // If we scrolled near the bottom (within 50px of ScrollableHeight), load the next page of history items
                            if (sv.ScrollableHeight > 0 && sv.VerticalOffset >= sv.ScrollableHeight - 50)
                            {
                                await _viewModel.LoadNextPageAsync();
                            }
                        };
                        Classes.Logger.LogAction("SCROLL_INIT", "Successfully hooked ShelfListView ScrollViewer for pagination.");
                    }
                    else
                    {
                        Classes.Logger.LogAction("SCROLL_INIT_WARN", "Could not find ScrollViewer child of ShelfListView.");
                    }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("SCROLL_INIT_FAIL", $"Failed to hook scroll events: {ex.Message}");
            }
            // ═══ MASCOT THEME ENGINE INIT ═══
            // Deferred initialization so it doesn't block main UI render
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Classes.ThemeManager.Instance.Initialize();
                    Classes.AnimationTriggerService.Instance.Initialize();

                    // ═══ Unified Header Mascot Event Routing ═══
                    // Route all mascot triggers directly to the header mascot control MascotIdle
                    _mascotAnimationRequestedHandler = (s, e) =>
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
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
                            try
                            {
                                // STEP 1: Always stop/clear the old mascot + wallpaper first
                                MascotIdle.StopAnimation();
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
                                    Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                    RestoreMicaBlur();
                                    Classes.Logger.LogAction("THEME", "Mode: Mica Blur — pure system backdrop");
                                }
                                else if (displayMode == "glass")
                                {
                                    // ═══ GLASS MODE ═══
                                    // Glassmorphism UI — frosted buttons, translucent cards, NO system blur
                                    Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                    ApplyNonMicaBackground();
                                    Classes.ThemeManager.Instance.ApplyGlassTheme();
                                    Classes.Logger.LogAction("THEME", "Mode: Glass — glassmorphism UI applied (no blur)");
                                }
                                else if (displayMode == "desktop")
                                {
                                    // ═══ FLYSHELF (DESKTOP WALLPAPER) MODE ═══
                                    // Clipboard gets the user's Windows desktop wallpaper, NO system blur
                                    ApplyNonMicaBackground();
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
                                else // displayMode == "theme"
                                {
                                    // ═══ CUSTOM THEME MODE ═══
                                    // Clipboard gets theme wallpaper + mascot animation, NO system blur
                                    ApplyNonMicaBackground();
                                    if (theme == null)
                                    {
                                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                        Classes.Logger.LogAction("THEME", "Mode: Theme — but no theme active, solid dark bg");
                                        return;
                                    }

                                    string? themeWp = Classes.ThemeManager.Instance.GetWallpaperPath();
                                    if (!string.IsNullOrEmpty(themeWp) && System.IO.File.Exists(themeWp))
                                    {
                                        Classes.SettingsManager.Current.ClipboardWallpaperPath = themeWp;
                                        ApplyWallpaper();
                                        Classes.Logger.LogAction("THEME", $"Mode: Theme '{theme.Name}' — wallpaper: {themeWp}");
                                    }
                                    else
                                    {
                                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                                        Classes.Logger.LogAction("THEME", $"Mode: Theme '{theme.Name}' — no wallpaper, solid dark bg");
                                    }

                                    // Start mascot idle animation
                                    Classes.AnimationTriggerService.Instance.StartIdleAnimation();
                                }
                            }
                            catch (Exception ex) { Classes.Logger.LogAction("THEME", $"Theme switch error: {ex.Message}"); }
                        });
                    };
                    Classes.ThemeManager.Instance.ActiveThemeChanged += _themeChangedHandler;

                    // ═══ Startup: Apply correct mode ═══
                    string startupMode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica";
                    if (startupMode == "glass")
                    {
                        // Glass mode — no system blur, glassmorphism UI
                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                        ApplyNonMicaBackground();
                        Classes.ThemeManager.Instance.ApplyGlassTheme();
                    }
                    else if (startupMode == "desktop")
                    {
                        // Desktop wallpaper mode — no system blur
                        ApplyNonMicaBackground();
                        _cachedDesktopWallpaperPath = null; // Force re-read
                        string desktopWp = GetDesktopWallpaperPath();
                        if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                        {
                            Classes.SettingsManager.Current.ClipboardWallpaperPath = desktopWp;
                            ApplyWallpaper();
                        }
                    }
                    else if (startupMode == "theme")
                    {
                        // Custom theme mode — no system blur
                        ApplyNonMicaBackground();
                        string? startupWp = Classes.ThemeManager.Instance.GetWallpaperPath();
                        if (!string.IsNullOrEmpty(startupWp) && System.IO.File.Exists(startupWp))
                        {
                            Classes.SettingsManager.Current.ClipboardWallpaperPath = startupWp;
                            ApplyWallpaper();
                        }
                    }
                    else
                    {
                        // "mica" mode — ensure clean slate: no wallpaper, just system blur
                        Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                        RestoreMicaBlur();
                    }

                    // ═══ START MASCOT IDLE ANIMATION ═══
                    // Must happen AFTER _mascotAnimationRequestedHandler is wired (line above).
                    // AnimationTriggerService.Initialize() fires StartIdleAnimation() too early
                    // (before the handler exists), so the event is lost. This is the real startup trigger.
                    if (Classes.ThemeManager.Instance.ActiveTheme != null && Classes.SettingsManager.Current.ThemeAnimationsEnabled)
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
        }

        /// <summary>
        /// Restores the system Mica/Acrylic blur backdrop on the clipboard window.
        /// Sets Background = Transparent so the system backdrop effect is visible.
        /// Falls back to solid gradient when system doesn't support blur.
        /// </summary>
        /// <summary>
        /// Clears ALL wallpaper/theme visual layers without touching the window backdrop.
        /// Shared cleanup used by both RestoreMicaBlur and ApplyNonMicaBackground.
        /// </summary>
        private void ClearWallpaperLayers()
        {
            try
            {
                // Clear animated GIF source (XamlAnimatedGif holds onto frames)
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Source = null;
                WallpaperBg.Visibility = Visibility.Collapsed;

                // Clear the radial gradient theme overlay
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;

                // Clear the frosted glass header + its image source
                WallpaperFrostImg.Source = null;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                WallpaperFrostTint.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x25, 0, 0, 0)); // Reset to default neutral tint

                // Stop mascot
                MascotIdle.StopAnimation();

                // Reset tracking
                _currentLoadedWallpaperPath = "";
            }
            catch { }
        }

        /// <summary>
        /// Restores Mica/Acrylic blur — ONLY for "mica" display mode.
        /// Clears all wallpaper layers, then enables the system acrylic backdrop.
        /// </summary>
        private void RestoreMicaBlur()
        {
            ClearWallpaperLayers();

            // ═══ RESTORE MICA/ACRYLIC BLUR ═══
            if (Classes.SettingsManager.Current.EnableBlurBehind && Classes.NativeMethods.ShouldUseBlur())
            {
                this.Background = System.Windows.Media.Brushes.Transparent;
                if (RootContent != null)
                    RootContent.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(1, 0, 0, 0)); // Near-transparent for hit testing
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.Acrylic;
            }
            else
            {
                ApplyPopupBackground(); // Solid gradient fallback when blur is disabled/unsupported
            }

            // Reset selection accent to default (violet)
            ResetSelectionAccent();
        }

        /// <summary>
        /// Applies a solid dark background with NO system blur — for Glass, Desktop, and Custom themes.
        /// Clears all wallpaper layers, disables SystemBackdropType, and sets a neutral dark bg.
        /// </summary>
        private void ApplyNonMicaBackground()
        {
            ClearWallpaperLayers();

            // Disable system blur/acrylic — only Mica mode gets it
            this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
            ApplyPopupBackground();

            // Reset selection accent to default (violet)
            ResetSelectionAccent();
        }

        /// <summary>
        /// Applies a neutral dark grey background for the popup clipboard
        /// (solid fallback when system blur is disabled or unsupported).
        /// </summary>
        private void ApplyPopupBackground()
        {
            // Clean neutral dark grey — no blue/indigo tint
            var grey = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(36, 36, 36)); // #242424 — Windows 11 dark surface
            grey.Freeze();
            this.Background = grey;
            if (RootContent != null) RootContent.Background = grey;
        }

        /// <summary>
        /// Injects the wallpaper's dominant color as selection accent brushes.
        /// Called from ApplyWallpaper() after ExtractDominantColor completes.
        /// </summary>
        private void ApplyDominantColorAccent(System.Windows.Media.Color dominant)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;

                var selBorder = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x60, dominant.R, dominant.G, dominant.B));
                selBorder.Freeze();
                var selBg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x10, dominant.R, dominant.G, dominant.B));
                selBg.Freeze();
                var focusBorder = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x80, dominant.R, dominant.G, dominant.B));
                focusBorder.Freeze();

                app.Resources["ShelfCardSelectionBorder"] = selBorder;
                app.Resources["ShelfCardSelectionBg"] = selBg;
                app.Resources["ShelfCardFocusBorder"] = focusBorder;
            }
            catch { }
        }

        /// <summary>
        /// Resets selection accent brushes to the default violet.
        /// Called when switching to Mica or non-wallpaper modes.
        /// </summary>
        private void ResetSelectionAccent()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;

                var selBorder = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x60, 0xA7, 0x8B, 0xFA)); // #60A78BFA
                selBorder.Freeze();
                var selBg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x10, 0xA7, 0x8B, 0xFA)); // #10A78BFA
                selBg.Freeze();
                var focusBorder = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x80, 0xA7, 0x8B, 0xFA)); // #80A78BFA
                focusBorder.Freeze();

                app.Resources["ShelfCardSelectionBorder"] = selBorder;
                app.Resources["ShelfCardSelectionBg"] = selBg;
                app.Resources["ShelfCardFocusBorder"] = focusBorder;
            }
            catch { }
        }

        /// <summary>
        /// Applies the user's wallpaper with frosted glass header + theme color gradient.
        /// </summary>
        /// <summary>Gets current Windows desktop wallpaper path from registry (cached).</summary>
        private static string GetDesktopWallpaperPath()
        {
            if (_cachedDesktopWallpaperPath != null)
                return _cachedDesktopWallpaperPath;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    _cachedDesktopWallpaperPath = key?.GetValue("Wallpaper") as string ?? "";
                    return _cachedDesktopWallpaperPath;
                }
            }
            catch 
            { 
                _cachedDesktopWallpaperPath = "";
                return ""; 
            }
        }

        private void ApplyWallpaper()
        {
            string path = Classes.SettingsManager.Current.ClipboardWallpaperPath;

            // If no wallpaper path set, clear all layers
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                if (_currentLoadedWallpaperPath == "" && WallpaperBg.Visibility == Visibility.Collapsed) return;
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Source = null;
                WallpaperBg.Visibility = Visibility.Collapsed;
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                _currentLoadedWallpaperPath = "";
                return;
            }


            if (path == _currentLoadedWallpaperPath)
            {
                return; // Already loaded! Bypasses heavy disk I/O and visual changes.
            }

            try
            {
                _currentLoadedWallpaperPath = path;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                bool isGif = ext == ".gif";

                if (isGif)
                {
                    // ═══ LIVE WALLPAPER: Animated GIF via XamlAnimatedGif ═══
                    WallpaperBg.Source = null; // Clear static source
                    var uri = new Uri(path, UriKind.Absolute);
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, uri);
                    XamlAnimatedGif.AnimationBehavior.SetRepeatBehavior(WallpaperBg,
                        System.Windows.Media.Animation.RepeatBehavior.Forever);
                    WallpaperBg.Visibility = Visibility.Visible;

                    // For GIF wallpapers, use a themed color directly (can't extract from animated)
                    WallpaperFrostHeader.Visibility = Visibility.Collapsed; // No frost for GIF (looks odd)
                    var themeColor = System.Windows.Media.Color.FromRgb(255, 140, 0); // Cozy dark orange / Gravity Cat
                    var centerColor = System.Windows.Media.Color.FromArgb(30, themeColor.R, themeColor.G, themeColor.B);
                    var edgeColor = System.Windows.Media.Color.FromArgb(120, (byte)(themeColor.R / 5), (byte)(themeColor.G / 5), (byte)(themeColor.B / 5));
                    WallpaperRadialBrush.GradientStops[0].Color = centerColor;
                    WallpaperRadialBrush.GradientStops[1].Color = edgeColor;
                    WallpaperThemeOverlay.Visibility = Visibility.Visible;

                    Classes.Logger.LogAction("WALLPAPER", $"Live animated wallpaper: {path}");
                }
                else
                {
                    // ═══ STATIC WALLPAPER: PNG/JPG via BitmapImage ═══
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null); // Clear any GIF

                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300; // Keep it lightweight
                    bmp.EndInit();
                    bmp.Freeze();

                    // Layer 1: Background image
                    WallpaperBg.Source = bmp;
                    WallpaperBg.Visibility = Visibility.Visible;

                    // Layer 3: Frosted glass header
                    WallpaperFrostImg.Source = bmp;
                    WallpaperFrostHeader.Visibility = Visibility.Visible;

                    // Extract dominant color for theme gradient asynchronously to prevent UI stutter
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            return ExtractDominantColor(bmp);
                        }
                        catch
                        {
                            return System.Windows.Media.Color.FromRgb(99, 102, 241); // Fallback indigo
                        }
                    }).ContinueWith(t =>
                    {
                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                        {
                            var dominantColor = t.Result;
                            Dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    var centerColor = System.Windows.Media.Color.FromArgb(40, dominantColor.R, dominantColor.G, dominantColor.B);
                                    var edgeColor = System.Windows.Media.Color.FromArgb(140, (byte)(dominantColor.R / 4), (byte)(dominantColor.G / 4), (byte)(dominantColor.B / 4));

                                    WallpaperRadialBrush.GradientStops[0].Color = centerColor;
                                    WallpaperRadialBrush.GradientStops[1].Color = edgeColor;
                                    WallpaperThemeOverlay.Visibility = Visibility.Visible;

                                    // Tint the frost header with the theme color
                                    WallpaperFrostTint.Background = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromArgb(90, dominantColor.R, dominantColor.G, dominantColor.B));

                                    // Inject wallpaper dominant color as selection accent
                                    ApplyDominantColorAccent(dominantColor);
                                }
                                catch { }
                            });
                        }
                    });
                }
            }
            catch
            {
                _currentLoadedWallpaperPath = "";
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Visibility = Visibility.Collapsed;
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Quick dominant color extraction by sampling a few pixels from the center.
        /// </summary>
        private static System.Windows.Media.Color ExtractDominantColor(System.Windows.Media.Imaging.BitmapImage bmp)
        {
            try
            {
                var formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = formatted.PixelWidth;
                int h = formatted.PixelHeight;
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];
                formatted.CopyPixels(pixels, stride, 0);

                // Sample 9 points in center region
                int totalR = 0, totalG = 0, totalB = 0, count = 0;
                int[] xs = { w / 4, w / 2, 3 * w / 4 };
                int[] ys = { h / 4, h / 2, 3 * h / 4 };

                foreach (int x in xs)
                    foreach (int y in ys)
                    {
                        int idx = y * stride + x * 4;
                        if (idx + 2 < pixels.Length)
                        {
                            totalB += pixels[idx];
                            totalG += pixels[idx + 1];
                            totalR += pixels[idx + 2];
                            count++;
                        }
                    }

                if (count > 0)
                    return System.Windows.Media.Color.FromRgb((byte)(totalR / count), (byte)(totalG / count), (byte)(totalB / count));
            }
            catch { }

            return System.Windows.Media.Color.FromRgb(99, 102, 241); // Fallback indigo
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
            }
            catch { /* Window already destroyed — nothing to clean up */ }
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == HOTKEY_ID)
                {
                    Classes.Logger.LogAction("TELEMETRY", "Hotkey Alt+C received inside WndProc");
                    ToggleMainClipboard();
                    handled = true;
                }
                else if (hotkeyId >= HOTKEY_QUICKPASTE_BASE + 1 && hotkeyId <= HOTKEY_QUICKPASTE_BASE + 10)
                {
                    // Alt+1=item0, Alt+2=item1, ..., Alt+9=item8, Alt+0=item9
                    int index = hotkeyId == HOTKEY_QUICKPASTE_BASE + 10 ? 9 : (hotkeyId - HOTKEY_QUICKPASTE_BASE - 1);
                    // CRITICAL: Defer clipboard + focus work out of WndProc to avoid dispatcher suspension crash
                    Dispatcher.InvokeAsync(() =>
                    {
                        Classes.Logger.LogAction("HOTKEY", $"Alt+{(index + 1) % 10} fired, items={_viewModel.DroppedItems.Count}");
                        if (index < _viewModel.DroppedItems.Count)
                        {
                            // Capture the target window — filter out our own window
                            IntPtr targetWindow = GetTargetForegroundWindow();
                            Classes.Logger.LogAction("HOTKEY", $"Target window: 0x{targetWindow:X}");
                            var item = _viewModel.DroppedItems[index];
                            
                            // Set clipboard directly — guard against echo
                            SetWritingClipboard(true);
                            try
                            {
                                if (!string.IsNullOrEmpty(item.RawContent))
                                    System.Windows.Clipboard.SetText(item.RawContent);
                                else if (!string.IsNullOrEmpty(item.FilePath))
                                {
                                    var dropList = new System.Collections.Specialized.StringCollection();
                                    dropList.Add(item.FilePath);
                                    System.Windows.Clipboard.SetFileDropList(dropList);
                                }
                            }
                            catch { }

                            // Force-restore focus using AttachThreadInput trick
                            uint targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
                            uint ourThreadId = GetCurrentThreadId();
                            if (targetThreadId != ourThreadId)
                                AttachThreadInput(ourThreadId, targetThreadId, true);
                            
                            SetForegroundWindow(targetWindow);
                            
                            if (targetThreadId != ourThreadId)
                                AttachThreadInput(ourThreadId, targetThreadId, false);

                            // Release Alt key FIRST — user is still holding it from Alt+N,
                            // otherwise the target app receives Alt+Ctrl+V instead of Ctrl+V
                            keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, 0);

                            // Fire Ctrl+V after a short async pause for key state to propagate
                            // Also clear the clipboard write guard after delay
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(50);
                                keybd_event((byte)VK_CONTROL, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, 0, 0);
                                keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, 0);
                                keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                                await Task.Delay(500); // Absorb WM_CLIPBOARDUPDATE
                                SetWritingClipboard(false);
                            });
                        }
                    });
                    handled = true;
                }
            }
            else if (msg == Classes.NativeMethods.WM_SETTINGCHANGE)
            {
                _cachedDesktopWallpaperPath = null;
                // Only re-apply if we're in FlyShelf desktop wallpaper mode
                if ((Classes.SettingsManager.Current.ThemeDisplayMode ?? "mica") == "desktop")
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            string desktopWp = GetDesktopWallpaperPath();
                            if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                            {
                                Classes.SettingsManager.Current.ClipboardWallpaperPath = desktopWp;
                                _currentLoadedWallpaperPath = ""; // Force reload
                                ApplyWallpaper();
                            }
                        }
                        catch { }
                    });
                }
            }
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                // GUARD: Skip clipboard events triggered by our own writes
                if (_isWritingClipboard)
                {
                    handled = true;
                    return IntPtr.Zero;
                }

                // DEBOUNCE: Reuse a single timer to avoid GC pressure.
                // 100ms collapses burst events while staying responsive.
                if (_clipboardDebounceTimer == null)
                {
                    _clipboardDebounceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(150) // 150ms debounce â€” fast response while still collapsing burst events
                    };
                    _clipboardDebounceTimer.Tick += (s, ev) =>
                    {
                        _clipboardDebounceTimer.Stop();
                        try
                        {
                            // PERF: Clipboard.GetDataObject() is a COM call that MUST run on the STA UI thread.
                            // Extract the minimum data here, then offload ALL processing to a background thread.
                            IDataObject data = Clipboard.GetDataObject();
                            if (data == null) return;

                            // PERF: Verbose format logging removed — was causing string alloc + I/O on every clipboard event

                            // Snapshot all data now while we're on the STA thread â€” IDataObject can't cross threads
                            string[] files = null;
                            string text = null;
                            System.Windows.Media.Imaging.BitmapSource bitmap = null;

                            // STEP 1: Always try to extract bitmap FIRST â€” screenshots from Snipping Tool
                            // set BOTH FileDrop AND Bitmap, but the file may not exist yet (async save).
                            try
                            {
                                if (data.GetDataPresent(DataFormats.Bitmap))
                                {
                                    bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                                }
                                if (bitmap == null && data.GetDataPresent(typeof(System.Windows.Media.Imaging.BitmapSource)))
                                {
                                    bitmap = data.GetData(typeof(System.Windows.Media.Imaging.BitmapSource)) as System.Windows.Media.Imaging.BitmapSource;
                                }
                                if (bitmap == null && data.GetDataPresent(DataFormats.Dib))
                                {
                                    bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                                }
                                if (bitmap != null && bitmap.CanFreeze) bitmap.Freeze(); // Make thread-safe
                            }
                            catch (Exception bmpEx) 
                            { 
                                Classes.Logger.LogAction("CLIPBOARD", $"Bitmap extraction failed: {bmpEx.Message}");
                            }

                            // STEP 2: Extract file paths
                            try
                            {
                                if (data.GetDataPresent(DataFormats.FileDrop))
                                    files = data.GetData(DataFormats.FileDrop) as string[];
                                if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
                                    files = data.GetData("FileNameW") as string[];
                                
                                // PERF: File list logging removed
                            }
                            catch { }

                            // STEP 3: If we have BOTH bitmap AND files, prefer bitmap for screenshots
                            // (Snipping Tool sets FileDrop but the file may not exist yet)
                            if (bitmap != null && files != null && files.Length > 0)
                            {
                                // Check if file actually exists â€” if not, the bitmap is the real data
                                bool allFilesExist = files.All(f => System.IO.File.Exists(f));
                                if (!allFilesExist)
                                {
                                    Classes.Logger.LogAction("CLIPBOARD", "Files don't exist yet â€” using bitmap instead");
                                    files = null; // Force bitmap path
                                }
                                else
                                {
                                    // Files exist â€” check if they're image files (prefer bitmap for images)
                                    string ext = System.IO.Path.GetExtension(files[0]).ToLower();
                                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
                                    {
                                        Classes.Logger.LogAction("CLIPBOARD", "Image file detected â€” using bitmap for richer preview");
                                        files = null; // Force bitmap path for image files
                                    }
                                }
                            }

                            // STEP 4: Extract text only if no bitmap and no files
                            if (bitmap == null && (files == null || files.Length == 0))
                            {
                                try
                                {
                                    if (data.GetDataPresent(DataFormats.UnicodeText))
                                        text = data.GetData(DataFormats.UnicodeText) as string;
                                    if (string.IsNullOrEmpty(text) && data.GetDataPresent(DataFormats.Text))
                                        text = data.GetData(DataFormats.Text) as string;
                                }
                                catch { }
                            }

                            // Now dispatch to background â€” no more COM calls needed
                            var vm = (FlyShelfViewModel)DataContext;
                            if (bitmap != null && (files == null || files.Length == 0))
                            {
                                // â•â•â• FIX: Filter out fully transparent/ghost images â•â•â•
                                // Some apps and screenshot tools place transparent bitmaps on clipboard.
                                // Check if >95% of pixels are fully transparent â€” if so, discard.
                                bool isGhostImage = false;
                                try
                                {
                                    var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                                    int w = converted.PixelWidth;
                                    int h = converted.PixelHeight;
                                    // Ultra-light ghost check: read 16 single pixels from a 4Ã—4 grid (64 bytes total)
                                    byte[] pixel = new byte[4];
                                    int transparentCount = 0;
                                    const int gridSize = 4;
                                    for (int gy = 0; gy < gridSize; gy++)
                                    {
                                        int y = (gy * 2 + 1) * h / (gridSize * 2); // Centered samples
                                        for (int gx = 0; gx < gridSize; gx++)
                                        {
                                            int x = (gx * 2 + 1) * w / (gridSize * 2);
                                            converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
                                            if (pixel[3] < 10) transparentCount++;
                                        }
                                    }
                                    if (transparentCount >= 15) // 15/16 = 93.75% transparent
                                    {
                                        isGhostImage = true;
                                        Classes.Logger.LogAction("CLIPBOARD", $"⛔ Rejected ghost image ({w}x{h}) — {transparentCount}/16 samples transparent");
                                    }
                                }
                                catch { }

                                if (!isGhostImage)
                                {
                                    Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as BITMAP ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
                                    var dataObj = new System.Windows.DataObject(typeof(System.Windows.Media.Imaging.BitmapSource), bitmap);
                                    Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false));
                                }
                            }
                            else if (files != null && files.Length > 0)
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({files.Length} items)");
                                var dataObj = new System.Windows.DataObject(DataFormats.FileDrop, files);
                                Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false));
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as TEXT ({text.Length} chars)");
                                var dataObj = new System.Windows.DataObject(DataFormats.UnicodeText, text);
                                Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false));
                            }
                            else
                            {
                                Classes.Logger.LogAction("CLIPBOARD", "→ No actionable data found on clipboard");
                            }
                        }
                        catch (Exception cbEx) { Classes.Logger.LogAction("CLIPBOARD", $"Handler error: {cbEx.Message}"); }
                    };
                }
                _clipboardDebounceTimer.Stop();
                _clipboardDebounceTimer.Start();
                
                handled = true;
            }
            return IntPtr.Zero;
        }

    }
}

