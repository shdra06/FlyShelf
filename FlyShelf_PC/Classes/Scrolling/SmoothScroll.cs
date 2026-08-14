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
    /// Decompiled 3.0.0.7 / 3.0.0 reference implementation.
    /// </summary>
    public static class SmoothScroll
    {
        // Compilation compatibility stub
        public static readonly ScrollProfile ClipboardProfile = new();

        // ═══ Natural Velocity Physics Constants (Calm Smooth Specs) ═══
        private const double ScrollFriction      = 0.932;  // Calm, controlled exponential decay
        private const double MaxVelocity         = 32.0;   // Maximum velocity cap (px/frame)
        private const double TouchpadMul         = 0.068;  // Calm touchpad impulse multiplier (smooth and controlled)
        private const double MouseImpulseScale   = 5.20;   // Calm mouse impulse per notch
        private const double DeltaCapTouchpad    = 70.0;   // Clamp raw trackpad delta
        private const double DeltaCapMouse       = 280.0;  // Clamp raw mouse delta
        private const double BlendFactor        = 0.48;   // Smooth, gradual acceleration ramp
        private const double DirectionBrakeMul   = 0.35;   // Retained velocity on direction reversal
        private const double MinVelocity         = 0.15;   // Velocity below this → complete stop
        private const double TargetFrameMs       = 16.667; // 60 FPS baseline

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        private static readonly List<ScrollViewer> _scrollKeysBuffer = new();
        private static readonly List<ScrollViewer> _completedBuffer = new();
        
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

        public static void Cleanup()
        {
            try { TimeEndPeriod(1); } catch { }
        }

        private class ScrollState
        {
            public double Velocity;
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;
            public long LastInputTime;
            public double PendingImpulse;  // Coalesced impulse — drained once per render frame
            public double TrueOffset;      // Sub-pixel precise position (tracked continuous offset)
            public double LastSetOffset;   // ScrollViewer offset after last write in the animation loop
            public long LastPrefetchTime;  // Last time we triggered prefetch during active scroll/deceleration
        }

        /// <summary>
        /// Event fired during coast phase every ~200ms to trigger image prefetching.
        /// MainWindow hooks this to call RenderVisibleThumbnails during deceleration,
        /// so images load before entering the viewport.
        /// </summary>
        public static event Action? CoastPrefetchNeeded;

        /// <summary>
        /// Returns the current scroll velocity (px/frame) for the given ScrollViewer.
        /// Used by RenderVisibleThumbnails to gate thumbnail loading during fast scrolling.
        /// Returns 0 if the ScrollViewer is not being scrolled.
        /// </summary>
        public static double GetCurrentVelocity(ScrollViewer sv)
        {
            if (_states.TryGetValue(sv, out var state) && state.IsAnimating)
                return Math.Abs(state.Velocity);
            return 0;
        }

        private static void EnableStaticCanvas(ScrollViewer sv)
        {
            // Set flag on Markdown renderer so scrolling bypasses any expensive re-layout
            MarkdownInlineRenderer.IsScrollingActive = true;
        }

        private static void DisableStaticCanvas(ScrollViewer sv)
        {
            MarkdownInlineRenderer.IsScrollingActive = false;
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

            // Restore system timer resolution when all scroll states are detached
            if (_states.Count == 0)
            {
                try { TimeEndPeriod(1); } catch { }
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

            double impulse;
            if (isTouchpad)
            {
                double capped = Math.Sign(delta) * Math.Min(Math.Abs((double)delta), DeltaCapTouchpad);
                impulse = capped * TouchpadMul;
            }
            else
            {
                double notches = delta / 120.0;
                double capped = Math.Sign(notches) * Math.Min(Math.Abs(notches), DeltaCapMouse / 120.0);
                impulse = capped * MouseImpulseScale;
            }

            // ═══ COALESCE: Accumulate impulse for per-frame drain ═══
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

            _scrollKeysBuffer.Clear();
            _scrollKeysBuffer.AddRange(_states.Keys);
            _completedBuffer.Clear();

            foreach (var sv in _scrollKeysBuffer)
            {
                if (!_states.TryGetValue(sv, out var state)) continue;

                if (!state.IsAnimating)
                {
                    _completedBuffer.Add(sv);
                    continue;
                }

                // ═══ SYNCHRONIZE WITH WPF LAYOUT SHIFTS ═══
                // Only absorb large external layout shifts (> 6.0px).
                // Micro layout shifts from VirtualizingStackPanel measuring containers
                // are synced into LastSetOffset without polluting TrueOffset.
                double actualOffset = sv.VerticalOffset;
                double wpfDelta = actualOffset - state.LastSetOffset;
                if (Math.Abs(wpfDelta) > 6.0)
                {
                    state.TrueOffset += wpfDelta;
                    state.LastSetOffset = actualOffset;
                }
                else if (Math.Abs(wpfDelta) > 0.001)
                {
                    state.LastSetOffset = actualOffset;
                }

                // ═══ FRAME TIME COMPENSATION ═══
                long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = (double)(currentTimestamp - state.LastFrameTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs <= 0) elapsedMs = 1.0;
                double timeScale = Math.Clamp(elapsedMs / TargetFrameMs, 0.1, 1.75);
                state.LastFrameTick = currentTimestamp;

                // ═══ PROGRESSIVE ACCELERATION & RESPONSIVE BLENDING ═══
                if (state.PendingImpulse != 0)
                {
                    double pending = state.PendingImpulse;
                    state.PendingImpulse = 0;

                    double targetVelocity = state.Velocity - pending;
                    targetVelocity = Math.Clamp(targetVelocity, -MaxVelocity, MaxVelocity);

                    bool isReversal = Math.Abs(state.Velocity) > 2.0 &&
                                     Math.Sign(targetVelocity) != Math.Sign(state.Velocity);

                    if (isReversal)
                    {
                        state.Velocity *= DirectionBrakeMul;
                        if (Math.Abs(state.Velocity) < 0.3)
                            state.Velocity = 0;
                    }
                    else
                    {
                        state.Velocity += (targetVelocity - state.Velocity) * BlendFactor;
                    }

                    state.Velocity = Math.Clamp(state.Velocity, -MaxVelocity, MaxVelocity);
                }

                // Stop if at boundary
                bool atBound = (state.TrueOffset <= 0 && state.Velocity < 0) ||
                               (state.TrueOffset >= sv.ScrollableHeight && state.Velocity > 0);

                if (atBound)
                {
                    state.Velocity = 0.0;
                    state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;
                    state.IsAnimating = false;
                    _completedBuffer.Add(sv);
                    continue;
                }

                // Apply velocity to offset with frame-time compensation
                double displacement = state.Velocity * timeScale;
                state.TrueOffset += displacement;
                state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                // Write sub-pixel offset directly to GPU compositor
                if (Math.Abs(state.TrueOffset - sv.VerticalOffset) >= 0.05)
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                state.LastSetOffset = state.TrueOffset;

                // Decay velocity continuously with exponential friction
                state.Velocity *= Math.Pow(ScrollFriction, timeScale);

                // Stop condition: velocity below imperceptible threshold
                if (Math.Abs(state.Velocity) < MinVelocity)
                {
                    state.Velocity = 0.0;
                    state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;
                    state.IsAnimating = false;
                    DisableStaticCanvas(sv);
                    _completedBuffer.Add(sv);
                }
                else
                {
                    // Trigger image prefetch every 150ms while scrolling at speed
                    long nowMs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    if (nowMs - state.LastPrefetchTime > 150 && Math.Abs(state.Velocity) > 1.0)
                    {
                        state.LastPrefetchTime = nowMs;
                        try { CoastPrefetchNeeded?.Invoke(); } catch { }
                    }
                    anyAnimating = true;
                }
            }

            foreach (var sv in _completedBuffer)
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
    }
}
