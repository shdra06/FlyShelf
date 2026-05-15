using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Natural-feeling smooth scroll for WPF, modeled after Windows 11 native clipboard.
    /// Uses frame-time-based physics with exponential ease-out deceleration.
    ///
    /// Two profiles:
    ///   LIST  — clipboard item lists (moderate speed, tight feel)
    ///   PAGE  — settings, diagnostics (faster, more page-like)
    /// </summary>
    public static class SmoothScroll
    {
        // ═══ LIST profile (clipboard / flyshelf — buttery smooth, responsive) ═══
        private const double ListFriction        = 0.92;   // per-frame decay (higher = longer glide, more premium)
        private const double ListMaxVelocity     = 90;     // px/frame cap (higher = faster swipes feel responsive)
        private const double ListTouchpadMul     = 0.55;   // touchpad impulse scale (was 0.35 — too weak)
        private const double ListMouseMul        = 0.65;   // mouse wheel impulse scale (was 0.45 — too weak)
        private const double ListMinImpulse      = 0.3;    // minimum impulse so gentle scrolls register

        // ═══ PAGE profile (settings, diagnostics — bigger sweeps, long glide) ═══
        private const double PageFriction        = 0.93;   // slightly higher friction for longer momentum
        private const double PageMaxVelocity     = 110;    // higher cap for big page scrolls
        private const double PageTouchpadMul     = 0.60;   // touchpad impulse for pages
        private const double PageMouseMul        = 0.70;   // mouse wheel impulse for pages
        private const double PageMinImpulse      = 0.4;    // minimum impulse

        // ═══ Shared constants ═══
        private const double MinVelocity         = 0.05;   // below this → stop (lower = smoother final stop)
        private const double DeltaCapTouchpad    = 80;     // clamp raw touchpad delta (raised for precision trackpads)
        private const double DeltaCapMouse       = 280;    // clamp raw mouse delta
        private const double DirectionBreakMul   = 0.2;    // velocity retained on direction reversal (lower = snappier reversal)
        private const double TargetFrameMs       = 16.667; // 60 fps baseline

        private enum Profile { List, Page }

        private class ScrollState
        {
            public double Velocity;
            public bool IsAnimating;
            public Profile Mode;
            public long LastFrameTick;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly HashSet<ScrollViewer> _listScrollViewers = new();
        private static bool _renderingAttached;

        /// <summary>
        /// Attach LIST profile to a specific ListView/ItemsControl.
        /// </summary>
        public static void AttachList(FrameworkElement element)
        {
            element.PreviewMouseWheel += OnListPreviewMouseWheel;
        }

        /// <summary>
        /// Attach PAGE profile to ALL ScrollViewers in a Window (skips LIST ones).
        /// </summary>
        public static void AttachToWindow(Window window)
        {
            window.PreviewMouseWheel += OnWindowPreviewMouseWheel;
        }

        public static void Detach(FrameworkElement element)
        {
            element.PreviewMouseWheel -= OnListPreviewMouseWheel;
        }

        // ═══ LIST handler ═══
        private static void OnListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer? sv = null;
            if (sender is ScrollViewer s)
                sv = s;
            else if (sender is ItemsControl ic)
                sv = FindVisualChild<ScrollViewer>(ic);

            if (sv == null) return;

            _listScrollViewers.Add(sv);
            ApplyImpulse(sv, e, Profile.List);
        }

        // ═══ Window-level PAGE handler ═══
        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            ScrollViewer? sv = FindScrollViewerAncestor(source);

            if (sv == null) return;
            if (sv.ScrollableHeight <= 0) return;
            if (_listScrollViewers.Contains(sv)) return;

            ApplyImpulse(sv, e, Profile.Page);
        }

        private static void ApplyImpulse(ScrollViewer sv, MouseWheelEventArgs e, Profile mode)
        {
            e.Handled = true;

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState { Mode = mode };
                _states[sv] = state;
            }

            double rawDelta = e.Delta;

            // Touchpad detection: touchpads typically send deltas < 120 (one notch = ±120)
            // Precision touchpads on Windows send high-resolution deltas in rapid succession
            bool isTouchpad = Math.Abs(rawDelta) < 120;

            double touchMul   = mode == Profile.List ? ListTouchpadMul   : PageTouchpadMul;
            double mouseMul   = mode == Profile.List ? ListMouseMul      : PageMouseMul;
            double minImpulse = mode == Profile.List ? ListMinImpulse    : PageMinImpulse;
            double maxVel     = mode == Profile.List ? ListMaxVelocity   : PageMaxVelocity;

            double impulse;
            if (isTouchpad)
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapTouchpad);
                impulse = capped * touchMul;
                // Guarantee minimum so very gentle scrolls still register
                if (Math.Abs(impulse) < minImpulse && impulse != 0)
                    impulse = Math.Sign(impulse) * minImpulse;
            }
            else
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapMouse);
                impulse = capped * mouseMul;
            }

            // Direction reversal: if scrolling the opposite way, partially brake first
            // This prevents the "bouncy" feel when quickly changing direction
            if (state.Velocity != 0 && Math.Sign(state.Velocity) != Math.Sign(-impulse))
            {
                state.Velocity *= DirectionBreakMul;
            }

            state.Velocity -= impulse;
            state.Velocity = Math.Clamp(state.Velocity, -maxVel, maxVel);

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                state.LastFrameTick = Environment.TickCount64;
                EnsureRenderingAttached();
            }
        }

        private static void EnsureRenderingAttached()
        {
            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;
            }
        }

        private static void OnRendering(object? sender, EventArgs e)
        {
            bool anyActive = false;
            long now = Environment.TickCount64;

            foreach (var kvp in _states)
            {
                var sv = kvp.Key;
                var state = kvp.Value;

                if (!state.IsAnimating) continue;

                // Frame-time compensation: normalize velocity against 60fps baseline
                // If a frame takes 32ms instead of 16ms, move proportionally more
                long elapsed = now - state.LastFrameTick;
                if (elapsed <= 0) elapsed = 1;
                double timeScale = elapsed / TargetFrameMs;
                // Clamp time scale to avoid huge jumps on lag spikes (e.g., window resize)
                timeScale = Math.Min(timeScale, 3.0);
                state.LastFrameTick = now;

                // Apply velocity with frame-time compensation
                double displacement = state.Velocity * timeScale;
                double newOffset = sv.VerticalOffset + displacement;
                newOffset = Math.Clamp(newOffset, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);

                // Exponential deceleration (friction applied per frame, scaled by time)
                double friction = state.Mode == Profile.List ? ListFriction : PageFriction;
                // Apply friction proportional to elapsed time:
                // For 1 frame (16.67ms), apply friction once. For 2 frames, apply twice, etc.
                state.Velocity *= Math.Pow(friction, timeScale);

                // Stop if at boundary or velocity negligible
                bool atBound = (newOffset <= 0 && state.Velocity < 0) ||
                               (newOffset >= sv.ScrollableHeight && state.Velocity > 0);
                if (Math.Abs(state.Velocity) < MinVelocity || atBound)
                {
                    state.Velocity = 0;
                    state.IsAnimating = false;
                }
                else
                {
                    anyActive = true;
                }
            }

            if (!anyActive)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        private static ScrollViewer? FindScrollViewerAncestor(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is ScrollViewer sv)
                    return sv;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
