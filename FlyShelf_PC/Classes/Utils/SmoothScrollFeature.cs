// ---------------------------------------------------------------
// SmoothScrollFeature — Lightweight smooth scroll engine for feature windows
// Provides buttery-smooth pixel-based scrolling for all secondary/feature
// windows (ShortcutsWindow, TransferManager, ReminderHistory, etc.)
//
// This is a SEPARATE engine from SmoothScroll (main clipboard) and
// SmoothScrollPCApp (HubWindow/dashboard). Do NOT merge these.
//
// Usage:
//   SmoothScrollFeature.Attach(this);   // in Window constructor
//   SmoothScrollFeature.Detach(this);   // in OnClosed
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight smooth scroll engine for feature/secondary windows.
    /// Uses cubic easing with velocity accumulation for a natural feel.
    /// Automatically finds ScrollViewers in the visual tree.
    /// </summary>
    public static class SmoothScrollFeature
    {
        // ═══ Physics Constants ═══
        private const double Friction            = 0.88;    // Per-frame velocity decay (lower = more friction)
        private const double MouseScrollPx       = 64.0;    // Pixels per mouse wheel notch
        private const double TouchpadScale       = 0.55;    // Sensitivity for trackpad deltas
        private const double MaxVelocity         = 50.0;    // Cap to prevent runaway scrolling
        private const double MinVelocity         = 0.3;     // Below this → stop animating
        private const double DirectionBrake      = 0.3;     // Retained velocity on direction reversal
        private const double VelocityBlend       = 0.65;    // Blend factor between old and new velocity
        private const double TargetFrameMs       = 16.667;  // 60 FPS baseline

        // ═══ Per-ScrollViewer State ═══
        private class ScrollState
        {
            public double Velocity;
            public double TargetOffset;
            public bool IsAnimating;
            public long LastFrameTick;
        }

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _svCache = new();
        private static readonly HashSet<Window> _attachedWindows = new();
        private static bool _renderingAttached;

        // ═══ Public API ═══

        /// <summary>
        /// Attach smooth scrolling to all ScrollViewers within a window.
        /// Call once in the window constructor or Loaded event.
        /// </summary>
        public static void Attach(Window window)
        {
            if (window == null || _attachedWindows.Contains(window)) return;
            _attachedWindows.Add(window);
            window.PreviewMouseWheel -= OnPreviewMouseWheel;
            window.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        /// <summary>
        /// Detach and clean up all state for the window.
        /// Call in OnClosed or Closed event.
        /// </summary>
        public static void Detach(Window window)
        {
            if (window == null) return;
            _attachedWindows.Remove(window);
            window.PreviewMouseWheel -= OnPreviewMouseWheel;

            // Clean up states for ScrollViewers in this window
            var toRemove = new List<ScrollViewer>();
            foreach (var sv in _states.Keys)
            {
                if (IsDescendantOf(sv, window))
                    toRemove.Add(sv);
            }
            foreach (var sv in toRemove)
                _states.Remove(sv);

            // Clear ancestor cache
            _svCache.Clear();

            if (_states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        /// <summary>
        /// Reset any in-flight scroll animation for a specific ScrollViewer.
        /// </summary>
        public static void Reset(ScrollViewer? sv)
        {
            if (sv == null) return;
            if (_states.Remove(sv) && _states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        // ═══ Input Handling ═══

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            // Prevent cache from growing unbounded
            if (_svCache.Count > 80) _svCache.Clear();

            var source = e.OriginalSource as DependencyObject;
            var sv = FindScrollableAncestor(source);
            if (sv == null) return;

            // Let events bubble at boundaries
            bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
            bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
            if (atTop || atBottom) return;

            e.Handled = true;
            ApplyImpulse(sv, e.Delta);
        }

        private static void ApplyImpulse(ScrollViewer sv, int rawDelta)
        {
            bool isTouchpad = (rawDelta % 120 != 0) || (Math.Abs(rawDelta) < 120);

            double impulse;
            if (isTouchpad)
            {
                // Trackpad: scale the raw delta directly
                impulse = rawDelta * TouchpadScale * -1.0;
            }
            else
            {
                // Mouse wheel: fixed pixel step per notch
                int notches = rawDelta / 120;
                impulse = notches * MouseScrollPx * -1.0;
            }

            // Clamp impulse
            impulse = Math.Clamp(impulse, -MaxVelocity * 8, MaxVelocity * 8);

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState
                {
                    Velocity = 0,
                    TargetOffset = sv.VerticalOffset,
                    LastFrameTick = Stopwatch.GetTimestamp(),
                    IsAnimating = false
                };
                _states[sv] = state;
            }

            // Direction reversal braking
            if (Math.Sign(impulse) != Math.Sign(state.Velocity) && Math.Abs(state.Velocity) > MinVelocity)
            {
                state.Velocity *= DirectionBrake;
            }

            // Blend new impulse with existing velocity
            double newVelocity = state.Velocity + impulse * (1.0 - VelocityBlend);
            state.Velocity = Math.Clamp(newVelocity, -MaxVelocity, MaxVelocity);
            state.IsAnimating = true;
            state.LastFrameTick = Stopwatch.GetTimestamp();

            // Start rendering loop if not already
            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;
            }
        }

        // ═══ Animation Loop ═══

        private static void OnRendering(object? sender, EventArgs e)
        {
            if (_states.Count == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
                return;
            }

            var completed = new List<ScrollViewer>();

            foreach (var kvp in _states)
            {
                var sv = kvp.Key;
                var state = kvp.Value;

                if (!state.IsAnimating) continue;

                // Frame-time compensation using high-precision Stopwatch
                long currentTick = Stopwatch.GetTimestamp();
                double frameMs = (double)(currentTick - state.LastFrameTick) * 1000.0 / Stopwatch.Frequency;
                state.LastFrameTick = currentTick;

                // Clamp frame time to prevent huge jumps after tab-away
                frameMs = Math.Clamp(frameMs, 1.0, 50.0);
                double frameScale = frameMs / TargetFrameMs;

                // Apply friction with frame compensation
                double frictionPerFrame = Math.Pow(Friction, frameScale);
                state.Velocity *= frictionPerFrame;

                // Apply velocity to offset
                double delta = state.Velocity * frameScale;
                double newOffset = sv.VerticalOffset + delta;

                // Clamp to bounds
                newOffset = Math.Clamp(newOffset, 0, sv.ScrollableHeight);

                // Pixel-snap + delta guard to avoid redundant layout passes
                double snappedOffset = Math.Round(newOffset);
                if (Math.Abs(snappedOffset - sv.VerticalOffset) >= 0.5)
                    sv.ScrollToVerticalOffset(snappedOffset);

                // Check if animation should stop
                if (Math.Abs(state.Velocity) < MinVelocity || newOffset <= 0 || newOffset >= sv.ScrollableHeight)
                {
                    state.Velocity = 0;
                    state.IsAnimating = false;
                    completed.Add(sv);
                }
            }

            // Clean up completed animations
            foreach (var sv in completed)
                _states.Remove(sv);

            if (_states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        // ═══ Visual Tree Helpers ═══

        private static ScrollViewer? FindScrollableAncestor(DependencyObject? element)
        {
            if (element == null) return null;

            // Cache lookup
            if (_svCache.TryGetValue(element, out var cached))
                return cached;

            DependencyObject? current = element;
            while (current != null)
            {
                if (current is ScrollViewer sv && sv.ScrollableHeight > 0)
                {
                    _svCache[element] = sv;
                    return sv;
                }
                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            DependencyObject? current = element;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
