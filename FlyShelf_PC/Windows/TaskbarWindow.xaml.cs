using FlyShelf.Classes;
using FlyShelf.Classes.Utils;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

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
        private bool _isFloatingMode = true; // Always true — widget always floats as a standalone TOPMOST window

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
            SettingsManager.Current.PropertyChanged += OnSettingsPropertyChanged;

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
                                if (_isFloatingMode)
                                {
                                    Classes.Logger.LogAction("WIDGET", $"Startup setup succeeded on attempt {attempt}");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Classes.Logger.LogAction("WIDGET", $"Startup setup attempt {attempt} failed: {ex.Message}");
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
            try { _timer?.Stop(); } catch { } // Best-effort: failure is acceptable
            try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged; } catch { } // Best-effort: failure is acceptable
            try { SettingsManager.Current.PropertyChanged -= OnSettingsPropertyChanged; } catch { } // Best-effort: failure is acceptable
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
            catch { } // Best-effort: failure is acceptable
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
            catch { } // Best-effort: failure is acceptable
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
                if (className.ToString() == "Shell_SecondaryTrayWnd")
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
        /// Ensures widget styles are correct for floating docked mode and invalidates caches.
        /// Called when auto-hide state changes.
        /// </summary>
        private void RefreshFloatingStyles(IntPtr widgetHwnd)
        {
            // Ensure popup style (not child)
            int style = GetWindowLong(widgetHwnd, GWL_STYLE);
            style = (style & ~WS_CHILD) | WS_POPUP;
            SetWindowLong(widgetHwnd, GWL_STYLE, style);

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

            Classes.Logger.LogAction("WIDGET", "Refreshed floating styles and invalidated caches");
        }

        // SwitchToEmbeddedMode removed — widget always floats as a standalone TOPMOST window.

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
                    Classes.Logger.LogAction("WIDGET", "ERROR: Could not find taskbar window handle — widget will not dock");
                    return;
                }

                bool autoHide = IsTaskbarAutoHideEnabled();
                if (autoHide)
                {
                    // Taskbar auto-hide is ON — hide the widget entirely
                    Visibility = Visibility.Hidden;
                    Classes.Logger.LogAction("WIDGET", "SetupWindow: taskbar auto-hide ON — widget hidden");
                }
                else
                {
                    // Floating docked mode: standalone TOPMOST window positioned over the taskbar
                    int style = GetWindowLong(taskbarWindowHandle, GWL_STYLE);
                    style = (style & ~WS_CHILD) | WS_POPUP;
                    SetWindowLong(taskbarWindowHandle, GWL_STYLE, style);

                    int exStyle = GetWindowLong(taskbarWindowHandle, GWL_EXSTYLE);
                    exStyle |= WS_EX_TOOLWINDOW;
                    SetWindowLong(taskbarWindowHandle, GWL_EXSTYLE, exStyle);

                    CalculateFloatingPosition(taskbarWindowHandle, taskbarHandle);
                    Classes.Logger.LogAction("WIDGET", "SetupWindow complete — widget in floating docked mode");
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
            catch { } // Best-effort: failure is acceptable
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

                if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
                {
                    if (autoHide || IsForegroundFullScreen())
                    {
                        // Auto-hide or full-screen is on — hide the widget
                        if (Visibility != Visibility.Hidden)
                            Visibility = Visibility.Hidden;
                        return;
                    }

                    Dispatcher.BeginInvoke(() => { CalculateFloatingPosition(interop.Handle, taskbarHandle); }, DispatcherPriority.Background);
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private bool IsForegroundFullScreen()
        {
            try
            {
                IntPtr fgHandle = GetForegroundWindow();
                if (fgHandle == IntPtr.Zero) return false;

                // Don't hide if the foreground window is our own window
                var interop = new WindowInteropHelper(this);
                if (fgHandle == interop.Handle) return false;

                // Check window class to avoid hiding when desktop is focused
                StringBuilder className = new StringBuilder(256);
                GetClassName(fgHandle, className, className.Capacity);
                string cls = className.ToString();
                if (cls == "Progman" || cls == "WorkerW") return false;

                GetWindowRect(fgHandle, out RECT fgRect);
                var monitor = MonitorUtil.GetMonitor(fgHandle);

                int fgWidth = fgRect.Right - fgRect.Left;
                int fgHeight = fgRect.Bottom - fgRect.Top;

                int monWidth = (int)monitor.monitorArea.Width;
                int monHeight = (int)monitor.monitorArea.Height;

                // If the foreground window occupies the entire monitor area (or slightly more to account for borders)
                if (fgWidth >= monWidth && fgHeight >= monHeight &&
                    fgRect.Left <= monitor.monitorArea.Left &&
                    fgRect.Top <= monitor.monitorArea.Top)
                {
                    return true;
                }
            }
            catch { } // Best-effort: failure is acceptable
            return false;
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

        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
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

                        // Retry SetupWindow with delays — taskbar HWND may not be ready immediately
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            if (_isClosed || !SettingsManager.Current.EnableTaskbarWidget) return;
                            try
                            {
                                SetupWindow();
                                if (_isFloatingMode)
                                {
                                    Classes.Logger.LogAction("WIDGET", $"Toggle-ON setup succeeded on attempt {attempt}");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Classes.Logger.LogAction("WIDGET", $"Toggle-ON setup attempt {attempt} failed: {ex.Message}");
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
        /// Positions the widget as a standalone floating window docked to the taskbar region.
        /// Vertically centered inside the taskbar using absolute screen coordinates + HWND_TOPMOST.
        /// Default position: lower-left corner of the screen (just above the taskbar).
        /// User can adjust horizontal position via the settings slider.
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

                // Get actual taskbar rect and monitor bounds (same as embedded mode)
                GetWindowRect(taskbarHandle, out RECT rawTaskbarRect);
                int taskbarActualHeight = rawTaskbarRect.Bottom - rawTaskbarRect.Top;

                // Skip if taskbar is too small (collapsed/transitioning)
                if (taskbarActualHeight < 25)
                {
                    _positionUpdateInProgress = false;
                    return;
                }

                var monitor = MonitorUtil.GetMonitor(taskbarHandle);
                Rect monitorArea = monitor.monitorArea;
                Rect workArea = monitor.workArea;

                // ═══ Y POSITION — identical to embedded CalculateAndSetPosition ═══
                // Detect the solid taskbar region from the gap between monitorArea and workArea
                double solidTop = monitorArea.Top;
                double solidHeight = monitorArea.Height;

                if (workArea.Top > monitorArea.Top) // Top taskbar
                {
                    solidHeight = workArea.Top - monitorArea.Top;
                }
                else if (workArea.Bottom < monitorArea.Bottom) // Bottom taskbar (most common)
                {
                    solidTop = workArea.Bottom;
                    solidHeight = monitorArea.Bottom - workArea.Bottom;
                }

                // Fallback if solid region detection fails
                if (solidHeight < 20)
                {
                    solidTop = rawTaskbarRect.Top;
                    solidHeight = taskbarActualHeight;
                }

                // Center the widget vertically inside the solid taskbar region
                int screenY = (int)(solidTop + (solidHeight - physicalHeight) / 2.0);

                // ═══ X POSITION — default lower-left, respect user alignment ═══
                int screenX;
                int alignment = SettingsManager.Current.WidgetTaskbarAlignment;

                // Detect taskbar icon centering (Windows 11 setting)
                bool isTaskbarCentered = true;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    {
                        if (key != null)
                        {
                            var valAl = key.GetValue("TaskbarAl");
                            if (valAl != null && Convert.ToInt32(valAl, CultureInfo.InvariantCulture) == 0)
                                isTaskbarCentered = false;
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable

                if (alignment == -1) // Auto — default to lower-left
                {
                    // Always left for floating mode — consistent with the clipboard popup fallback
                    screenX = (int)monitorArea.Left + 8;
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

                // Apply user's manual horizontal offset (only for presets, not custom percentage)
                if (alignment != 3)
                {
                    screenX += SettingsManager.Current.WidgetHorizontalOffset;
                }

                // Size the widget WPF controls
                Widget.Width = physicalWidth / dpiScale;
                Widget.Height = physicalHeight / dpiScale;
                double targetLogicalWidth = physicalWidth / dpiScale;
                double targetLogicalHeight = physicalHeight / dpiScale;
                if (Math.Abs(this.Width - targetLogicalWidth) > 0.01)
                    this.Width = targetLogicalWidth;
                if (Math.Abs(this.Height - targetLogicalHeight) > 0.01)
                    this.Height = targetLogicalHeight;

                // ═══ Z-ORDER — TOPMOST to float above the taskbar surface ═══
                // The embedded widget sits on top of taskbar siblings via SetWindowPos(0),
                // for floating mode we use TOPMOST to achieve the same visibility.
                const int HWND_TOPMOST = -1;

                if (screenX != _lastWidgetLeft || screenY != _lastWidgetTop ||
                    physicalWidth != _lastWidgetW || physicalHeight != _lastWidgetH)
                {
                    SetWindowPos(widgetHwnd, HWND_TOPMOST,
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
                    // Maintain Z-order
                    SetWindowPos(widgetHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }

                Visibility = Visibility.Visible;
            }
            catch { } // Best-effort: failure is acceptable
            finally
            {
                _positionUpdateInProgress = false;
            }
        }

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
