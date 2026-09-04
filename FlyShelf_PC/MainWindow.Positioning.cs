using FlyShelf.ViewModels;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf
{
    /// <summary>
    /// MainWindow partial — Window Positioning, Spawn Logic, Focus Management, Scroll & Thumbnails.
    /// Contains: ShowNearPosition, FocusFirstItemContainer, ShelfListView_ScrollChanged,
    ///           ShelfListView_MouseLeave, RenderVisibleThumbnails, SuspendThemeAnimations, ResumeThemeAnimations.
    /// </summary>
    public partial class MainWindow
    {
        // Cached frozen easing function — shared across all thumbnail fade-in animations (FIX M5)
        private static readonly System.Windows.Media.Animation.CubicEase s_cachedEaseOut =
            CreateFrozenEaseOut();
        private static System.Windows.Media.Animation.CubicEase CreateFrozenEaseOut()
        {
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        // [FIX ANIM-10]: Cached frozen icon fade-in animation — avoids per-load DoubleAnimation allocation
        private static readonly System.Windows.Media.Animation.DoubleAnimation s_iconFadeIn = CreateIconFadeIn();
        private static System.Windows.Media.Animation.DoubleAnimation CreateIconFadeIn()
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = s_cachedEaseOut
            };
            anim.Freeze();
            return anim;
        }
        private System.Windows.Rect GetWorkAreaForPoint(double x, double y)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            // Use Win32 MonitorFromPoint + GetMonitorInfo for correct multi-monitor work area
            var pt = new Classes.NativeMethods.POINT { X = (int)(x * dpi.DpiScaleX), Y = (int)(y * dpi.DpiScaleY) };
            IntPtr hMonitor = Classes.NativeMethods.MonitorFromPoint(pt, Classes.NativeMethods.MonitorFromWindowFlags.DEFAULTTONEAREST);
            var mi = new Classes.NativeMethods.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<Classes.NativeMethods.MONITORINFOEX>();
            if (Classes.NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                var rc = mi.rcWork;
                return new System.Windows.Rect(rc.Left / dpi.DpiScaleX, rc.Top / dpi.DpiScaleY, (rc.Right - rc.Left) / dpi.DpiScaleX, (rc.Bottom - rc.Top) / dpi.DpiScaleY);
            }
            return SystemParameters.WorkArea; // fallback
        }
        private DateTime _lastSortContextTime = DateTime.MinValue;

        private bool _borderColorSet = false;

        // ═══ Low-Level Keyboard Hook for no-focus arrow navigation ═══
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private IntPtr _keyboardHookId = IntPtr.Zero;
        private Classes.NativeMethods.LowLevelKeyboardProc? _keyboardHookProc;

        // ═══ Low-Level Mouse Hook for click-to-release arrow ownership ═══
        private IntPtr _mouseHookId = IntPtr.Zero;
        private Classes.NativeMethods.LowLevelMouseProc? _mouseHookProc;
        /// <summary>When true, the keyboard hook intercepts Up/Down/Enter/Escape for clipboard navigation.
        /// Starts true when the clipboard is summoned. Set to false when the user clicks outside
        /// the clipboard window (giving arrows back to the target app). Set back to true when
        /// the user clicks on the clipboard window.</summary>
        private bool _hookOwnsArrows = true;

        /// <summary>True when the clipboard was spawned in no-focus mode (stealFocus=false).
        /// Used by CopyItemAndPaste to skip SetForegroundWindow since the target app already has focus.</summary>
        private bool _spawnedWithoutFocus = false;
        /// <summary>False until the first spawn animation completes. Used to allow extra
        /// render frames on the very first spawn so WPF's initial layout pass finishes
        /// before the fade-in animation starts (prevents first-spawn jitter).</summary>
        private bool _hasCompletedFirstSpawn = false;
        private bool _needsDwmDesktopSettleWait = false;

        private bool MoveToCurrentVirtualDesktop(IntPtr hwnd, bool force = false)
        {
            try
            {
                var vdm = GetVirtualDesktopManager();
                if (vdm != null)
                {
                    // ALWAYS check if window is on current desktop — pinned windows return true.
                    // Skip all heavy lifting if it's already visible here.
                    if (IsWindowOnCurrentVirtualDesktop(hwnd))
                    {
                        Classes.Logger.LogAction("DESKTOP", "Window already on current virtual desktop.");
                        return true;
                    }

                    // Try COM move
                    Guid desktopId = _currentDesktopId;
                    if (desktopId == Guid.Empty)
                    {
                        IntPtr fg = GetForegroundWindow();
                        if (fg != IntPtr.Zero && fg != hwnd)
                            vdm.GetWindowDesktopId(fg, out desktopId);
                    }

                    if (desktopId != Guid.Empty)
                    {
                        int hr = vdm.MoveWindowToDesktop(hwnd, ref desktopId);
                        Classes.Logger.LogAction("DESKTOP", $"MoveWindowToDesktop HR=0x{hr:X8} target={desktopId}");
                        _summonedDesktopId = desktopId;
                        _currentDesktopId = desktopId;
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("DESKTOP_ERR", $"COM move error: {ex.Message}");
            }

            // Fallback: Hide+Show cycle to force DWM re-association
            // Only runs if IsWindowOnCurrentVirtualDesktop returned false above.
            try
            {
                Classes.Logger.LogAction("DESKTOP", "Forcing Hide+Show cycle for DWM re-association.");
                Classes.NativeMethods.ShowWindow(hwnd, 0 /*SW_HIDE*/);
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle | WS_EX_APPWINDOW) & ~WS_EX_NOACTIVATE);
                Classes.NativeMethods.ShowWindow(hwnd, 5 /*SW_SHOW*/);
                exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle & ~WS_EX_APPWINDOW) | WS_EX_NOACTIVATE);
                Classes.NativeMethods.SetWindowPos(hwnd,
                    -1 /*HWND_TOPMOST*/, 0, 0, 0, 0,
                    Classes.NativeMethods.SWP_NOMOVE | Classes.NativeMethods.SWP_NOSIZE |
                    Classes.NativeMethods.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
                Classes.Logger.LogAction("DESKTOP", "Hide+Show cycle complete.");
                return true;
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("DESKTOP_ERR", $"Hide+Show cycle error: {ex.Message}");
            }
            return false;
        }

        public void ShowNearPosition(double targetX, double targetY, int mode = 0, bool isPersistent = false, bool stealFocus = true, bool? knownOnOtherDesktop = null)
        {
            _hasOptimizedThisHide = false;
            Classes.Logger.LogAction("TELEMETRY", $"ShowNearPosition entered, mode={mode}, isPersistent={isPersistent}, stealFocus={stealFocus}");
            Classes.SpawnProfiler.Instance.BeginSpawn(this);
            
            // ═══ DESKTOP SWITCH JITTER PREVENTER ═══
            // If we are summoning the window after a virtual desktop switch, DWM requires
            // some time to register/reallocate the redirection surface and complete the active desktop switch transition.
            // We set _needsDwmDesktopSettleWait = true to defer the animation start by 100ms
            // while keeping the window at Opacity=0. This lets DWM settle and prevents any frame drops during the animation.
            bool isOnOtherDesktop = knownOnOtherDesktop == true || 
                                    (_summonedDesktopId != Guid.Empty &&
                                     _currentDesktopId != Guid.Empty &&
                                     _currentDesktopId != _summonedDesktopId);

            if (isOnOtherDesktop || _desktopSwitchedSinceLastDismiss)
            {
                _needsDwmDesktopSettleWait = true;
                _desktopSwitchedSinceLastDismiss = false;
                Classes.Logger.LogAction("DESKTOP", "Desktop switch detected during summon — setting _needsDwmDesktopSettleWait to true.");
            }

            // NOTE: VD handling removed — window is always pinned to all virtual desktops
            // (WS_EX_APPWINDOW is never set), so IsWindowOnCurrentVirtualDesktop always returns true.


            if (mode == 0)
            {
                EnsureClipboardMode();
            }
            Classes.SpawnProfiler.Instance.Mark("ENSURE_CLIPBOARD_MODE");
            CloseSearch();
            CloseEmojiPicker();
            Classes.SpawnProfiler.Instance.Mark("CLOSE_SEARCH_EMOJI");

            // ═══ SCROLL RESET ON EVERY SUMMON ═══
            // Previously scroll was only reset in AnimateAndHide (dismiss), but if the dismiss
            // sequence was interrupted or SmoothScroll had residual state, the offset persisted.
            // Always scroll to top on summon so the user sees the most recent card first.
            // STABILITY FIX: Cache GetShelfScrollViewer() — the old double-call could return
            // null on the first call (window not yet in visual tree), skipping ResetScrollState
            // while the second call succeeded, leaving residual SmoothScroll velocity.
            try
            {
                _viewModel?.CollapseAllExpandedItems();
                var sv = GetShelfScrollViewer();
                Classes.SmoothScroll.ResetScrollState(sv);
                if (sv != null)
                {
                    sv.ScrollToVerticalOffset(0);
                    sv.ScrollToTop();
                }
                if (ShelfListView.Items.Count > 0)
                    ShelfListView.SelectedIndex = 0;
            }
            catch { }

            // Increment spawn token at the very beginning of the summon sequence.
            // This immediately invalidates any active or pending dismiss/hide animation callbacks.
            ++_spawnToken;

            // PERF: Use cached foreground window — avoids expensive EnumWindows P/Invoke scan
            _previousForegroundWindow = _lastActiveExternalWindow != IntPtr.Zero && IsWindow(_lastActiveExternalWindow)
                ? _lastActiveExternalWindow
                : GetForegroundWindow();

            _spawnTime = DateTime.Now;
            _isPersistentMode = isPersistent;

            // JITTER FIX: Disable edge-locking and mark show-animating BEFORE any mode change
            // or layout work. Without this, the SizeChanged handler fires during mode setup
            // with _isEdgeLocked=true from the PREVIOUS spawn, causing a 120px position bounce.
            _isEdgeLocked = false;
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
                _isShowAnimating = true;

            Classes.SpawnProfiler.Instance.Mark("PRE_ABORT_HIDE");

            bool needsDefer = false;

            // Abort hide animation if one is actively running
            if (_isAnimatingHide)
            {
                _isAnimatingHide = false;
                needsDefer = true;
                try
                {
                    // Set base opacity to 0 and clear animation clocks immediately.
                    this.Opacity = 0;
                    this.BeginAnimation(OpacityProperty, null);
                    if (RootContent.RenderTransform is TranslateTransform tt)
                    {
                        tt.BeginAnimation(TranslateTransform.YProperty, null);
                        tt.Y = 0;
                    }
                    RootContent.Opacity = 1;
                    _isCurrentlySummoned = false; // Bypass guard
                    HideWindowInternal(); // Move offscreen immediately to hide from the DWM composition surface
                }
                catch { } // Best-effort: failure is acceptable
            }

            if (_isCurrentlySummoned)
            {
                needsDefer = true;
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                RootContent.Opacity = 1;
                _cachedSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _cachedSlideTransform.Y = 0;
                if (!ReferenceEquals(RootContent.RenderTransform, _cachedSlideTransform))
                    RootContent.RenderTransform = _cachedSlideTransform;
                _isCurrentlySummoned = false; // Bypass guard
                HideWindowInternal(); // Move offscreen immediately to hide from the DWM composition surface
            }

            Classes.SpawnProfiler.Instance.Mark("PRE_SET_MODE");
            // PERF: Set mode before internal call — needed for layout calculations
            _viewModel.CurrentMode = mode;
            Classes.SpawnProfiler.Instance.Mark("SET_MODE");

            // Capture parameters for background callback
            double finalX = targetX;
            double finalY = targetY;
            int finalMode = mode;
            bool finalPersistent = isPersistent;
            bool finalStealFocus = stealFocus;

            if (needsDefer)
            {
                Classes.Logger.LogAction("TELEMETRY", "Deferring ShowNearPositionInternal to Background priority to prevent re-summon flashes.");
                Dispatcher.InvokeAsync(() =>
                {
                    ShowNearPositionInternal(finalX, finalY, finalMode, finalPersistent, finalStealFocus);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                ShowNearPositionInternal(targetX, targetY, mode, isPersistent, stealFocus);
            }
        }

        private void ShowNearPositionInternal(double targetX, double targetY, int mode, bool isPersistent, bool stealFocus)
        {
            // Safety: clear any stale DWM cloak from aborted previous animation
            var safetyHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (safetyHwnd != IntPtr.Zero)
            {
                int uncloakVal = 0;
                Classes.NativeMethods.DwmSetWindowAttribute(safetyHwnd, Classes.NativeMethods.DWMWA_CLOAK, ref uncloakVal, sizeof(int));
            }
            // NOTE: DWM cloaking was previously used here to hide positioning artifacts,
            // but it caused the fade-in animation to be invisible for the first ~20-30%,
            // making the spawn feel laggy/jumpy. The anti-black-box sequence below
            // (opacity=0 → start animation → move onscreen) handles this correctly
            // without cloaking — the first visible frame is already at ~0% opacity.

            // PERF: Capture the virtual desktop ID asynchronously — these COM calls can take
            // 10-100ms+ when Explorer is busy during desktop switches. The desktop ID is only
            // used for dismiss logic, not for the actual spawn, so deferring is safe.
            // Seed from _currentDesktopId (continuously updated by ForegroundChangedCallback).
            // NEVER reset to Guid.Empty — that breaks all desktop switch detection.
            // The async Task.Run below will refine this if possible.
            _summonedDesktopId = _currentDesktopId;
            System.Threading.Volatile.Write(ref _lastActiveExternalWindowWasOnCurrentAtSummon, false);
            IntPtr capturedFg = GetForegroundWindow();
            IntPtr capturedLastExternal = _lastActiveExternalWindow;
            IntPtr hwndCopyForDesktop = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Guid seededDesktopId = _summonedDesktopId; // Capture the seed — don't overwrite if already valid
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // COM object must be created per-call inside Task.Run
                    var bgVdm = (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
                    
                    // Capture _lastActiveExternalWindowWasOnCurrentAtSummon for callback use
                    if (capturedLastExternal != IntPtr.Zero && IsWindow(capturedLastExternal))
                    {
                        int hrCheck = bgVdm.IsWindowOnCurrentVirtualDesktop(capturedLastExternal, out int onCurrent);
                        if (hrCheck == 0 && onCurrent != 0)
                        {
                            System.Threading.Volatile.Write(ref _lastActiveExternalWindowWasOnCurrentAtSummon, true);
                            // Only update _summonedDesktopId if it's still Empty (wasn't set by MoveWindowToDesktop)
                            if (_summonedDesktopId == Guid.Empty)
                            {
                                bgVdm.GetWindowDesktopId(capturedLastExternal, out Guid dId);
                                if (dId != Guid.Empty)
                                {
                                    Dispatcher.InvokeAsync(() =>
                                    {
                                        _summonedDesktopId = dId;
                                        _currentDesktopId = dId;
                                    });
                                }
                            }
                        }
                    }

                    // Fallback: try the foreground window (but skip if it's our own handle)
                    if (_summonedDesktopId == Guid.Empty && capturedFg != IntPtr.Zero && capturedFg != hwndCopyForDesktop)
                    {
                        bgVdm.GetWindowDesktopId(capturedFg, out Guid desktopId);
                        if (desktopId != Guid.Empty)
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                _summonedDesktopId = desktopId;
                                _currentDesktopId = desktopId;
                            });
                        }
                    }
                    
                    // Sync current desktop ID with summoned ID if we have it
                    Guid currentSummonedId = _summonedDesktopId;
                    if (currentSummonedId != Guid.Empty)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            _currentDesktopId = currentSummonedId;
                        });
                    }

                    Classes.Logger.LogAction("DESKTOP", $"Summoned on virtual desktop: {_summonedDesktopId}, prevWindowWasOnCurrent: {System.Threading.Volatile.Read(ref _lastActiveExternalWindowWasOnCurrentAtSummon)}");
                }
                catch (Exception ex)
                {
                    // COM call failed
                    Classes.Logger.LogAction("DESKTOP_ERR", $"Failed to capture summoned desktop ID: {ex.Message}");
                }
            });

            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }

            // PERF: Removed ShowInTaskbar toggle — it destroys/recreates the Win32 HWND (200-500ms penalty)

            _isSuppressingSizeSync = true;
            try
            {
                _viewModel.CurrentMode = mode;
                this.MaxHeight = _viewModel.CurrentFlyShelfMaxHeight;
                this.Width = _viewModel.CurrentFlyShelfWidth;

                // Force a deterministic height so the window doesn't bounce around with SizeToContent
                if (mode == 0)
                {
                    // Mini mode: let content drive height, capped by MaxHeight
                    if (this.SizeToContent != SizeToContent.Height)
                        this.SizeToContent = SizeToContent.Height;
                    if (!double.IsNaN(this.Height))
                        this.Height = double.NaN;
                }
                else
                {
                    // Mode 1/2: use the stored height exactly — no content-driven fluctuation
                    if (this.SizeToContent != SizeToContent.Manual)
                        this.SizeToContent = SizeToContent.Manual;
                    if (this.Height != (double)_viewModel.CurrentFlyShelfMaxHeight)
                        this.Height = _viewModel.CurrentFlyShelfMaxHeight;
                }

                UpdateToolbarButtonsVisibility();
                Classes.SpawnProfiler.Instance.Mark("LAYOUT_SETUP");
                // PERF: Skip UpdateLayout() here — already done offscreen in ShowNearPosition()
                // before the deferred callback. This eliminates the duplicate layout pass.
            }
            finally
            {
                _isSuppressingSizeSync = false;
            }

            var workArea = GetWorkAreaForPoint(targetX, targetY);
            double safeWidth = double.IsNaN(this.Width) ? 360 : this.Width;
            if (safeWidth <= 0) safeWidth = 320;

            // SAFETY FALLBACK: If coordinates are uninitialized or invalid (e.g. -1 or NaN), default to bottom-left corner
            if (targetX == -1 || targetY == -1 || double.IsNaN(targetX) || double.IsNaN(targetY))
            {
                targetX = workArea.Left + 16 + (safeWidth / 2);
                targetY = workArea.Top + workArea.Height;
                Classes.Logger.LogAction("POSITION_FALLBACK", $"Invalid coords overridden to bottom-left fallback: X={targetX}, Y={targetY}");
            }

            double rawX = targetX - (safeWidth / 2);
            if (rawX + safeWidth > workArea.Left + workArea.Width - 16)
                rawX = workArea.Left + workArea.Width - safeWidth - 16;
            if (rawX < workArea.Left + 16)
                rawX = workArea.Left + 16;

            double rawY = targetY - 16;
            // Clamp bottom: window bottom edge must stay above the bottom of the work area
            if (rawY > workArea.Top + workArea.Height - 16)
                rawY = workArea.Top + workArea.Height - 16;

            // PERF: Use hardcoded cached physical height on startup or already-resolved ActualHeight.
            // This completely eliminates dynamic spawning height calculations and spawning jumps!
            double realHeight = this.ActualHeight > 0 ? this.ActualHeight : 
                (_lastActualHeight > 0 ? _lastActualHeight : 
                (double.IsNaN(this.Height) ? FlyShelf.Classes.SettingsManager.Current.MiniFormHeight : this.Height));

            // SAFETY: If all height caches are empty/zero (e.g. very first summon before layout),
            // use a sane default so the minBottomEdge clamp actually works.
            if (realHeight <= 0 || double.IsNaN(realHeight))
                realHeight = 400;

            // CRITICAL FIX: Ensure the bottom edge is far enough down so the full window
            // fits within the work area. Without this, shaking near the top of the screen
            // pushes the window's Top above workArea.Top, rendering it half off-screen.
            double minBottomEdge = workArea.Top + realHeight + 36; // 16px top margin + 20px bottom offset
            // Also cap the bottom edge so the window doesn't extend below the work area
            double maxBottomEdge = workArea.Top + workArea.Height - 16;
            if (rawY < minBottomEdge)
                rawY = minBottomEdge;
            if (rawY > maxBottomEdge)
                rawY = maxBottomEdge;

            _lockedBottomEdge = rawY;
            // _isEdgeLocked already set to false at the top of ShowNearPosition (before mode change)

            // ═══ CRITICAL: JITTER-FREE SPAWN SEQUENCE ═══
            // Order: Position → Activate → Animate
            // Profiler analysis: Starting animation BEFORE positioning causes a 29ms DWM stall
            // when the window moves from -20000 to visible area (DWM must composite the new window).
            // By positioning FIRST at opacity=0 (invisible), DWM settles before animation starts.

            this.ShowActivated = stealFocus;
            _spawnedWithoutFocus = !stealFocus;

            // 1. Clear old animation clocks. Keep window completely invisible during positioning to prevent solid box flash.
            RootContent.BeginAnimation(UIElement.OpacityProperty, null);
            RootContent.Opacity = 1.0; 
            this.BeginAnimation(OpacityProperty, null);
            this.Opacity = 0; // True zero on window so it is invisible

            // Force zero opacity at Win32 level immediately before moving/showing
            try
            {
                var hwndLayered = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwndLayered != IntPtr.Zero)
                {
                    int exStyle = GetWindowLong(hwndLayered, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_LAYERED) == 0)
                    {
                        SetWindowLong(hwndLayered, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                    }
                    SetLayeredWindowAttributes(hwndLayered, 0, 0, LWA_ALPHA);
                }
            }
            catch { }

            // Preemptively cloak the window at the DWM level so it is 100% invisible 
            // when moved onscreen and shown, preventing any flash of a black/white border box.
            try
            {
                var hwndCloak = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwndCloak != IntPtr.Zero)
                {
                    int cloakVal = 1;
                    DwmSetWindowAttribute(hwndCloak, DWMWA_CLOAK, ref cloakVal, sizeof(int));
                }
            }
            catch { }

            // Preemptively suspend Mica backdrop before moving onscreen to avoid backdrop pop
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int backdropNone = 1; // DWMSBT_NONE
                    DwmSetWindowAttribute(hwnd, 38, ref backdropNone, sizeof(int));
                }
            }
            catch { }

            // ═══ PRE-EMPTIVE GC: Drain Gen0 pressure before animation ═══
            // Diagnostic logs show intermittent 26ms GC pauses between BeginAnimation()
            // and the first render frame. Clean spawns get 2 frames at opacity=0 (GC happened
            // before), jittery spawns lose a frame to GC (first visible frame jumps to 0.30).
            // Running Gen0 GC now (while still invisible) prevents GC during the animation.
            // GC.Collect(0, GCCollectionMode.Optimized, false, false);

            // Classes.SpawnProfiler.Instance.Mark("CLEAR_ANIM_CLOCKS");

            // ═══ SPAWN DIAGNOSTIC: Wire up & start recording ═══
            /*
            var diag = Classes.SpawnDiagnostic.Instance;
            diag.GetIsShowAnimating = () => _isShowAnimating;
            diag.GetIsCurrentlySummoned = () => _isCurrentlySummoned;
            diag.GetIsEdgeLocked = () => _isEdgeLocked;
            diag.GetIsAnimatingHide = () => _isAnimatingHide;
            diag.GetSpawnGeneration = () => _spawnGeneration;
            diag.GetIsNotesActive = () => _isNotesActive;
            diag.GetIsTodoActive = () => _isTodoActive;
            diag.GetLockedBottomEdge = () => _lockedBottomEdge;
            diag.GetLastActualHeight = () => _lastActualHeight;
            diag.GetRootContent = () => RootContent;
            diag.GetNotesPanel = () => NotesPanel;
            diag.GetTodoPanel = () => TodoPanel;
            diag.GetShelfListView = () => ShelfListView;
            diag.MarkPhase("CLEAR_ANIM");
            diag.BeginRecording(this);
            */

            // ═══ ELEMENT POSITION TRACKER: Per-element jitter detection ═══
            /*
            var ept = Classes.ElementPositionTracker.Instance;
            ept.GetSlideTransformY = () => _cachedSlideTransform.Y;
            // Register all major elements — first time only
            if (!_elementsRegistered)
            {
                _elementsRegistered = true;
                ept.RegisterElement("RootContent", () => RootContent);
                ept.RegisterElement("HeaderStack", () => HeaderAndFiltersStack);
                ept.RegisterElement("TopHeaderGrid", () => TopHeaderGrid);
                ept.RegisterElement("SearchToggle", () => SearchToggleBtn);
                ept.RegisterElement("NotesToggle", () => NotesToggleBtn);
                ept.RegisterElement("ShelfListView", () => ShelfListView);
                ept.RegisterElement("NotesPanel", () => NotesPanel);
                ept.RegisterElement("TodoPanel", () => TodoPanel);
                ept.RegisterElement("AltPanel", () => AltClipboardPanel);
                ept.RegisterElement("AltListView", () => AltShelfListView);
            }
            ept.MarkPhase("SETUP");
            ept.BeginRecording(this);
            */
            
            // Reset the cached slide transform instead of setting RenderTransform=null.
            // Setting null invalidates the entire render tree; resetting Y is a no-op.
            if (RootContent.RenderTransform is TranslateTransform existingTT)
            {
                existingTT.BeginAnimation(TranslateTransform.YProperty, null);
                existingTT.Y = 0;
            }
            RootContent.Opacity = 1;
            _spawnGeneration++;
            _isCurrentlySummoned = true;

            // ═══ ALWAYS SCROLL TO TOP ON OPEN ═══
            // User expects to see the most recent clipboard item first.
            try
            {
                Classes.SmoothScroll.ResetScrollState(GetShelfScrollViewer());
                var svTop = GetShelfScrollViewer();
                if (svTop != null)
                {
                    svTop.ScrollToVerticalOffset(0);
                    svTop.ScrollToTop();
                }
            }
            catch { }
            // _isShowAnimating already set to true at the top of ShowNearPosition (before mode change)
            // Ensure it's still true here (belt-and-suspenders):
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
                _isShowAnimating = true;

            double computedTop = _lockedBottomEdge - realHeight - 20;
            if (computedTop < workArea.Top + 16)
                computedTop = workArea.Top + 16;
            if (computedTop + realHeight > workArea.Top + workArea.Height - 16)
                computedTop = workArea.Top + workArea.Height - realHeight - 16;

            // 2. Move onscreen FIRST — window is at opacity=0, completely invisible.
            //    Setting Left/Top sequentially in WPF causes two distinct Win32 SetWindowPos calls,
            //    which triggers activation fights and causes layout/rendering stutters.
            //    Instead, we perform a single, atomic native SetWindowPos call to move the window.
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                    int x = (int)Math.Round(rawX * dpi.DpiScaleX);
                    int y = (int)Math.Round(computedTop * dpi.DpiScaleY);
                    
                    uint flags = Classes.NativeMethods.SWP_NOSIZE | Classes.NativeMethods.SWP_SHOWWINDOW;
                    if (!stealFocus)
                    {
                        flags |= Classes.NativeMethods.SWP_NOACTIVATE;
                    }
                    
                    Classes.NativeMethods.SetWindowPos(hwnd, -1 /*HWND_TOPMOST*/, x, y, 0, 0, flags);
                    
                    if (!stealFocus)
                    {
                        // Force WPF HWND Z-order re-evaluation without taking keyboard focus
                        this.Topmost = false;
                        this.Topmost = true;
                    }

                    // Sync WPF properties (will be a no-op if already updated by WM_WINDOWPOSCHANGED)
                    this.Left = Math.Round(rawX);
                    this.Top = Math.Round(computedTop);
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("SPAWN_ERR", $"SetWindowPos failed: {ex.Message}");
                this.Left = Math.Round(rawX);
                this.Top = Math.Round(computedTop);
            }

            // 3. Bring to front — window is positioned but still invisible (opacity=0).
            if (stealFocus)
            {
                this.Activate();
            }
            // diag.MarkPhase("ACTIVATED");
            // diag.MarkEvent("ACTIVATION_DONE");
            // Classes.SpawnProfiler.Instance.Mark("ACTIVATION_DONE");

            // 4. Start animation.
            if (Classes.SettingsManager.Current.EnableSummonAnimations)
            {
                if (_needsDwmDesktopSettleWait)
                {
                    _needsDwmDesktopSettleWait = false;
                    Classes.Logger.LogAction("DESKTOP", "Slow Path: Deferring animation start by 50ms for DWM virtual desktop settle.");
                    
                    // Force the window to be onscreen but invisible (Opacity = 0)
                    this.Opacity = 0;
 
                    // Wait 50ms at Opacity=0 to let DWM settle on the new desktop
                    var settleTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render);
                    settleTimer.Interval = TimeSpan.FromMilliseconds(50);
                    int capturedSpawnGen = _spawnGeneration;
                    settleTimer.Tick += (s, ev) =>
                    {
                        settleTimer.Stop();
                        if (_spawnGeneration != capturedSpawnGen || !_isCurrentlySummoned) return; // stale summon or dismissed
                        
                        Classes.Logger.LogAction("DESKTOP", "Slow Path: 50ms wait complete, starting animation.");
                        PlayShowAnimation();
                    };
                    settleTimer.Start();
                }
                else if (_hasCompletedFirstSpawn)
                {
                    // ═══ FAST PATH (subsequent spawns) ═══
                    // Play animation IMMEDIATELY — no render frame wait.
                    // DWM transitions are disabled, window is already positioned,
                    // and the visual tree is warm. Waiting a frame here creates
                    // a visible 30ms gap (the jitter the user sees).
                    // ForceFirstSpawnRepaint() is now called inside PlayShowAnimation() itself
                    // to ensure the compositor flush happens on EVERY spawn, not just the first.
                    PlayShowAnimation();
                    Classes.SpawnProfiler.Instance.Mark("PLAY_SHOW_ANIMATION");
                }
                else
                {
                    // ═══ FIRST SPAWN PATH ═══
                    // Wait exactly ONE render frame for WPF to realize the visual tree.
                    // On first spawn, the visual tree is cold (no items templated yet).
                    int capturedSpawnGen = _spawnGeneration;
                    EventHandler renderHandler = null!;
                    renderHandler = (s, ev) =>
                    {
                        System.Windows.Media.CompositionTarget.Rendering -= renderHandler;
                        if (_spawnGeneration != capturedSpawnGen || !_isCurrentlySummoned) return; // stale or dismissed
 
                        _hasCompletedFirstSpawn = true;
                        PlayShowAnimation(); // ForceFirstSpawnRepaint() is called inside
                        Classes.SpawnProfiler.Instance.Mark("PLAY_SHOW_ANIMATION");
                    };
                    System.Windows.Media.CompositionTarget.Rendering += renderHandler;
                }
            }
            else
            {
                // Force 100% opacity at Win32 level before uncloaking in no-animation path
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                        if ((exStyle & WS_EX_LAYERED) == 0)
                        {
                            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                        }
                        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                    }
                }
                catch { }

                // Uncloak for no-animation path
                try
                {
                    var hwndUncloak = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwndUncloak != IntPtr.Zero)
                    {
                        int uncloakVal = 0;
                        DwmSetWindowAttribute(hwndUncloak, DWMWA_CLOAK, ref uncloakVal, sizeof(int));
                    }
                }
                catch { } // Best-effort: failure is acceptable

                this.Opacity = 1.0;
                _isEdgeLocked = true;
                UpdatePositionToLockedBottomEdge();
            }


            // Explicitly set DWM border color synchronously on each summon to prevent OS/MicaWPF composition resets.
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

            // Explicitly set DWM border color on each summon to prevent OS/MicaWPF composition resets.
            // PERF: Defer to Background priority so it runs after the spawn animation is fully started.
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
            }, System.Windows.Threading.DispatcherPriority.Background);

            // Defer mascot/HQ rendering to Background priority — avoids blocking spawn animation
            Dispatcher.InvokeAsync(() =>
            {

                // PERF: Do NOT resume mascot/GIF immediately — let the clipboard spawn lag-free first.
                // Defer mascot + wallpaper GIF start to perfectly sync with the end of the 1000ms appear transition.
                _mascotDelayTimer?.Stop();
                if (_mascotDelayTimer == null)
                {
                    _mascotDelayTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(1000)
                    };
                    _mascotDelayTimer.Tick += (s, ev) =>
                    {
                        _mascotDelayTimer.Stop();
                        if (!_isCurrentlySummoned || _isAnimatingHide) return; // Window was dismissed before timer fired
                        try
                        {
                            var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                            animator?.Play();
                            MascotIdle.ResumePlayback();
                            Classes.AnimationTriggerService.Instance.StartIdleAnimation();
                        }
                        catch { } // Best-effort: failure is acceptable
                    };
                }
                _mascotDelayTimer.Start();

                // Trigger visible high-quality render after the spawn animation completes (300ms)
                if (_scrollHighQualityTimer == null)
                {
                    _scrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer();
                    _scrollHighQualityTimer.Tick += (s, ev) =>
                    {
                        _scrollHighQualityTimer.Stop();
                        RenderVisibleThumbnails(onlyFirstTen: false);
                    };
                }
                else
                {
                    _scrollHighQualityTimer.Stop();
                }
                _scrollHighQualityTimer.Interval = TimeSpan.FromMilliseconds(300);
                _scrollHighQualityTimer.Start();

                // NOTE: DWM uncloaking was previously here but has been removed.
                // The anti-black-box spawn sequence handles visibility correctly without cloaking.
            }, System.Windows.Threading.DispatcherPriority.Background);

            int currentToken = _spawnToken;

            // Give keyboard focus to the ListView so arrow keys + Enter work immediately
            // PERF: Defer focus to Background priority so it runs after layout + animation
            if (stealFocus)
            {
                Dispatcher.InvokeAsync(() => FocusFirstItemContainer(),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                // No-focus mode: install a low-level keyboard hook so arrow keys
                // navigate the clipboard list without stealing focus from the target app.
                InstallKeyboardHook();

                // Pre-select the first item so the user sees what will be pasted
                Dispatcher.InvokeAsync(() =>
                {
                    if (_viewModel.DroppedItems.Count > 0 && ShelfListView.SelectedIndex < 0)
                        ShelfListView.SelectedIndex = 0;
                }, System.Windows.Threading.DispatcherPriority.Background);
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
                if (index == 0)
                {
                    var sv = GetShelfScrollViewer();
                    sv?.ScrollToTop();
                }
                else
                {
                    ShelfListView.ScrollIntoView(container);
                }
            }
            else
            {
                // Otherwise, register event handler to focus as soon as they are ready:
                EventHandler? statusHandler = null;
                var timeoutTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                statusHandler = (s, ev) =>
                {
                    if (ShelfListView.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        ShelfListView.ItemContainerGenerator.StatusChanged -= statusHandler;
                        timeoutTimer.Stop();
                        Dispatcher.InvokeAsync(() =>
                        {
                            var lazyContainer = ShelfListView.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
                            if (lazyContainer != null)
                            {
                                lazyContainer.Focus();
                                Keyboard.Focus(lazyContainer);
                                if (index == 0)
                                {
                                    var sv = GetShelfScrollViewer();
                                    sv?.ScrollToTop();
                                }
                                else
                                {
                                    ShelfListView.ScrollIntoView(lazyContainer);
                                }
                            }
                            else
                            {
                                ShelfListView.Focus();
                            }
                        }, System.Windows.Threading.DispatcherPriority.Input);
                    }
                };
                timeoutTimer.Tick += (s, ev) =>
                {
                    timeoutTimer.Stop();
                    ShelfListView.ItemContainerGenerator.StatusChanged -= statusHandler;
                };
                timeoutTimer.Start();
                ShelfListView.ItemContainerGenerator.StatusChanged += statusHandler;
                ShelfListView.Focus();
            }
        }

        private void ShelfListView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // DELETION SCROLL GUARD: If we're in the middle of deleting an item,
            // the VirtualizingStackPanel is recalculating extents during its layout pass.
            // Intercept the scroll change and immediately correct the offset to keep
            // the anchor card pinned. This fires synchronously during the layout pass,
            // so no frame is ever rendered with the wrong offset.
            if (_isDeletionScrollGuardActive && e.VerticalChange != 0)
            {
                try
                {
                    var sv = GetShelfScrollViewer();
                    if (sv != null && _deletionAnchorIndex >= 0 && _deletionAnchorIndex < ShelfListView.Items.Count)
                    {
                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(_deletionAnchorIndex) as ListViewItem;
                        if (container != null)
                        {
                            var transform = container.TransformToAncestor(this);
                            var currentPos = transform.Transform(new Point(0, 0));
                            double drift = currentPos.Y - _deletionAnchorTargetY;
                            _deletionLog?.Add($"  GUARD HIT: scrollΔ={e.VerticalChange:+0.0;-0.0}  anchorY={currentPos.Y:F1}  drift={drift:+0.0;-0.0}px  offset={sv.VerticalOffset:F2}  extent={sv.ExtentHeight:F2}");
                            if (Math.Abs(drift) > 0.5)
                            {
                                double correctedOffset = sv.VerticalOffset + drift;
                                correctedOffset = Math.Max(0, Math.Min(correctedOffset, sv.ScrollableHeight));
                                sv.ScrollToVerticalOffset(correctedOffset);
                                _deletionLog?.Add($"  GUARD FIX: → {correctedOffset:F2}");
                            }
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable
                return; // Don't process velocity/scrolling state during deletion
            }

            if (e.VerticalChange == 0) return;

            long nowTick = Environment.TickCount64;  // No GC allocation (unlike DateTime.UtcNow)
            double elapsedMs = nowTick - _lastScrollTimeTick;
            _lastScrollTimeTick = nowTick;

            double change = Math.Abs(e.VerticalChange);
            double instVelocity = elapsedMs > 0 ? change / elapsedMs : change;

            if (elapsedMs > 500)
            {
                _scrollVelocity = instVelocity;
            }
            else
            {
                // Smooth the velocity using an EMA (exponential moving average)
                _scrollVelocity = 0.7 * _scrollVelocity + 0.3 * instVelocity;
            }

            // Mark that active scrolling is happening, and suppress hover buttons.
            // Only fire PropertyChanged on state TRANSITIONS to avoid 60 binding re-evaluations/sec.
            if (!_viewModel.IsScrolling) _viewModel.IsScrolling = true;
            if (_viewModel.AllowHover) _viewModel.AllowHover = false;

            // Start or reset the timer to reset IsScrolling back to false after a delay
            if (_scrollDecayTimer == null)
            {
                _scrollDecayTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100) // Reset 100ms after scroll activity stops
                };
                _scrollDecayTimer.Tick += (s, ev) =>
                {
                    _scrollDecayTimer.Stop();
                    _viewModel.IsScrolling = false;
                    _scrollVelocity = 0;
                    // Do not set AllowHover = true here; keep it false until the user physically moves the mouse!
                };
            }
            else
            {
                _scrollDecayTimer.Stop();
            }

            _scrollDecayTimer.Start();

            // ═══ LIVE THUMBNAIL LOADING (scroll-speed-aware) ═══
            // Load thumbnails during scroll at Background priority with 200ms throttle.
            // RenderVisibleThumbnails gates on scroll velocity internally:
            //   - Slow scroll (< 8 px/frame): load normally → images "appear" loaded
            //   - Fast scroll (> 8 px/frame): skip loading → preserve scroll smoothness
            if (_scrollLiveLoadTimer == null)
            {
                _scrollLiveLoadTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(350) // 350ms throttle — high enough to prevent layout storm during fast scroll
                };
                _scrollLiveLoadTimer.Tick += (s, ev) =>
                {
                    RenderVisibleThumbnails(onlyFirstTen: false);
                };
            }
            if (!_scrollLiveLoadTimer.IsEnabled) _scrollLiveLoadTimer.Start();

            // Start or reset the snappier 30ms stoppage timer for final high-quality pass when scroll stops
            if (_scrollHighQualityTimer == null)
            {
                _scrollHighQualityTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(30)
                };
                _scrollHighQualityTimer.Tick += (s, ev) =>
                {
                    _scrollHighQualityTimer.Stop();
                    // Stop live loading timer when scroll fully stops
                    _scrollLiveLoadTimer?.Stop();
                    RenderVisibleThumbnails(onlyFirstTen: false);
                };
            }
            else
            {
                _scrollHighQualityTimer.Stop();
                _scrollHighQualityTimer.Interval = TimeSpan.FromMilliseconds(30); // Reset from any track-click throttle
            }

            _scrollHighQualityTimer.Start();
        }

        private void ShelfListView_MouseLeave(object sender, MouseEventArgs e)
        {
            // Reset AllowHover back to true when the mouse leaves the list view area entirely
            _viewModel.AllowHover = true;
        }

        private bool _isRenderingThumbnails;
        private readonly HashSet<int> _alwaysLoadedImageIndices = new();
        private readonly HashSet<ClipboardItem> _activeLoadedImages = new();

        private void RenderVisibleThumbnails(bool onlyFirstTen = false, bool isEvictionPass = false)
        {
            if (_isRenderingThumbnails) return; // Reentrancy guard — prevent overlapping timer passes
            _isRenderingThumbnails = true;
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!this.IsVisible) { _isRenderingThumbnails = false; return; }

                    // Throttle thumbnail evaluation during fast scrolling — but still allow
                    // loading when scroll velocity is low (user browsing slowly)
                    if (_viewModel.IsScrolling && !isEvictionPass)
                    {
                        if (_scrollVelocity > 2.0) // Only skip during genuinely fast scroll
                        {
                            _isRenderingThumbnails = false;
                            return;
                        }
                    }

                    // Guard: Ensure containers are fully generated before evaluating visibility or eviction
                    if (ShelfListView.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        if (_scrollHighQualityTimer != null && !_scrollHighQualityTimer.IsEnabled)
                        {
                            _scrollHighQualityTimer.Interval = TimeSpan.FromMilliseconds(100);
                            _scrollHighQualityTimer.Start();
                        }
                        return;
                    }

                    // Periodic eviction background timer — fires every 1.5s to find bitmaps
                    // that have been off-screen long enough to safely free.
                    if (_evictionBackgroundTimer == null)
                    {
                        _evictionBackgroundTimer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1500)
                        };
                        _evictionBackgroundTimer.Tick += (s, ev) =>
                        {
                            if (!this.IsVisible) return;
                            RenderVisibleThumbnails(onlyFirstTen: false, isEvictionPass: true);
                        };
                        _evictionBackgroundTimer.Start();
                    }
                    else if (!_evictionBackgroundTimer.IsEnabled)
                    {
                        _evictionBackgroundTimer.Start();
                    }

                    var sv = GetShelfScrollViewer();
                    if (sv == null) return;

                    double viewportWidth = sv.ViewportWidth;
                    double viewportHeight = sv.ViewportHeight;
                    if (viewportHeight <= 0 || viewportWidth <= 0) return;

                    // ═══ SCROLL-SPEED-AWARE PREFETCH GATING ═══
                    var scrollSv = GetShelfScrollViewer();
                    double scrollVelocity = scrollSv != null ? Classes.SmoothScroll.GetCurrentVelocity(scrollSv) : 0;
                    bool isFastScrolling = scrollVelocity > 30.0; // > 30 px/frame = truly fast scroll (flicks)

                    double prefetchOverdraw = isFastScrolling ? 200 : 1200;
                    Rect viewportRect = new Rect(0, -prefetchOverdraw, viewportWidth, viewportHeight + prefetchOverdraw * 2);
                    int count = ShelfListView.Items.Count;

                    // ═══ PASS 1: Always-loaded first 6 images (cheap, covers top of list) ═══
                    _alwaysLoadedImageIndices.Clear();
                    {
                        int imgCount = 0;
                        int topScanLimit = Math.Min(count, 50);
                        for (int i = 0; i < topScanLimit && imgCount < 6; i++)
                        {
                            var item = ShelfListView.Items[i] as ClipboardItem;
                            if (item == null) continue;
                            if (item.ItemType != ClipboardItemType.Image && item.ItemType != ClipboardItemType.QRCode)
                            {
                                if (item.ItemType == ClipboardItemType.File && !string.IsNullOrEmpty(item.FilePath) && Classes.ImageThumbnailManager.IsImageExtension(System.IO.Path.GetExtension(item.FilePath)))
                                {
                                    item.ItemType = ClipboardItemType.Image;
                                    item.Extension = System.IO.Path.GetExtension(item.FilePath).ToUpperInvariant().TrimStart('.');
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            imgCount++;
                            _alwaysLoadedImageIndices.Add(i);

                            if (!item.IsLoadedHighQuality && !item.IsLoadingHighQuality)
                            {
                                item.EnsureThumbnailLoadedAsync();
                                _activeLoadedImages.Add(item);
                            }
                            // Top 6 images are never evicted
                            item.LeftViewportTime = null;
                        }
                    }

                    // ═══ EVICTION PASS: Scan ONLY tracked active loaded images ═══
                    if (isEvictionPass)
                    {
                        if (_hubWindowInstance == null || !_hubWindowInstance.IsVisible)
                        {
                            var now = DateTime.UtcNow;
                            var loadedSnapshot = _activeLoadedImages.ToList();
                            foreach (var item in loadedSnapshot)
                            {
                                if (item.IsPinned) { item.LeftViewportTime = null; continue; }

                                int idx = ShelfListView.Items.IndexOf(item);
                                if (idx >= 0 && _alwaysLoadedImageIndices.Contains(idx))
                                {
                                    item.LeftViewportTime = null;
                                    continue;
                                }

                                var container = idx >= 0 ? ShelfListView.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement : null;
                                bool isVisible = false;
                                if (container != null && container.IsLoaded)
                                {
                                    try
                                    {
                                        GeneralTransform transform = container.TransformToAncestor(sv);
                                        Point containerPt = transform.Transform(new Point(0, 0));
                                        Rect bounds = new Rect(0, containerPt.Y, container.ActualWidth, container.ActualHeight);
                                        isVisible = viewportRect.IntersectsWith(bounds);
                                    }
                                    catch { }
                                }

                                if (isVisible)
                                {
                                    item.LeftViewportTime = null;
                                }
                                else
                                {
                                    if (item.LeftViewportTime == null)
                                    {
                                        item.LeftViewportTime = now;
                                    }
                                    else if ((now - item.LeftViewportTime.Value).TotalMilliseconds >= 30000) // 30 seconds eviction
                                    {
                                        item.Icon = null;
                                        item.IsLoadedHighQuality = false;
                                        item.IsLoadingHighQuality = false;
                                        item.LeftViewportTime = null;
                                        _activeLoadedImages.Remove(item);
                                    }
                                }
                            }
                        }
                        return;
                    }

                    // ═══ PASS 2: Viewport-Bounded Loading Scan ═══
                    int loadScanStart, loadScanEnd;
                    if (count > 100)
                    {
                        double estimatedItemHeight = 120.0;
                        int estimatedViewportIndex = (int)(sv.VerticalOffset / estimatedItemHeight);
                        int viewportItems = (int)((viewportHeight + 2400) / estimatedItemHeight);
                        loadScanStart = Math.Max(0, estimatedViewportIndex - viewportItems);
                        loadScanEnd = Math.Min(count - 1, estimatedViewportIndex + viewportItems * 2);
                    }
                    else
                    {
                        loadScanStart = 0;
                        loadScanEnd = count - 1;
                    }

                    for (int i = loadScanStart; i <= loadScanEnd; i++)
                    {
                        var item = ShelfListView.Items[i] as ClipboardItem;
                        if (item == null) continue;

                        if (item.ItemType != ClipboardItemType.Image && item.ItemType != ClipboardItemType.QRCode)
                        {
                            if (item.ItemType == ClipboardItemType.File && !string.IsNullOrEmpty(item.FilePath) && Classes.ImageThumbnailManager.IsImageExtension(System.IO.Path.GetExtension(item.FilePath)))
                            {
                                item.ItemType = ClipboardItemType.Image;
                                item.Extension = System.IO.Path.GetExtension(item.FilePath).ToUpperInvariant().TrimStart('.');
                            }
                            else
                            {
                                continue;
                            }
                        }

                        if (item.IsLoadedHighQuality && item.Icon != null)
                            continue;

                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                        bool isVisible = false;

                        if (container != null && container.IsLoaded)
                        {
                            try
                            {
                                GeneralTransform transform = container.TransformToAncestor(sv);
                                Point containerPt = transform.Transform(new Point(0, 0));
                                Rect bounds = new Rect(0, containerPt.Y, container.ActualWidth, container.ActualHeight);
                                isVisible = viewportRect.IntersectsWith(bounds);
                            }
                            catch { }
                        }
                        else
                        {
                            // Container not realized yet but item is within scan range
                            isVisible = true;
                        }

                        if (isVisible)
                        {
                            item.LeftViewportTime = null;

                            if (!item.IsLoadedHighQuality && !item.IsLoadingHighQuality)
                            {
                                item.EnsureThumbnailLoadedAsync();
                                _activeLoadedImages.Add(item);
                            }
                        }
                    }

                    // Eviction complete — let .NET GC naturally reclaim Gen 0 allocations
                    // without forcing a collection that could cause micro-stutters during scroll.


                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("SCROLL_LOAD_ERR", $"Error in RenderVisibleThumbnails: {ex.Message}");
                }
                finally
                {
                    _isRenderingThumbnails = false;
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }


        private bool _themeAnimationsSuspended = false;

        public void SuspendThemeAnimations()
        {
            if (_themeAnimationsSuspended) return;
            _themeAnimationsSuspended = true;
            try
            {
                MascotIdle.PausePlayback();
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                animator?.Pause();
            }
            catch { } // Best-effort: failure is acceptable
        }

        public void ResumeThemeAnimations()
        {
            if (!_themeAnimationsSuspended) return;
            _themeAnimationsSuspended = false;
            try
            {
                if (_isCurrentlySummoned && !_isAnimatingHide && Classes.SettingsManager.Current.ThemeAnimationsEnabled)
                {
                    MascotIdle.ResumePlayback();
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                    animator?.Play();
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        // ═══ Low-Level Keyboard Hook — Arrow Navigation Without Focus ═══

        /// <summary>
        /// Installs a WH_KEYBOARD_LL hook so Up/Down/Enter/Escape work on the clipboard
        /// even though it doesn't have keyboard focus (stealFocus=false).
        /// Also installs a WH_MOUSE_LL hook to detect clicks inside/outside the clipboard
        /// for click-to-release arrow ownership.
        /// </summary>
        private void InstallKeyboardHook()
        {
            // PC-7 FIX: Always reset arrow ownership on summon, even if hook is already installed.
            // Previously, re-summon without uninstall skipped this, leaving arrows dead.
            _hookOwnsArrows = true;

            if (_keyboardHookId != IntPtr.Zero) return; // Already installed

            _keyboardHookProc = KeyboardHookCallback;
            _mouseHookProc = MouseHookCallback;
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            {
                var mainModule = curProcess.MainModule;
                if (mainModule == null) return;
                using (mainModule)
                {
                    var hMod = Classes.NativeMethods.GetModuleHandle(mainModule.ModuleName);
                    _keyboardHookId = Classes.NativeMethods.SetWindowsHookEx(
                        Classes.NativeMethods.WH_KEYBOARD_LL,
                        _keyboardHookProc,
                        hMod,
                        0);
                    _mouseHookId = Classes.NativeMethods.SetWindowsHookEx(
                        Classes.NativeMethods.WH_MOUSE_LL,
                        _mouseHookProc,
                        hMod,
                        0);
                }
            }
        }

        /// <summary>
        /// Removes the low-level keyboard and mouse hooks. Safe to call multiple times.
        /// </summary>
        private void UninstallKeyboardHook()
        {
            if (_keyboardHookId != IntPtr.Zero)
            {
                Classes.NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }
            _keyboardHookProc = null;

            if (_mouseHookId != IntPtr.Zero)
            {
                Classes.NativeMethods.UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            _mouseHookProc = null;
        }

        /// <summary>
        /// Low-level keyboard hook callback. Intercepts Up/Down/Enter/Escape ONLY when
        /// the clipboard is visible (_isCurrentlySummoned) and _hookOwnsArrows is true.
        /// Navigates the ListView programmatically without stealing focus from the target app.
        /// Arrow ownership is released when the user clicks outside the clipboard (detected
        /// by the companion mouse hook) and reclaimed when they click inside it.
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)Classes.NativeMethods.WM_KEYDOWN && _isCurrentlySummoned && !_isAnimatingHide)
            {
                // When Notes or Todo panel is open, don't intercept any keys —
                // let them pass through to the text boxes for normal editing.
                if (_isNotesActive || _isTodoActive)
                    return Classes.NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
                int vkCode = Marshal.ReadInt32(lParam);

                // Only intercept navigation keys if we currently own arrow input.
                // Ownership starts as true when the clipboard is summoned.
                // Clicking outside the clipboard releases ownership (mouse hook sets _hookOwnsArrows=false).
                // Clicking back on the clipboard reclaims ownership (_hookOwnsArrows=true).
                if (vkCode == VK_DOWN || vkCode == VK_UP || vkCode == VK_RETURN || vkCode == VK_ESCAPE)
                {
                    if (!_hookOwnsArrows)
                    {
                        // Ownership released — let keys pass through to the target app
                        return Classes.NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
                    }
                }

                if (vkCode == VK_DOWN)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        int count = _viewModel.DroppedItems.Count;
                        if (count == 0) return;
                        int next = ShelfListView.SelectedIndex + 1;
                        if (next >= count) next = count - 1;
                        ShelfListView.SelectedIndex = next;
                        ShelfListView.ScrollIntoView(ShelfListView.SelectedItem);
                    });
                    return (IntPtr)1; // Swallow the key
                }

                if (vkCode == VK_UP)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        int count = _viewModel.DroppedItems.Count;
                        if (count == 0) return;
                        int prev = ShelfListView.SelectedIndex - 1;
                        if (prev < 0) prev = 0;
                        ShelfListView.SelectedIndex = prev;
                        ShelfListView.ScrollIntoView(ShelfListView.SelectedItem);
                    });
                    return (IntPtr)1; // Swallow the key
                }

                if (vkCode == VK_RETURN)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ShelfListView.SelectedItem is ClipboardItem item)
                        {
                            _ = CopyItemAndPaste(item, hideWindow: true);
                        }
                    });
                    return (IntPtr)1; // Swallow the key
                }

                if (vkCode == VK_ESCAPE)
                {
                    Dispatcher.InvokeAsync(() => AnimateAndHide());
                    return (IntPtr)1; // Swallow the key
                }
            }

            return Classes.NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        /// <summary>
        /// Low-level mouse hook callback. Detects mouse clicks to toggle arrow-key ownership:
        /// - Click inside the clipboard window → reclaim arrow ownership (_hookOwnsArrows = true)
        /// - Click outside the clipboard window → release arrow ownership (_hookOwnsArrows = false)
        /// This lets the user "click outside" to give arrows back to their app,
        /// then "click on clipboard" to navigate it again — matching Win+V behavior.
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isCurrentlySummoned && !_isAnimatingHide)
            {
                int msg = checked((int)wParam);
                if (msg == Classes.NativeMethods.WM_LBUTTONDOWN ||
                    msg == Classes.NativeMethods.WM_RBUTTONDOWN ||
                    msg == Classes.NativeMethods.WM_MBUTTONDOWN)
                {
                    try
                    {
                        if (Classes.NativeMethods.GetCursorPos(out var pt))
                        {
                            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                            if (hwnd != IntPtr.Zero && Classes.NativeMethods.GetWindowRect(hwnd, out var rect))
                            {
                                bool clickedOnClipboard = pt.X >= rect.Left && pt.X <= rect.Right &&
                                                         pt.Y >= rect.Top && pt.Y <= rect.Bottom;
                                _hookOwnsArrows = clickedOnClipboard;
                            }
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                }
            }

            return Classes.NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private void UpdatePositionToLockedBottomEdge()
        {
            if (this.ActualHeight > 0)
            {
                var workArea = GetWorkAreaForPoint(this.Left + this.ActualWidth / 2, this.Top + this.ActualHeight / 2);
                double newTop = _lockedBottomEdge - this.ActualHeight - 20;
                if (newTop < workArea.Top + 16)
                    newTop = workArea.Top + 16;
                if (newTop + this.ActualHeight > workArea.Top + workArea.Height - 16)
                    newTop = workArea.Top + workArea.Height - this.ActualHeight - 16;
                this.Top = newTop;
            }
        }

        /// <summary>
        /// Handles display DPI changes (monitor switch, Windows scaling change).
        /// Recalculates locked positioning edges so the clipboard doesn't shift.
        /// </summary>
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            try
            {
                if (_lockedBottomEdge > 0)
                {
                    double ratio = newDpi.PixelsPerDip / oldDpi.PixelsPerDip;
                    _lockedBottomEdge = _lockedBottomEdge * ratio;
                }
                Classes.Logger.LogAction("DPI_CHANGED", $"DPI changed from {oldDpi.PixelsPerDip} to {newDpi.PixelsPerDip}");
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("DPI_ERR", $"OnDpiChanged failed: {ex.Message}");
            }
        }
    }
}
