using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        // ═══ Natural Velocity Physics Constants (Clipboard Specs) ═══
        private const double ScrollFriction      = 0.94;   // Per-frame decay (smooth luxurious glide for mouse wheel sweeps)
        private const double MaxVelocity         = 45.0;   // Maximum speed cap in pixels/frame (reduced from 90.0 to force more drawing steps, stable scrolling, and prevent high-speed stroboscopic jumps)
        private const double TouchpadMul         = 0.09;   // Touchpad micro-step scale multiplier (precise, slightly controlled)
        private const double MouseMul            = 0.06;   // Mouse wheel step scale multiplier (reduced from 0.45 to target ~120px scroll distance per notch)
        private const double MinImpulse          = 0.3;    // Minimum impulse threshold for micro-scrolls
        private const double MinVelocity         = 0.05;   // Velocity below this → complete stop (prevents sub-pixel crawl and end-of-scroll micro jitter)
        private const double DeltaCapTouchpad    = 120.0;  // Clamps raw trackpad delta packets to absorb speed spikes (raised from 80 to allow faster swipes)
        private const double DeltaCapMouse       = 280.0;  // Clamps raw mouse delta packets
        private const double DirectionBrakeMul   = 0.35;   // Retained velocity on reversal (raised from 0.2 — touchpad finger noise causes false reversals)
        private const double TargetFrameMs       = 16.667; // 60 FPS baseline

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        
        private static bool _renderingAttached;

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            int ProcessInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
            uint ProcessInformationSize
        );

        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        static SmoothScroll()
        {
            // 1. Elevate Windows scheduler timer resolution to 1ms to prevent rendering timer stutters
            try
            {
                TimeBeginPeriod(1);
            }
            catch { }

            // 2. Disable Windows 11 EcoQoS (Power Throttling) for this process to force unthrottled thread execution
            try
            {
                var hProcess = System.Diagnostics.Process.GetCurrentProcess().Handle;
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0 // Disable speed limiting / EcoQoS throttling
                };
                uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(state);
                SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, size);
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
            public double PendingImpulse;  // Coalesced impulse — drained once per render frame
            public double TrueOffset;      // Sub-pixel precise position (never sent to ScrollViewer)
            public double LastSetOffset;   // ScrollViewer offset after last write in the animation loop
            // ═══ ANALYTICAL COAST PHASE (iOS-inspired) ═══
            public bool InCoastPhase;
            public long CoastStartTime;
            public double CoastStartVelocity;
            public double CoastStartOffset;
            public long LastPrefetchTime;   // Last time we triggered prefetch during coast
            public double CoastDecayPerMs;  // Per-ms decay rate computed from active friction at coast start
        }

        /// <summary>
        /// Event fired during coast phase every ~200ms to trigger image prefetching.
        /// MainWindow hooks this to call RenderVisibleThumbnails during deceleration,
        /// so images load before entering the viewport.
        /// </summary>
        public static event Action? CoastPrefetchNeeded;

        private static void EnableStaticCanvas(ScrollViewer sv)
        {
            // Set text rendering to Grayscale during active scrolling to eliminate ClearType sub-pixel color fringing/rainbow shimmer,
            // giving the text a clean, premium, and solid macOS-like texture during motion.
            TextOptions.SetTextRenderingMode(sv, TextRenderingMode.Grayscale);
        }

        private static void DisableStaticCanvas(ScrollViewer sv)
        {
            // Restore text rendering to ClearType when scrolling stops, providing maximum static sharpness.
            TextOptions.SetTextRenderingMode(sv, TextRenderingMode.ClearType);
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

        private static void ApplyImpulse(ScrollViewer sv, int delta)
        {
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

            // ═══ COALESCE: Accumulate impulse for per-frame drain ═══
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
                EnableStaticCanvas(sv);
            }

            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;

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

                // ═══ SYNCHRONIZE WITH WPF LAYOUT SHIFTS ═══
                // If WPF's layout engine shifted the viewport offset (e.g. due to virtualization 
                // recycling or asynchronous image loading changes), absorb the delta to prevent 
                // fighting the layout engine, which causes scroll friction and jitter.
                double actualOffset = sv.VerticalOffset;
                double wpfDelta = actualOffset - state.LastSetOffset;
                if (Math.Abs(wpfDelta) > 0.001)
                {
                    state.TrueOffset += wpfDelta;
                }

                // ═══ SMOOTH VELOCITY BLENDING ═══
                // Instead of applying impulse as an instant velocity jump, blend toward
                // the target velocity using exponential smoothing (EMA). This creates:
                // 1. Smooth speed transitions when the user accelerates/decelerates
                // 2. Natural direction reversals that curve through zero velocity
                //    instead of snapping to the opposite direction
                if (state.PendingImpulse != 0)
                {
                    double pending = state.PendingImpulse;
                    state.PendingImpulse = 0;

                    double targetVelocity = state.Velocity - pending;
                    targetVelocity = Math.Clamp(targetVelocity, -MaxVelocity, MaxVelocity);

                    // Detect direction reversal
                    bool isReversal = state.Velocity != 0 && Math.Sign(targetVelocity) != Math.Sign(state.Velocity);

                    // Blend rate: lower for reversals (smoother curve through zero),
                    // higher for same-direction (responsive acceleration)
                    double blendRate = isReversal ? 0.25 : 0.45;

                    state.Velocity += (targetVelocity - state.Velocity) * blendRate;
                    state.Velocity = Math.Clamp(state.Velocity, -MaxVelocity, MaxVelocity);
                }

                // Frame-time compensation: normalize velocity against 60 FPS baseline (16.667ms)
                long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = (double)(currentTimestamp - state.LastFrameTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs <= 0) elapsedMs = 1.0;
                double timeScale = elapsedMs / TargetFrameMs;
                
                // Clamp time scale to avoid huge jumps on lag spikes
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

                // ═══ iOS-INSPIRED ANALYTICAL COASTING ═══
                // Instead of per-frame v *= friction (which accumulates rounding errors and
                // stutters on frame timing variations), compute the EXACT position from the
                // elapsed time since the user lifted their finger.
                //
                // Math: position(t) = pos0 + v0 * (d^t - 1) / ln(d)
                //        velocity(t) = v0 * d^t
                // where d = per-ms decay rate, t = elapsed ms since coast start.
                //
                // This produces a mathematically perfect smooth exponential curve that is
                // completely immune to frame rate jitter — every frame lands on the exact
                // point of the curve regardless of when it renders.

                long nowMs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                bool userStopped = (nowMs - state.LastInputTime) > 60;

                // ─── ACTIVE INPUT PHASE (finger on touchpad) ───
                if (!userStopped || state.PendingImpulse != 0)
                {
                    // Exit coast phase if we were in it (user resumed scrolling)
                    state.InCoastPhase = false;

                    // ═══ MICRO-SCROLL DIRECT TRACKING ═══
                    // For very small touchpad gestures (|velocity| < 1.5 px/frame), bypass the
                    // velocity/friction animation entirely and apply displacement directly.
                    // At sub-1px velocities, WPF's device-pixel snapping causes visible "steps" —
                    // the content sits at the same pixel for several frames then jumps.
                    // Direct tracking gives smooth 1:1 finger-following for micro-adjustments.
                    if (state.IsTouchpad && Math.Abs(state.Velocity) < 1.5 && state.PendingImpulse != 0)
                    {
                        // Apply the impulse directly as displacement (1:1 tracking)
                        double directDisplacement = -state.PendingImpulse * (TargetFrameMs / 1.0);
                        state.TrueOffset += directDisplacement;
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        // Keep velocity low — don't accumulate momentum from micro-scrolls
                        state.Velocity = state.Velocity * 0.5;
                        anyAnimating = true;
                    }
                    else
                    {
                        // Apply velocity with frame-time compensation
                        double displacement = state.Velocity * timeScale;
                        state.TrueOffset += displacement;
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;

                        // Velocity-adaptive friction during active input
                        double friction;
                        if (state.IsTouchpad)
                        {
                            double absV = Math.Abs(state.Velocity);
                            double slowFriction = 0.97;  // Tight control for precise slow scrolling
                            double fastFriction = 0.93;  // Momentum glide for fast swipes
                            double t = Math.Clamp((absV - 3.0) / 9.0, 0.0, 1.0);
                            t = t * t * (3.0 - 2.0 * t); // Smoothstep
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
                // ─── ANALYTICAL COAST PHASE (finger lifted) ───
                else
                {
                    // Enter coast phase: record the exact start state
                    if (!state.InCoastPhase)
                    {
                        // MICRO-COAST BYPASS: If velocity is too small to produce a visible
                        // smooth animation (< 0.8 px/frame → total coast ~2px), stop immediately.
                        // This prevents step-wise micro-animations — micro-scrolls just stop
                        // cleanly where the finger left off.
                        if (state.IsTouchpad && Math.Abs(state.Velocity) < 0.8)
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

                        // ═══ SEAMLESS TRANSITION ═══
                        // Compute the coast decay rate from the SAME velocity-adaptive friction
                        // that was running during active input. This ensures zero deceleration
                        // jump at the transition — the coast curve is a mathematically exact
                        // continuation of the active friction curve.
                        if (state.IsTouchpad)
                        {
                            double absV = Math.Abs(state.Velocity);
                            double slowFriction = 0.97;
                            double fastFriction = 0.93;
                            double t = Math.Clamp((absV - 3.0) / 9.0, 0.0, 1.0);
                            t = t * t * (3.0 - 2.0 * t);
                            double frictionPerFrame = slowFriction + (fastFriction - slowFriction) * t;
                            // Convert per-frame friction to per-ms: f_ms = f_frame^(1/16.667)
                            state.CoastDecayPerMs = Math.Pow(frictionPerFrame, 1.0 / TargetFrameMs);
                        }
                        else
                        {
                            state.CoastDecayPerMs = 0.9962;
                        }
                    }

                    double decayPerMs = state.CoastDecayPerMs;
                    double lnDecay = Math.Log(decayPerMs);

                    // Convert initial velocity from px/frame to px/ms
                    double v0_ms = state.CoastStartVelocity / TargetFrameMs;

                    double coastElapsedMs = nowMs - state.CoastStartTime;

                    // Analytical position and velocity — exact, no accumulation errors
                    double decayPow = Math.Pow(decayPerMs, coastElapsedMs);
                    double analyticalOffset = state.CoastStartOffset + v0_ms * (decayPow - 1.0) / lnDecay;
                    double analyticalVelocity_ms = v0_ms * decayPow;

                    // Convert velocity back to px/frame for state consistency
                    state.Velocity = analyticalVelocity_ms * TargetFrameMs;

                    // Clamp to scrollable bounds
                    analyticalOffset = Math.Clamp(analyticalOffset, 0, sv.ScrollableHeight);
                    state.TrueOffset = analyticalOffset;

                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;

                    // ═══ EARLY CLEARTYPE RESTORATION ═══
                    // At low velocity (< 2 px/frame), text barely moves — ClearType shimmer
                    // is imperceptible but Grayscale blur IS visible, making the braking phase
                    // look "sluggish." Switch back to sharp ClearType early for crisp text
                    // during the final deceleration, creating a premium "settling" feel.
                    if (Math.Abs(state.Velocity) < 2.0)
                    {
                        DisableStaticCanvas(sv); // Restore ClearType
                    }

                    // Stop condition: velocity is imperceptible (< 0.05 px/frame)
                    if (Math.Abs(state.Velocity) < MinVelocity)
                    {
                        state.Velocity = 0.0;
                        state.TrueOffset = Math.Round(state.TrueOffset);
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        state.IsAnimating = false;
                        state.InCoastPhase = false;
                        completed.Add(sv);
                    }
                    else
                    {
                        // ═══ COAST-PHASE PREFETCH ═══
                        // During deceleration, trigger image prefetching every 200ms.
                        // This loads images in the expanded ±800px prefetch zone while
                        // the list is coasting, so they appear "instantly" when entering viewport.
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

                // Resume theme animations now that scrolling has stopped
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
