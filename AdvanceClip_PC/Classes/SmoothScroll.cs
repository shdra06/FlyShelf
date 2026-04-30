using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Physics-based smooth scroll for WPF. Intercepts PreviewMouseWheel on any
    /// ScrollViewer/ListView and applies velocity-based deceleration for a
    /// macOS-like feel. Works with both touchpad (small frequent deltas) and
    /// mouse wheel (large ±120 deltas).
    ///
    /// Usage:
    ///   SmoothScroll.Attach(myListView);       // specific control
    ///   SmoothScroll.AttachToWindow(myWindow);  // all ScrollViewers in window
    /// </summary>
    public static class SmoothScroll
    {
        // ═══ Configuration ═══
        private const double Friction = 0.85;           // Per-frame velocity decay (0.85 = smooth, 0.7 = snappy)
        private const double MaxVelocity = 60;           // Max pixels per frame
        private const double MinVelocity = 0.3;          // Stop threshold
        private const double MouseWheelMultiplier = 0.3;  // Scale for mouse wheel (±120 → ~36px impulse)
        private const double TouchpadMultiplier = 0.6;    // Scale for touchpad (small deltas → gentle scroll)
        private const double DeltaCapTouchpad = 40;       // Max single touchpad delta to accept
        private const double DeltaCapMouse = 200;         // Max single mouse delta to accept

        // ═══ Per-ScrollViewer state ═══
        private class ScrollState
        {
            public double Velocity;
            public bool IsAnimating;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static bool _renderingAttached = false;

        /// <summary>
        /// Attach smooth scrolling to a specific control (ScrollViewer or ListView).
        /// </summary>
        public static void Attach(FrameworkElement element)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        /// <summary>
        /// Attach smooth scrolling to ALL ScrollViewers in a Window.
        /// Catches PreviewMouseWheel at the window level and finds the nearest
        /// scrollable ancestor of the event source.
        /// </summary>
        public static void AttachToWindow(Window window)
        {
            window.PreviewMouseWheel += OnWindowPreviewMouseWheel;
        }

        /// <summary>
        /// Detach smooth scrolling from a control.
        /// </summary>
        public static void Detach(FrameworkElement element)
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }

        // ═══ Specific control handler ═══
        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer? sv = null;
            if (sender is ScrollViewer s)
                sv = s;
            else if (sender is ItemsControl ic)
                sv = FindVisualChild<ScrollViewer>(ic);

            if (sv == null) return;

            ApplyScrollImpulse(sv, e);
        }

        // ═══ Window-level handler — finds nearest scrollable ancestor ═══
        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Walk up from the original source to find the nearest ScrollViewer
            DependencyObject? source = e.OriginalSource as DependencyObject;
            ScrollViewer? sv = FindScrollViewerAncestor(source);

            if (sv == null) return;

            // Only smooth-scroll if this ScrollViewer actually has content to scroll
            if (sv.ScrollableHeight <= 0) return;

            ApplyScrollImpulse(sv, e);
        }

        private static void ApplyScrollImpulse(ScrollViewer sv, MouseWheelEventArgs e)
        {
            e.Handled = true;

            // Get or create state
            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState();
                _states[sv] = state;
            }

            // Detect touchpad vs mouse: touchpad sends small deltas, mouse sends ±120
            double rawDelta = e.Delta;
            bool isTouchpad = Math.Abs(rawDelta) < 120;
            double impulse;

            if (isTouchpad)
            {
                // Cap and scale touchpad delta
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapTouchpad);
                impulse = capped * TouchpadMultiplier;
            }
            else
            {
                // Mouse wheel: ±120 per notch, cap extreme values
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapMouse);
                impulse = capped * MouseWheelMultiplier;
            }

            // Add impulse to velocity (negative because scroll offset increases downward)
            state.Velocity -= impulse;

            // Clamp velocity
            state.Velocity = Math.Clamp(state.Velocity, -MaxVelocity, MaxVelocity);

            // Start animation if not already running
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

                // Apply velocity
                double newOffset = sv.VerticalOffset + state.Velocity;

                // Clamp to bounds
                newOffset = Math.Clamp(newOffset, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);

                // Apply friction
                state.Velocity *= Friction;

                // Stop when velocity is negligible
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

            // Unhook rendering when nothing is animating (save CPU)
            if (!anyActive)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        /// <summary>
        /// Walk up the visual tree to find the nearest ScrollViewer ancestor.
        /// </summary>
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

        /// <summary>
        /// Find the first child of type T in the visual tree.
        /// </summary>
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
