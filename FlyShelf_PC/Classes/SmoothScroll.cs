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
    /// Custom configuration profile for the smooth scroll engine.
    /// </summary>
    public class ScrollProfile
    {
        public double MouseEase { get; set; } = 0.18;
        public double MouseScrollStep { get; set; } = 96.0;
        public double TouchpadEase { get; set; } = 1.0; // 1.0 = direct response (zero artificial LERP lag)
        public double TouchpadMultiplier { get; set; } = 0.85;
    }

    /// <summary>
    /// Premium target-based smooth scroll engine for WPF.
    /// Modeled after Windows 11 / Modern Web (Chrome, Edge) native scroll behaviors.
    /// Supports modular scrolling profiles for Clipboard vs PC App windows.
    /// </summary>
    public static class SmoothScroll
    {
        // ═══ Global scrolling profiles ═══
        
        /// <summary>
        /// Highly responsive, tactile profile designed for the floating Clipboard Overlay (snappy glides).
        /// </summary>
        public static readonly ScrollProfile ClipboardProfile = new()
        {
            MouseEase = 0.20,
            MouseScrollStep = 90.0,
            TouchpadEase = 1.0,           // Direct trackpad input
            TouchpadMultiplier = 0.85
        };

        /// <summary>
        /// Silky smooth, modern web-like sweeping glide designed for the PC Dashboard application.
        /// </summary>
        public static readonly ScrollProfile PCAppProfile = new()
        {
            MouseEase = 0.11,             // Luxurious long glide
            MouseScrollStep = 120.0,      // Deeper notch steps for tall settings/logs pages
            TouchpadEase = 1.0,           // Direct trackpad input
            TouchpadMultiplier = 0.85
        };

        private static readonly ScrollProfile _defaultProfile = new();
        private static readonly Dictionary<Window, ScrollProfile> _windowProfiles = new();
        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        
        private static bool _renderingAttached;
        private static DispatcherTimer? _cleanupTimer;
        private const double TargetFrameMs = 16.667; // 60 FPS standard baseline

        private class ScrollState
        {
            public double TargetOffset;
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;
            public ScrollProfile Profile = null!;
        }

        /// <summary>
        /// Hook unified window-wide scroll logic at the Window level with a custom profile.
        /// </summary>
        public static void AttachToWindow(Window window, ScrollProfile? profile = null)
        {
            window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            window.PreviewMouseWheel += OnWindowPreviewMouseWheel;

            _windowProfiles[window] = profile ?? _defaultProfile;
        }

        /// <summary>
        /// Unhook and clean up references.
        /// </summary>
        public static void DetachFromWindow(Window window)
        {
            window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;
            _windowProfiles.Remove(window);

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
            }

            if (_states.Count == 0 && _renderingAttached)
            {
                _cleanupTimer?.Stop();
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        private static void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            Window? window = sender as Window;
            if (window == null) return;

            if (!_windowProfiles.TryGetValue(window, out var profile))
            {
                profile = _defaultProfile;
            }

            DependencyObject? source = e.OriginalSource as DependencyObject;
            ScrollViewer? sv = FindScrollableScrollViewerAncestor(source);

            if (sv == null) return;

            // Bubbling Boundary check: If we are already at the top/bottom physical limits,
            // do not handle the event. Let it bubble naturally to parent ScrollViewers.
            bool atTopBoundary = sv.VerticalOffset <= 0 && e.Delta > 0;
            bool atBottomBoundary = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;

            if (atTopBoundary || atBottomBoundary)
            {
                return;
            }

            // Intercept and handle scroll
            e.Handled = true;
            ApplyScroll(sv, e.Delta, profile);
        }

        private static void ApplyScroll(ScrollViewer sv, int delta, ScrollProfile profile)
        {
            // Cancel cleanup timer since there is active scroll input
            if (_cleanupTimer != null && _cleanupTimer.IsEnabled)
            {
                _cleanupTimer.Stop();
            }

            // Distinguish Precision Touchpad (high frequency, small deltas) vs Mouse Wheel (discrete 120s)
            bool isTouchpad = (delta % 120 != 0) || (Math.Abs(delta) < 120);

            // Direct touchpad follow logic (if touchpad ease >= 1.0, bypass LERP loop for 1:1 tactile follow)
            if (isTouchpad && profile.TouchpadEase >= 1.0)
            {
                double scrollAmount = -delta * profile.TouchpadMultiplier;
                double nextOffset = Math.Clamp(sv.VerticalOffset + scrollAmount, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(nextOffset);

                // If currently animating via mouse wheel, update the target to stay in sync
                if (_states.TryGetValue(sv, out var activeState) && activeState.IsAnimating)
                {
                    activeState.TargetOffset = nextOffset;
                }
                return;
            }

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState
                {
                    TargetOffset = sv.VerticalOffset
                };
                _states[sv] = state;
            }

            state.IsTouchpad = isTouchpad;
            state.Profile = profile;

            if (state.IsTouchpad)
            {
                double scrollAmount = -delta * profile.TouchpadMultiplier;
                state.TargetOffset += scrollAmount;
            }
            else
            {
                double scrollAmount = -(delta / 120.0) * profile.MouseScrollStep;
                
                // Snap starting target if starting a fresh scroll or reversing direction
                if (!state.IsAnimating || Math.Sign(scrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset))
                {
                    state.TargetOffset = sv.VerticalOffset;
                }
                
                state.TargetOffset += scrollAmount;
            }

            state.TargetOffset = Math.Clamp(state.TargetOffset, 0, sv.ScrollableHeight);

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                state.LastFrameTick = Environment.TickCount64;
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

                long elapsed = now - state.LastFrameTick;
                if (elapsed <= 0) elapsed = 1;
                double timeScale = elapsed / TargetFrameMs;
                
                timeScale = Math.Min(timeScale, 4.0);
                state.LastFrameTick = now;

                double currentOffset = sv.VerticalOffset;
                double diff = state.TargetOffset - currentOffset;

                if (Math.Abs(diff) < 0.01)
                {
                    sv.ScrollToVerticalOffset(state.TargetOffset);
                    state.IsAnimating = false;
                    completed.Add(sv);
                }
                else
                {
                    double ease = state.IsTouchpad 
                        ? state.Profile.TouchpadEase 
                        : state.Profile.MouseEase;

                    double factor = 1.0 - Math.Pow(1.0 - ease, timeScale);
                    double step = diff * factor;
                    double nextOffset = currentOffset + step;
                    
                    nextOffset = Math.Clamp(nextOffset, 0, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(nextOffset);

                    if (nextOffset <= 0 || nextOffset >= sv.ScrollableHeight)
                    {
                        state.IsAnimating = false;
                        completed.Add(sv);
                    }
                    else
                    {
                        anyAnimating = true;
                    }
                }
            }

            foreach (var sv in completed)
            {
                _states.Remove(sv);
            }

            if (!anyAnimating)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        private static ScrollViewer? FindScrollableScrollViewerAncestor(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is ScrollViewer sv && sv.ScrollableHeight > 0)
                {
                    return sv;
                }
                element = VisualTreeHelper.GetParent(element);
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
    }
}
