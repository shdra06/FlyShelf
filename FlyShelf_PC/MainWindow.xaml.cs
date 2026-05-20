using AdvanceClip.ViewModels;
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

namespace AdvanceClip
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
        private double _lockedBottomEdge = 0;
        private bool _isEdgeLocked = false;
        private Windows.TaskbarWindow? _taskbarWidget;
        private System.Windows.Threading.DispatcherTimer? _clipboardDebounceTimer;
        private DateTime _lastMergeToggleTime = DateTime.MinValue;
        private IntPtr _lastActiveExternalWindow = IntPtr.Zero;

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

        // Hover preview popup state
        private System.Windows.Threading.DispatcherTimer? _hoverPreviewTimer;
        private ClipboardItem? _hoveredItem;
        private Windows.PreviewPopup? _activePreviewPopup;

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
                // Skip re-focus if a topmost child window (QuickLook) is active — prevents infinite activation loop
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
            };

            // Live-refresh wallpaper when user changes it in settings
            Classes.SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Classes.AdvanceSettings.ClipboardWallpaperPath))
                    Dispatcher.InvokeAsync(() => ApplyWallpaper());
            };

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

        private void MicaWindow_Loaded(object sender, RoutedEventArgs e)
        {
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

            // DWM border styling — must happen after window is shown
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
                        {
                            int colorNone = DWMWA_COLOR_NONE;
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

            // Attach LIST-mode smooth scrolling (very slow for clipboard items)
            Classes.SmoothScroll.AttachList(ShelfListView);

            // Apply wallpaper if configured
            // Apply wallpaper deferred (100ms) to not block UI init
            System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => {
                Dispatcher.InvokeAsync(() => { try { ApplyWallpaper(); } catch { } });
            });

            // Blur-off or system transparency disabled: solid dark gradient fallback
            if (!Classes.SettingsManager.Current.EnableBlurBehind || !Classes.NativeMethods.ShouldUseBlur())
            {
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                System.Threading.Tasks.Task.Delay(150).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ApplyPopupBackground();
                    });
                });
            }

            // Pre-initialize the heavy Hub Window in the background when the app is idle
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
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// Applies the dark gradient background for the popup clipboard (solid fallback for no-blur).
        /// </summary>
        private void ApplyPopupBackground()
        {
            var gradient = new System.Windows.Media.LinearGradientBrush();
            gradient.StartPoint = new System.Windows.Point(0.5, 0);
            gradient.EndPoint = new System.Windows.Point(0.5, 1);
            gradient.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(32, 32, 48), 0));    // #202030 soft indigo
            gradient.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(28, 28, 42), 0.5));  // #1C1C2A mid tone
            gradient.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(24, 24, 38), 1));    // #181826 base
            gradient.Freeze();
            this.Background = gradient;
            if (RootContent != null) RootContent.Background = gradient;
        }

        /// <summary>
        /// Applies the user's wallpaper with frosted glass header + theme color gradient.
        /// </summary>
        /// <summary>Gets current Windows desktop wallpaper path from registry.</summary>
        private static string GetDesktopWallpaperPath()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                return key?.GetValue("Wallpaper") as string ?? "";
            }
            catch { return ""; }
        }

        private void ApplyWallpaper()
        {
            string path = Classes.SettingsManager.Current.ClipboardWallpaperPath;

            // If no custom wallpaper set, use the current Windows desktop wallpaper
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                try
                {
                    string desktopWp = GetDesktopWallpaperPath();
                    if (!string.IsNullOrEmpty(desktopWp) && System.IO.File.Exists(desktopWp))
                        path = desktopWp;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                // No wallpaper at all — hide all layers
                WallpaperBg.Visibility = Visibility.Collapsed;
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 400; // Keep it lightweight
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
                            }
                            catch { }
                        });
                    }
                });
            }
            catch
            {
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
                    // Toggle: if already visible, just hide and return
                    if (this.IsVisible)
                    {
                        AnimateAndHide();
                        handled = true;
                        return IntPtr.Zero;
                    }
                    var workArea = SystemParameters.WorkArea;
                    ShowNearPosition(workArea.Left + 16, workArea.Top + workArea.Height, 1, true, true);
                    handled = true;
                }
                else if (hotkeyId >= HOTKEY_QUICKPASTE_BASE + 1 && hotkeyId <= HOTKEY_QUICKPASTE_BASE + 10)
                {
                    // Alt+1=item0, Alt+2=item1, ..., Alt+9=item8, Alt+0=item9
                    int index = hotkeyId == HOTKEY_QUICKPASTE_BASE + 10 ? 9 : (hotkeyId - HOTKEY_QUICKPASTE_BASE - 1);
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
                    handled = true;
                }
            }
            else if (msg == Classes.NativeMethods.WM_SETTINGCHANGE)
            {
                Dispatcher.InvokeAsync(() => { try { ApplyWallpaper(); } catch { } });
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
                        Interval = TimeSpan.FromMilliseconds(150) // 150ms debounce — fast response while still collapsing burst events
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

                            // DEBUG: Log all clipboard formats to diagnose screenshot detection issues
                            try
                            {
                                var formats = data.GetFormats();
                                Classes.Logger.LogAction("CLIPBOARD", $"Formats: {string.Join(", ", formats)}");
                            }
                            catch { }

                            // Snapshot all data now while we're on the STA thread — IDataObject can't cross threads
                            string[] files = null;
                            string text = null;
                            System.Windows.Media.Imaging.BitmapSource bitmap = null;

                            // STEP 1: Always try to extract bitmap FIRST — screenshots from Snipping Tool
                            // set BOTH FileDrop AND Bitmap, but the file may not exist yet (async save).
                            try
                            {
                                if (data.GetDataPresent(DataFormats.Bitmap))
                                {
                                    bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                                    Classes.Logger.LogAction("CLIPBOARD", $"Bitmap via DataFormats.Bitmap: {(bitmap != null ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" : "null")}");
                                }
                                if (bitmap == null && data.GetDataPresent(typeof(System.Windows.Media.Imaging.BitmapSource)))
                                {
                                    bitmap = data.GetData(typeof(System.Windows.Media.Imaging.BitmapSource)) as System.Windows.Media.Imaging.BitmapSource;
                                    Classes.Logger.LogAction("CLIPBOARD", $"Bitmap via BitmapSource type: {(bitmap != null ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" : "null")}");
                                }
                                if (bitmap == null && data.GetDataPresent(DataFormats.Dib))
                                {
                                    bitmap = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                                    Classes.Logger.LogAction("CLIPBOARD", $"Bitmap via DIB fallback: {(bitmap != null ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" : "null")}");
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
                                
                                if (files != null && files.Length > 0)
                                    Classes.Logger.LogAction("CLIPBOARD", $"Files: {string.Join(", ", files)}");
                            }
                            catch { }

                            // STEP 3: If we have BOTH bitmap AND files, prefer bitmap for screenshots
                            // (Snipping Tool sets FileDrop but the file may not exist yet)
                            if (bitmap != null && files != null && files.Length > 0)
                            {
                                // Check if file actually exists — if not, the bitmap is the real data
                                bool allFilesExist = files.All(f => System.IO.File.Exists(f));
                                if (!allFilesExist)
                                {
                                    Classes.Logger.LogAction("CLIPBOARD", "Files don't exist yet — using bitmap instead");
                                    files = null; // Force bitmap path
                                }
                                else
                                {
                                    // Files exist — check if they're image files (prefer bitmap for images)
                                    string ext = System.IO.Path.GetExtension(files[0]).ToLower();
                                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
                                    {
                                        Classes.Logger.LogAction("CLIPBOARD", "Image file detected — using bitmap for richer preview");
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

                            // Now dispatch to background — no more COM calls needed
                            var vm = (FlyShelfViewModel)DataContext;
                            if (bitmap != null && (files == null || files.Length == 0))
                            {
                                // ═══ FIX: Filter out fully transparent/ghost images ═══
                                // Some apps and screenshot tools place transparent bitmaps on clipboard.
                                // Check if >95% of pixels are fully transparent — if so, discard.
                                bool isGhostImage = false;
                                try
                                {
                                    var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                                    int w = converted.PixelWidth;
                                    int h = converted.PixelHeight;
                                    // Ultra-light ghost check: read 16 single pixels from a 4×4 grid (64 bytes total)
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
                                    System.Threading.Tasks.Task.Run(() =>
                                        Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false)));
                                }
                            }
                            else if (files != null && files.Length > 0)
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as FILES ({files.Length} items)");
                                var dataObj = new System.Windows.DataObject(DataFormats.FileDrop, files);
                                System.Threading.Tasks.Task.Run(() =>
                                    Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false)));
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                Classes.Logger.LogAction("CLIPBOARD", $"→ Routing as TEXT ({text.Length} chars)");
                                var dataObj = new System.Windows.DataObject(DataFormats.UnicodeText, text);
                                System.Threading.Tasks.Task.Run(() =>
                                    Application.Current.Dispatcher.InvokeAsync(() => vm.HandleDrop(dataObj, false)));
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

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // Guard: don't fight QuickLook for focus
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is AdvanceClip.Windows.QuickLookWindow && w.IsActive)) return;
            if (_isAnimatingHide) return;
            this.Opacity = 1.0;
            int colorNone = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
        }


        /// <summary>
        /// Auto-hide the clipboard shelf when user clicks elsewhere (e.g. to type in another app).
        /// Respects persistent mode and prevents accidental dismissal during the first 400ms after spawn.
        /// </summary>
        private void MicaWindow_Deactivated(object sender, EventArgs e)
        {
            // Don't auto-hide in persistent/docked mode (taskbar widget click)
            if (_isPersistentMode) return;

            // Guard: Don't dismiss if the window JUST appeared (prevents flicker from focus races)
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 100) return;

            // Don't dismiss while user is mid-drag
            if (_isDragHovering) return;

            // Don't dismiss if focus went to our own QuickLook window
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is AdvanceClip.Windows.QuickLookWindow && w.IsActive)) return;

            // Auto-hide when user clicks away
            if (this.IsVisible)
            {
                AnimateAndHide();
            }
        }

        /// <summary>
        /// Native Win32 callback triggered when the active foreground window changes globally.
        /// Handles auto-dismissing FlyShelf when shown in a non-activated / non-focus-stealing state.
        /// </summary>
        private void ForegroundChangedCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd != IntPtr.Zero)
            {
                // Get thread/process ID of the new foreground window
                GetWindowThreadProcessId(hwnd, out uint focusedProcId);
                uint currProcId = (uint)System.Environment.ProcessId;

                // Cache the last active external window, filtering out our own app, taskbar, desktop, and standard system Windows Core UI
                if (focusedProcId != currProcId)
                {
                    var sbClass = new System.Text.StringBuilder(256);
                    GetClassName(hwnd, sbClass, 256);
                    string clsName = sbClass.ToString();
                    if (clsName != "Shell_TrayWnd" && 
                        clsName != "Shell_SecondaryTrayWnd" && 
                        clsName != "WorkerW" && 
                        clsName != "Progman" && 
                        clsName != "Windows.UI.Core.CoreWindow" &&
                        clsName != "MultitaskingViewFrame")
                    {
                        _lastActiveExternalWindow = hwnd;
                    }
                }
            }

            if (_isPersistentMode) return;

            // Don't auto-dismiss during first 250ms of spawn to avoid startup focus race transitions
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 250) return;

            if (_isDragHovering) return;

            // Get thread/process ID of the new foreground window
            GetWindowThreadProcessId(hwnd, out uint focusedProcessId);
            uint currentProcessId = (uint)System.Environment.ProcessId;

            // If the focused window belongs to our own app (e.g. MainWindow, HubWindow, QuickLook), do not dismiss
            if (focusedProcessId == currentProcessId) return;

            // Foreground changed to another app (browser, editor, desktop, etc.)! Auto-dismiss FlyShelf!
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (this.IsVisible && !_isAnimatingHide)
                {
                    AnimateAndHide();
                }
            });
        }
        private bool _isPersistentMode = false;
        private bool _isAnimatingHide = false;

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        private void PlayShowAnimation()
        {
            RootContent.RenderTransformOrigin = new Point(0.5, 1);
            RootContent.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(0.97, 0.97), new TranslateTransform(0, 6) }
            };
            RootContent.Opacity = 0;

            var dur = TimeSpan.FromMilliseconds(200);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

            RootContent.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
            ((TransformGroup)RootContent.RenderTransform).Children[1].BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(6, 0, dur) { EasingFunction = ease });
        }

        /// <summary>Fast dismiss animation on inner content, then hides window.</summary>
        private void AnimateAndHide()
        {
            if (_isAnimatingHide || !this.IsVisible) return;
            _isAnimatingHide = true;

            // Clear PDF merge selections so they don't persist on reopen
            DismissMergeState();
            CloseSearch();

            RootContent.RenderTransformOrigin = new Point(0.5, 1);
            RootContent.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) }
            };

            var dur = TimeSpan.FromMilliseconds(140);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            fadeOut.Completed += (s, e) =>
            {
                try
                {
                    this.Hide();
                    RootContent.BeginAnimation(OpacityProperty, null);
                    RootContent.Opacity = 1;
                    RootContent.RenderTransform = null;
                }
                catch { }
                _isAnimatingHide = false;
            };

            RootContent.BeginAnimation(OpacityProperty, fadeOut);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.97, dur) { EasingFunction = ease });
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.97, dur) { EasingFunction = ease });
            ((TransformGroup)RootContent.RenderTransform).Children[1].BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 5, dur) { EasingFunction = ease });
        }
        private DateTime _spawnTime = DateTime.MinValue;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;
        internal static bool _isWritingClipboard = false;
        private static System.Threading.Timer _clipboardWriteResetTimer;
        
        /// <summary>
        /// Sets _isWritingClipboard = true with automatic 2-second safety reset.
        /// Prevents the flag from getting stuck true if an exception prevents the finally block.
        /// </summary>
        internal static void SetWritingClipboard(bool value)
        {
            _isWritingClipboard = value;
            _clipboardWriteResetTimer?.Dispose();
            if (value)
            {
                _clipboardWriteResetTimer = new System.Threading.Timer(_ =>
                {
                    if (_isWritingClipboard)
                    {
                        Classes.Logger.LogAction("CLIPBOARD", "⚠️ _isWritingClipboard was stuck true — auto-reset after 2s safety timeout");
                        _isWritingClipboard = false;
                    }
                }, null, 2000, System.Threading.Timeout.Infinite);
            }
        }

        private IntPtr GetTargetForegroundWindow()
        {
            IntPtr ptr = GetForegroundWindow();
            
            var sb = new System.Text.StringBuilder(256);
            GetClassName(ptr, sb, 256);
            string className = sb.ToString();

            if (className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" || className == "WorkerW" || className == "Progman")
            {
                // Quick bypass: If we have a cached valid/visible external window, return it instantly!
                if (_lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow) && IsWindowVisible(_lastActiveExternalWindow))
                {
                    return _lastActiveExternalWindow;
                }

                IntPtr target = IntPtr.Zero;
                uint currentProcessId = GetCurrentProcessId();
                EnumWindows((wnd, param) =>
                {
                    if (IsWindowVisible(wnd))
                    {
                        uint processId;
                        GetWindowThreadProcessId(wnd, out processId);
                        if (processId != currentProcessId)
                        {
                            GetClassName(wnd, sb, 256);
                            string cName = sb.ToString();
                            if (cName != "Shell_TrayWnd" && cName != "Shell_SecondaryTrayWnd" && cName != "WorkerW" && cName != "Progman")
                            {
                                GetWindowText(wnd, sb, 256);
                                if (sb.Length > 0 && sb.ToString() != "FlyShelf" && sb.ToString() != "Program Manager")
                                {
                                    target = wnd;
                                    return false; 
                                }
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
                if (target != IntPtr.Zero) return target;
            }
            
            return ptr;
        }

        public void ShowNearPosition(double targetX, double targetY, int mode = 0, bool isPersistent = false, bool stealFocus = true)
        {
            CloseSearch();
            _previousForegroundWindow = GetTargetForegroundWindow();
            
            // AdvanceClip Phase 2: Live AI Memory Association
            if (_previousForegroundWindow != IntPtr.Zero)
            {
                var sbTitle = new System.Text.StringBuilder(256);
                GetWindowText(_previousForegroundWindow, sbTitle, 256);
                string currentTitle = sbTitle.ToString();
                
                // Fire sorting asynchronously AFTER the UI finishes its layout!
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() => _viewModel.SortForContext(currentTitle));
                });
            }

            _spawnTime = DateTime.Now;
            _isPersistentMode = isPersistent;
            if (this.IsVisible)
            {
                this.Hide(); 
            }

            this.ShowInTaskbar = true;
            this.ShowInTaskbar = false;

            _viewModel.CurrentMode = mode;
            this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
            this.Width = _viewModel.CurrentFlyShelfWidth;

            // Always reset selection to the first item when showing/opening the shelf
            if (_viewModel.DroppedItems.Count > 0)
            {
                ShelfListView.SelectedIndex = 0;
            }

            // Force a deterministic height so the window doesn't bounce around with SizeToContent
            if (mode == 0)
            {
                // Mini mode: let content drive height, capped by MaxHeight
                this.SizeToContent = SizeToContent.Height;
                this.Height = double.NaN;
            }
            else
            {
                // Mode 1/2: use the stored height exactly — no content-driven fluctuation
                this.SizeToContent = SizeToContent.Manual;
                this.Height = _viewModel.CurrentFlyShelfMaxHeight;
            }

            var workArea = SystemParameters.WorkArea;
            double safeWidth = double.IsNaN(this.Width) ? 360 : this.Width;
            if (safeWidth <= 0) safeWidth = 320;

            double rawX = targetX - (safeWidth / 2);
            if (rawX + safeWidth > workArea.Left + workArea.Width - 16)
                rawX = workArea.Left + workArea.Width - safeWidth - 16;
            if (rawX < workArea.Left + 16)
                rawX = workArea.Left + 16;

            double rawY = targetY - 16;
            if (rawY > workArea.Top + workArea.Height - 16)
                rawY = workArea.Top + workArea.Height - 16;
            
            _lockedBottomEdge = rawY;
            _isEdgeLocked = true;

            this.Left = rawX;
            // Best-guess initial bound from user settings before ActualHeight resolves
            double initialSafeHeight = double.IsNaN(this.Height) ? AdvanceClip.Classes.SettingsManager.Current.MiniFormHeight : this.Height;
            this.Top = _lockedBottomEdge - initialSafeHeight - 20;

            if (stealFocus)
            {
                this.ShowActivated = true;
                RootContent.Opacity = 0;
                this.Show();
                this.Activate();
                { int cn = DWMWA_COLOR_NONE; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref cn, sizeof(int)); }
                PlayShowAnimation();
            }
            else
            {
                this.ShowActivated = false;
                RootContent.Opacity = 0;
                this.Show();
                { int cn = DWMWA_COLOR_NONE; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref cn, sizeof(int)); }
                PlayShowAnimation();
            }

            this.UpdateLayout();
            if (this.ActualHeight > 0)
            {
                // Push it 20px dynamically upward to completely avoid taskbar z-index clipping!
                this.Top = _lockedBottomEdge - this.ActualHeight - 20; 
                
                if (this.Top < workArea.Top)
                {
                    this.Top = workArea.Top + 20;
                }
            }

            int currentToken = ++_spawnToken;

            // Give keyboard focus to the ListView so arrow keys + Enter work immediately
            if (stealFocus)
            {
                FocusFirstItemContainer();
            }
        }

        private void FocusFirstItemContainer()
        {
            if (_viewModel.DroppedItems.Count == 0) return;

            if (ShelfListView.SelectedIndex < 0)
                ShelfListView.SelectedIndex = 0;

            int index = ShelfListView.SelectedIndex;
            
            // If the containers are already generated, focus immediately:
            var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
            if (container != null)
            {
                container.Focus();
                Keyboard.Focus(container);
                ShelfListView.ScrollIntoView(container);
            }
            else
            {
                // Otherwise, register event handler to focus as soon as they are ready:
                EventHandler? statusHandler = null;
                statusHandler = (s, ev) =>
                {
                    if (ShelfListView.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        ShelfListView.ItemContainerGenerator.StatusChanged -= statusHandler;
                        Dispatcher.InvokeAsync(() =>
                        {
                            var lazyContainer = ShelfListView.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
                            if (lazyContainer != null)
                            {
                                lazyContainer.Focus();
                                Keyboard.Focus(lazyContainer);
                                ShelfListView.ScrollIntoView(lazyContainer);
                            }
                            else
                            {
                                ShelfListView.Focus();
                            }
                        }, System.Windows.Threading.DispatcherPriority.Input);
                    }
                };
                ShelfListView.ItemContainerGenerator.StatusChanged += statusHandler;
                ShelfListView.Focus();
            }
        }

    }
}

