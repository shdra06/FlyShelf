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
            if (this.Opacity < 0.05) return; // Guard: window is in invisible pre-animation phase (first spawn)
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
        private static extern int SetProcessWorkingSetSize(IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

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

                            Classes.Logger.LogAction("VD_CB", $"BG_CHECK | summoned={_isCurrentlySummoned} notes={_isNotesActive} todo={_isTodoActive} summonedId={_summonedDesktopId:N}");

                            // Get thread/process ID of the new foreground window
                            uint focusedProcId = 0;
                            if (hwnd != IntPtr.Zero)
                            {
                                GetWindowThreadProcessId(hwnd, out focusedProcId);
                            }
                            uint currProcId = (uint)System.Environment.ProcessId;

                            // ═══ DESKTOP SWITCH DETECTION ═══
                            // Strategy: Get the FOREGROUND window's desktop GUID and compare
                            // with _summonedDesktopId. This is the most reliable signal because
                            // it doesn't depend on our own window's VDM state (which is broken
                            // for pinned windows).
                            if (hwnd != IntPtr.Zero && hwnd != myHwnd && focusedProcId != currProcId)
                            {
                                int hr = localVdm.GetWindowDesktopId(hwnd, out Guid currentDesktopId);
                                if (hr == 0 && currentDesktopId != Guid.Empty)
                                {
                                    // Always track the current desktop (used by ToggleMainClipboard)
                                    _currentDesktopId = currentDesktopId;

                                    Classes.Logger.LogAction("VD_CB", $"CHECK1: fgDesktop={currentDesktopId:N} summonedId={_summonedDesktopId:N} match={currentDesktopId == _summonedDesktopId}");

                                    if (_summonedDesktopId != Guid.Empty && currentDesktopId != _summonedDesktopId)
                                    {
                                        desktopSwitched = true;
                                        Classes.Logger.LogAction("VD_CB", "CHECK1: DESKTOP SWITCH DETECTED via GUID comparison");
                                    }
                                    if (_summonedDesktopId == Guid.Empty)
                                    {
                                        _summonedDesktopId = currentDesktopId;
                                    }
                                }
                            }

                            // Fallback: check _lastActiveExternalWindow
                            if (!desktopSwitched && _lastActiveExternalWindowWasOnCurrentAtSummon && 
                                _lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow))
                            {
                                int hr = localVdm.IsWindowOnCurrentVirtualDesktop(_lastActiveExternalWindow, out int onCurrent);
                                if (hr == 0 && onCurrent == 0)
                                {
                                    desktopSwitched = true;
                                    // Invalidate _currentDesktopId since we couldn't get the new desktop's GUID
                                    _currentDesktopId = Guid.Empty;
                                    Classes.Logger.LogAction("VD_CB", "CHECK2: _lastActiveExternalWindow NOT on current VD → desktop switch detected");
                                }
                            }

                            // Ultimate fallback for Notes/Todo: when WS_EX_APPWINDOW is set,
                            // the window is tied to a specific desktop despite being "pinned".
                            if (!desktopSwitched && (_isNotesActive || _isTodoActive))
                            {
                                int hr = localVdm.IsWindowOnCurrentVirtualDesktop(myHwnd, out int onCurrent);
                                if (hr == 0 && onCurrent == 0)
                                {
                                    desktopSwitched = true;
                                    _currentDesktopId = Guid.Empty;
                                    Classes.Logger.LogAction("VD_CB", "CHECK3: Our own window NOT on current VD (Notes/Todo WS_EX_APPWINDOW) → desktop switch detected");
                                }
                            }

                            Classes.Logger.LogAction("VD_CB", $"RESULT: desktopSwitched={desktopSwitched}");

                            if (desktopSwitched)
                            {
                                int capturedGeneration = _spawnGeneration;
                                Classes.Logger.LogAction("VD_CB", $"DISMISS: Dispatching AnimateAndHide to UI thread (gen={capturedGeneration})");

                                // User switched to a different virtual desktop — force clipboard mode
                                Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (_spawnGeneration != capturedGeneration) return;

                                    // ═══ NUCLEAR RESET: Force clipboard mode on desktop switch ═══
                                    // Close any active panels and clear ALL panel state.
                                    // This guarantees Alt+C on the new desktop shows a fresh clipboard.
                                    EnsureClipboardMode();
                                    _lastPanelBeforeDismiss = null;
                                    _desktopSwitchedSinceLastDismiss = true;

                                    if (_isCurrentlySummoned && !_isAnimatingHide)
                                    {
                                        Classes.Logger.LogAction("VD_CB", $"DISMISS: Executing on UI thread. notes={_isNotesActive} todo={_isTodoActive}");
                                        AnimateAndHide();
                                    }
                                    else
                                    {
                                        Classes.Logger.LogAction("VD_CB", $"DISMISS: Not summoned but reset to clipboard mode (notes={_isNotesActive} todo={_isTodoActive})");
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
        private string? _lastPanelBeforeDismiss = null; // "notes" or "todo" — remembers panel for same-desktop resummon

        /// <summary>
        /// Starts or restarts the 1-minute auto-revert timer for Notes/Todo panels.
        /// If the timer fires and a panel is still active, it auto-closes the panel
        /// and ensures the next summon shows the clipboard (with fast desktop reset).
        /// </summary>
        private void StartPanelAutoRevertTimer()
        {
            StopPanelAutoRevertTimer();
            _panelAutoRevertTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _panelAutoRevertTimer.Tick += PanelAutoRevertTimer_Tick;
            _panelAutoRevertTimer.Start();
        }

        private void StopPanelAutoRevertTimer()
        {
            if (_panelAutoRevertTimer != null)
            {
                _panelAutoRevertTimer.Stop();
                _panelAutoRevertTimer.Tick -= PanelAutoRevertTimer_Tick;
                _panelAutoRevertTimer = null;
            }
        }

        private void PanelAutoRevertTimer_Tick(object? sender, EventArgs e)
        {
            StopPanelAutoRevertTimer(); // One-shot

            // If a panel is still active after 1 minute, auto-revert to clipboard mode
            if (_isNotesActive || _isTodoActive)
            {
                Classes.Logger.LogAction("AUTO_REVERT", "Panel idle for 1 minute — auto-reverting to clipboard mode.");

                if (_isNotesActive) CloseNotesPanel(immediate: true);
                if (_isTodoActive) CloseTodoPanel(immediate: true);

                // Ensure clean state for next summon — clear panel memory too
                _lastPanelBeforeDismiss = null;
                _desktopSwitchedSinceLastDismiss = true;
                _isCurrentlySummoned = false;
                UninstallKeyboardHook();
                _isAnimatingHide = false;
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                this.Left = -20000;
                this.Top = -20000;

                // Release memory
                OptimizeMemoryUsage();
            }
        }


        // ═══ CACHED FROZEN ANIMATIONS (zero-alloc spawn) ═══
        // Created once, frozen, reused every spawn. Frozen animations run entirely on
        // WPF's composition thread (GPU), completely immune to UI thread GC pauses.
        private static readonly System.Windows.Media.Animation.DoubleAnimation _cachedOpacityAnim;
        private static readonly System.Windows.Media.Animation.DoubleAnimation _cachedSlideInAnim;
        private readonly TranslateTransform _cachedSlideTransform = new TranslateTransform(0, 10);

        static MainWindow()
        {
            _cachedOpacityAnim = new System.Windows.Media.Animation.DoubleAnimation(0.01, 1, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            _cachedOpacityAnim.Freeze();

            _cachedSlideInAnim = new System.Windows.Media.Animation.DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            _cachedSlideInAnim.Freeze();
        }

        // Cached timer for clearing _isShowAnimating after animation completes
        private System.Windows.Threading.DispatcherTimer? _showAnimEndTimer;

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        private void PlayShowAnimation()
        {
            _isShowAnimating = true;

            // NOTE: Do NOT call UpdatePositionToLockedBottomEdge() here.
            // The initial positioning was already done in ShowNearPositionInternal using cached height.
            // Calling it again here with ActualHeight (which may differ) causes a visible position jump.

            // Reset the slide transform to start position.
            // CRITICAL: Don't reassign RenderTransform if it's already our cached instance —
            // reassigning invalidates the entire visual tree's cached render transform,
            // causing a DWM recomposition flash that looks like a tremor/shake.
            _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _cachedSlideTransform.Y = 6;
            if (!ReferenceEquals(RootContent.RenderTransform, _cachedSlideTransform))
                RootContent.RenderTransform = _cachedSlideTransform;

            // PERF: Use the FROZEN opacity animation directly — it runs entirely on WPF's
            // composition thread (GPU), immune to UI thread GC pauses. Previously we Clone()d
            // it to attach a Completed handler, but Clone() unfreezes the animation, pulling
            // it back to the UI thread where GC pauses cause 30-43ms frame drops.
            //
            // Instead, detect completion via CompositionTarget.Rendering by monitoring opacity.
            // The slide animation (280ms) is longer than opacity (250ms), so we wait for slide=0.
            this.BeginAnimation(OpacityProperty, _cachedOpacityAnim);
            _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, _cachedSlideInAnim);

            // Detect animation completion on the render thread without unfreezing anything.
            // Check when the slide Y reaches 0 (end of 280ms slide animation).
            int capturedGen = _spawnGeneration;
            EventHandler completionHandler = null!;
            completionHandler = (s, e) =>
            {
                // Bail if a new spawn started (stale handler)
                if (_spawnGeneration != capturedGen)
                {
                    System.Windows.Media.CompositionTarget.Rendering -= completionHandler;
                    return;
                }
                // Wait until slide animation has effectively finished (Y ≈ 0)
                if (_cachedSlideTransform.Y > 0.05) return;

                System.Windows.Media.CompositionTarget.Rendering -= completionHandler;

                _isShowAnimating = false;
                _isEdgeLocked = true;
                _showAnimEndTime = DateTime.UtcNow; // Start 500ms post-animation cooldown for SizeChanged
                // Snap _lockedBottomEdge to the CURRENT window position instead of moving
                // the window. This prevents a visible jump when ActualHeight differs from
                // the cached height used during initial positioning.
                if (this.ActualHeight > 0)
                    _lockedBottomEdge = this.Top + this.ActualHeight + 20;

                // JITTER FIX: After animation completes, remove the animation clock.
                // Leaving a TranslateTransform(Y≈0) with an active clock on a
                // UseLayoutRounding window causes WPF to continuously apply sub-pixel
                // corrections that shimmer on Mica surfaces.
                _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _cachedSlideTransform.Y = 0;
            };
            System.Windows.Media.CompositionTarget.Rendering += completionHandler;
        }

        /// <summary>
        /// Fixes the "half render box" on first spawn by mimicking what user scroll
        /// interaction does — nudge the ScrollViewer offset by 1px and back. This forces
        /// WPF's composition thread to fully re-render the viewport from its cold cache.
        /// </summary>
        private void ForceFirstSpawnRepaint()
        {
            // Two deferred dispatches: first waits for layout, second for render.
            // This ensures the ScrollViewer's visual tree is fully realized before nudging.
            Dispatcher.InvokeAsync(() =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var sv = GetShelfScrollViewer();
                        if (sv != null)
                        {
                            double offset = sv.VerticalOffset;
                            sv.ScrollToVerticalOffset(offset + 1);
                            // Dispatch the restore at lower priority so the +1 actually renders
                            Dispatcher.InvokeAsync(() =>
                            {
                                sv.ScrollToVerticalOffset(offset);
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                        else
                        {
                            // Fallback: invalidate the entire visual tree
                            RootContent.InvalidateVisual();
                            ShelfListView.InvalidateVisual();
                        }
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }, System.Windows.Threading.DispatcherPriority.Render);
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
            UninstallKeyboardHook(); // Release arrow-key hook so keys return to the target app

            Classes.Logger.LogAction("VD_HIDE", $"AnimateAndHide | notes={_isNotesActive} todo={_isTodoActive} deskSwitchFlag={_desktopSwitchedSinceLastDismiss}");

            // ═══ CLOSE NOTES/TODO ═══
            if (_isNotesActive || _isTodoActive)
            {
                // Always save which panel was active — ToggleMainClipboard decides whether to restore
                _lastPanelBeforeDismiss = _isNotesActive ? "notes" : "todo";
                Classes.Logger.LogAction("VD_HIDE", $"Panel close: saved={_lastPanelBeforeDismiss}");

                if (_isNotesActive) CloseNotesPanel(immediate: true);
                if (_isTodoActive) CloseTodoPanel(immediate: true);
            }

            StopPanelAutoRevertTimer();
            _mascotDelayTimer?.Stop();
            _isAnimatingHide = false;
            _lastActualHeight = this.ActualHeight;

            DismissMergeState();
            CloseSearch();

            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            try
            {
                // Make window invisible
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                RootContent.Opacity = 1;

                // JITTER FIX: Don't null-out RenderTransform on hide.
                // Setting RenderTransform=null destroys WPF's cached render tree transform.
                // On the next show, reassigning it causes a full visual tree invalidation
                // and DWM recomposition flash that looks like a shake/tremor.
                // Instead, just reset the cached transform's Y to 0 (a no-op visually).
                _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _cachedSlideTransform.Y = 0;
                if (!ReferenceEquals(RootContent.RenderTransform, _cachedSlideTransform))
                    RootContent.RenderTransform = _cachedSlideTransform;

                // Reset scroll
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
                // This ensures WPF has rendered and committed a 0% opacity frame to DWM first.
                // HideWindowInternal has a guard: if _isCurrentlySummoned was set by a new show,
                // it aborts to avoid clobbering the new show.
                Dispatcher.InvokeAsync(() =>
                {
                    HideWindowInternal();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }

            // Pause GIF mascot and wallpaper decoding
            try
            {
                MascotIdle.PausePlayback();
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                animator?.Pause();
            }
            catch { }

            // Optimize memory
            OptimizeMemoryUsage();
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

            // 2. GC + working set trim — keep 30MB minimum to avoid cold-start animation jitter.
            // SetProcessWorkingSetSize(-1,-1) evicts ALL pages including WPF rendering pipeline,
            // causing page faults on first spawn. A 30MB floor keeps composition thread buffers resident.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Gen 1 optimized collection — reclaims short-lived objects without freezing UI
                    System.GC.Collect(1, System.GCCollectionMode.Optimized, false);
                    System.GC.WaitForPendingFinalizers();

                    // Set working set to 30MB min / 60MB max — keeps WPF rendering pages resident
                    // while still releasing image bitmap pages back to OS standby list
                    const nint MIN_WS = 30 * 1024 * 1024;  // 30 MB
                    const nint MAX_WS = 60 * 1024 * 1024;  // 60 MB
                    using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        SetProcessWorkingSetSize(currentProcess.Handle, MIN_WS, MAX_WS);
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
