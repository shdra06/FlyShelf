using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Legacy profile stub for backward compatibility with MainWindow.xaml.cs compilation parameters.
    /// </summary>
    public class ScrollProfile
    {
    }

    /// <summary>
    /// Premium hardware-accelerated velocity-based physics scrolling engine.
    /// Modeled after Windows 11 native clipboard overlay and premium web momentum curves.
    /// Combines frame-time compensated physics, progressive touchpad scaling, and dynamic GPU canvas caching.
    /// </summary>
    public static class SmoothScroll
    {
        // Compilation compatibility stub
        public static readonly ScrollProfile ClipboardProfile = new();


        // â•â•â• v3.0.0 Microsoft Store Constants (5a90d8e â€” smooth, no overshoot) â•â•â•
        private const double ScrollFriction      = 0.94;
        private const double MaxVelocity         = 45.0;
        private const double TouchpadMul         = 0.09;
        private const double MouseMul            = 0.06;   // v3.0.0: precision touchpads send delta=120 â†’ classified as mouse
        private const double MinImpulse          = 0.3;
        private const double MinVelocity         = 0.20;
        private const double DeltaCapTouchpad    = 120.0;
        private const double DeltaCapMouse       = 280.0;
        private const double VelocityBlendFactor = 0.55;
        private const double ReversalBrakeFactor = 0.40;
        private const double TargetFrameMs       = 16.667;

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        private static readonly List<ScrollViewer> _scrollKeysBuffer = new(4);
        private static readonly List<ScrollViewer> _completedBuffer = new(4);
        
        private static bool _renderingAttached;

        /// <summary>
        /// [FIX H-16]: Reverse the TimeBeginPeriod(1) call. Must be called on app exit
        /// to restore the system timer resolution and avoid impacting battery life.
        /// </summary>
        public static void Cleanup()
        {
            try { NativeMethods.TimeEndPeriod(1); } catch { }
        }

        static SmoothScroll()
        {
            // 1. Elevate Windows scheduler timer resolution to 1ms to prevent rendering timer stutters
            try
            {
                NativeMethods.TimeBeginPeriod(1);
            }
            catch { }

            // 2. Disable Windows 11 EcoQoS (Power Throttling) for this process to force unthrottled thread execution
            try
            {
                var hProcess = System.Diagnostics.Process.GetCurrentProcess().Handle;
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0 // Disable speed limiting / EcoQoS throttling
                };
                uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(state);
                NativeMethods.SetProcessInformation(hProcess, NativeMethods.ProcessPowerThrottling, ref state, size);
            }
            catch { }
        }

        private class ScrollState
        {
            public double Velocity;
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;
            public long LastInputTime;
            public double PendingImpulse;  // Coalesced impulse â€” drained once per render frame
            public double TrueOffset;      // Sub-pixel precise position (never sent to ScrollViewer)
            public double LastSetOffset;   // ScrollViewer offset after last write in the animation loop
            // â•â•â• ANALYTICAL COAST PHASE (iOS-inspired) â•â•â•
            public bool InCoastPhase;
            public long CoastStartTime;
            public double CoastStartVelocity;
            public double CoastStartOffset;
            public long LastPrefetchTime;   // Last time we triggered prefetch during coast
            public double CoastDecayPerMs;  // Per-ms decay rate computed from active friction at coast start
            // â•â•â• LANDING ANIMATION (smooth ease-out to final position) â•â•â•
            public bool InLandingPhase;
            public long LandingStartTime;
            public double LandingStartOffset;
            public double LandingTargetOffset;
            public double LandingDurationMs;
            // â•â•â• DELTA SMOOTHING â•â•â•
            public double LastDelta;           // Previous frame's displacement for smoothing
            // â•â•â• GPU FAST-PATH (RenderTransform dual-layer) â•â•â•
            public TranslateTransform? GpuTransform; // Applied to ScrollContentPresenter
            public ScrollContentPresenter? Presenter; // Cached for hit-test suppression
            public double GpuVisualOffset;     // Accumulated visual offset since last layout sync
            public long LastLayoutSyncTick;     // Timestamp of last ScrollToVerticalOffset call
            public double LayoutSyncOffset;     // TrueOffset at last layout sync
            // â•â•â• TELEMETRY â•â•â•
            public double LastWpfDelta;         // Most recent wpfDelta for telemetry recording
        }

        /// <summary>
        /// Event fired during coast phase every ~200ms to trigger image prefetching.
        /// MainWindow hooks this to call RenderVisibleThumbnails during deceleration,
        /// so images load before entering the viewport.
        /// </summary>
        public static event Action? CoastPrefetchNeeded;

        // Thread-static flag set per frame to indicate whether this is a layout sync frame
        [ThreadStatic] private static bool _isLayoutSyncFrame;

        /// <summary>
        /// Simplified scroll helper matching v3.0.7 MSIX approach.
        /// Uses pixel-snapped values for normal scrolling.
        /// Sub-pixel mode available for landing phase to prevent stepping.
        /// </summary>
        private static void DualLayerScroll(ScrollViewer sv, ScrollState state, double targetOffset, bool subPixel = false)
        {
            if (subPixel)
            {
                // SUB-PIXEL MODE: For landing phase â€” prevents visible 0,1,0,1 stepping
                // at low velocities. WPF renders sub-pixel offsets smoothly.
                sv.ScrollToVerticalOffset(targetOffset);
                state.LastSetOffset = targetOffset;
            }
            else
            {
                double roundedOffset = Math.Round(targetOffset);
                if (Math.Abs(roundedOffset - sv.VerticalOffset) >= 0.5)
                {
                    sv.ScrollToVerticalOffset(roundedOffset);
                }
                state.LastSetOffset = roundedOffset;
            }
        }

        private static void EnableStaticCanvas(ScrollViewer sv)
        {
            // NOTE: Grayscale text rendering was removed â€” it made text appear visibly
            // bolder/thicker during scroll, which looked bad. Chrome and VS Code do NOT
            // switch text rendering modes during scroll. Modern WPF handles ClearType
            // during motion without noticeable shimmer on most hardware.
            // Suppress expensive markdown inline rendering during scroll (the real perf win)
            MarkdownInlineRenderer.IsScrollingActive = true;

            // â•â•â• SUPPRESS HIT-TESTING DURING SCROLL â•â•â•
            // Disabling IsHitTestVisible on the ScrollContentPresenter prevents ALL
            // IsMouseOver triggers, tooltip evaluations, and cursor changes from firing
            // on scroll items. This eliminates WPF layout invalidation from hover
            // effects as the mouse passes over items during scroll.
            if (_states.TryGetValue(sv, out var state) && state.Presenter != null)
            {
                state.Presenter.IsHitTestVisible = false;
            }
        }

        private static void DisableStaticCanvas(ScrollViewer sv)
        {
            // Re-enable full markdown rendering when scroll stops
            MarkdownInlineRenderer.IsScrollingActive = false;

            // Re-enable hit testing when scroll stops
            if (_states.TryGetValue(sv, out var state))
            {
                if (state.Presenter != null)
                {
                    state.Presenter.IsHitTestVisible = true;
                }
            }
        }

        /// <summary>
        /// Find the ScrollContentPresenter inside a ScrollViewer's visual tree.
        /// We attach a TranslateTransform to this element for GPU-accelerated
        /// visual scrolling between layout sync frames.
        /// </summary>
        private static ScrollContentPresenter? FindScrollContentPresenter(ScrollViewer sv)
        {
            // Walk the visual tree to find the ScrollContentPresenter
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(sv); i++)
            {
                var child = VisualTreeHelper.GetChild(sv, i);
                if (child is ScrollContentPresenter scp) return scp;

                // One level deeper (Grid â†’ ScrollContentPresenter)
                if (child is FrameworkElement fe)
                {
                    for (int j = 0; j < VisualTreeHelper.GetChildrenCount(fe); j++)
                    {
                        var grandchild = VisualTreeHelper.GetChild(fe, j);
                        if (grandchild is ScrollContentPresenter scp2) return scp2;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Hook window-wide scroll logic at the Window level.
        /// </summary>
        public static void AttachToWindow(Window window, ScrollProfile? profile = null)
        {
            window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            window.PreviewMouseWheel += OnWindowPreviewMouseWheel;
        }

        /// <summary>
        /// Unhook and clean up references.
        /// </summary>
        public static void DetachFromWindow(Window window)
        {
            window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            _ancestorCache.Clear();

            var toRemove = new List<ScrollViewer>();
            foreach (var sv in _states.Keys)
            {
                if (IsDescendantOf(sv, window))
                {
                    toRemove.Add(sv);
                }
            }

            foreach (var sv in toRemove)
            {
                _states.Remove(sv);
                DisableStaticCanvas(sv);
            }

            if (_states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        /// <summary>
        /// Clears any in-flight smooth scroll animation state for the given ScrollViewer.
        /// </summary>
        public static void ResetScrollState(ScrollViewer? sv)
        {
            if (sv == null) return;
            DisableStaticCanvas(sv);
            if (_states.Remove(sv) && _states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            if (_ancestorCache.Count > 120)
            {
                _ancestorCache.Clear();
            }

            Window? window = sender as Window;
            if (window == null) return;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            ScrollViewer? sv = FindScrollableScrollViewerAncestor(source);

            if (sv == null) return;

            // Bubbling Boundary check: let events bubble if limits are already reached
            bool atTopBoundary = sv.VerticalOffset <= 0 && e.Delta > 0;
            bool atBottomBoundary = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;

            if (atTopBoundary || atBottomBoundary)
            {
                return;
            }

            // Intercept and handle scroll
            e.Handled = true;
            ApplyImpulse(sv, e.Delta);
        }

        // [FIX M-24]: Purge entries for unloaded ScrollViewers to prevent GC leak
        private static void PurgeUnloadedViewers()
        {
            var dead = _states.Keys.Where(sv => !sv.IsLoaded).ToList();
            foreach (var key in dead) _states.Remove(key);
        }

        private static void ApplyImpulse(ScrollViewer sv, int delta)
        {
            // [FIX M-24]: Periodic cleanup of unloaded ScrollViewer references
            if (_states.Count > 50) PurgeUnloadedViewers();
            // Distinguish Precision Touchpad (high frequency, small deltas) vs Mouse Wheel (discrete 120s)
            bool isTouchpad = (delta % 120 != 0) || (Math.Abs(delta) < 120);

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState();
                _states[sv] = state;
            }

            long now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            state.IsTouchpad = isTouchpad;
            state.LastInputTime = now;

            double rawDelta = delta;
            double impulse;

            if (state.IsTouchpad)
            {
                // Continuous, linear trackpad scroll mapping to mirror the mouse wheel's consistency 
                // and respond natively to fine trackpad acceleration/deceleration.
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapTouchpad);
                impulse = capped * TouchpadMul;
            }
            else
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapMouse);
                impulse = capped * MouseMul;
            }

            // Γ²ÉΓ²ÉΓ²É COALESCE: Accumulate impulse for per-frame drain Γ²ÉΓ²ÉΓ²É
            // Precision touchpads fire 200-500 Hz bursts between render frames.
            // Applying each micro-impulse individually compounds velocity into
            // visible "step jumps." Accumulating and draining once per render
            // frame replicates the natural throttling a low-level input hook provides.
            state.PendingImpulse += impulse;


            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                state.LastFrameTick = System.Diagnostics.Stopwatch.GetTimestamp();
                state.TrueOffset = sv.VerticalOffset;  // Seed from current real position
                state.LastSetOffset = sv.VerticalOffset;
                state.LastLayoutSyncTick = state.LastFrameTick;
                state.LayoutSyncOffset = state.TrueOffset;
                EnableStaticCanvas(sv);

                // Initialize GPU fast-path: attach TranslateTransform to ScrollContentPresenter
                if (state.GpuTransform == null)
                {
                    var presenter = FindScrollContentPresenter(sv);
                    if (presenter != null)
                    {
                        state.Presenter = presenter; // Cache for hit-test suppression
                    }
                }
            }

            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;

                // Elevate UI thread priority during scroll â€” v3.0.7 MSIX proven pattern
                // Prevents background threads from stealing CPU time during frame rendering
                try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal; } catch { }

                // Suspend theme animations to free up 100% UI thread budget for buttery smooth scrolling
                try
                {
                    var parentWin = Window.GetWindow(sv) as MainWindow;
                    parentWin?.SuspendThemeAnimations();
                }
                catch { }
            }
        }

        private static void OnRendering(object? sender, EventArgs e)
        {
            bool anyAnimating = false;
            long now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            var scrollKeys = _states.Keys.ToList();
            var completed = new List<ScrollViewer>();

            foreach (var sv in scrollKeys)
            {
                if (!_states.TryGetValue(sv, out var state)) continue;

                if (!state.IsAnimating)
                {
                    completed.Add(sv);
                    continue;
                }

                // â•â•â• SYNCHRONIZE WITH WPF LAYOUT SHIFTS â•â•â•
                double actualOffset = sv.VerticalOffset;
                double wpfDelta = actualOffset - state.LastSetOffset;
                if (Math.Abs(wpfDelta) > 5.0)
                {
                    state.TrueOffset += wpfDelta;
                    state.LastSetOffset = actualOffset;
                }
                else if (Math.Abs(wpfDelta) > 0.001)
                {
                    state.LastSetOffset = actualOffset;
                }

                // â•â•â• SMOOTH VELOCITY BLENDING â•â•â•
                if (state.PendingImpulse != 0)
                {
                    double pending = state.PendingImpulse;
                    state.PendingImpulse = 0;

                    double targetVelocity = state.Velocity - pending;
                    targetVelocity = Math.Clamp(targetVelocity, -MaxVelocity, MaxVelocity);

                    bool isReversal = Math.Abs(state.Velocity) > 2.5 &&
                                     Math.Sign(targetVelocity) != Math.Sign(state.Velocity);

                    if (isReversal)
                    {
                        state.Velocity *= 0.40;
                        if (Math.Abs(state.Velocity) < 0.3)
                            state.Velocity = 0;
                    }
                    else
                    {
                        state.Velocity += (targetVelocity - state.Velocity) * VelocityBlendFactor;
                    }

                    state.Velocity = Math.Clamp(state.Velocity, -MaxVelocity, MaxVelocity);
                }

                // Frame-time compensation
                long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = (double)(currentTimestamp - state.LastFrameTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs <= 0) elapsedMs = 1.0;
                double timeScale = elapsedMs / TargetFrameMs;
                timeScale = Math.Min(timeScale, 3.0);
                state.LastFrameTick = currentTimestamp;

                // Stop if at boundary
                bool atBound = (state.TrueOffset <= 0 && state.Velocity < 0) ||
                               (state.TrueOffset >= sv.ScrollableHeight && state.Velocity > 0);

                if (atBound)
                {
                    state.Velocity = 0.0;
                    state.TrueOffset = Math.Clamp(Math.Round(state.TrueOffset), 0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;
                    state.IsAnimating = false;
                    completed.Add(sv);
                    continue;
                }

                long nowMs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                bool userStopped = (nowMs - state.LastInputTime) > 60;

                // â”€â”€â”€ ACTIVE INPUT PHASE â”€â”€â”€
                if (!userStopped || state.PendingImpulse != 0)
                {
                    state.InCoastPhase = false;

                    if (state.IsTouchpad && Math.Abs(state.Velocity) < 0.3 && state.PendingImpulse != 0)
                    {
                        double directDisplacement = -state.PendingImpulse * TargetFrameMs;
                        state.TrueOffset += directDisplacement;
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        state.Velocity = 0;
                        anyAnimating = true;
                    }
                    else
                    {
                        double displacement = state.Velocity * timeScale;
                        state.TrueOffset += displacement;
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;

                        double friction;
                        if (state.IsTouchpad)
                        {
                            double absV = Math.Abs(state.Velocity);
                            double slowFriction = 0.96;
                            double fastFriction = 0.93;
                            double t = Math.Clamp((absV - 3.0) / 9.0, 0.0, 1.0);
                            t = t * t * (3.0 - 2.0 * t);
                            friction = slowFriction + (fastFriction - slowFriction) * t;
                        }
                        else
                        {
                            friction = ScrollFriction;
                        }

                        state.Velocity *= Math.Pow(friction, timeScale);
                        anyAnimating = true;
                    }
                }
                // â”€â”€â”€ ANALYTICAL COAST PHASE â”€â”€â”€
                else
                {
                    if (!state.InCoastPhase)
                    {
                        if (state.IsTouchpad && Math.Abs(state.Velocity) < 0.50)
                        {
                            state.Velocity = 0.0;
                            state.TrueOffset = Math.Round(state.TrueOffset);
                            state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                            sv.ScrollToVerticalOffset(state.TrueOffset);
                            state.LastSetOffset = state.TrueOffset;
                            state.IsAnimating = false;
                            completed.Add(sv);
                            continue;
                        }
                        state.InCoastPhase = true;
                        state.CoastStartTime = nowMs;
                        state.CoastStartVelocity = state.Velocity;
                        state.CoastStartOffset = state.TrueOffset;

                        if (state.IsTouchpad)
                        {
                            double absV = Math.Abs(state.Velocity);
                            double slowFriction = 0.96;
                            double fastFriction = 0.93;
                            double t = Math.Clamp((absV - 3.0) / 9.0, 0.0, 1.0);
                            t = t * t * (3.0 - 2.0 * t);
                            double frictionPerFrame = slowFriction + (fastFriction - slowFriction) * t;
                            state.CoastDecayPerMs = Math.Pow(frictionPerFrame, 1.0 / TargetFrameMs);
                        }
                        else
                        {
                            state.CoastDecayPerMs = 0.9962;
                        }
                    }

                    double decayPerMs = state.CoastDecayPerMs;
                    double lnDecay = Math.Log(decayPerMs);
                    double v0_ms = state.CoastStartVelocity / TargetFrameMs;
                    double coastElapsedMs = nowMs - state.CoastStartTime;
                    double decayPow = Math.Pow(decayPerMs, coastElapsedMs);
                    double analyticalOffset = state.CoastStartOffset + v0_ms * (decayPow - 1.0) / lnDecay;
                    double analyticalVelocity_ms = v0_ms * decayPow;

                    state.Velocity = analyticalVelocity_ms * TargetFrameMs;
                    analyticalOffset = Math.Clamp(analyticalOffset, 0, sv.ScrollableHeight);
                    state.TrueOffset = analyticalOffset;
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;

                    if (Math.Abs(state.Velocity) < 0.50)
                    {
                        state.Velocity = 0.0;
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        state.IsAnimating = false;
                        state.InCoastPhase = false;
                        DisableStaticCanvas(sv);
                        completed.Add(sv);
                    }
                    else
                    {
                        if (nowMs - state.LastPrefetchTime > 200)
                        {
                            state.LastPrefetchTime = nowMs;
                            try { CoastPrefetchNeeded?.Invoke(); } catch { }
                        }
                        anyAnimating = true;
                    }
                }
            }

            foreach (var sv in completed)
            {
                _states.Remove(sv);
                DisableStaticCanvas(sv);
            }

            if (!anyAnimating)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;

                try
                {
                    var mainWin = Application.Current.MainWindow as MainWindow;
                    mainWin?.ResumeThemeAnimations();
                }
                catch { }
            }
        }
        private static ScrollViewer? FindScrollableScrollViewerAncestor(DependencyObject? element)
        {
            if (element == null) return null;

            if (_ancestorCache.TryGetValue(element, out var cachedSv))
            {
                return cachedSv;
            }

            var current = element;
            while (current != null)
            {
                if (current is ScrollViewer sv && sv.ScrollableHeight > 0)
                {
                    _ancestorCache[element] = sv;
                    return sv;
                }
                if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return false;
        }
        /// <summary>
        /// Walks the visual tree downward to find the first descendant of type T.
        /// Used to locate the VirtualizingStackPanel inside a ScrollViewer for GPU caching.
        /// </summary>
        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
