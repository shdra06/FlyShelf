// ---------------------------------------------------------------
// SmoothScrollFeature — Premium velocity-based smooth scrolling
// for all secondary windows (popup dialogs, editors, settings).
//
// Drop-in replacement: call Attach(window) in constructor,
// Detach(window) on close. Shared CompositionTarget.Rendering
// render loop across all windows for minimal overhead.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight velocity-based smooth scrolling for secondary windows.
    /// Uses CompositionTarget.Rendering (vsync-synced), impulse coalescing,
    /// adaptive friction, pixel-snapping, and analytical coasting.
    /// </summary>
    public static class SmoothScrollFeature
    {
        // ═══ Physics Constants ═══
        private const double TouchpadMul        = 0.09;     // Touchpad micro-step scale multiplier
        private const double MouseMul           = 0.06;     // Mouse wheel step scale multiplier
        private const double MaxVelocity        = 40.0;     // Maximum speed cap (px/frame)
        private const double MinVelocity        = 0.40;     // Below this → stop
        private const double DeltaCapTouchpad   = 120.0;    // Clamp raw trackpad delta
        private const double DeltaCapMouse      = 280.0;    // Clamp raw mouse delta
        private const double TargetFrameMs      = 16.667;   // 60 FPS baseline
        private const double BlendFactorTouchpad = 0.35;    // Soft blend for touchpad
        private const double BlendFactorMouse    = 0.55;    // Snappy blend for mouse wheel
        private const double FrictionSlow       = 0.95;     // Friction for slow scrolling
        private const double FrictionFast       = 0.91;     // Friction for fast scrolling

        private static readonly Dictionary<Window, MouseWheelEventHandler> _handlers = new();
        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        private static bool _renderingAttached;

        private class ScrollState
        {
            public double Velocity;
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;
            public long LastInputTime;
            public double PendingImpulse;
            public double TrueOffset;
            public double LastSetOffset;
        }

        /// <summary>
        /// Attaches smooth scroll handling to all ScrollViewers in the window.
        /// </summary>
        public static void Attach(Window window)
        {
            if (window == null || _handlers.ContainsKey(window)) return;

            MouseWheelEventHandler handler = (sender, e) =>
            {
                if (e.Handled) return;

                DependencyObject? source = e.OriginalSource as DependencyObject;
                ScrollViewer? sv = FindScrollViewer(source);
                if (sv == null) return;

                // Boundary check: let events bubble at top/bottom limits
                bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
                bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
                if (atTop || atBottom) return;

                e.Handled = true;
                ApplyImpulse(sv, e.Delta);
            };

            window.PreviewMouseWheel += handler;
            _handlers[window] = handler;
        }

        /// <summary>
        /// Detaches smooth scroll handling from the window.
        /// </summary>
        public static void Detach(Window window)
        {
            if (window == null || !_handlers.TryGetValue(window, out var handler)) return;
            window.PreviewMouseWheel -= handler;
            _handlers.Remove(window);

            // Clean up scroll states for ScrollViewers in this window
            var toRemove = _states.Keys.Where(sv => IsDescendantOf(sv, window)).ToList();
            foreach (var sv in toRemove)
                _states.Remove(sv);

            _ancestorCache.Clear();

            if (_states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        private static void ApplyImpulse(ScrollViewer sv, int delta)
        {
            bool isTouchpad = (delta % 120 != 0) || (Math.Abs(delta) < 120);

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState();
                _states[sv] = state;
            }

            long now = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            state.IsTouchpad = isTouchpad;
            state.LastInputTime = now;

            double rawDelta = delta;
            double impulse;

            if (isTouchpad)
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapTouchpad);
                impulse = capped * TouchpadMul;
            }
            else
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapMouse);
                impulse = capped * MouseMul;
            }

            // Coalesce impulses — drained once per render frame
            state.PendingImpulse += impulse;

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                state.LastFrameTick = Stopwatch.GetTimestamp();
                state.TrueOffset = sv.VerticalOffset;
                state.LastSetOffset = sv.VerticalOffset;
            }

            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;
            }
        }

        private static void OnRendering(object? sender, EventArgs e)
        {
            bool anyAnimating = false;
            long nowMs = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

            var scrollKeys = _states.Keys.ToList();
            var completed = new List<ScrollViewer>();

            foreach (var sv in scrollKeys)
            {
                if (!_states.TryGetValue(sv, out var state)) continue;
                if (!state.IsAnimating) { completed.Add(sv); continue; }

                // Synchronize with WPF layout shifts
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

                // Drain coalesced impulse into velocity
                if (state.PendingImpulse != 0)
                {
                    double pending = state.PendingImpulse;
                    state.PendingImpulse = 0;

                    double targetVelocity = state.Velocity - pending;
                    targetVelocity = Math.Clamp(targetVelocity, -MaxVelocity, MaxVelocity);

                    double reversalThreshold = state.IsTouchpad ? 4.0 : 2.5;
                    bool isReversal = Math.Abs(state.Velocity) > reversalThreshold &&
                                     Math.Sign(targetVelocity) != Math.Sign(state.Velocity);

                    if (isReversal)
                    {
                        state.Velocity *= 0.40;
                        if (Math.Abs(state.Velocity) < 0.3) state.Velocity = 0;
                    }
                    else
                    {
                        double blend = state.IsTouchpad ? BlendFactorTouchpad : BlendFactorMouse;
                        state.Velocity += (targetVelocity - state.Velocity) * blend;
                    }

                    state.Velocity = Math.Clamp(state.Velocity, -MaxVelocity, MaxVelocity);
                }

                // Frame-time compensation
                long currentTimestamp = Stopwatch.GetTimestamp();
                double elapsedMs = (double)(currentTimestamp - state.LastFrameTick) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs <= 0) elapsedMs = 1.0;
                double timeScale = Math.Min(elapsedMs / TargetFrameMs, 3.0);
                state.LastFrameTick = currentTimestamp;

                // Boundary check
                bool atBound = (state.TrueOffset <= 0 && state.Velocity < 0) ||
                               (state.TrueOffset >= sv.ScrollableHeight && state.Velocity > 0);

                if (atBound)
                {
                    state.Velocity = 0;
                    state.TrueOffset = Math.Clamp(Math.Round(state.TrueOffset), 0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;
                    state.IsAnimating = false;
                    completed.Add(sv);
                    continue;
                }

                // Apply velocity with frame-time compensation
                double displacement = state.Velocity * timeScale;
                state.TrueOffset += displacement;
                state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                // Pixel-snap + delta guard
                double snappedOffset = Math.Round(state.TrueOffset);
                if (Math.Abs(snappedOffset - sv.VerticalOffset) >= 0.5)
                    sv.ScrollToVerticalOffset(snappedOffset);
                state.LastSetOffset = snappedOffset;

                // Velocity-adaptive friction
                double absV = Math.Abs(state.Velocity);
                double t = Math.Clamp((absV - 2.0) / 18.0, 0.0, 1.0);
                t = t * t * (3.0 - 2.0 * t); // Smoothstep
                double friction = FrictionSlow + (FrictionFast - FrictionSlow) * t;
                state.Velocity *= Math.Pow(friction, timeScale);

                // Stop when velocity is imperceptible
                if (Math.Abs(state.Velocity) < MinVelocity)
                {
                    state.Velocity = 0;
                    state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(state.TrueOffset);
                    state.LastSetOffset = state.TrueOffset;
                    state.IsAnimating = false;
                    completed.Add(sv);
                }
                else
                {
                    anyAnimating = true;
                }
            }

            foreach (var sv in completed)
                _states.Remove(sv);

            if (!anyAnimating)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject? source)
        {
            if (source == null) return null;

            if (_ancestorCache.Count > 120)
                _ancestorCache.Clear();

            if (_ancestorCache.TryGetValue(source, out var cached))
                return cached;

            var current = source;
            while (current != null)
            {
                if (current is ScrollViewer sv && sv.ScrollableHeight > 0)
                {
                    _ancestorCache[source] = sv;
                    return sv;
                }
                if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                    current = VisualTreeHelper.GetParent(current);
                else
                    current = LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                    current = VisualTreeHelper.GetParent(current);
                else
                    current = LogicalTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
