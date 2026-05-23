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
        public double TouchpadEase { get; set; } = 1.0;         // 1.0 = direct response (zero artificial LERP lag)
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
            MouseEase = 0.28,             // Snappy, quick-stopping, non-floaty LERP (Win+V style)
            MouseScrollStep = 72.0,       // Controlled responsive notch step
            TouchpadEase = 0.20,          // Buttery smooth LERP decay (enables smooth deceleration coasting)
            TouchpadMultiplier = 0.70
        };

        /// <summary>
        /// Silky smooth, modern web-like sweeping glide designed for the PC Dashboard application.
        /// </summary>
        public static readonly ScrollProfile PCAppProfile = new()
        {
            MouseEase = 0.11,             // Luxurious long glide (restored to perfect committed state)
            MouseScrollStep = 120.0,      // Deeper notch steps for tall settings/logs pages
            TouchpadEase = 1.0,           // Direct trackpad input
            TouchpadMultiplier = 0.85
        };

        private static readonly ScrollProfile _defaultProfile = new();
        private static readonly Dictionary<Window, ScrollProfile> _windowProfiles = new();
        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        
        private static bool _renderingAttached;
        private static DispatcherTimer? _cleanupTimer;
        private const double TargetFrameMs = 16.667; // 60 FPS standard baseline

        private class ScrollState
        {
            public double TargetOffset;
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;
            public long LastInputTime;
            public double AccumulatedTouchpadScrollAmount; // Coalesces and accumulates pre-boosted scroll distance
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
            }

            if (_states.Count == 0 && _renderingAttached)
            {
                _cleanupTimer?.Stop();
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
            }
        }

        /// <summary>
        /// Clears any in-flight smooth scroll animation state for the given ScrollViewer.
        /// Call this before programmatically resetting the scroll offset so that a stale
        /// TargetOffset from a previous scroll session doesn't fight the reset.
        /// </summary>
        public static void ResetScrollState(ScrollViewer? sv)
        {
            if (sv == null) return;
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
                double directScrollAmount = -delta * profile.TouchpadMultiplier;
                double nextOffset = Math.Clamp(sv.VerticalOffset + directScrollAmount, 0, sv.ScrollableHeight);
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

            long now = Environment.TickCount64;
            long timeSinceLastInput = now - state.LastInputTime;
            state.LastInputTime = now;

            state.IsTouchpad = isTouchpad;
            state.Profile = profile;

            // Calculate input packet velocity (delta units per millisecond)
            double dt = timeSinceLastInput > 0 ? timeSinceLastInput : 1.0;
            double inputVelocity = Math.Abs(delta) / dt;

            // ═══ Kinetic Acceleration (Turbo Booster) ═══
            // If the user scrolls with high velocity (a sudden quick burst),
            // dynamically scale up the scroll distance to generate kinetic momentum.
            double accelerationMultiplier = 1.0;
            if (state.IsTouchpad && inputVelocity > 2.5)
            {
                accelerationMultiplier = Math.Min(2.5, 1.0 + (inputVelocity - 2.5) * 0.15);
            }

            double scrollAmount = -delta * profile.TouchpadMultiplier * accelerationMultiplier;

            // ═══ Touchpad Input Coalescing/Throttling (VSync-Lock Engine) ═══
            // If the LERP animation is active, buffer incoming touchpad packets and process them 
            // exactly once per frame tick in OnRendering (synchronized to screen VSync).
            // Exception: If the user reverses scroll direction or a time gap (>250ms/350ms) occurs,
            // we bypass buffering to instantly snap the animation and stay highly responsive.
            if (state.IsTouchpad && state.IsAnimating)
            {
                bool isReversing = Math.Sign(scrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset);
                long gapAllowance = (scrollAmount < 0) ? 350 : 250;

                if (timeSinceLastInput <= gapAllowance && !isReversing)
                {
                    state.AccumulatedTouchpadScrollAmount += scrollAmount;
                    return;
                }
            }

            double maxOvershoot = sv.ActualHeight > 50 ? (sv.ActualHeight * 0.8) : 400.0;

            if (profile == ClipboardProfile)
            {
                if (state.IsTouchpad)
                {
                    // Snap target offset to current vertical offset if starting a fresh scroll,
                    // changing scroll direction, or if there is a real time gap (>250ms) between events
                    // (fingers lifted or gesture paused). This allows short driver-level inertia phases 
                    // to unite perfectly with direct dragging without any visual stutters or breaks!
                    bool isReversing = Math.Sign(scrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset);
                    
                    // Asymmetrical Merging Gap: Increase time allowance slightly when scrolling UP (350ms)
                    // to perfectly accommodate the less linear finger-extension phase.
                    long gapAllowance = (scrollAmount < 0) ? 350 : 250;
                    if (!state.IsAnimating || timeSinceLastInput > gapAllowance || isReversing)
                    {
                        state.TargetOffset = sv.VerticalOffset;
                    }

                    state.TargetOffset = Math.Clamp(state.TargetOffset + scrollAmount, 
                                                    sv.VerticalOffset - maxOvershoot, 
                                                    sv.VerticalOffset + maxOvershoot);
                }
                else
                {
                    double mouseScrollAmount = -(delta / 120.0) * profile.MouseScrollStep;
                    
                    // Snap starting target if starting a fresh scroll or reversing direction
                    if (!state.IsAnimating || Math.Sign(mouseScrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset))
                    {
                        state.TargetOffset = sv.VerticalOffset;
                    }
                    
                    state.TargetOffset = Math.Clamp(state.TargetOffset + mouseScrollAmount, 
                                                    sv.VerticalOffset - (maxOvershoot * 1.2), 
                                                    sv.VerticalOffset + (maxOvershoot * 1.2));
                }
            }
            else
            {
                if (state.IsTouchpad)
                {
                    state.TargetOffset += scrollAmount;
                }
                else
                {
                    double mouseScrollAmount = -(delta / 120.0) * profile.MouseScrollStep;
                    
                    // Snap starting target if starting a fresh scroll or reversing direction
                    if (!state.IsAnimating || Math.Sign(mouseScrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset))
                    {
                        state.TargetOffset = sv.VerticalOffset;
                    }
                    
                    state.TargetOffset += mouseScrollAmount;
                }
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

                // Suspend theme animations to free up 100% UI thread budget for buttery smooth scrolling!
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

                // Apply any deferred high-frequency touchpad input queued during this frame interval
                if (state.IsTouchpad && state.AccumulatedTouchpadScrollAmount != 0)
                {
                    double scrollAmount = state.AccumulatedTouchpadScrollAmount;
                    state.AccumulatedTouchpadScrollAmount = 0; // Clear coalesced queue
                    
                    double maxOvershoot = sv.ActualHeight > 50 ? (sv.ActualHeight * 0.8) : 400.0;
                    if (state.Profile == ClipboardProfile)
                    {
                        state.TargetOffset = Math.Clamp(state.TargetOffset + scrollAmount, 
                                                        sv.VerticalOffset - maxOvershoot, 
                                                        sv.VerticalOffset + maxOvershoot);
                    }
                    else
                    {
                        state.TargetOffset += scrollAmount;
                    }
                    state.TargetOffset = Math.Clamp(state.TargetOffset, 0, sv.ScrollableHeight);
                }

                long elapsed = now - state.LastFrameTick;
                if (elapsed <= 0) elapsed = 1;
                double timeScale = elapsed / TargetFrameMs;
                
                timeScale = Math.Min(timeScale, 4.0);
                state.LastFrameTick = now;

                double currentOffset = sv.VerticalOffset;
                double diff = state.TargetOffset - currentOffset;

                // Asymmetrical Snapping: Quiet threshold (0.01px) when scrolling UP to let slow coasting glides finish smoothly
                double snapThreshold = (state.Profile == ClipboardProfile) 
                    ? (diff < 0 ? 0.01 : 0.05) 
                    : 0.01;

                if (Math.Abs(diff) < snapThreshold)
                {
                    sv.ScrollToVerticalOffset(state.TargetOffset);
                    state.IsAnimating = false;
                    completed.Add(sv);
                }
                else
                {
                    // Asymmetrical LERP Friction: Apply slightly more fluid ease (0.16) when scrolling UP
                    // to perfectly balance physical finger extension dynamics.
                    double baseEase = state.IsTouchpad 
                        ? (diff < 0 ? 0.16 : state.Profile.TouchpadEase) 
                        : state.Profile.MouseEase;

                    double ease = baseEase;
                    if (state.IsTouchpad)
                    {
                        // ═══ Dynamic Friction (Variable Drag Curve) ═══
                        // As we approach the target offset (diff gets small), we gradually decay the LERP ease constant.
                        // This extends the tail of the coasting phase into an ultra-luxurious, whispers-soft, gradual slowing stop.
                        // It completely prevents sudden stops or end-of-flick jerks, slowing down in a beautiful native glide!
                        double distance = Math.Abs(diff);
                        if (distance < 80.0) // 80px deceleration boundary window
                        {
                            double ratio = distance / 80.0;
                            ease = 0.04 + (baseEase - 0.04) * ratio; // Decay ease down to a whisper-soft 0.04 at the tail
                        }
                    }

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
    }
}
