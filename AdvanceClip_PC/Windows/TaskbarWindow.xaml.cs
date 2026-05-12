using AdvanceClip.Classes;
using AdvanceClip.Classes.Utils;
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
using static AdvanceClip.Classes.NativeMethods;

namespace AdvanceClip.Windows
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

            if (SettingsManager.Current.EnableTaskbarWidget)
            {
                Show();
                _timer.Start();
            }

            Classes.Logger.LogAction("WIDGET", "TaskbarWindow created and Show() called");
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            int colorNone = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
            source.AddHook(WindowProc);
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
            if (!SettingsManager.Current.EnableTaskbarWidget)
            {
                Visibility = Visibility.Hidden;
                return;
            }
            SetupWindow();
            _mainWindow = (MainWindow)Application.Current.MainWindow;
            Widget.SetMainWindow(_mainWindow);
        }

        private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbarSelected)
        {
            var monitors = MonitorUtil.GetMonitors();
            var selectedMonitor = MonitorUtil.GetSelectedMonitor();
            isMainTaskbarSelected = true;

            var mainHwnd = FindWindow("Shell_TrayWnd", null);
            if (MonitorUtil.GetMonitor(mainHwnd).deviceId == selectedMonitor.deviceId)
                return mainHwnd;

            if (monitors.Count == 1)
                return mainHwnd;

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
                if (secondHwnd != IntPtr.Zero) return secondHwnd;
            }

            EnumWindows((wnd, param) =>
            {
                secondHwnd = checkWindowClass(wnd);
                if (secondHwnd != IntPtr.Zero) return false;
                return true;
            }, IntPtr.Zero);

            if (secondHwnd != IntPtr.Zero) return secondHwnd;

            isMainTaskbarSelected = true;
            return mainHwnd;
        }

        private void SetupWindow()
        {
            try
            {
                var interop = new WindowInteropHelper(this);
                IntPtr taskbarWindowHandle = interop.Handle;
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

        private void UpdatePosition()
        {
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
            if (_positionUpdateInProgress) return;
            _positionUpdateInProgress = true;

            try
            {
                double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
                if (dpiScale <= 0) return;

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

                POINT containerPos = new() { X = taskbarRect.Left, Y = taskbarRect.Top };
                ScreenToClient(taskbarHandle, ref containerPos);

                SetWindowPos(taskbarWindowHandle, 0,
                         containerPos.X, containerPos.Y,
                         taskbarWidth, taskbarHeight,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);

                var wRect = PositionWidget(taskbarHandle, taskbarRect, dpiScale, isSizeChanged);
                UpdateWindowRegion(taskbarWindowHandle, wRect);
            }
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
        private (int left, int width) FindTaskbarFreeZone(IntPtr taskbarHandle, int taskbarWidth, double dpiScale)
        {
            // Cache for 2 seconds to avoid expensive enumeration every 500ms
            if (_cachedFreeZoneLeft >= 0 && (DateTime.Now - _lastFreeZoneScan).TotalSeconds < 2)
                return (_cachedFreeZoneLeft, _cachedFreeZoneWidth);

            try
            {
                // Enumerate direct child windows of the taskbar to find occupied zones
                var occupiedZones = new List<(int left, int right)>();
                
                EnumChildWindows(taskbarHandle, (hwnd, lParam) =>
                {
                    // Only consider visible, direct children
                    if (!IsWindowVisible(hwnd) || GetParent(hwnd) != taskbarHandle)
                        return true;

                    GetWindowRect(hwnd, out RECT childRect);
                    
                    // Convert to taskbar-local coordinates
                    POINT childPt = new() { X = childRect.Left, Y = childRect.Top };
                    ScreenToClient(taskbarHandle, ref childPt);
                    int childWidth = childRect.Right - childRect.Left;
                    
                    if (childWidth > 5) // Skip tiny/invisible elements
                    {
                        occupiedZones.Add((childPt.X, childPt.X + childWidth));
                    }
                    return true;
                }, IntPtr.Zero);

                if (occupiedZones.Count > 0)
                {
                    // Sort by left position
                    occupiedZones.Sort((a, b) => a.left.CompareTo(b.left));

                    // Find the largest gap between occupied zones
                    int bestGapLeft = 0, bestGapWidth = 0;
                    int lastRight = 0;
                    
                    foreach (var zone in occupiedZones)
                    {
                        int gapWidth = zone.left - lastRight;
                        if (gapWidth > bestGapWidth)
                        {
                            bestGapWidth = gapWidth;
                            bestGapLeft = lastRight;
                        }
                        if (zone.right > lastRight)
                            lastRight = zone.right;
                    }
                    
                    // Also check gap after last occupied zone to end of taskbar
                    int trailingGap = taskbarWidth - lastRight;
                    if (trailingGap > bestGapWidth)
                    {
                        bestGapWidth = trailingGap;
                        bestGapLeft = lastRight;
                    }

                    if (bestGapWidth > 60) // Only use if there's meaningful space
                    {
                        _cachedFreeZoneLeft = bestGapLeft;
                        _cachedFreeZoneWidth = bestGapWidth;
                        _lastFreeZoneScan = DateTime.Now;
                        return (bestGapLeft, bestGapWidth);
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET", $"Free zone detection failed: {ex.Message}");
            }

            // Fallback: full taskbar width with padding
            _cachedFreeZoneLeft = 12;
            _cachedFreeZoneWidth = taskbarWidth - 24;
            _lastFreeZoneScan = DateTime.Now;
            return (12, taskbarWidth - 24);
        }

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private Rect PositionWidget(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isSizeChanged)
        {
            var (logicalWidth, logicalHeight) = Widget.CalculateSize(dpiScale);

            int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
            int physicalHeight = (int)(logicalHeight * dpiScale);

            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int widgetTop = (taskbarHeight - physicalHeight) / 2;
            
            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
            int align = SettingsManager.Current.WidgetTaskbarAlignment;

            // Detect free zone on the taskbar (avoiding Windows widgets, system tray, etc.)
            var (freeLeft, freeWidth) = FindTaskbarFreeZone(taskbarHandle, taskbarWidth, dpiScale);

            int widgetLeft;
            if (align == 1) // Centered in free zone
            {
                widgetLeft = freeLeft + (freeWidth - physicalWidth) / 2;
            }
            else if (align == 2) // Right side of free zone
            {
                widgetLeft = freeLeft + freeWidth - physicalWidth - 8;
            }
            else // Left side of free zone (default)
            {
                widgetLeft = freeLeft + 8;
            }

            // Clamp to valid range
            if (widgetLeft < 4) widgetLeft = 4;
            if (widgetLeft + physicalWidth > taskbarWidth - 4)
                widgetLeft = taskbarWidth - physicalWidth - 4;

            Canvas.SetLeft(Widget, widgetLeft / dpiScale);
            Canvas.SetTop(Widget, widgetTop / dpiScale);
            Widget.Width = physicalWidth / dpiScale;
            Widget.Height = physicalHeight / dpiScale;

            Visibility = Visibility.Visible;

            return new Rect(Canvas.GetLeft(Widget) * dpiScale, Canvas.GetTop(Widget) * dpiScale, Widget.Width * dpiScale, Widget.Height * dpiScale);
        }



        private (bool, Rect) GetTaskbarFrameRect(IntPtr taskbarHandle)
        {
            GetWindowRect(taskbarHandle, out RECT rect);
            return (true, new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
    }
}