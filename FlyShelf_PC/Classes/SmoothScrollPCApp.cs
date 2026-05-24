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
        // ═══ iOS-Inspired Exponential Decay Physics ═══
        // v(t) = v₀ × e^(-t/τ)  where τ is the time constant in seconds
        // ═══ iOS-Inspired Exponential Decay Physics ═══
        // v(t) = v₀ × e^(-t/τ)  where τ is the time constant in seconds
        private const double TimeConstantTrackpad = 0.200;    // Tighter desktop feel (200ms coast) — snappier than iOS 325ms
        private const double TimeConstantMouse    = 0.160;    // Chrome-inspired: precise wheel with quick settle
        private const double MaxVelocity          = 6000.0;   // pixels/second hard cap
        private const double MinVelocity          = 0.5;      // pixels/second → complete stop
        private const double DirectionBrakeFactor = 0.15;     // Retain 15% velocity on direction reversal

        // ═══ Input Scaling ═══
        // Touchpad deltas are treated as direct pixel displacement (like Chrome).
        // Converted to velocity via: impulse = delta × scale / τ
        private const double TouchpadScale        = 0.50;     // Sensitivity multiplier for trackpad deltas
        private const double MouseStepPx          = 96.0;     // Pixels per mouse wheel notch (standard Windows)
        private const double MouseImpulseBoost    = 2.0;      // Step distance → velocity burst multiplier
        private const double DeltaCapTouchpad     = 60.0;     // Clamp raw trackpad delta to absorb driver spikes
        private const double DeltaCapMouse        = 360.0;    // Clamp raw mouse delta

        // ═══ Progressive Touchpad Acceleration ═══
        // Slow drags → 1:1 linear. Fast swipes → up to 2× multiplier.
        private const double ProgressiveFloor     = 0.50;     // Minimum multiplier (gentle drag)
        private const double ProgressiveCeiling   = 1.00;     // Maximum multiplier (fast swipe)
        private const double ProgressiveThreshold = 40.0;     // Delta magnitude for full acceleration

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
            public double Velocity;         // pixels/second (positive = scrolling down)
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;       // Environment.TickCount64 of last render frame
            public long LastInputTime;       // Environment.TickCount64 of last input event
            public double PendingImpulse;    // Coalesced velocity impulse — drained once per render frame
            public double TrueOffset;        // Sub-pixel precise scroll position (physics layer)
        }

        // ═══ GPU Static Canvas Caching ═══

        private static void EnableStaticCanvas(ScrollViewer sv)
        {
            try
            {
                var target = FindDescendant<VirtualizingStackPanel>(sv) as UIElement
                             ?? sv.Content as UIElement;
                if (target != null)
                {
                    // Use DPI-aware scale to prevent blurry text on high-DPI displays
                    double dpiScale = 1.0;
                    try { dpiScale = VisualTreeHelper.GetDpi(sv).DpiScaleX; } catch { }

                    target.CacheMode = new BitmapCache
                    {
                        EnableClearType = true,
                        RenderAtScale = dpiScale
                    };
                }
            }
            catch { }
        }

        private static void DisableStaticCanvas(ScrollViewer sv)
        {
            try
            {
                var target = FindDescendant<VirtualizingStackPanel>(sv) as UIElement
                             ?? sv.Content as UIElement;
                if (target != null)
                {
                    target.CacheMode = null;
                }
            }
            catch { }
        }

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

            double impulse; // pixels/second to add to velocity

            if (isTouchpad)
            {
                // ═══ Progressive Touchpad — Direct Pixel Displacement ═══
                // Treat touchpad deltas as pixel offsets (like Chrome), not raw velocity.
                // Convert displacement → velocity via: v = displacement / τ
                // This ensures the exponential decay integrates to exactly the intended distance.
                double rawAbs = Math.Abs(delta);
                double capped = Math.Min(rawAbs, DeltaCapTouchpad);
                double speedRatio = Math.Min(capped / ProgressiveThreshold, 1.0);
                double progressiveMul = ProgressiveFloor + (ProgressiveCeiling - ProgressiveFloor) * speedRatio;

                double displacement = capped * progressiveMul * TouchpadScale;
                impulse = -Math.Sign(delta) * displacement / TimeConstantTrackpad;
            }
            else
            {
                // ═══ Mouse Wheel: Discrete Notch → Velocity Burst ═══
                double notches = delta / 120.0;
                double capped = Math.Sign(notches) * Math.Min(Math.Abs(notches), DeltaCapMouse / 120.0);
                impulse = -capped * MouseStepPx * MouseImpulseBoost;
            }

            // ═══ COALESCE: Accumulate impulse for per-frame drain ═══
            // Precision touchpads fire 200-500 Hz bursts between render frames.
            // Accumulating and draining once per frame prevents velocity compounding.
            state.PendingImpulse += impulse;

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                state.LastFrameTick = Environment.TickCount64;
                state.TrueOffset = sv.VerticalOffset;
                EnableStaticCanvas(sv);
            }

            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;
                ElevateUIThreadPriority();
            }
        }

        // ═══ Render Loop — iOS Exponential Decay Physics ═══

        private static void OnRendering(object? sender, EventArgs e)
        {
            bool anyAnimating = false;
            long now = Environment.TickCount64;

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

                // ═══ DRAIN COALESCED INPUT ═══
                if (state.PendingImpulse != 0)
                {
                    double pending = state.PendingImpulse;
                    state.PendingImpulse = 0;

                    // Direction reversal: partially brake previous velocity for snappy response
                    if (state.Velocity != 0 && Math.Sign(state.Velocity) != Math.Sign(pending))
                    {
                        state.Velocity *= DirectionBrakeFactor;
                    }

                    state.Velocity += pending;
                    state.Velocity = Math.Clamp(state.Velocity, -MaxVelocity, MaxVelocity);
                }

                // ═══ Frame-Time Compensation ═══
                long elapsedMs = now - state.LastFrameTick;
                if (elapsedMs <= 0) elapsedMs = 1;
                double deltaTime = elapsedMs / 1000.0; // Convert to seconds
                deltaTime = Math.Min(deltaTime, 0.050); // Cap at 50ms (20 FPS floor) to prevent huge jumps
                state.LastFrameTick = now;

                // ═══ Integrate Position ═══
                // s += v × Δt  (basic Euler integration with real time)
                state.TrueOffset += state.Velocity * deltaTime;
                state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                // ═══ PIXEL-SNAP: Render at integer pixel for crisp text ═══
                double snappedOffset = Math.Round(state.TrueOffset);
                sv.ScrollToVerticalOffset(snappedOffset);

                // ═══ iOS Exponential Decay ═══
                // v(t+Δt) = v(t) × e^(-Δt/τ)
                // τ = time constant: higher = longer coast
                double tau = state.IsTouchpad ? TimeConstantTrackpad : TimeConstantMouse;
                state.Velocity *= Math.Exp(-deltaTime / tau);

                // ═══ Stop Conditions ═══
                bool atBound = (state.TrueOffset <= 0 && state.Velocity < 0) ||
                               (state.TrueOffset >= sv.ScrollableHeight && state.Velocity > 0);

                if (Math.Abs(state.Velocity) < MinVelocity || atBound)
                {
                    state.Velocity = 0.0;
                    state.IsAnimating = false;
                    completed.Add(sv);
                }
                else
                {
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
