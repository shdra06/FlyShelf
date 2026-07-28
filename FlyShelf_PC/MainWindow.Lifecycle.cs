using FlyShelf.ViewModels;
using FlyShelf.Classes;
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
        private bool _hasOptimizedThisHide = false;

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (!_isCurrentlySummoned) return; // Guard: don't resurrect a hidden/dismissed window
            if (_isAnimatingHide) return;
            if (_isShowAnimating) return; // Don't override opacity during show animation
            if (this.Opacity < 0.05) return; // Guard: window is in invisible pre-animation phase (first spawn)
            // Guard: don't fight QuickLook for focus
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w is FlyShelf.Windows.QuickLookWindow && w.IsActive)) return;
            this.Opacity = 1.0;
            
            // Set DWM border color synchronously to prevent flashing
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int cn = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                }
            }
            catch { }
            
            // Defer DWM border color setting to Send priority to prevent blocking DWM frame synchronization on activation while applying it immediately
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int cn = DWMWA_COLOR_NONE;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }, System.Windows.Threading.DispatcherPriority.Send);

            // ═══ Auto-refresh desktop wallpaper if it changed while window was hidden ═══
            // Registry read is instant (~0ms) — safe to call on every activation.
            RefreshDesktopWallpaperIfChanged();
        }


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
            bool isMouseDown = ((NativeMethods.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) || ((NativeMethods.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0);
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

            // Set DWM border color synchronously to prevent flashing
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int cn = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                }
            }
            catch { }

            // Defer DWM border color setting to Send priority to prevent blocking DWM frame synchronization on deactivation
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int cn = DWMWA_COLOR_NONE;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }, System.Windows.Threading.DispatcherPriority.Send);
        }

        /// <summary>
        /// Native Win32 callback triggered when the active foreground window changes globally.
        /// Only auto-dismisses FlyShelf when the user switches to a different virtual desktop.
        /// </summary>
        private void ForegroundChangedCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero) return;

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

            // Capture UI state on the UI thread before launching the background check task
            bool isSummoned = _isCurrentlySummoned;
            bool isHideAnimating = _isAnimatingHide;
            bool isNotes = _isNotesActive;
            bool isTodo = _isTodoActive;
            Guid summonedId = _summonedDesktopId;
            IntPtr lastActiveExt = _lastActiveExternalWindow;
            bool lastActiveExtWasOnCurrent = System.Threading.Volatile.Read(ref _lastActiveExternalWindowWasOnCurrentAtSummon);
            double msSinceSpawn = (DateTime.Now - _spawnTime).TotalMilliseconds;

            // Run the check asynchronously on a background thread so we NEVER block the UI thread on focus changes
            try
            {
                var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (myHwnd != IntPtr.Zero)
                {
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            // Wait for VDM state to settle — EVENT_SYSTEM_FOREGROUND fires
                            // before IVirtualDesktopManager is fully updated during desktop switches.
                            // Without this delay, GetWindowDesktopId returns stale/empty GUIDs.
                            await System.Threading.Tasks.Task.Delay(80);

                            if (_cachedVdm == null)
                                _cachedVdm = new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
                            var localVdm = (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)_cachedVdm;
                            
                            // Get thread/process ID of the new foreground window
                            uint focusedProcIdCheck = 0;
                            GetWindowThreadProcessId(hwnd, out focusedProcIdCheck);
                            uint currProcIdCheck = (uint)System.Environment.ProcessId;

                            Guid currentDesktopId = Guid.Empty;

                            // ═══ DESKTOP GUID RESOLUTION ═══
                            if (hwnd != IntPtr.Zero && hwnd != myHwnd && focusedProcIdCheck != currProcIdCheck)
                            {
                                int hr = localVdm.GetWindowDesktopId(hwnd, out Guid fgDesktopId);
                                if (hr == 0 && fgDesktopId != Guid.Empty)
                                {
                                    currentDesktopId = fgDesktopId;
                                    // Always update the cached current desktop ID on the UI thread
                                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        _currentDesktopId = fgDesktopId;
                                    });
                                }
                            }

                            // Only perform dismiss logic if the window is currently active and not animating hide.
                            // GUARD: If the window was summoned less than 500ms ago, do NOT dismiss it.
                            // This blocks delayed virtual desktop callbacks from immediately closing a newly summoned window.
                            if (isSummoned && !isHideAnimating && msSinceSpawn >= 500)
                            {
                                bool desktopSwitched = false;

                                Classes.Logger.LogAction("VD_CB", $"BG_CHECK | summoned={isSummoned} notes={isNotes} todo={isTodo} summonedId={summonedId:N}");

                                if (currentDesktopId != Guid.Empty)
                                {
                                    Classes.Logger.LogAction("VD_CB", $"CHECK1: fgDesktop={currentDesktopId:N} summonedId={summonedId:N} match={currentDesktopId == summonedId}");

                                    if (summonedId != Guid.Empty && currentDesktopId != summonedId)
                                    {
                                        desktopSwitched = true;
                                        Classes.Logger.LogAction("VD_CB", "CHECK1: DESKTOP SWITCH DETECTED via GUID comparison");
                                    }
                                    if (summonedId == Guid.Empty)
                                    {
                                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            _summonedDesktopId = currentDesktopId;
                                        });
                                    }
                                }

                                // Fallback: check _lastActiveExternalWindow
                                if (!desktopSwitched && lastActiveExtWasOnCurrent && 
                                    lastActiveExt != IntPtr.Zero && IsWindow(lastActiveExt))
                                {
                                    int hr = localVdm.IsWindowOnCurrentVirtualDesktop(lastActiveExt, out int onCurrent);
                                    if (hr == 0 && onCurrent == 0)
                                    {
                                        desktopSwitched = true;
                                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            _currentDesktopId = Guid.Empty;
                                        });
                                        Classes.Logger.LogAction("VD_CB", "CHECK2: _lastActiveExternalWindow NOT on current VD → desktop switch detected");
                                    }
                                }

                                // Ultimate fallback for Notes/Todo
                                if (!desktopSwitched && (isNotes || isTodo))
                                {
                                    int hr = localVdm.IsWindowOnCurrentVirtualDesktop(myHwnd, out int onCurrent);
                                    if (hr == 0 && onCurrent == 0)
                                    {
                                        desktopSwitched = true;
                                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            _currentDesktopId = Guid.Empty;
                                        });
                                        Classes.Logger.LogAction("VD_CB", "CHECK3: Our own window NOT on current VD (Notes/Todo WS_EX_APPWINDOW) → desktop switch detected");
                                    }
                                }

                                Classes.Logger.LogAction("VD_CB", $"RESULT: desktopSwitched={desktopSwitched}");

                                if (desktopSwitched)
                                {
                                    int capturedGeneration = _spawnGeneration;
                                    Classes.Logger.LogAction("VD_CB", $"DISMISS: Dispatching AnimateAndHide to UI thread (gen={capturedGeneration})");

                                    // User switched to a different virtual desktop — force clipboard mode
                                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        if (_spawnGeneration != capturedGeneration) return;

                                        // ═══ NUCLEAR RESET: Force clipboard mode on desktop switch ═══
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
                        }
                        catch { } // Best-effort: failure is acceptable
                    });
                }
            }
            catch { /* COM may fail on older Windows builds — silently ignore */ }
        }
        private FlyShelf.Classes.NativeMethods.VirtualDesktopManager? _cachedVdm;
        private bool _isPersistentMode = false;
        private bool _isAnimatingHide = false;
        private bool _isShowAnimating = false;
        private DateTime _showAnimationEndTime = DateTime.MinValue;
        private bool _isApplyingTheme = false;
        private volatile int _spawnGeneration = 0; // Incremented on each spawn to invalidate stale callbacks
        private bool _desktopSwitchedSinceLastDismiss = false; // True when dismiss was triggered by a desktop switch
        private string? _lastPanelBeforeDismiss = null; // "notes", "todo", or "research" — remembers panel for same-desktop resummon

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
            if (_isNotesActive || _isTodoActive || _isResearchActive)
            {
                Classes.Logger.LogAction("AUTO_REVERT", "Panel idle for 1 minute — auto-reverting to clipboard mode.");

                if (_isNotesActive) CloseNotesPanel(immediate: true);
                if (_isTodoActive) CloseTodoPanel(immediate: true);
                if (_isResearchActive) CloseResearchPanel(immediate: true);

                // PC-10 FIX: Don't set _desktopSwitchedSinceLastDismiss = true here.
                // That flag triggers a 50ms DWM settle delay on re-summon which is wrong
                // since no desktop switch actually occurred. Clearing _lastPanelBeforeDismiss
                // is sufficient to prevent panel restore.
                _lastPanelBeforeDismiss = null;
                _isCurrentlySummoned = false;
                UninstallKeyboardHook();
                _isAnimatingHide = false;

                // Consistent cleanup — same as AnimateAndHide
                DismissMergeState();
                CloseSearch();
                if (_isFilterBarActive) ToggleFilterBar(false);
                IsDragHovering = false;
                _viewModel.IsScrolling = false;
                _viewModel.AllowHover = true;
                _evictionBackgroundTimer?.Stop();
                _scrollLiveLoadTimer?.Stop();
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                // JITTER FIX: Hide via Win32 instead of moving to -20000
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                        Classes.NativeMethods.ShowWindow(hwnd, 0 /*SW_HIDE*/);
                }
                catch { }

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
            // FIX: Start from 0 (not 0.01) — DWM skips Mica glass composition at opacity=0,
            // but at 0.01 it forces per-frame Mica blending causing flickering artifacts.
            // Snappy animations: Both animations at 150ms for lightweight and ultra-responsive feel.
            _cachedOpacityAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            _cachedOpacityAnim.Freeze();

            _cachedSlideInAnim = new System.Windows.Media.Animation.DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            _cachedSlideInAnim.Freeze();
        }

        // [FIX STABLE-4]: CompositionTarget.Rendering handler for precise animation end detection
        private EventHandler? _showAnimRenderHandler;

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        private void PlayShowAnimation()
        {
            // Classes.SpawnDiagnostic.Instance.MarkPhase("PLAY_SHOW_ANIM");
            // Classes.SpawnDiagnostic.Instance.MarkEvent("ANIM_START");
            _isShowAnimating = true;
            _showAnimationEndTime = DateTime.UtcNow.AddMilliseconds(150);

            // Ensure WS_EX_LAYERED is set and opacity is 0 at the Win32 level BEFORE uncloaking.
            // This prevents a 1-2 frame flash of a solid window box when DWM uncloaks it
            // before WPF's composition thread has updated the layered window attributes.
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_LAYERED) == 0)
                    {
                        SetWindowLongSafe(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                    }
                    SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
                }
            }
            catch { }

            // ═══ DWM UNCLOAK ═══
            // Uncloak the window now that it is positioned and ready to fade in
            try
            {
                var hwndUncloak = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwndUncloak != IntPtr.Zero)
                {
                    int uncloakVal = 0;
                    DwmSetWindowAttribute(hwndUncloak, DWMWA_CLOAK, ref uncloakVal, sizeof(int));
                }
            }
            catch { }

            // ═══ MICA BACKDROP SUSPEND ═══
            // Temporarily disable Mica glass during animation to make the window
            // completely transparent (invisible) while RootContent.Opacity is 0.
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int backdropNone = 1; // DWMSBT_NONE
                    DwmSetWindowAttribute(hwnd, 38, ref backdropNone, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE
                }
            }
            catch { }

            // UseLayoutRounding stays true — the slide animation uses RenderTransform
            // (TranslateTransform) which bypasses layout rounding entirely. Previously,
            // disabling it caused icons/buttons to render at fractional sizes during
            // the 150ms animation, appearing visibly larger then snapping back.

            // ═══ AERO UI: Suspend decorative overlays during animation ═══
            // The AltClipboardPanel has 3 layered gradient borders (themed gradient,
            // arctic frost, inner glow) each with CornerRadius=14 + ClipToBounds.
            // These force WPF's compositor to do per-frame gradient blending + clipping.
            // Since the window starts at opacity=0, these are invisible during early frames.
            // Hiding them cuts compositing cost by ~60%.
            if (_isAltUIActive && AltArcticOverlay != null)
            {
                AltArcticOverlay.Visibility = Visibility.Collapsed;
            }

            // NOTE: Do NOT call UpdatePositionToLockedBottomEdge() here.
            // The initial positioning was already done in ShowNearPositionInternal using cached height.
            // Calling it again here with ActualHeight (which may differ) causes a visible position jump.

            // Reset the slide transform to start position.
            // CRITICAL: Don't reassign RenderTransform if it's already our cached instance —
            // reassigning invalidates the entire visual tree's cached render transform,
            // causing a DWM recomposition flash that looks like a tremor/shake.
            _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _cachedSlideTransform.Y = 0; // Keep at 0 to avoid slide offset
            if (!ReferenceEquals(RootContent.RenderTransform, _cachedSlideTransform))
                RootContent.RenderTransform = _cachedSlideTransform;

            // PERF: Use the FROZEN opacity animation directly — it runs entirely on WPF's
            // composition thread (GPU), immune to UI thread GC pauses. Previously we Clone()d
            // it to attach a Completed handler, but Clone() unfreezes the animation, pulling
            // it back to the UI thread where GC pauses cause 30-43ms frame drops.
            //
            // Instead, detect completion via a frame-rate independent timer.
            // Both animations are 150ms.
            // ═══ GREY-BOX PREVENTION: Reset any lingering scroll state ═══
            // If a previous session ended mid-scroll (especially via Hub button),
            // the scroll engine's CompositionTarget.Rendering may still be firing
            // with stale velocity/offset. Clear it before re-rendering.
            try
            {
                var sv = GetShelfScrollViewer();
                Classes.SmoothScroll.ResetScrollState(sv);
            }
            catch { }

            RootContent.BeginAnimation(UIElement.OpacityProperty, null);
            RootContent.Opacity = 1.0;
            this.BeginAnimation(OpacityProperty, _cachedOpacityAnim);
            _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, _cachedSlideInAnim);

            // ═══ COMPOSITOR FLUSH: Force a repaint on every spawn ═══
            // Without this, the DWM uncloak above races against WPF's compositor, causing
            // a half-rendered frame to be presented before the visual tree is fully flushed.
            // This shows as a "half grey box" that only resolves when the user interacts.
            // The scroll-nudge (+1/-1px) forces WPF's SurfacePresenter to fully re-compose
            // the render target, eliminating the stale cached frame. Runs at Background priority
            // so it does not block the animation's first frame.
            ForceFirstSpawnRepaint();

            // ═══ DEFERRED SCROLL-TO-TOP ═══
            // The early ScrollToTop in ShowNearPosition runs while the window is still offscreen
            // (Left=-20000), where WPF may skip layout updates. This deferred call at Loaded
            // priority runs after the window is repositioned onscreen and the visual tree is active,
            // guaranteeing the scroll position resets to 0 on every respawn.
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var sv2 = GetShelfScrollViewer();
                    if (sv2 != null && sv2.VerticalOffset > 0)
                    {
                        sv2.ScrollToVerticalOffset(0);
                        sv2.ScrollToTop();
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            int capturedGen = _spawnGeneration;
            // [FIX STABLE-4]: Use CompositionTarget.Rendering with timestamp for precise animation end detection
            // More accurate than DispatcherTimer which can drift under UI thread load
            // PC-6 FIX: Remove any stale handler from previous show cycle
            if (_showAnimRenderHandler != null)
            {
                CompositionTarget.Rendering -= _showAnimRenderHandler;
                _showAnimRenderHandler = null;
            }
            var animStartTime = DateTime.UtcNow;
            EventHandler onAnimRenderFrame = null!;
            onAnimRenderFrame = (s, ev) =>
            {
                if ((DateTime.UtcNow - animStartTime).TotalMilliseconds < 155) return; // 150ms + 5ms safety margin
                CompositionTarget.Rendering -= onAnimRenderFrame;
                _showAnimRenderHandler = null;
                // Bail if a new spawn started (stale handler) or if the window was dismissed
                if (_spawnGeneration != capturedGen || !_isCurrentlySummoned) return;

                _isShowAnimating = false;
                _isEdgeLocked = true;
                // Classes.SpawnDiagnostic.Instance.MarkPhase("ANIM_COMPLETE");
                // Classes.SpawnDiagnostic.Instance.MarkEvent("ANIM_DONE");
                // Stop diagnostic recording 200ms after completion to capture settle
                // var diagStopTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                // diagStopTimer.Tick += (_, _) => { diagStopTimer.Stop(); Classes.SpawnDiagnostic.Instance.StopRecording(); };
                // diagStopTimer.Start();
                _showAnimEndTime = DateTime.UtcNow; // Start 500ms post-animation cooldown for SizeChanged

                // Snap _lockedBottomEdge to the CURRENT window position instead of moving
                // the window. This prevents a visible jump when ActualHeight differs from
                // the cached height used during initial positioning.
                if (this.ActualHeight > 0)
                    _lockedBottomEdge = this.Top + this.ActualHeight + 20;

                // DEFERRED CLEANUP: Don't snap the transform or re-enable rounding on
                // the same frame — that fights the last animation frame and causes a
                // visible desync (slide snaps to 0 while opacity is still at 0.96).
                // Wait one more render frame for the animation clocks to fully settle.
                Dispatcher.InvokeAsync(() =>
                {
                    if (_spawnGeneration != capturedGen || !_isCurrentlySummoned) return;

                    // Clear animation clocks and snap to final values
                    _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                    _cachedSlideTransform.Y = 0;
                    RootContent.BeginAnimation(UIElement.OpacityProperty, null);
                    RootContent.Opacity = 1;
                    this.BeginAnimation(OpacityProperty, null);
                    this.Opacity = 1;
                    // UseLayoutRounding stays true throughout — no toggle needed

                    // ═══ MICA BACKDROP RESTORE ═══
                    // Re-enable Mica glass now that animation is done and all values are settled.
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            int backdropMica = 2; // DWMSBT_MAINWINDOW (Mica)
                            DwmSetWindowAttribute(hwnd, 38, ref backdropMica, sizeof(int));
                        }
                    }
                    catch { }

                    // ═══ AERO UI: Restore decorative overlays ═══
                    // Only show the white frost overlay for Default/ArcticSnow (light themes).
                    // For color-themed palettes, the themed gradient must remain visible.
                    if (_isAltUIActive && AltArcticOverlay != null)
                    {
                        string activeColorTheme = Classes.SettingsManager.Current.ColorThemeName ?? "Default";
                        bool isLightTheme = string.IsNullOrEmpty(activeColorTheme)
                                            || activeColorTheme.Equals("Default", System.StringComparison.OrdinalIgnoreCase)
                                            || activeColorTheme.Equals("ArcticSnow", System.StringComparison.OrdinalIgnoreCase);
                        AltArcticOverlay.Visibility = isLightTheme ? Visibility.Visible : Visibility.Collapsed;
                    }
                }, System.Windows.Threading.DispatcherPriority.Render);
            };
            CompositionTarget.Rendering += onAnimRenderFrame;
            _showAnimRenderHandler = onAnimRenderFrame; // PC-6: Track for cleanup
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
                    catch { } // Best-effort: failure is acceptable
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
            _isShowAnimating = false; // Reset show animating flag on dismiss
            UninstallKeyboardHook(); // Release arrow-key hook so keys return to the target app

            Classes.Logger.LogAction("VD_HIDE", $"AnimateAndHide | notes={_isNotesActive} todo={_isTodoActive} deskSwitchFlag={_desktopSwitchedSinceLastDismiss}");

            // ═══ CLOSE NOTES/TODO/RESEARCH ═══
            if (_isNotesActive || _isTodoActive || _isResearchActive)
            {
                // Always save which panel was active — ToggleMainClipboard decides whether to restore
                _lastPanelBeforeDismiss = _isNotesActive ? "notes" : _isTodoActive ? "todo" : "research";
                Classes.Logger.LogAction("VD_HIDE", $"Panel close: saved={_lastPanelBeforeDismiss}");

                if (_isNotesActive) CloseNotesPanel(immediate: true);
                if (_isTodoActive) CloseTodoPanel(immediate: true);
                if (_isResearchActive) CloseResearchPanel(immediate: true);
            }

            StopPanelAutoRevertTimer();
            _mascotDelayTimer?.Stop();
            _isAnimatingHide = false;
            _lastActualHeight = this.ActualHeight;

            DismissMergeState();
            CloseSearch();
            // Reset category filters on dismiss — prevents stale filter persisting
            // when Hub changes the filter while clipboard is hidden
            if (_activeCategoryFilter != null) ClearCategoryFilter();
            if (_altActiveCategory != null) ApplyAltCategoryFilter(null);
            if (_isFilterBarActive) ToggleFilterBar(false);

            // PC-8: Reset drag hover indicator
            IsDragHovering = false;

            // PC-9: Reset scroll/hover state so hover buttons work on re-summon
            _viewModel.IsScrolling = false;
            _viewModel.AllowHover = true;
            _viewModel?.CollapseAllExpandedItems();

            // PC-2/PC-3: Stop background timers that fire on hidden window
            _evictionBackgroundTimer?.Stop();
            _scrollLiveLoadTimer?.Stop();

            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            try
            {
                // Make window invisible
                RootContent.BeginAnimation(UIElement.OpacityProperty, null);
                RootContent.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                this.Opacity = 0;

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
                // STABILITY FIX: Cache GetShelfScrollViewer() — same race fix as summon path.
                try
                {
                    var sv = GetShelfScrollViewer();
                    Classes.SmoothScroll.ResetScrollState(sv);
                    if (ShelfListView.Items.Count > 0)
                        ShelfListView.SelectedIndex = 0;
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
            _hasOptimizedThisHide = true;
            OptimizeMemoryUsage();
        }
        private DateTime _spawnTime = DateTime.MinValue;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;
        private static int _clipboardWriteRefCount = 0;
        /// <summary>When true, clipboard monitoring is paused because Notes or Todo panel is open.</summary>
        internal static volatile bool _clipboardPanelSuppressed = false;
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
        private static System.Windows.Threading.DispatcherTimer? _clipboardWriteResetTimer;
        
        internal static void SetWritingClipboard(bool value)
        {
            if (value)
            {
                System.Threading.Interlocked.Increment(ref _clipboardWriteRefCount);
                lock (_timerLock)
                {
                    _clipboardWriteResetTimer?.Stop();
                    _clipboardWriteResetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
                    _clipboardWriteResetTimer.Tick += (s, e) =>
                    {
                        _clipboardWriteResetTimer?.Stop();
                        if (System.Threading.Volatile.Read(ref _clipboardWriteRefCount) > 0)
                        {
                            Classes.Logger.LogAction("CLIPBOARD", "⚠️ _isWritingClipboard was stuck true — auto-reset after 2s safety timeout");
                            System.Threading.Interlocked.Exchange(ref _clipboardWriteRefCount, 0);
                        }
                    };
                    _clipboardWriteResetTimer.Start();
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
                        _clipboardWriteResetTimer?.Stop();
                        _clipboardWriteResetTimer = null;
                    }
                }
            }
        }

        public void OptimizeMemoryUsage()
        {
            // 1. Evict non-pinned image/QR thumbnails to free heavy image memory, keeping top 6 always loaded
            //    Increasing this limit keeps more parsed templates and decoded images warm in RAM,
            //    completely hiding the sudden appearance/decode lags on summon.
            try
            {
                if (_viewModel?.DroppedItems != null)
                {
                    // Skip eviction when Hub window is visible — both windows share
                    // the same ClipboardItem objects, and nulling icons here blanks out
                    // thumbnails the Hub is actively displaying.
                    bool hubVisible = _hubWindowInstance != null && _hubWindowInstance.IsVisible;
                    if (!hubVisible)
                    {
                        int imageCount = 0;
                        foreach (var item in _viewModel.DroppedItems)
                        {
                            if (item == null) continue;

                            if (item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                            {
                                imageCount++;
                                if (imageCount <= 6)
                                {
                                    // Keep the top 6 images loaded in RAM to hide the sudden appearance on summon
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
            }
            catch { }

            // 2. GC + working set trim — aggressive memory reclaiming when unsummoned.
            //    Run a forced Gen 2 Garbage Collection to reclaim all WPF controls, resources, and image caches,
            //    then set working set limits to -1 to empty the working set and page out inactive memory.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        currentProcess.Refresh();
                        long workingSet = currentProcess.WorkingSet64;

                        // Only trim if working set is higher than 45MB
                        if (workingSet > 45 * 1024 * 1024)
                        {
                            // Removed forced GC.Collect — let the runtime manage memory naturally.
                            // Previous code caused unnecessary Gen2 collection pauses (10-100ms).

                            // Set working set floor to 20MB, ceiling to 50MB for aggressive idle trimming.
                            // 20MB keeps .NET runtime + core WPF resources resident, avoiding cold-start lag.
                            // 50MB ceiling (down from 80MB) lets OS reclaim more inactive pages at idle.
                            const nint MIN_WS = 20 * 1024 * 1024;   // 20 MB
                            const nint MAX_WS = 50 * 1024 * 1024;   // 50 MB
                            NativeMethods.SetProcessWorkingSetSize(currentProcess.Handle, MIN_WS, MAX_WS);
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable
            });
        }

        private IntPtr GetTargetForegroundWindow()
        {
            IntPtr ptr = GetForegroundWindow();
            
            var sbClass = new System.Text.StringBuilder(256);
            var sbTitle = new System.Text.StringBuilder(256);
            GetClassName(ptr, sbClass, 256);
            string className = sbClass.ToString();

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
                            sbClass.Clear();
                            GetClassName(wnd, sbClass, 256);
                            string cName = sbClass.ToString();
                            if (cName != "Shell_TrayWnd" && cName != "Shell_SecondaryTrayWnd" && cName != "WorkerW" && cName != "Progman")
                            {
                                sbTitle.Clear();
                                GetWindowText(wnd, sbTitle, 256);
                                if (sbTitle.Length > 0 && sbTitle.ToString() != "FlyShelf" && sbTitle.ToString() != "Program Manager")
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
