using FlyShelf.Classes;
using FlyShelf.Classes.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using static FlyShelf.Classes.NativeMethods;

namespace FlyShelf.Windows
{
    public partial class TaskbarWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private readonly double _scale = 0.9;

        private MainWindow? _mainWindow;
        private bool _positionUpdateInProgress;
        
        private int _lastTaskbarWidth = -1;
        private int _lastTaskbarHeight = -1;
        private Rect _lastTaskbarFrameRect = Rect.Empty;

        // Cached free-zone detection
        private int _cachedFreeZoneLeft = -1;
        private int _cachedFreeZoneWidth = -1;
        private DateTime _lastFreeZoneScan = DateTime.MinValue;
        private bool _isClosed = false;
        private bool _isFloatingMode = false; // True when taskbar auto-hide is on — widget floats independently

        // Position stability — avoid redundant SetWindowPos calls that cause flicker
        private int _lastWidgetLeft = -1;
        private int _lastWidgetTop = -1;
        private int _lastWidgetW = -1;
        private int _lastWidgetH = -1;
        private Rect _lastWidgetRect = Rect.Empty;

        // Caching for Taskbar HWND to avoid heavy EnumWindows P/Invoke queries
        private IntPtr _cachedTaskbarHandle = IntPtr.Zero;
        private bool _cachedIsMainTaskbarSelected = true;

        private bool _lastCachedTaskbarCentered = false;
        private bool _lastCachedWidgetsVisible = true;

        // Cached Widgets button right edge (detected via UI Automation)
        private int _cachedWidgetsButtonRight = -1;
        private DateTime _lastWidgetsButtonScan = DateTime.MinValue;

