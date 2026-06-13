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
                        int cn = DWMWA_COLOR_NONE;
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
                        int cn = DWMWA_COLOR_NONE;
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
            bool lastActiveExtWasOnCurrent = _lastActiveExternalWindowWasOnCurrentAtSummon;
            double msSinceSpawn = (DateTime.Now - _spawnTime).TotalMilliseconds;

            // Run the check asynchronously on a background thread so we NEVER block the UI thread on focus changes
            try
            {
                var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (myHwnd != IntPtr.Zero)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // Wait for VDM state to settle — EVENT_SYSTEM_FOREGROUND fires
                            // before IVirtualDesktopManager is fully updated during desktop switches.
                            // Without this delay, GetWindowDesktopId returns stale/empty GUIDs.
                            System.Threading.Thread.Sleep(80);

                            var localVdm = (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
                            
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
                                    Application.Current.Dispatcher.InvokeAsync(() =>
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
                                        Application.Current.Dispatcher.InvokeAsync(() =>
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
                                        Application.Current.Dispatcher.InvokeAsync(() =>
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
                                        Application.Current.Dispatcher.InvokeAsync(() =>
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
                                    Application.Current.Dispatcher.InvokeAsync(() =>
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
                        catch { }
                    });
                }
            }
            catch { /* COM may fail on older Windows builds — silently ignore */ }
        }
        private bool _isPersistentMode = false;
        private bool _isAnimatingHide = false;
        private bool _isShowAnimating = false;
        private DateTime _showAnimationEndTime = DateTime.MinValue;
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

        // Cached timer for clearing _isShowAnimating after animation completes
        private System.Windows.Threading.DispatcherTimer? _showAnimEndTimer;

        /// <summary>Fast appear animation on inner content (preserves Mica glass).</summary>
        private void PlayShowAnimation()
        {
            // Classes.SpawnDiagnostic.Instance.MarkPhase("PLAY_SHOW_ANIM");
            // Classes.SpawnDiagnostic.Instance.MarkEvent("ANIM_START");
            _isShowAnimating = true;
            _showAnimationEndTime = DateTime.UtcNow.AddMilliseconds(150);

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

            // FIX 5: Disable UseLayoutRounding during animation to prevent integer-snap
            // oscillation. Fractional SlideY values (e.g., 4.78, 2.14) fight with rounding,
            // causing content to snap between pixels each frame = 1px jitter.
            RootContent.UseLayoutRounding = false;

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
            this.BeginAnimation(OpacityProperty, null);
            this.Opacity = 1.0;
            RootContent.BeginAnimation(UIElement.OpacityProperty, _cachedOpacityAnim);
            _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, _cachedSlideInAnim);

            int capturedGen = _spawnGeneration;
            var animTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render);
            animTimer.Interval = TimeSpan.FromMilliseconds(150);
            animTimer.Tick += (s, ev) =>
            {
                animTimer.Stop();
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
                    // Re-enable UseLayoutRounding now that everything is at integer positions
                    RootContent.UseLayoutRounding = true;

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
                    if (_isAltUIActive && AltArcticOverlay != null)
                    {
                        AltArcticOverlay.Visibility = Visibility.Visible;
                    }
                }, System.Windows.Threading.DispatcherPriority.Render);
            };
            animTimer.Start();
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
            _isShowAnimating = false; // Reset show animating flag on dismiss
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
            // 1. Evict non-pinned image/QR thumbnails to free heavy image memory, keeping top 6 always loaded
            //    Increasing this limit keeps more parsed templates and decoded images warm in RAM,
            //    completely hiding the sudden appearance/decode lags on summon.
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
            catch { }

            // 2. GC + working set trim — keep 60MB minimum to avoid cold-start animation jitter.
            //    By checking if process memory is already under 65MB, we avoid unnecessary collections,
            //    allowing WPF styles, visual templates, and asset caches to remain fully resident.
            //    If we exceed 65MB, we trim down to a 60MB floor.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        currentProcess.Refresh();
                        long workingSet = currentProcess.WorkingSet64;

                        // Only trim if working set is higher than 65MB
                        if (workingSet > 65 * 1024 * 1024)
                        {
                            // Gen 1 optimized collection — reclaims short-lived objects without freezing UI
                            System.GC.Collect(1, System.GCCollectionMode.Optimized, false);
                            System.GC.WaitForPendingFinalizers();

                            // Set working set floor to 60MB to ensure WPF pages and templates are kept resident
                            const nint MIN_WS = 60 * 1024 * 1024;   // 60 MB
                            const nint MAX_WS = 120 * 1024 * 1024;  // 120 MB
                            SetProcessWorkingSetSize(currentProcess.Handle, MIN_WS, MAX_WS);
                        }
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
