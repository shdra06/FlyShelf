using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// World-class smooth scroll engine for the PC Dashboard (HubWindow).
    /// Combines iOS-style exponential velocity decay, Chrome-inspired adaptive time constants,
    /// per-frame input coalescing, progressive touchpad acceleration, pixel-snapped GPU rendering,
    /// and frame-time compensated physics for the best scrolling feel on any desktop app.
    /// </summary>
    public static class SmoothScrollPCApp
    {
        // ═══ VS Code target-based animation constants ═══
        private const double TargetDurationMs     = 125.0;    // VS Code uses exactly 125ms duration
        private const double PreAdvanceMs         = 10.0;     // VS Code pretends animation already started for 10ms for instant feel

        // ═══ Input Scaling ═══
        private const double TouchpadScale        = 0.60;     // Sensitivity multiplier for trackpad deltas (flick & drag)
        private const double MouseStepPx          = 96.0;     // Pixels per mouse wheel notch (standard Windows)
        private const double MouseImpulseBoost    = 1.5;      // Mouse wheel step multiplier
        private const double DeltaCapTouchpad     = 60.0;     // Clamp raw trackpad delta to absorb driver spikes
        private const double DeltaCapMouse        = 360.0;    // Clamp raw mouse delta

        // ═══ Progressive Touchpad Acceleration ═══
        private const double ProgressiveFloor     = 0.50;
        private const double ProgressiveCeiling   = 1.00;
        private const double ProgressiveThreshold = 40.0;

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();

        private static bool _renderingAttached;

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

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

        static SmoothScrollPCApp()
        {
            // Elevate Windows scheduler timer resolution to 1ms
            try { TimeBeginPeriod(1); } catch { }

            // Disable Windows 11 EcoQoS power throttling for unthrottled rendering
            try
            {
                var hProcess = System.Diagnostics.Process.GetCurrentProcess().Handle;
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(state);
                SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, size);
            }
            catch { }
        }

        private class ScrollState
        {
            public bool IsAnimating;
            public bool IsTouchpad;
            
            // VS Code target-based animation state
            public double FromOffset;
            public double ToOffset;
            public long StartTimeMs;
            public double DurationMs;
            public double ViewportHeight;

            public double PendingDelta;      // Coalesced target displacement
            public long LastInputTime;       // Environment.TickCount64 of last input event
        }

        // ═══ GPU Caching — Disabled ═══
        // BitmapCache degrades ClearType text quality during scroll, causing blurry text.
        // With 3-page virtualization cache + UseLayoutRounding + SnapsToDevicePixels,
        // items don't recycle during scroll, so BitmapCache is unnecessary.

        private static void EnableStaticCanvas(ScrollViewer sv) { }
        private static void DisableStaticCanvas(ScrollViewer sv) { }

        // ═══ Public API ═══

        /// <summary>
        /// Hook window-wide scroll interception for the HubWindow.
        /// </summary>
        public static void AttachToWindow(Window window)
        {
            window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            window.PreviewMouseWheel += OnWindowPreviewMouseWheel;
        }

        /// <summary>
        /// Unhook and clean up all state.
        /// </summary>
        public static void DetachFromWindow(Window window)
        {
            window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            _ancestorCache.Clear();

            var toRemove = new List<ScrollViewer>();
            foreach (var sv in _states.Keys)
            {
                if (IsDescendantOf(sv, window))
                    toRemove.Add(sv);
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
                RestoreUIThreadPriority();
            }
        }

        /// <summary>
        /// Clears any in-flight animation state for the given ScrollViewer.
        /// </summary>
        public static void ResetScrollState(ScrollViewer? sv)
        {
            if (sv == null) return;
            DisableStaticCanvas(sv);
            if (_states.Remove(sv) && _states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
                RestoreUIThreadPriority();
            }
        }

        // ═══ Input Handling ═══

        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            if (_ancestorCache.Count > 120)
                _ancestorCache.Clear();

            if (sender is not Window window) return;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            ScrollViewer? sv = FindScrollableScrollViewerAncestor(source);
            if (sv == null) return;

            // Boundary check: let events bubble if at top/bottom limits
            bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
            bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
            if (atTop || atBottom)
                return;

            e.Handled = true;
            ApplyImpulse(sv, e.Delta);
        }

        private static void ApplyImpulse(ScrollViewer sv, int delta)
        {
            bool isTouchpad = (delta % 120 != 0) || (Math.Abs(delta) < 120);

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState();
                _states[sv] = state;
            }

            long now = Environment.TickCount64;
            state.IsTouchpad = isTouchpad;
            state.LastInputTime = now;

            double displacement;

            if (isTouchpad)
            {
                // Treat touchpad deltas as raw pixel displacement
                double rawAbs = Math.Abs(delta);
                double capped = Math.Min(rawAbs, DeltaCapTouchpad);
                double speedRatio = Math.Min(capped / ProgressiveThreshold, 1.0);
                double progressiveMul = ProgressiveFloor + (ProgressiveCeiling - ProgressiveFloor) * speedRatio;

                displacement = -Math.Sign(delta) * capped * progressiveMul * TouchpadScale;
            }
            else
            {
                // Mouse wheel notches
                double notches = delta / 120.0;
                double capped = Math.Sign(notches) * Math.Min(Math.Abs(notches), DeltaCapMouse / 120.0);
                displacement = -capped * MouseStepPx * MouseImpulseBoost;
            }

            // Coalesce incoming scroll deltas to prevent micro-stuttering
            state.PendingDelta += displacement;

            if (!state.IsAnimating)
            {
                // Seed starting values
                state.FromOffset = sv.VerticalOffset;
                state.ToOffset = sv.VerticalOffset;
                state.ViewportHeight = sv.ViewportHeight;
            }

            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;
                ElevateUIThreadPriority();
            }
        }

        // ═══ Render Loop ═══

        private static void OnRendering(object? sender, EventArgs e)
        {
            bool anyAnimating = false;
            long now = Environment.TickCount64;

            var scrollKeys = _states.Keys.ToList();
            var completed = new List<ScrollViewer>();

            foreach (var sv in scrollKeys)
            {
                if (!_states.TryGetValue(sv, out var state)) continue;

                // ═══ DRAIN COALESCED INPUT ═══
                if (state.PendingDelta != 0)
                {
                    double pending = state.PendingDelta;
                    state.PendingDelta = 0;

                    if (!state.IsAnimating)
                    {
                        // Start a new VS Code smooth scrolling operation
                        state.FromOffset = sv.VerticalOffset;
                        state.ToOffset = Math.Clamp(state.FromOffset + pending, 0.0, sv.ScrollableHeight);
                        state.StartTimeMs = now - (long)PreAdvanceMs;
                        state.DurationMs = TargetDurationMs + PreAdvanceMs;
                        state.ViewportHeight = sv.ViewportHeight;
                        state.IsAnimating = true;
                    }
                    else
                    {
                        // Retarget ongoing animation:
                        // Calculate current animated position using the old animation at the current time
                        long elapsed = now - state.StartTimeMs;
                        double completion = state.DurationMs > 0 ? elapsed / state.DurationMs : 1.0;
                        double currentOffset = GetPositionAtCompletion(state.FromOffset, state.ToOffset, state.ViewportHeight, completion);

                        state.FromOffset = currentOffset;
                        state.ToOffset = Math.Clamp(state.ToOffset + pending, 0.0, sv.ScrollableHeight);
                        state.StartTimeMs = now - (long)PreAdvanceMs;
                        state.DurationMs = TargetDurationMs + PreAdvanceMs;
                        state.ViewportHeight = sv.ViewportHeight;
                    }
                }

                if (!state.IsAnimating)
                {
                    completed.Add(sv);
                    continue;
                }

                // ═══ Calculate Animation Position ═══
                long elapsedAnim = now - state.StartTimeMs;
                double animCompletion = state.DurationMs > 0 ? (double)elapsedAnim / state.DurationMs : 1.0;

                if (animCompletion >= 1.0)
                {
                    // Snap exactly to final target
                    double finalTarget = Math.Clamp(state.ToOffset, 0.0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(finalTarget);
                    
                    state.IsAnimating = false;
                    completed.Add(sv);
                }
                else
                {
                    double nextOffset = GetPositionAtCompletion(state.FromOffset, state.ToOffset, state.ViewportHeight, animCompletion);
                    nextOffset = Math.Clamp(nextOffset, 0.0, sv.ScrollableHeight);
                    
                    // Scroll directly to the fractional offset for infinite scrolling smoothness
                    sv.ScrollToVerticalOffset(nextOffset);
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
                RestoreUIThreadPriority();
            }
        }

        // ═══ VS Code Mathematical Interpolation with Huge Jump Composed Easing ═══

        private static double GetPositionAtCompletion(double from, double to, double viewportSize, double completion)
        {
            completion = Math.Clamp(completion, 0.0, 1.0);
            double delta = Math.Abs(from - to);
            
            // VS Code optimization for giant jumps: if delta > 2.5 * viewportSize, compose two easeOutCubic curves
            if (viewportSize > 0 && delta > 2.5 * viewportSize)
            {
                double stop1, stop2;
                if (from < to)
                {
                    // Scroll to 75% of the viewportSize
                    stop1 = from + 0.75 * viewportSize;
                    stop2 = to - 0.75 * viewportSize;
                }
                else
                {
                    stop1 = from - 0.75 * viewportSize;
                    stop2 = to + 0.75 * viewportSize;
                }

                double cut = 0.33;
                if (completion < cut)
                {
                    double localCompletion = completion / cut;
                    return from + (stop1 - from) * EaseOutCubic(localCompletion);
                }
                else
                {
                    double localCompletion = (completion - cut) / (1.0 - cut);
                    return stop2 + (to - stop2) * EaseOutCubic(localCompletion);
                }
            }
            
            // Standard easeOutCubic interpolation
            return from + (to - from) * EaseOutCubic(completion);
        }

        private static double EaseOutCubic(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return 1.0 - Math.Pow(1.0 - t, 3);
        }

        // ═══ Thread Priority Management ═══

        private static void ElevateUIThreadPriority()
        {
            try
            {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;
            }
            catch { }
        }

        private static void RestoreUIThreadPriority()
        {
            try
            {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
            }
            catch { }
        }

        // ═══ Visual Tree Helpers ═══

        private static ScrollViewer? FindScrollableScrollViewerAncestor(DependencyObject? element)
        {
            if (element == null) return null;

            if (_ancestorCache.TryGetValue(element, out var cachedSv))
                return cachedSv;

            var current = element;
            while (current != null)
            {
                if (current is ScrollViewer sv && sv.ScrollableHeight > 0)
                {
                    _ancestorCache[element] = sv;
                    return sv;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// Walks the visual tree downward to find the first descendant of type T.
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
