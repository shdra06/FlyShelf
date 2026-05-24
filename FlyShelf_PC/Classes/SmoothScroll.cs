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
        private const double ScrollFriction      = 0.92;   // Per-frame decay (higher = longer free coasting)
        private const double MaxVelocity         = 90.0;   // Maximum speed cap in pixels/frame
        private const double TouchpadMul         = 0.55;   // Touchpad micro-step scale multiplier
        private const double MouseMul            = 0.65;   // Mouse wheel step scale multiplier
        private const double MinImpulse          = 0.3;    // Minimum impulse threshold for micro-scrolls
        private const double MinVelocity         = 0.05;   // Velocity below this → complete stop (prevents sub-pixel crawl)
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
        }

        private static void EnableStaticCanvas(ScrollViewer sv)
        {
            try
            {
                // Target the VirtualizingStackPanel directly — it holds all realized items.
                // Caching this panel means the GPU rasterizes the entire item area once,
                // then translates the texture during scroll. No per-frame text re-rendering.
                var target = FindDescendant<VirtualizingStackPanel>(sv) as UIElement
                             ?? sv.Content as UIElement;
                if (target != null)
                {
                    target.CacheMode = new BitmapCache
                    {
                        EnableClearType = true,
                        RenderAtScale = 1.0
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

            long now = Environment.TickCount64;
            state.IsTouchpad = isTouchpad;
            state.LastInputTime = now;

            double rawDelta = delta;
            double impulse;

            if (state.IsTouchpad)
            {
                // Progressive velocity scaling for touchpad flick acceleration
                double capped = Math.Sign(rawDelta) * Math.Min(Math.Abs(rawDelta), DeltaCapTouchpad);
                double speedFactor = Math.Min(Math.Abs(rawDelta) / 40.0, 1.0);
                double progressiveMul = 0.30 + (0.45 * speedFactor); // Ranges 0.30 (gentle drag) to 0.75 (fast swipe)
                impulse = capped * progressiveMul * TouchpadMul;

                if (Math.Abs(impulse) < MinImpulse && impulse != 0)
                {
                    impulse = Math.Sign(impulse) * MinImpulse;
                }
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
                state.LastFrameTick = Environment.TickCount64;
                state.TrueOffset = sv.VerticalOffset;  // Seed from current real position
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
                long elapsed = now - state.LastFrameTick;
                if (elapsed <= 0) elapsed = 1;
                double timeScale = elapsed / TargetFrameMs;
                
                // Clamp time scale to avoid huge jumps on lag spikes
                timeScale = Math.Min(timeScale, 3.0);
                state.LastFrameTick = now;

                // Apply velocity vector with frame-time compensation
                double displacement = state.Velocity * timeScale;
                state.TrueOffset += displacement;
                state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                // ═══ PIXEL-SNAP: Render at integer pixel to eliminate sub-pixel text shimmer ═══
                // TrueOffset tracks the real fractional position for smooth physics,
                // but the ScrollViewer always receives a whole-pixel offset so ClearType
                // glyph weights never fluctuate mid-scroll.
                double snappedOffset = Math.Round(state.TrueOffset);
                sv.ScrollToVerticalOffset(snappedOffset);

                // Exponential deceleration (friction decay)
                double friction = state.IsTouchpad 
                    ? 0.86  // Natural touchpad coast — smooth deceleration over ~400ms
                    : ScrollFriction; // Luxurious free coasting glide for mouse wheel sweeps

                state.Velocity *= Math.Pow(friction, timeScale);

                // Stop if at boundary or velocity is negligible
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