        public TaskbarWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500); // 2fps is plenty — taskbar rarely moves
            _timer.Tick += (s, e) => UpdatePosition();

            // Listen for system preference changes (like taskbar auto-hide toggling)
            try
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            }
            catch { }

            // Listen for the toggle setting change
            SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AdvanceSettings.EnableTaskbarWidget))
                {
                    Dispatcher.Invoke(async () =>
                    {
                        if (_isClosed) return;
                        if (SettingsManager.Current.EnableTaskbarWidget)
                        {
                            // Ensure _mainWindow reference is set (may be null if widget was OFF at startup)
                            if (_mainWindow == null)
                            {
                                _mainWindow = Application.Current.MainWindow as MainWindow;
                                if (_mainWindow != null)
                                {
                                    Widget.SetMainWindow(_mainWindow);
                                }
                            }

                            Show();
                            _timer.Start();

                            // Retry SetupWindow with delays — taskbar HWND may not respond immediately
                            for (int attempt = 1; attempt <= 3; attempt++)
                            {
                                if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;
                                try
                                {
                                    SetupWindow();
                                    var interop = new System.Windows.Interop.WindowInteropHelper(this);
                                    if (interop.Handle != IntPtr.Zero)
                                    {
                                        bool isEmbedded = NativeMethods.GetParent(interop.Handle) != IntPtr.Zero;
                                        if (isEmbedded || _isFloatingMode)
                                        {
                                            Classes.Logger.LogAction("WIDGET", $"Toggle-ON embed succeeded on attempt {attempt}");
                                            break;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Classes.Logger.LogAction("WIDGET", $"Toggle-ON embed attempt {attempt} failed: {ex.Message}");
                                }
                                await Task.Delay(600);
                            }
                        }
                        else
                        {
                            _timer.Stop();
                            Visibility = Visibility.Hidden;
                        }
                    });
                }
                else if (e.PropertyName == nameof(AdvanceSettings.WidgetTaskbarAlignment) ||
                         e.PropertyName == nameof(AdvanceSettings.WidgetHorizontalOffset))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;
                        // Invalidate cache immediately to force repositioning
                        _cachedFreeZoneLeft = -1;
                        _lastFreeZoneScan = DateTime.MinValue;
                        _lastWidgetLeft = -1; // Invalidate position cache
                        UpdatePosition();
                    });
                }
            };

            // Defer startup activation to AFTER the constructor + Loaded events complete.
            // Calling Show()+SetupWindow() during the constructor (while MainWindow's Loaded
            // event is still running) causes the taskbar embedding to fail silently.
            if (SettingsManager.Current.EnableTaskbarWidget)
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        // Wait for the full WPF layout pass + MainWindow initialization to finish
                        await Task.Delay(1200);
                        if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;

                        Show();
                        _timer.Start();

                        // Retry SetupWindow up to 3 times — taskbar HWND may not be ready on first try
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;
                            try
                            {
                                SetupWindow();
                                var interop = new System.Windows.Interop.WindowInteropHelper(this);
                                if (interop.Handle != IntPtr.Zero && NativeMethods.GetParent(interop.Handle) != IntPtr.Zero)
                                {
                                    Classes.Logger.LogAction("WIDGET", $"Startup embed succeeded on attempt {attempt}");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Classes.Logger.LogAction("WIDGET", $"Startup embed attempt {attempt} failed: {ex.Message}");
                            }
                            await Task.Delay(800);
                        }
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("WIDGET", $"Startup embed thread failed: {ex.Message}");
                    }
                }, DispatcherPriority.Background);
            }

            Classes.Logger.LogAction("WIDGET", "TaskbarWindow constructor completed");
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            try { _timer?.Stop(); } catch { }
            try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged; } catch { }
            base.OnClosed(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_isClosed) return;
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    int colorNone = DWMWA_COLOR_DARK_GRAY;
                    DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
                }
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (_isClosed) return;
            try
            {
                HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
                source?.AddHook(WindowProc);
            }
            catch { }
        }

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case 0x003D: // WM_GETOBJECT — suppress accessibility queries
                case 0x0281: // WM_IME_SETCONTEXT
                case 0x0282: // WM_IME_NOTIFY
                    handled = true;
                    return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isClosed) return;
            if (!SettingsManager.Current.EnableTaskbarWidget)
            {
                Visibility = Visibility.Hidden;
                return;
            }
            SetupWindow();
            _mainWindow = Application.Current.MainWindow as MainWindow;
            if (_mainWindow != null)
            {
                Widget.SetMainWindow(_mainWindow);
            }
        }

        private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbarSelected)
        {
            if (_cachedTaskbarHandle != IntPtr.Zero && IsWindow(_cachedTaskbarHandle))
            {
                isMainTaskbarSelected = _cachedIsMainTaskbarSelected;
                return _cachedTaskbarHandle;
            }

            var monitors = MonitorUtil.GetMonitors();
            var selectedMonitor = MonitorUtil.GetSelectedMonitor();
            isMainTaskbarSelected = true;

            var mainHwnd = FindWindow("Shell_TrayWnd", null);
            if (MonitorUtil.GetMonitor(mainHwnd).deviceId == selectedMonitor.deviceId)
            {
                _cachedTaskbarHandle = mainHwnd;
                _cachedIsMainTaskbarSelected = true;
                return mainHwnd;
            }

            if (monitors.Count == 1)
            {
                _cachedTaskbarHandle = mainHwnd;
                _cachedIsMainTaskbarSelected = true;
                return mainHwnd;
            }

            isMainTaskbarSelected = false;
            IntPtr secondHwnd = IntPtr.Zero;
            StringBuilder className = new(256);
            IntPtr checkWindowClass(IntPtr wnd)
            {
                var len = GetClassName(wnd, className, className.Capacity);
                if (className.Equals("Shell_SecondaryTrayWnd"))
                {
                    if (MonitorUtil.GetMonitor(wnd).deviceId == selectedMonitor.deviceId)
                        return wnd;
                }
                return IntPtr.Zero;
            }

            if (mainHwnd != IntPtr.Zero)
            {
                uint threadId = GetWindowThreadProcessId(mainHwnd, IntPtr.Zero);
                EnumThreadWindows(threadId, (wnd, param) =>
                {
                    secondHwnd = checkWindowClass(wnd);
                    if (secondHwnd != IntPtr.Zero) return false;
                    return true;
                }, IntPtr.Zero);
                if (secondHwnd != IntPtr.Zero)
                {
                    _cachedTaskbarHandle = secondHwnd;
                    _cachedIsMainTaskbarSelected = false;
                    return secondHwnd;
                }
            }

            EnumWindows((wnd, param) =>
            {
                secondHwnd = checkWindowClass(wnd);
                if (secondHwnd != IntPtr.Zero) return false;
                return true;
            }, IntPtr.Zero);

            if (secondHwnd != IntPtr.Zero)
            {
                _cachedTaskbarHandle = secondHwnd;
                _cachedIsMainTaskbarSelected = false;
                return secondHwnd;
            }

            isMainTaskbarSelected = true;
            _cachedTaskbarHandle = mainHwnd;
            _cachedIsMainTaskbarSelected = true;
            return mainHwnd;
        }

        // ═══ Auto-hide detection via Shell AppBar API ═══
        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        private const uint ABM_GETSTATE = 0x04;
        private const int ABS_AUTOHIDE = 0x01;

        private bool IsTaskbarAutoHideEnabled()
        {
            try
            {
                var abd = new APPBARDATA();
                abd.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                IntPtr result = SHAppBarMessage(ABM_GETSTATE, ref abd);
                return (result.ToInt32() & ABS_AUTOHIDE) != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Switches widget from embedded (child of taskbar) to floating (standalone topmost window).
        /// </summary>
        private void SwitchToFloatingMode(IntPtr widgetHwnd)
        {
            if (_isFloatingMode) return;
            _isFloatingMode = true;

            // Un-parent from taskbar
            SetParent(widgetHwnd, IntPtr.Zero);

            // Restore popup style (remove WS_CHILD, add WS_POPUP)
            int style = GetWindowLong(widgetHwnd, GWL_STYLE);
            style = (style & ~WS_CHILD) | WS_POPUP;
            SetWindowLong(widgetHwnd, GWL_STYLE, style);

            // Keep tool window
            int exStyle = GetWindowLong(widgetHwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(widgetHwnd, GWL_EXSTYLE, exStyle);

            // Invalidate ALL caches to force complete recalculation
            _lastWidgetLeft = -1;
            _lastWidgetTop = -1;
            _lastWidgetW = -1;
            _lastWidgetH = -1;
            _cachedFreeZoneLeft = -1;
            _lastFreeZoneScan = DateTime.MinValue;
            _lastTaskbarWidth = -1;
            _lastTaskbarHeight = -1;
            _lastTaskbarFrameRect = Rect.Empty;
            _positionUpdateInProgress = false;

            Classes.Logger.LogAction("WIDGET", "Switched to FLOATING mode (taskbar auto-hide detected)");
        }

        /// <summary>
        /// Switches widget from floating back to embedded (child of taskbar).
        /// </summary>
        private void SwitchToEmbeddedMode(IntPtr widgetHwnd, IntPtr taskbarHandle)
        {
            if (!_isFloatingMode) return;
            _isFloatingMode = false;

            // Re-parent to taskbar
            int style = GetWindowLong(widgetHwnd, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLong(widgetHwnd, GWL_STYLE, style);

            int exStyle = GetWindowLong(widgetHwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(widgetHwnd, GWL_EXSTYLE, exStyle);

            SetParent(widgetHwnd, taskbarHandle);

            // Force the native window to be visible after re-parenting
            const int SW_SHOWNOACTIVATE = 8;
            NativeMethods.ShowWindow(widgetHwnd, SW_SHOWNOACTIVATE);

            // Invalidate ALL caches so CalculateAndSetPosition starts completely fresh
            // (taskbar dimensions change between auto-hide and normal mode)
            _lastWidgetLeft = -1;
            _lastWidgetTop = -1;
            _lastWidgetW = -1;
            _lastWidgetH = -1;
            _cachedFreeZoneLeft = -1;
            _lastFreeZoneScan = DateTime.MinValue;
            _lastTaskbarWidth = -1;
            _lastTaskbarHeight = -1;
            _lastTaskbarFrameRect = Rect.Empty;
            _cachedWidgetsButtonRight = -1;
            _lastWidgetsButtonScan = DateTime.MinValue;
            _positionUpdateInProgress = false;

            // Force WPF visibility
            Visibility = Visibility.Visible;

            Classes.Logger.LogAction("WIDGET", "Switched to EMBEDDED mode (taskbar auto-hide disabled) — all caches reset");
        }

        private void SetupWindow()
        {
            if (_isClosed) return;
            try
            {
                var interop = new WindowInteropHelper(this);
                IntPtr taskbarWindowHandle = interop.Handle;
                if (taskbarWindowHandle == IntPtr.Zero)
                {
                    Classes.Logger.LogAction("WIDGET", "SetupWindow: taskbarWindowHandle is Zero");
                    return;
                }

                IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);
                Classes.Logger.LogAction("WIDGET", $"SetupWindow: widgetHwnd={taskbarWindowHandle}, taskbarHwnd={taskbarHandle}, isMain={isMainTaskbarSelected}");

                if (taskbarHandle == IntPtr.Zero)
                {
                    Classes.Logger.LogAction("WIDGET", "ERROR: Could not find taskbar window handle — widget will not embed");
                    return;
                }

                bool autoHide = IsTaskbarAutoHideEnabled();
                if (autoHide)
                {
                    // Taskbar auto-hide is ON — hide the widget entirely
                    _isFloatingMode = true; // Track state so we know to re-embed when auto-hide turns off
                    Visibility = Visibility.Hidden;
                    Classes.Logger.LogAction("WIDGET", "SetupWindow: taskbar auto-hide ON — widget hidden");
                }
                else
                {
                    _isFloatingMode = false;

                    int style = GetWindowLong(taskbarWindowHandle, GWL_STYLE);
                    style = (style & ~WS_POPUP) | WS_CHILD;
                    SetWindowLong(taskbarWindowHandle, GWL_STYLE, style);

                    int exStyle = GetWindowLong(taskbarWindowHandle, GWL_EXSTYLE);
                    exStyle |= WS_EX_TOOLWINDOW;
                    SetWindowLong(taskbarWindowHandle, GWL_EXSTYLE, exStyle);

                    SetParent(taskbarWindowHandle, taskbarHandle);

                    CalculateAndSetPosition(taskbarHandle, taskbarWindowHandle);
                    Classes.Logger.LogAction("WIDGET", "SetupWindow complete — widget embedded in taskbar");
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET", $"SetupWindow FAILED: {ex.Message}");
            }
        }

        private void UpdateWindowRegion(IntPtr windowHandle, params Rect[] rects)
        {
            if (_isClosed) return;
            try
            {
                IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
                foreach (var r in rects)
                {
                    if (r == Rect.Empty) continue;
                    IntPtr newRgn = CreateRectRgn((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
                    if (newRgn != IntPtr.Zero)
                    {
                        CombineRgn(rgn, rgn, newRgn, 2);
                        DeleteObject(newRgn);
                    }
                }
                SetWindowRgn(windowHandle, rgn, true);
            }
            catch { }
        }

        private void UpdatePosition()
        {
            if (_isClosed) return;
            if (!SettingsManager.Current.EnableTaskbarWidget)
            {
                if (Visibility != Visibility.Hidden)
                    Visibility = Visibility.Hidden;
                return;
            }

            try
            {
                var interop = new WindowInteropHelper(this);
                IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);

                if (interop.Handle == IntPtr.Zero) return;

                // Dynamically detect auto-hide state changes
                bool autoHide = IsTaskbarAutoHideEnabled();

                if (autoHide && !_isFloatingMode)
                {
                    // Switched to auto-hide — hide the widget entirely
                    _isFloatingMode = true;
                    Visibility = Visibility.Hidden;
                    Classes.Logger.LogAction("WIDGET", "Auto-hide enabled — widget hidden");
                    return;
                }
                else if (!autoHide && _isFloatingMode)
                {
                    // Auto-hide turned off — re-show and re-embed in taskbar
                    _isFloatingMode = false;
                    Visibility = Visibility.Visible;
                    if (taskbarHandle != IntPtr.Zero)
                    {
                        SetupWindow(); // Re-setup in embedded mode
                    }
                    Classes.Logger.LogAction("WIDGET", "Auto-hide disabled — widget re-shown and re-embedded");
                }

                if (_isFloatingMode)
                {
                    // Auto-hide is on — keep widget hidden
                    if (Visibility != Visibility.Hidden)
                        Visibility = Visibility.Hidden;
                    return;
                }
                else
                {
                    // Embedded mode: normal taskbar child positioning
                    IntPtr currentParent = GetParent(interop.Handle);
                    if (currentParent == IntPtr.Zero || (currentParent != taskbarHandle && !NativeMethods.IsChild(taskbarHandle, currentParent)))
                    {
                        SetParent(interop.Handle, taskbarHandle);
                    }

                    if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
                    {
                        Dispatcher.BeginInvoke(() => { CalculateAndSetPosition(taskbarHandle, interop.Handle); }, DispatcherPriority.Background);
                    }
                }
            }
            catch { }
        }

        public void ForceReposition()
        {
            if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;

            // Invalidate all position/size caches to force a layout recalculation
            _lastWidgetLeft = -1;
            _lastWidgetTop = -1;
            _lastWidgetW = -1;
            _lastWidgetH = -1;
            _cachedFreeZoneLeft = -1;
            _lastFreeZoneScan = DateTime.MinValue;
            _lastTaskbarWidth = -1;
            _lastTaskbarHeight = -1;
            _lastTaskbarFrameRect = Rect.Empty;
            _cachedWidgetsButtonRight = -1;
            _lastWidgetsButtonScan = DateTime.MinValue;

            // Trigger the initial update
            UpdatePosition();

            // Schedule a series of subsequent updates to guarantee correct placement
            // as Windows transitions the taskbar animation
            ScheduleDelayedUpdates();
        }

        private void ScheduleDelayedUpdates()
        {
            int[] delays = { 100, 350, 700, 1100, 1600 };
            foreach (var delay in delays)
            {
                Task.Delay(delay).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;

                        _lastWidgetLeft = -1;
                        _lastWidgetTop = -1;
                        _lastWidgetW = -1;
                        _lastWidgetH = -1;
                        _cachedFreeZoneLeft = -1;
                        _lastFreeZoneScan = DateTime.MinValue;
                        _lastTaskbarWidth = -1;
                        _lastTaskbarHeight = -1;
                        _lastTaskbarFrameRect = Rect.Empty;

                        UpdatePosition();
                    }), DispatcherPriority.Background);
                });
            }
        }

        private void SystemEvents_UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (_isClosed) return;
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General ||
                e.Category == Microsoft.Win32.UserPreferenceCategory.Policy ||
                e.Category == Microsoft.Win32.UserPreferenceCategory.Window)
            {
                Classes.Logger.LogAction("WIDGET", $"UserPreferenceChanged ({e.Category}) detected — forcing immediate reposition");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ForceReposition();
                }), DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Checks if the auto-hide taskbar is currently visible (slid into view) by comparing
        /// its screen position to the monitor edge. When hidden, the taskbar is positioned
        /// mostly off-screen (only 2px visible at the edge).
        /// </summary>
        private bool IsTaskbarCurrentlyVisible(IntPtr taskbarHandle)
        {
            try
            {
                GetWindowRect(taskbarHandle, out RECT taskbarRect);
                var monitor = MonitorUtil.GetMonitor(taskbarHandle);
                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

                // The taskbar is at the bottom. When hidden, its Top is near monitorArea.Bottom
                // (only ~2px peeking). When visible, its Top is well above the bottom.
                // Consider it visible if more than 10px of the taskbar is showing.
                int visiblePixels = (int)monitor.monitorArea.Bottom - taskbarRect.Top;
                return visiblePixels > 10;
            }
            catch { return true; }
        }

        /// <summary>
        /// Positions the widget as a standalone floating window at the bottom edge of the screen.
        /// Used when the taskbar has auto-hide enabled — the widget stays visible at the screen edge
        /// so the user can always click it.
        /// </summary>
        private void CalculateFloatingPosition(IntPtr widgetHwnd, IntPtr taskbarHandle)
        {
            if (_isClosed) return;
            if (_positionUpdateInProgress) return;
            _positionUpdateInProgress = true;

            try
            {
                double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
                if (dpiScale <= 0) dpiScale = 1.0;

                var (logicalWidth, logicalHeight) = Widget.CalculateSize(dpiScale);
                int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
                int physicalHeight = (int)(logicalHeight * dpiScale);

                // Get monitor bounds
                var monitor = MonitorUtil.GetMonitor(taskbarHandle);
                Rect monitorArea = monitor.monitorArea;

                // Detect taskbar alignment for left/right positioning
                bool isTaskbarCentered = true;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    {
                        if (key != null)
                        {
                            var valAl = key.GetValue("TaskbarAl");
                            if (valAl != null && Convert.ToInt32(valAl) == 0)
                                isTaskbarCentered = false;
                        }
                    }
                }
                catch { }

                // Position at the very bottom of the screen, flush with the edge
                int screenX;
                int alignment = SettingsManager.Current.WidgetTaskbarAlignment;
                if (alignment == -1)
                {
                    if (isTaskbarCentered)
                    {
                        // Left side — after the widgets area
                        screenX = (int)monitorArea.Left + 8;
                    }
                    else
                    {
                        // Right side — beside where system tray would be
                        screenX = (int)(monitorArea.Right) - physicalWidth - 8;
                    }
                }
                else if (alignment == 0) // Far Left
                {
                    screenX = (int)monitorArea.Left + 8;
                }
                else if (alignment == 1) // After Start
                {
                    if (isTaskbarCentered)
                    {
                        screenX = (int)monitorArea.Left + (int)(160 * dpiScale) + 8;
                    }
                    else
                    {
                        screenX = (int)monitorArea.Left + (int)(180 * dpiScale) + 8;
                    }
                }
                else if (alignment == 2) // Before Tray
                {
                    screenX = (int)(monitorArea.Right) - physicalWidth - 8;
                }
                else // 3 (Custom Percentage)
                {
                    int range = (int)(monitorArea.Width - physicalWidth);
                    if (range < 0) range = 0;
                    screenX = (int)monitorArea.Left + (int)((SettingsManager.Current.WidgetHorizontalOffset / 100.0) * range);
                }

                int screenY = (int)(monitorArea.Bottom) - physicalHeight;

                // Size the widget WPF controls
                Widget.Width = physicalWidth / dpiScale;
                Widget.Height = physicalHeight / dpiScale;
                double targetLogicalWidth = physicalWidth / dpiScale;
                double targetLogicalHeight = physicalHeight / dpiScale;
                if (Math.Abs(this.Width - targetLogicalWidth) > 0.01)
                    this.Width = targetLogicalWidth;
                if (Math.Abs(this.Height - targetLogicalHeight) > 0.01)
                    this.Height = targetLogicalHeight;

                // HWND_BOTTOM = 1 to keep behind all app windows (like the taskbar when auto-hidden)
                const int HWND_BOTTOM = 1;

                // Apply user's manual horizontal offset (only for presets, not custom percentage)
                if (alignment != 3)
                {
                    screenX += SettingsManager.Current.WidgetHorizontalOffset;
                }

                if (screenX != _lastWidgetLeft || screenY != _lastWidgetTop ||
                    physicalWidth != _lastWidgetW || physicalHeight != _lastWidgetH)
                {
                    SetWindowPos(widgetHwnd, HWND_BOTTOM,
                             screenX, screenY,
                             physicalWidth, physicalHeight,
                             SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    _lastWidgetLeft = screenX;
                    _lastWidgetTop = screenY;
                    _lastWidgetW = physicalWidth;
                    _lastWidgetH = physicalHeight;
                }
                else
                {
                    // Keep behind other windows
                    SetWindowPos(widgetHwnd, HWND_BOTTOM, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }

                Visibility = Visibility.Visible;
            }
            catch { }
            finally
            {
                _positionUpdateInProgress = false;
            }
        }

        private void CalculateAndSetPosition(IntPtr taskbarHandle, IntPtr taskbarWindowHandle)
        {
            if (_isClosed) return;
            if (_positionUpdateInProgress) return;
            _positionUpdateInProgress = true;

            try
            {
                double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
                if (dpiScale <= 0) dpiScale = 1.0;

                GetWindowRect(taskbarHandle, out RECT rawTaskbarRect);
                int currentWidth = rawTaskbarRect.Right - rawTaskbarRect.Left;
                int currentHeight = rawTaskbarRect.Bottom - rawTaskbarRect.Top;

                if (currentHeight < 25)
                {
                    _positionUpdateInProgress = false;
                    return;
                }

                bool isSizeChanged = currentWidth != _lastTaskbarWidth;
                _lastTaskbarWidth = currentWidth;
                _lastTaskbarHeight = currentHeight;

                if (isSizeChanged)
                {
                    _cachedFreeZoneLeft = -1;
                    _lastFreeZoneScan = DateTime.MinValue;
                }

                if (isSizeChanged || _lastTaskbarFrameRect == Rect.Empty)
                {
                    (bool success, Rect result) = GetTaskbarFrameRect(taskbarHandle);
                    if (success) _lastTaskbarFrameRect = result;
                    else _lastTaskbarFrameRect = new Rect(rawTaskbarRect.Left, rawTaskbarRect.Top, currentWidth, currentHeight);
                }

                RECT taskbarRect = new RECT
                {
                    Left = rawTaskbarRect.Left,
                    Top = rawTaskbarRect.Top,
                    Right = rawTaskbarRect.Left + (int)_lastTaskbarFrameRect.Width,
                    Bottom = rawTaskbarRect.Top + (int)_lastTaskbarFrameRect.Height
                };

                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
                int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

                // Calculate the widget's physical dimensions
                var (logicalWidth, logicalHeight) = Widget.CalculateSize(dpiScale);
                int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
                int physicalHeight = (int)(logicalHeight * dpiScale);

                // Get the exact monitor bounds and work area in physical pixels to calculate visible area
                var monitor = MonitorUtil.GetMonitor(taskbarHandle);
                Rect monitorArea = monitor.monitorArea;
                Rect workArea = monitor.workArea;

                double solidTop = monitorArea.Top;
                double solidHeight = monitorArea.Height;

                if (workArea.Top > monitorArea.Top) // Top taskbar
                {
                    solidHeight = workArea.Top - monitorArea.Top;
                }
                else if (workArea.Bottom < monitorArea.Bottom) // Bottom taskbar
                {
                    solidTop = workArea.Bottom;
                    solidHeight = monitorArea.Bottom - workArea.Bottom;
                }

                // If orientation-based solid taskbar height is not detected or extremely small, fallback
                if (solidHeight < 20)
                {
                    solidTop = rawTaskbarRect.Top;
                    solidHeight = currentHeight;
                }

                // Position widget relative to the taskbar client area
                var (widgetLeft, _) = FindTaskbarFreeZone(taskbarHandle, taskbarWidth, dpiScale, physicalWidth);

                // Calculate screen Y coordinate to center the widget vertically inside the solid visible taskbar
                double screenY = solidTop + (solidHeight - physicalHeight) / 2.0;

                // Convert screen Y coordinate to taskbar window client coordinates
                POINT containerPos = new() { X = 0, Y = (int)screenY };
                ScreenToClient(taskbarHandle, ref containerPos);

                // Set parent-local coordinates: X is from FindTaskbarFreeZone, Y is mapped client coordinate
                containerPos.X = widgetLeft;

                // Get the actual parent window handle (handles third-party customizer re-parenting)
                IntPtr actualParent = GetParent(taskbarWindowHandle);
                if (actualParent == IntPtr.Zero)
                {
                    actualParent = taskbarHandle; // Fallback to taskbar handle
                }

                // If actual parent is different from the target taskbarHandle, translate the coordinates!
                if (actualParent != taskbarHandle)
                {
                    // Convert taskbarHandle client coordinates to absolute screen coordinates
                    NativeMethods.ClientToScreen(taskbarHandle, ref containerPos);
                    // Convert screen coordinates to actual parent client coordinates
                    NativeMethods.ScreenToClient(actualParent, ref containerPos);
                }

                // Size the widget WPF controls to match exactly (to avoid WPF layout loops)
                Widget.Width = physicalWidth / dpiScale;
                Widget.Height = physicalHeight / dpiScale;
                
                double targetLogicalWidth = physicalWidth / dpiScale;
                double targetLogicalHeight = physicalHeight / dpiScale;
                if (Math.Abs(this.Width - targetLogicalWidth) > 0.01)
                    this.Width = targetLogicalWidth;
                if (Math.Abs(this.Height - targetLogicalHeight) > 0.01)
                    this.Height = targetLogicalHeight;

                // Apply user's manual horizontal offset (only for presets, not custom percentage)
                if (SettingsManager.Current.WidgetTaskbarAlignment != 3)
                {
                    containerPos.X += SettingsManager.Current.WidgetHorizontalOffset;
                }

                // Only call SetWindowPos if the container position/size actually changed — avoids flicker
                if (containerPos.X != _lastWidgetLeft || containerPos.Y != _lastWidgetTop ||
                    physicalWidth != _lastWidgetW || physicalHeight != _lastWidgetH)
                {
                    SetWindowPos(taskbarWindowHandle, 0,
                             containerPos.X, containerPos.Y,
                             physicalWidth, physicalHeight,
                             SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);
                    _lastWidgetLeft = containerPos.X;
                    _lastWidgetTop = containerPos.Y;
                    _lastWidgetW = physicalWidth;
                    _lastWidgetH = physicalHeight;
                    Classes.Logger.LogAction("WIDGET", $"SetWindowPos (size/pos changed): containerPos.X={containerPos.X}, containerPos.Y={containerPos.Y}, physicalWidth={physicalWidth}, physicalHeight={physicalHeight}");
                }
                else
                {
                    // Periodically force Z-order to top of taskbar siblings to prevent being buried
                    SetWindowPos(taskbarWindowHandle, 0, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);
                }

                Visibility = Visibility.Visible;
            }
            catch { }
            finally
            {
                _positionUpdateInProgress = false;
            }
        }

        /// <summary>
        /// Finds a free clickable zone on the taskbar by scanning for the gap between
        /// the Start/Search/Widgets area and the system tray.
        /// Uses Win32 child-window enumeration to detect occupied regions,
        /// then places the widget in the remaining free space.
        /// Falls back to the user's alignment preference if detection fails.
        /// </summary>
        private (int left, int width) FindTaskbarFreeZone(IntPtr taskbarHandle, int taskbarWidth, double dpiScale, int physicalWidth)
        {
            // Detect if Windows 11 taskbar icons are centered using the registry key
            bool isTaskbarCentered = false;
            bool isWidgetsVisible = true;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    if (key != null)
                    {
                        var valAl = key.GetValue("TaskbarAl");
                        if (valAl != null && Convert.ToInt32(valAl) == 1)
                        {
                            isTaskbarCentered = true;
                        }

                        var valDa = key.GetValue("TaskbarDa");
                        if (valDa != null && Convert.ToInt32(valDa) == 0)
                        {
                            isWidgetsVisible = false;
                        }
                    }
                }
            }
            catch { }

            // If alignment or widgets visibility changed since last scan, invalidate the cache to reposition instantly
            if (isTaskbarCentered != _lastCachedTaskbarCentered || isWidgetsVisible != _lastCachedWidgetsVisible)
            {
                _cachedFreeZoneLeft = -1;
                _lastFreeZoneScan = DateTime.MinValue;
                _cachedWidgetsButtonRight = -1;
                _lastWidgetsButtonScan = DateTime.MinValue;
                _lastCachedTaskbarCentered = isTaskbarCentered;
                _lastCachedWidgetsVisible = isWidgetsVisible;
            }

            // Cache for 5 seconds to avoid expensive enumeration every 500ms
            if (_cachedFreeZoneLeft >= 0 && (DateTime.Now - _lastFreeZoneScan).TotalSeconds < 5)
                return (_cachedFreeZoneLeft, _cachedFreeZoneWidth);

            int alignment = SettingsManager.Current.WidgetTaskbarAlignment;
            if (alignment != -1)
            {
                int targetLeft = 0;
                if (alignment == 0) // Far Left
                {
                    targetLeft = (int)(8 * dpiScale);
                }
                else if (alignment == 1) // After Start
                {
                    if (isTaskbarCentered)
                    {
                        int widgetsRight = DetectWidgetsButtonRight(taskbarHandle);
                        targetLeft = (widgetsRight > 0 ? widgetsRight : (int)(160 * dpiScale)) + 8;
                    }
                    else
                    {
                        targetLeft = (int)(180 * dpiScale) + 8;
                    }
                }
                else if (alignment == 2) // Before Tray
                {
                    IntPtr trayHwnd = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (trayHwnd != IntPtr.Zero)
                    {
                        GetWindowRect(trayHwnd, out RECT trayRect);
                        POINT trayPt = new POINT { X = trayRect.Left, Y = trayRect.Top };
                        ScreenToClient(taskbarHandle, ref trayPt);
                        targetLeft = trayPt.X - physicalWidth - 8;
                    }
                    else
                    {
                        targetLeft = taskbarWidth - physicalWidth - (int)(12 * dpiScale) - 8;
                    }
                }
                else // 3 (Custom Percentage)
                {
                    int range = taskbarWidth - physicalWidth;
                    if (range < 0) range = 0;
                    targetLeft = (int)((SettingsManager.Current.WidgetHorizontalOffset / 100.0) * range);
                }

                _cachedFreeZoneLeft = targetLeft;
                _cachedFreeZoneWidth = physicalWidth;
                _lastFreeZoneScan = DateTime.Now;
                return (targetLeft, physicalWidth);
            }

            // FAST PATH: When centered taskbar + Widgets button OFF, skip unreliable EnumChildWindows
            // gap detection entirely. Win11's XAML Islands taskbar renders all icons inside a single
            // DesktopWindowContentBridge that spans full width — EnumChildWindows can't see individual
            // buttons, so gap detection produces wrong results on many machines (Lenovo ThinkPad AI, etc.).
            // Instead, use a safe direct position at the far-left corner of the taskbar.
            if (isTaskbarCentered && !isWidgetsVisible)
            {
                int safeLeft = (int)(12 * dpiScale) + 8;
                _cachedFreeZoneLeft = safeLeft;
                _cachedFreeZoneWidth = physicalWidth;
                _lastFreeZoneScan = DateTime.Now;
                Classes.Logger.LogAction("WIDGET", $"FindTaskbarFreeZone: FAST PATH (centered + no widgets) → left={safeLeft}");
                return (safeLeft, physicalWidth);
            }

            try
            {
                IntPtr myHwnd = new WindowInteropHelper(this).Handle;
                var occupiedZones = new List<(int left, int right)>();

                // Protect the Start/Search area if left-aligned, or the Widgets corner if centered (only if Widgets button is visible)
                if (!isTaskbarCentered)
                {
                    occupiedZones.Add((0, (int)(180 * dpiScale)));
                }
                else
                {
                    if (isWidgetsVisible)
                    {
                        // Use UI Automation to detect the actual Widgets button width
                        int widgetsRight = DetectWidgetsButtonRight(taskbarHandle);
                        if (widgetsRight > 0)
                        {
                            occupiedZones.Add((0, widgetsRight));
                        }
                        else
                        {
                            // Fallback: conservative estimate if UI Automation fails
                            occupiedZones.Add((0, (int)(160 * dpiScale)));
                        }
                    }
                    else
                    {
                        occupiedZones.Add((0, (int)(12 * dpiScale))); // Just protect the leftmost margin if Widgets is disabled
                    }
                }

                Classes.Logger.LogAction("WIDGET", $"FindTaskbarFreeZone: Scanning child windows of taskbarHandle={taskbarHandle}, taskbarWidth={taskbarWidth}");

                EnumChildWindows(taskbarHandle, (hwnd, lParam) =>
                {
                    // Only consider visible
                    if (!IsWindowVisible(hwnd))
                        return true;

                    if (hwnd == myHwnd)
                        return true;

                    var parentHwnd = GetParent(hwnd);
                    StringBuilder className = new(256);
                    GetClassName(hwnd, className, className.Capacity);
                    string clsName = className.ToString();

                    GetWindowRect(hwnd, out RECT childRect);
                    
                    // Convert to taskbar-local coordinates
                    POINT childPt = new() { X = childRect.Left, Y = childRect.Top };
                    ScreenToClient(taskbarHandle, ref childPt);
                    int childWidth = childRect.Right - childRect.Left;
                    int childRight = childPt.X + childWidth;
                    
                    // Skip containers spanning the entire taskbar (like DesktopWindowContentBridge)
                    if (childWidth >= taskbarWidth - 50)
                        return true;

                    Classes.Logger.LogAction("WIDGET_ENUM", $"hwnd={hwnd}, class='{clsName}', parent={parentHwnd}, left={childPt.X}, width={childWidth}, right={childRight}");

                    if (childWidth > 5) // Skip tiny/invisible elements
                    {
                        // Record the occupied zone of the taskbar buttons or direct child windows
                        if (clsName == "MSTaskSwWClass" || 
                            clsName == "MSTaskListWClass" || 
                            clsName == "ReBarWindow32" || 
                            clsName == "TrayNotifyWnd" ||
                            clsName == "Button" ||
                            clsName == "TrayButton" ||
                            clsName.Contains("Search") ||
                            clsName.Contains("DesktopWindowContentBridge") ||
                            parentHwnd == taskbarHandle)
                        {
                            occupiedZones.Add((childPt.X, childRight));
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (occupiedZones.Count > 0)
                {
                    // Sort by left position
                    occupiedZones.Sort((a, b) => a.left.CompareTo(b.left));

                    // Merge overlapping zones to simplify gap detection
                    var mergedZones = new List<(int left, int right)>();
                    var current = occupiedZones[0];
                    for (int i = 1; i < occupiedZones.Count; i++)
                    {
                        var next = occupiedZones[i];
                        if (next.left <= current.right)
                        {
                            current.right = Math.Max(current.right, next.right);
                        }
                        else
                        {
                            mergedZones.Add(current);
                            current = next;
                        }
                    }
                    mergedZones.Add(current);

                    // Find all gaps between merged zones that are wide enough to fit the widget
                    var gaps = new List<(int left, int width)>();
                    int lastRight = 0;
                    
                    foreach (var zone in mergedZones)
                    {
                        int gapWidth = zone.left - lastRight;
                        if (gapWidth >= physicalWidth + 16)
                        {
                            gaps.Add((lastRight, gapWidth));
                        }
                        if (zone.right > lastRight)
                            lastRight = zone.right;
                    }
                    
                    // Also check gap after last occupied zone to end of taskbar
                    int trailingGap = taskbarWidth - lastRight;
                    if (trailingGap >= physicalWidth + 16)
                    {
                        gaps.Add((lastRight, trailingGap));
                    }

                    Classes.Logger.LogAction("WIDGET", $"FindTaskbarFreeZone: occupiedZones merged={mergedZones.Count}, gaps count={gaps.Count}");

                    // Dynamic positioning based on Windows taskbar alignment:
                    // - Centered taskbar → widget goes to the LEFT (leftmost gap)
                    // - Left-aligned taskbar → widget goes to the RIGHT (beside system tray)
                    int effectiveAlign;
                    if (isTaskbarCentered)
                    {
                        effectiveAlign = 0; // Left — the left side is free when taskbar icons are centered
                    }
                    else
                    {
                        effectiveAlign = 2; // Right — left side is occupied by Start/Search when left-aligned
                    }
                    
                    if (gaps.Count > 0)
                    {
                        (int left, int width) selectedGap;
                        int widgetPos = 0;

                        if (effectiveAlign == 0) // Left: pick the leftmost gap
                        {
                            selectedGap = gaps[0];
                            widgetPos = selectedGap.left + 8;
                        }
                        else // Right: pick the rightmost gap (beside system tray)
                        {
                            selectedGap = gaps[gaps.Count - 1];
                            widgetPos = selectedGap.left + selectedGap.width - physicalWidth - 8;
                        }

                        _cachedFreeZoneLeft = widgetPos;
                        _cachedFreeZoneWidth = physicalWidth;
                        _lastFreeZoneScan = DateTime.Now;
                        
                        Classes.Logger.LogAction("WIDGET", $"FindTaskbarFreeZone: taskbarCentered={isTaskbarCentered}, effectiveAlign={effectiveAlign}, widgetPos={widgetPos}");
                        return (widgetPos, physicalWidth);
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET", $"Free zone detection failed: {ex.Message}");
            }

            // Fallback: full taskbar width with padding, using dynamic alignment
            int fallbackLeft = 12;
            int fallbackWidth = taskbarWidth - 24;
            int widgetLeft = 0;

            if (isTaskbarCentered)
            {
                if (isWidgetsVisible)
                {
                    widgetLeft = (int)(320 * dpiScale) + 8; // Left side, safely cleared of Widgets weather/news text
                }
                else
                {
                    widgetLeft = fallbackLeft + 8; // Just protect standard margin if disabled
                }
            }
            else
            {
                widgetLeft = fallbackLeft + fallbackWidth - physicalWidth - 8; // Right side (beside tray)
            }

            _cachedFreeZoneLeft = widgetLeft;
            _cachedFreeZoneWidth = physicalWidth;
            _lastFreeZoneScan = DateTime.Now;
            return (widgetLeft, physicalWidth);
        }

        /// <summary>
        /// Uses UI Automation to detect the actual right edge of the Windows 11 Widgets button
        /// (the weather/news toggle on the far left of the taskbar). The Widgets button is rendered
        /// via XAML Islands and has no traditional HWND, so standard window enumeration cannot detect it.
        /// The result is cached for 30 seconds since the button size changes infrequently.
        /// </summary>
        private int DetectWidgetsButtonRight(IntPtr taskbarHandle)
        {
            // Return cached value if still fresh (30 seconds — Widgets button size rarely changes)
            if (_cachedWidgetsButtonRight > 0 && (DateTime.Now - _lastWidgetsButtonScan).TotalSeconds < 30)
                return _cachedWidgetsButtonRight;

            try
            {
                var taskbarElement = AutomationElement.FromHandle(taskbarHandle);
                if (taskbarElement == null) return _cachedWidgetsButtonRight > 0 ? _cachedWidgetsButtonRight : -1;

                // Find the Widgets button by its AutomationId ('WidgetsButton')
                var widgetsButton = taskbarElement.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton")
                );

                if (widgetsButton != null)
                {
                    var bounds = widgetsButton.Current.BoundingRectangle;
                    if (!bounds.IsEmpty && bounds.Width > 0)
                    {
                        // Convert screen-space right edge to taskbar-local coordinate
                        POINT pt = new() { X = (int)(bounds.Left + bounds.Width), Y = (int)bounds.Top };
                        ScreenToClient(taskbarHandle, ref pt);
                        _cachedWidgetsButtonRight = pt.X;
                        _lastWidgetsButtonScan = DateTime.Now;

                        Classes.Logger.LogAction("WIDGET", $"DetectWidgetsButtonRight: UIA detected WidgetsButton right={pt.X} (screen: L={bounds.Left}, W={bounds.Width})");
                        return _cachedWidgetsButtonRight;
                    }
                }

                Classes.Logger.LogAction("WIDGET", "DetectWidgetsButtonRight: WidgetsButton not found via UIA");
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET", $"DetectWidgetsButtonRight failed: {ex.Message}");
            }

            return _cachedWidgetsButtonRight > 0 ? _cachedWidgetsButtonRight : -1;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        public Point GetWidgetScreenPosition()
        {
            if (_isClosed) return new Point(-1, -1);
            try
            {
                var interop = new WindowInteropHelper(this);
                IntPtr taskbarWindowHandle = interop.Handle;
                IntPtr taskbarHandle = _cachedTaskbarHandle != IntPtr.Zero && IsWindow(_cachedTaskbarHandle)
                     ? _cachedTaskbarHandle
                     : GetSelectedTaskbarHandle(out _);
                if (taskbarHandle != IntPtr.Zero && taskbarWindowHandle != IntPtr.Zero)
                {
                    // Check if the widget is actually embedded in the taskbar!
                    IntPtr parent = NativeMethods.GetParent(taskbarWindowHandle);
                    if (parent == IntPtr.Zero && !_isFloatingMode)
                    {
                        // Not yet embedded and not in floating mode — position is not yet valid!
                        return new Point(-1, -1);
                    }

                    double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
                    if (dpiScale <= 0) dpiScale = 1.0;

                    GetWindowRect(taskbarWindowHandle, out RECT rect);
                    
                    // If the position is at (0, 0), it's also invalid (not yet positioned/embedded on startup)
                    if (rect.Left == 0 && rect.Top == 0)
                    {
                        return new Point(-1, -1);
                    }

                    double physicalWidth = rect.Right - rect.Left;
                    double physicalCenterX = rect.Left + (physicalWidth / 2.0);

                    double logicalCenterX = physicalCenterX / dpiScale;
                    double logicalTopY = rect.Top / dpiScale;

                    return new Point(logicalCenterX, logicalTopY);
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET_POS_ERR", ex.ToString());
            }

            return new Point(-1, -1);
        }

        private (bool, Rect) GetTaskbarFrameRect(IntPtr taskbarHandle)
        {
            GetWindowRect(taskbarHandle, out RECT rect);
            return (true, new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
    }
}