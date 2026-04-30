using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Physics-based smooth scroll for WPF with two profiles:
    ///   LIST  — very slow, for clipboard item lists (many small items)
    ///   PAGE  — normal speed, for settings/full-page content
    /// </summary>
    public static class SmoothScroll
    {
        // ═══ LIST profile (clipboard items — must be slow) ═══
        private const double ListFriction = 0.93;
        private const double ListMaxVelocity = 14;
        private const double ListTouchpadMul = 0.05;
        private const double ListMouseMul = 0.08;
        private const double ListMinImpulse = 0.25;

        // ═══ PAGE profile (settings, diagnostics — calm, controlled) ═══
        private const double PageFriction = 0.93;
        private const double PageMaxVelocity = 20;
        private const double PageTouchpadMul = 0.12;
        private const double PageMouseMul = 0.10;
        private const double PageMinImpulse = 0.5;

        // ═══ Shared ═══
        private const double MinVelocity = 0.12;
        private const double DeltaCapTouchpad = 20;
        private const double DeltaCapMouse = 160;

        private enum Profile { List, Page }

        private class ScrollState
        {
            public double Velocity;
            public bool IsAnimating;
            public Profile Mode;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly HashSet<ScrollViewer> _listScrollViewers = new();
        private static bool _renderingAttached = false;

        /// <summary>
        /// Attach LIST profile (very slow) to a specific ListView.
        /// Use for clipboard item lists.
        /// </summary>
        public static void AttachList(FrameworkElement element)
        {
            element.PreviewMouseWheel += OnListPreviewMouseWheel;
        }

        /// <summary>
        /// Attach PAGE profile (normal speed) to ALL ScrollViewers in a Window.
        /// Skips ScrollViewers already registered as LIST.
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

            _listScrollViewers.Add(sv); // Mark as list-mode
            ApplyImpulse(sv, e, Profile.List);
        }

        // ═══ Window-level PAGE handler ═══
        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            ScrollViewer? sv = FindScrollViewerAncestor(source);

            if (sv == null) return;
            if (sv.ScrollableHeight <= 0) return;

            // If this ScrollViewer is already handled as LIST, skip (it has its own handler)
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
            bool isTouchpad = Math.Abs(rawDelta) < 120;

            // Pick multipliers based on profile
            double touchMul = mode == Profile.List ? ListTouchpadMul : PageTouchpadMul;
            double mouseMul = mode == Profile.List ? ListMouseMul : PageMouseMul;
            double minImpulse = mode == Profile.List ? ListMinImpulse : PageMinImpulse;
            double maxVel = mode == Profile.List ? ListMaxVelocity : PageMaxVelocity;

            double impulse;
            if (isTouchpad)
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapTouchpad);
                impulse = capped * touchMul;
                // Guarantee minimum so very slow scrolls don't skip
                if (Math.Abs(impulse) < minImpulse && impulse != 0)
                    impulse = Math.Sign(impulse) * minImpulse;
            }
            else
            {
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapMouse);
                impulse = capped * mouseMul;
            }

            state.Velocity -= impulse;
            state.Velocity = Math.Clamp(state.Velocity, -maxVel, maxVel);

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
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

            foreach (var kvp in _states)
            {
                var sv = kvp.Key;
                var state = kvp.Value;

                if (!state.IsAnimating) continue;

                double newOffset = sv.VerticalOffset + state.Velocity;
                newOffset = Math.Clamp(newOffset, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);

                double friction = state.Mode == Profile.List ? ListFriction : PageFriction;
                state.Velocity *= friction;

                if (Math.Abs(state.Velocity) < MinVelocity)
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
