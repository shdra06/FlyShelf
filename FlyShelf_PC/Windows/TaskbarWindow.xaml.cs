using FlyShelf.Classes;
using FlyShelf.Classes.Utils;
using System;
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

        // Position stability — avoid redundant SetWindowPos calls that cause flicker
        private int _lastWidgetLeft = -1;
        private int _lastWidgetTop = -1;
        private int _lastWidgetW = -1;
        private int _lastWidgetH = -1;
        private Rect _lastWidgetRect = Rect.Empty;

        // Caching for Taskbar HWND to avoid heavy EnumWindows P/Invoke queries
        private IntPtr _cachedTaskbarHandle = IntPtr.Zero;
        private bool _cachedIsMainTaskbarSelected = true;

        public TaskbarWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500); // 2fps is plenty — taskbar rarely moves
            _timer.Tick += (s, e) => UpdatePosition();

            // Listen for the toggle setting change
            SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AdvanceSettings.EnableTaskbarWidget))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_isClosed) return;
                        if (SettingsManager.Current.EnableTaskbarWidget)
                        {
                            Show();
                            SetupWindow();
                            _timer.Start();
                        }
                        else
                        {
                            _timer.Stop();
                            Visibility = Visibility.Hidden;
                        }
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

                if (GetParent(interop.Handle) != taskbarHandle)
                {
                    SetParent(interop.Handle, taskbarHandle);
                }

                if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
                {
                    Dispatcher.BeginInvoke(() => { CalculateAndSetPosition(taskbarHandle, interop.Handle); }, DispatcherPriority.Background);
                }
            }
            catch { }
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

                // Size the widget WPF controls to match exactly (to avoid WPF layout loops)
                Widget.Width = physicalWidth / dpiScale;
                Widget.Height = physicalHeight / dpiScale;
                
                double targetLogicalWidth = physicalWidth / dpiScale;
                double targetLogicalHeight = physicalHeight / dpiScale;
                if (Math.Abs(this.Width - targetLogicalWidth) > 0.01)
                    this.Width = targetLogicalWidth;
                if (Math.Abs(this.Height - targetLogicalHeight) > 0.01)
                    this.Height = targetLogicalHeight;

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
            // Cache for 5 seconds to avoid expensive enumeration every 500ms
            if (_cachedFreeZoneLeft >= 0 && (DateTime.Now - _lastFreeZoneScan).TotalSeconds < 5)
                return (_cachedFreeZoneLeft, _cachedFreeZoneWidth);

            try
            {
                IntPtr myHwnd = new WindowInteropHelper(this).Handle;
                var occupiedZones = new List<(int left, int right)>();
                
                // Detect if Windows 11 taskbar icons are centered using the registry key
                bool isTaskbarCentered = false;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    {
                        var val = key?.GetValue("TaskbarAl");
                        if (val != null && Convert.ToInt32(val) == 1)
                        {
                            isTaskbarCentered = true;
                        }
                    }
                }
                catch { }

                // Protect the Start/Search area if left-aligned, or the Widgets corner if centered
                if (!isTaskbarCentered)
                {
                    occupiedZones.Add((0, 180));
                }
                else
                {
                    occupiedZones.Add((0, 200)); // Protect the Widgets area on the far left corner on Win11 (expanded to 200 to clear dynamic weather text)
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

                    int align = SettingsManager.Current.WidgetTaskbarAlignment;
                    
                    if (gaps.Count > 0)
                    {
                        (int left, int width) selectedGap;
                        int widgetPos = 0;

                        if (align == 0) // Left alignment: pick the leftmost gap
                        {
                            selectedGap = gaps[0];
                            widgetPos = selectedGap.left + 8;
                        }
                        else if (align == 2) // Right alignment: pick the rightmost gap
                        {
                            selectedGap = gaps[gaps.Count - 1];
                            widgetPos = selectedGap.left + selectedGap.width - physicalWidth - 8;
                        }
                        else // Center alignment: pick the largest gap and center in it
                        {
                            var sortedGaps = gaps.OrderByDescending(g => g.width).ToList();
                            selectedGap = sortedGaps[0];
                            widgetPos = selectedGap.left + (selectedGap.width - physicalWidth) / 2;
                        }

                        _cachedFreeZoneLeft = widgetPos;
                        _cachedFreeZoneWidth = physicalWidth;
                        _lastFreeZoneScan = DateTime.Now;
                        
                        Classes.Logger.LogAction("WIDGET", $"FindTaskbarFreeZone: align={align}, selected widgetPos={widgetPos}");
                        return (widgetPos, physicalWidth);
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET", $"Free zone detection failed: {ex.Message}");
            }

            // Fallback: full taskbar width with padding
            int fallbackLeft = 12;
            int fallbackWidth = taskbarWidth - 24;
            int widgetLeft = 0;
            int fallbackAlign = SettingsManager.Current.WidgetTaskbarAlignment;
            if (fallbackAlign == 1) // Center
            {
                widgetLeft = fallbackLeft + (fallbackWidth - physicalWidth) / 2;
            }
            else if (fallbackAlign == 2) // Right
            {
                widgetLeft = fallbackLeft + fallbackWidth - physicalWidth - 8;
            }
            else // Left
            {
                widgetLeft = fallbackLeft + 8;
            }

            _cachedFreeZoneLeft = widgetLeft;
            _cachedFreeZoneWidth = physicalWidth;
            _lastFreeZoneScan = DateTime.Now;
            return (widgetLeft, physicalWidth);
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
                    double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
                    if (dpiScale <= 0) dpiScale = 1.0;

                    GetWindowRect(taskbarWindowHandle, out RECT rect);
                    
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