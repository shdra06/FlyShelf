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
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_isAnimatingHide) return;
            // Guard: don't fight QuickLook for focus
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is FlyShelf.Windows.QuickLookWindow && w.IsActive)) return;
            this.Opacity = 1.0;
            
            // Explicitly set DWM border color on activation to prevent DWM/MicaWindow from resetting it to system accent
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


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;

        private System.Windows.Threading.DispatcherTimer? _dragActiveDismissTimer;

        private void StartDragActiveDismissTimer()
        {
            if (_dragActiveDismissTimer == null)
            {
                _dragActiveDismissTimer = new System.Windows.Threading.DispatcherTimer();
                _dragActiveDismissTimer.Interval = TimeSpan.FromMilliseconds(100);
                _dragActiveDismissTimer.Tick += DragActiveDismissTimer_Tick;
            }
            _dragActiveDismissTimer.Start();
        }

        private void StopDragActiveDismissTimer()
        {
            _dragActiveDismissTimer?.Stop();
        }

        private void DragActiveDismissTimer_Tick(object? sender, EventArgs e)
        {
            // Check if left or right mouse button is physically held down
            bool isMouseDown = ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) || ((GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0);
            if (isMouseDown)
            {
                // Mid-drag, keep clipboard alive
                return;
            }

            // Drag/click released!
            StopDragActiveDismissTimer();

            // Only hide if the window is not currently active and mouse is not hovering over the window
            if (this.IsActive || _isDragHovering)
            {
                return;
            }

            if (this.IsVisible && !_isAnimatingHide)
            {
                // No-op: clipboard no longer auto-dismisses on focus loss.
                // Kept for drag-hover lifecycle tracking only.
            }
        }

        /// <summary>
        /// Window deactivation handler — intentionally does NOT auto-hide.
        /// The clipboard should only be dismissed via close button, Alt+C, widget toggle, or desktop switch.
        /// </summary>
        private void MicaWindow_Deactivated(object sender, EventArgs e)
        {
            // Intentional no-op: clipboard stays visible when clicking elsewhere.
            // Dismiss only via explicit user action (close button, Alt+C, widget, desktop switch).
        }

        /// <summary>
        /// Native Win32 callback triggered when the active foreground window changes globally.
        /// Only auto-dismisses FlyShelf when the user switches to a different virtual desktop.
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

            // Only auto-dismiss when the user switches virtual desktops.
            // All other focus changes (clicking another app, etc.) are ignored.
            if (!this.IsVisible || _isAnimatingHide) return;

            // Check if our window is still on the current virtual desktop
            try
            {
                var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (myHwnd != IntPtr.Zero)
                {
                    var vdm = (Classes.NativeMethods.IVirtualDesktopManager)new Classes.NativeMethods.VirtualDesktopManager();
                    int hr = vdm.IsWindowOnCurrentVirtualDesktop(myHwnd, out bool onCurrent);
                    if (hr >= 0 && !onCurrent)
                    {
                        // User switched to a different virtual desktop — dismiss the clipboard
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (this.IsVisible && !_isAnimatingHide)
                            {
                                AnimateAndHide();
                            }
                        });
                    }
                }
            }
            catch { /* COM may fail on older Windows builds — silently ignore */ }
        }
        private bool _isPersistentMode = false;
        private bool _isAnimatingHide = false;

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        // PERF: Cached animation objects — avoid GC pressure from allocating new ones on every show
        private static readonly TimeSpan _showAnimDuration = TimeSpan.FromMilliseconds(200);
        private static readonly System.Windows.Media.Animation.CubicEase _showEaseOut = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        private static readonly System.Windows.Media.Animation.DoubleAnimation _fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, _showAnimDuration) { EasingFunction = _showEaseOut };
        private static readonly System.Windows.Media.Animation.DoubleAnimation _scaleIn = new System.Windows.Media.Animation.DoubleAnimation(0.97, 1, _showAnimDuration) { EasingFunction = _showEaseOut };
        private static readonly System.Windows.Media.Animation.DoubleAnimation _slideIn = new System.Windows.Media.Animation.DoubleAnimation(6, 0, _showAnimDuration) { EasingFunction = _showEaseOut };

        private void PlayShowAnimation()
        {
            RootContent.RenderTransformOrigin = new Point(0.5, 1);
            RootContent.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(0.97, 0.97), new TranslateTransform(0, 6) }
            };
            RootContent.Opacity = 0;

            RootContent.BeginAnimation(OpacityProperty, _fadeIn);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, _scaleIn);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, _scaleIn);
            ((TransformGroup)RootContent.RenderTransform).Children[1].BeginAnimation(TranslateTransform.YProperty, _slideIn);
        }

        /// <summary>Fast dismiss animation on inner content, then hides window.</summary>
        // PERF: Cached hide animation objects — avoid GC pressure from allocating new ones on every dismiss
        private static readonly TimeSpan _hideAnimDuration = TimeSpan.FromMilliseconds(100);
        private static readonly System.Windows.Media.Animation.CubicEase _hideEaseIn = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
        private static readonly System.Windows.Media.Animation.DoubleAnimation _scaleOutX = new System.Windows.Media.Animation.DoubleAnimation(1, 0.97, _hideAnimDuration) { EasingFunction = _hideEaseIn };
        private static readonly System.Windows.Media.Animation.DoubleAnimation _scaleOutY = new System.Windows.Media.Animation.DoubleAnimation(1, 0.97, _hideAnimDuration) { EasingFunction = _hideEaseIn };
        private static readonly System.Windows.Media.Animation.DoubleAnimation _slideOut = new System.Windows.Media.Animation.DoubleAnimation(0, 5, _hideAnimDuration) { EasingFunction = _hideEaseIn };

        // PERF: Deferred mascot/GIF resume timer — mascot starts 1s after spawn, not during spawn
        private System.Windows.Threading.DispatcherTimer? _mascotDelayTimer;

        private void AnimateAndHide()
        {
            StopDragActiveDismissTimer();
            if (_isAnimatingHide || !this.IsVisible) return;

            // PERF: Cancel any pending mascot timer
            _mascotDelayTimer?.Stop();

            if (!Classes.SettingsManager.Current.EnableSummonAnimations)
            {
                DismissMergeState();
                CloseSearch();

                try
                {
                    this.Hide();
                    RootContent.BeginAnimation(OpacityProperty, null);
                    RootContent.Opacity = 1;
                    RootContent.RenderTransform = null;
                }
                catch { }

                // PERF: Pause mascot/GIF at Background priority — just freeze frames, don't destroy/reload
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        MascotIdle.PausePlayback();
                        var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                        animator?.Pause();
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            _isAnimatingHide = true;
            _lastActualHeight = this.ActualHeight;

            // Clear PDF merge selections so they don't persist on reopen
            DismissMergeState();
            CloseSearch();

            RootContent.RenderTransformOrigin = new Point(0.5, 1);
            RootContent.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) }
            };

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, _hideAnimDuration) { EasingFunction = _hideEaseIn };
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

                // PERF: Pause mascot/GIF at Background priority — just freeze frames, don't destroy/reload
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        MascotIdle.PausePlayback();
                        var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                        animator?.Pause();
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);
            };

            RootContent.BeginAnimation(OpacityProperty, fadeOut);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleXProperty, _scaleOutX);
            ((TransformGroup)RootContent.RenderTransform).Children[0].BeginAnimation(ScaleTransform.ScaleYProperty, _scaleOutY);
            ((TransformGroup)RootContent.RenderTransform).Children[1].BeginAnimation(TranslateTransform.YProperty, _slideOut);
        }
        private DateTime _spawnTime = DateTime.MinValue;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;
        private static int _clipboardWriteRefCount = 0;
        internal static bool _isWritingClipboard
        {
            get => System.Threading.Volatile.Read(ref _clipboardWriteRefCount) > 0;
            set
            {
                if (value)
                {
                    System.Threading.Interlocked.Increment(ref _clipboardWriteRefCount);
                }
                else
                {
                    int current;
                    do
                    {
                        current = _clipboardWriteRefCount;
                        if (current <= 0) break;
                    } while (System.Threading.Interlocked.CompareExchange(ref _clipboardWriteRefCount, current - 1, current) != current);
                }
            }
        }
        private static readonly object _timerLock = new object();
        private static System.Threading.Timer? _clipboardWriteResetTimer;
        
        internal static void SetWritingClipboard(bool value)
        {
            if (value)
            {
                System.Threading.Interlocked.Increment(ref _clipboardWriteRefCount);
                lock (_timerLock)
                {
                    _clipboardWriteResetTimer?.Dispose();
                    _clipboardWriteResetTimer = new System.Threading.Timer(_ =>
                    {
                        if (System.Threading.Volatile.Read(ref _clipboardWriteRefCount) > 0)
                        {
                            Classes.Logger.LogAction("CLIPBOARD", "⚠️ _isWritingClipboard was stuck true — auto-reset after 2s safety timeout");
                            System.Threading.Interlocked.Exchange(ref _clipboardWriteRefCount, 0);
                        }
                    }, null, 2000, System.Threading.Timeout.Infinite);
                }
            }
            else
            {
                int current;
                do
                {
                    current = _clipboardWriteRefCount;
                    if (current <= 0) break;
                } while (System.Threading.Interlocked.CompareExchange(ref _clipboardWriteRefCount, current - 1, current) != current);

                if (System.Threading.Volatile.Read(ref _clipboardWriteRefCount) == 0)
                {
                    lock (_timerLock)
                    {
                        _clipboardWriteResetTimer?.Dispose();
                        _clipboardWriteResetTimer = null;
                    }
                }
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

        // ═══ ShowNearPosition, Focus, Scroll & Thumbnail methods moved to MainWindow.Positioning.cs ═══
    }
}
