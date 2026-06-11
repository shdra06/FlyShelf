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
            if (_isShowAnimating) return; // Don't override opacity during show animation
            // Guard: don't fight QuickLook for focus
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is FlyShelf.Windows.QuickLookWindow && w.IsActive)) return;
            this.Opacity = 1.0;
            
            // Defer DWM border color setting to Background priority to prevent blocking DWM frame synchronization on activation
            Dispatcher.InvokeAsync(() =>
            {
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
            }, System.Windows.Threading.DispatcherPriority.Background);
        }


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
        private static extern int SetProcessWorkingSetSize(IntPtr process, int minimumWorkingSetSize, int maximumWorkingSetSize);

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

            if (_isCurrentlySummoned && !_isAnimatingHide)
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

            // Defer DWM border color setting to Background priority to prevent blocking DWM frame synchronization on deactivation
            Dispatcher.InvokeAsync(() =>
            {
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
            }, System.Windows.Threading.DispatcherPriority.Background);
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
            if (!_isCurrentlySummoned || _isAnimatingHide) return;

            // Check if our window is still on the current virtual desktop
            try
            {
                var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (myHwnd != IntPtr.Zero)
                {
                    // Run the check asynchronously on a background thread so we NEVER block the UI thread on focus changes
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // Wait for VDM state to settle — EVENT_SYSTEM_FOREGROUND fires
                            // before IVirtualDesktopManager is fully updated during desktop switches.
                            // Without this delay, GetWindowDesktopId returns stale/empty GUIDs.
                            System.Threading.Thread.Sleep(80);

                            var localVdm = (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
                            
                            bool desktopSwitched = false;

                            // Get thread/process ID of the new foreground window
                            uint focusedProcId = 0;
                            if (hwnd != IntPtr.Zero)
                            {
                                GetWindowThreadProcessId(hwnd, out focusedProcId);
                            }
                            uint currProcId = (uint)System.Environment.ProcessId;

                            // 1. Get desktop ID of the newly focused window
                            if (hwnd != IntPtr.Zero && hwnd != myHwnd && focusedProcId != currProcId)
                            {
                                int hr = localVdm.GetWindowDesktopId(hwnd, out Guid currentDesktopId);
                                if (hr == 0 && currentDesktopId != Guid.Empty)
                                {
                                    if (_summonedDesktopId != Guid.Empty && currentDesktopId != _summonedDesktopId)
                                    {
                                        desktopSwitched = true;
                                    }
                                }
                            }

                            // 2. Fallback using _lastActiveExternalWindow (only run if verified to be on current desktop at summon)
                            if (!desktopSwitched && _lastActiveExternalWindowWasOnCurrentAtSummon && 
                                _lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow))
                            {
                                int hr = localVdm.IsWindowOnCurrentVirtualDesktop(_lastActiveExternalWindow, out int onCurrent);
                                if (hr == 0 && onCurrent == 0)
                                {
                                    desktopSwitched = true;
                                }
                            }

                            // 3. Ultimate fallback: check if OUR OWN window is still on the current desktop.
                            // This is the most reliable signal — especially when Notes/Todo mode adds
                            // WS_EX_APPWINDOW, tying the window to a specific desktop.
                            if (!desktopSwitched)
                            {
                                int hr = localVdm.IsWindowOnCurrentVirtualDesktop(myHwnd, out int onCurrent);
                                if (hr == 0 && onCurrent == 0)
                                {
                                    desktopSwitched = true;
                                }
                            }

                            if (desktopSwitched)
                            {
                                // Capture the spawn generation BEFORE dispatching to UI thread.
                                // If a new spawn happens between now and when the lambda runs,
                                // the generation will differ → skip the stale dismiss.
                                int capturedGeneration = _spawnGeneration;

                                // User switched to a different virtual desktop — dismiss on UI thread
                                Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    // Skip if a new spawn happened since this callback was queued.
                                    // This prevents the stale callback from hiding a clipboard
                                    // that was just spawned on the new desktop via Alt+C.
                                    if (_spawnGeneration != capturedGeneration) return;

                                    if (_isCurrentlySummoned && !_isAnimatingHide)
                                    {
                                        if (_isNotesActive)
                                            CloseNotesPanel(immediate: true);
                                        if (_isTodoActive)
                                            CloseTodoPanel(immediate: true);

                                        _desktopSwitchedSinceLastDismiss = true;
                                        AnimateAndHide();
                                    }
                                });
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { /* COM may fail on older Windows builds — silently ignore */ }
        }
        private bool _isPersistentMode = false;
        private bool _isAnimatingHide = false;
        private bool _isShowAnimating = false;
        private bool _isApplyingTheme = false;
        private volatile int _spawnGeneration = 0; // Incremented on each spawn to invalidate stale callbacks
        private bool _desktopSwitchedSinceLastDismiss = false; // True when dismiss was triggered by a desktop switch

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        private void PlayShowAnimation()
        {
            _isShowAnimating = true;

            // Set starting state for slide-in (TranslateY = 10)
            RootContent.RenderTransform = new TranslateTransform(0, 10);

            // ═══ COMPOSITION-THREAD ACCELERATED SPAWN PROFILE (GPU-BOUND) ═══
            // Opacity Animation: Front-loaded 0 -> 1 over 120ms with QuinticEaseOut.
            // Runs entirely on the GPU render thread, completely immune to UI thread layout stalls!
            var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            opacityAnim.Completed += (s, e) =>
            {
                _isShowAnimating = false;
            };

            // Slide In Animation: Back-loaded 10 -> 0 over 200ms with CubicEaseOut.
            // Also runs entirely on the GPU render thread, providing a buttery-smooth soft settle!
            var slideInAnim = new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            this.BeginAnimation(OpacityProperty, opacityAnim);
            RootContent.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slideInAnim);
        }

        // PERF: Deferred mascot/GIF resume timer — mascot starts 1s after spawn, not during spawn
        private System.Windows.Threading.DispatcherTimer? _mascotDelayTimer;

        /// <summary>Instant dismiss of FlyShelf window with zero latency or animations.</summary>
        public void AnimateAndHide()
        {
            StopDragActiveDismissTimer();
            if (!_isCurrentlySummoned) return;

            // ═══ CRITICAL: Set unsummoned IMMEDIATELY ═══
            _isCurrentlySummoned = false;
            // Note: _desktopSwitchedSinceLastDismiss is set by ForegroundChangedCallback
            // BEFORE calling AnimateAndHide. For normal dismiss (Alt+C), it stays false.

            // ═══ CLOSE NOTES/TODO BEFORE HIDING ═══
            // Must close panels HERE, not in HideWindowInternal.
            // If panels are still active when HideWindowInternal runs,
            // it minimizes (opacity=1) instead of moving offscreen (Left=-20000).
            // Minimized windows break the zombie detector (Left < -10000 && Opacity < 0.01).
            if (_isNotesActive) CloseNotesPanel(immediate: true);
            if (_isTodoActive) CloseTodoPanel(immediate: true);

            // Cancel any pending mascot timers
            _mascotDelayTimer?.Stop();

            _isAnimatingHide = false;
            _lastActualHeight = this.ActualHeight;

            // Clear merge state & close search instantly
            DismissMergeState();
            CloseSearch();

            // Close the overflow popup so it doesn't linger on screen after dismiss
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            try
            {
                // Reset window opacity to 0 and clear any active animations immediately onscreen.
                // This makes the window instantly invisible to the user on the screen.
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                RootContent.Opacity = 1;
                
                // Clear any active translation offsets
                if (RootContent.RenderTransform is TranslateTransform tt)
                {
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                }
                RootContent.RenderTransform = null;

                // ═══ SCROLL RESET DURING DISMISS ═══
                // Reset scroll position while the window is invisible (opacity=0).
                // This eliminates the visible jitter on next spawn — the scroll is
                // already at position 0 when the fade-in animation starts.
                try
                {
                    Classes.SmoothScroll.ResetScrollState(GetShelfScrollViewer());
                    if (ShelfListView.Items.Count > 0)
                        ShelfListView.SelectedIndex = 0;
                    var sv = GetShelfScrollViewer();
                    if (sv != null && sv.VerticalOffset > 0)
                    {
                        sv.ScrollToVerticalOffset(0);
                        sv.ScrollToTop();
                    }
                }
                catch { }

                // Defer the offscreen move to Background priority.
                // This ensures WPF has rendered and committed a 0% opacity frame to DWM first,
                // clearing the composition cache before the window is translated offscreen.
                Dispatcher.InvokeAsync(() =>
                {
                    HideWindowInternal();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }

            // Pause all GIF mascot and wallpaper decoding loops immediately
            try
            {
                MascotIdle.PausePlayback();
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                animator?.Pause();
            }
            catch { }
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

        public void OptimizeMemoryUsage()
        {
            // 1. Evict non-pinned image/QR thumbnails to free heavy image memory, keeping top 5 always loaded
            try
            {
                if (_viewModel?.DroppedItems != null)
                {
                    int imageCount = 0;
                    foreach (var item in _viewModel.DroppedItems)
                    {
                        if (item == null) continue;

                        if (item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                        {
                            imageCount++;
                            if (imageCount <= 5)
                            {
                                // Keep the top 5 images loaded in RAM to hide the sudden appearance on summon
                                continue;
                            }

                            if (!item.IsPinned)
                            {
                                item.Icon = null;
                                item.IsLoadedHighQuality = false;
                                item.IsLoadingHighQuality = false;
                                item.LeftViewportTime = null;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Perform Garbage Collection and Empty the Process Working Set asynchronously
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Force complete Gen 2 collection
                    System.GC.Collect(2, System.GCCollectionMode.Forced, true);
                    System.GC.WaitForPendingFinalizers();

                    // Empty process working set to release physical RAM pages back to OS standby list
                    using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        SetProcessWorkingSetSize(currentProcess.Handle, -1, -1);
                    }
                }
                catch { }
            });
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
