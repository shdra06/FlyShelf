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
        private const double TouchpadMul         = 0.07;   // Touchpad micro-step scale multiplier (adjusted for continuous linear input mapping)
        private const double MouseMul            = 0.06;   // Mouse wheel step scale multiplier (reduced from 0.45 to target ~120px scroll distance per notch)
        private const double MinImpulse          = 0.3;    // Minimum impulse threshold for micro-scrolls
        private const double MinVelocity         = 0.05;   // Velocity below this → complete stop (prevents sub-pixel crawl and end-of-scroll micro jitter)
        private const double DeltaCapTouchpad    = 80.0;   // Clamps raw trackpad delta packets to absorb speed spikes
        private const double DeltaCapMouse       = 280.0;  // Clamps raw mouse delta packets
        private const double DirectionBrakeMul   = 0.2;    // Retained velocity on reversal (partial braking feels snappy)
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
        }

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

                // ═══ DRAIN COALESCED INPUT ═══
                // Apply all accumulated impulse as a single velocity change per frame.
                // This prevents burst touchpad events from compounding into visible jumps.
                if (state.PendingImpulse != 0)
                {
                    double pending = state.PendingImpulse;
                    state.PendingImpulse = 0;

                    // Direction reversal: partially brake previous velocity
                    if (state.Velocity != 0 && Math.Sign(state.Velocity) != Math.Sign(-pending))
                    {
                        state.Velocity *= DirectionBrakeMul;
                    }

                    state.Velocity -= pending;
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

                // Check if user has stopped active scrolling and scroller should transition to critically damped spring settling
                long nowMs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                bool isCoasting = (nowMs - state.LastInputTime) > 100;
                double settleThreshold = state.IsTouchpad ? 0.6 : 1.2;

                if (isCoasting && Math.Abs(state.Velocity) < settleThreshold)
                {
                    double target = Math.Clamp(Math.Round(state.TrueOffset), 0, sv.ScrollableHeight);
                    double x = state.TrueOffset - target;

                    // If we are extremely close to target and velocity is negligible, complete the animation
                    if (Math.Abs(x) < 0.01 && Math.Abs(state.Velocity) < 0.02)
                    {
                        state.Velocity = 0.0;
                        state.TrueOffset = target;
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        state.IsAnimating = false;
                        completed.Add(sv);
                    }
                    else
                    {
                        // Critically damped spring-damper to ease smoothly to the target integer pixel
                        double omega = state.IsTouchpad ? 0.3 : 0.22;
                        double exp = Math.Exp(-omega * timeScale);
                        double A = x;
                        double B = state.Velocity + omega * A;

                        double newX = (A + B * timeScale) * exp;
                        double newV = (B - omega * (A + B * timeScale)) * exp;

                        state.TrueOffset = Math.Clamp(target + newX, 0, sv.ScrollableHeight);
                        state.Velocity = newV;

                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        anyAnimating = true;
                    }
                }
                else
                {
                    // Apply velocity vector with frame-time compensation
                    double displacement = state.Velocity * timeScale;
                    state.TrueOffset += displacement;
                    state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;

                    // Exponential deceleration (friction decay)
                    double friction = state.IsTouchpad 
                        ? 0.88  // Decays slower to bridge trackpad input gaps
                        : ScrollFriction; // Luxurious free coasting glide for mouse wheel sweeps

                    state.Velocity *= Math.Pow(friction, timeScale);
                    anyAnimating = true;
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
