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
        private const double MouseImpulseBoost    = 3.0;      // Mouse wheel step multiplier (increased from 1.5 for faster mouse scrolling)
        private const double DeltaCapTouchpad     = 60.0;     // Clamp raw trackpad delta to absorb driver spikes
        private const double DeltaCapMouse        = 360.0;    // Clamp raw mouse delta

        // ═══ Mouse Velocity Physics (mouse-only smooth momentum) ═══
        private const double MouseVelocityFriction   = 0.88;     // Per-frame exponential decay — smooth deceleration
        private const double MouseMaxVelocity        = 60.0;     // Maximum velocity cap (px/frame)
        private const double MouseImpulseScale       = 65.0;     // Raw velocity impulse per wheel notch (blended at 0.55)
        private const double MouseMinVelocity        = 1.5;      // Below this → stop animation (cuts imperceptible tail)
        private const double MouseDirectionBrakeMul  = 0.35;     // Retained velocity on direction reversal
        private const double MouseTargetFrameMs      = 16.667;   // 60 FPS baseline for frame-time compensation

        // ═══ Progressive Touchpad Acceleration ═══
        private const double ProgressiveFloor     = 0.50;
        private const double ProgressiveCeiling   = 1.00;
        private const double ProgressiveThreshold = 40.0;

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        private static List<ScrollViewer> _scrollKeysBuffer = new();
        private static List<ScrollViewer> _completedBuffer = new();

        private static bool _renderingAttached;
        private static bool _timerResolutionElevated;
        private static int _timerResolutionRefCount;  // Bug 5 fix: balanced timeBeginPeriod/timeEndPeriod

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

        static SmoothScrollPCApp()
        {
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
            catch { } // Best-effort: failure is acceptable
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

            public double PendingDelta;      // Coalesced target displacement (touchpad only)
            public long LastInputTime;       // Environment.TickCount64 of last input event

            public double LastOffset;        // Position at the previous rendering frame
            public long LastFrameTick;       // Timestamp of the previous rendering frame

            // Mouse velocity mode state
            public bool IsMouseVelocityMode;
            public double MouseVelocity;
            public double PendingMouseImpulse;
            public double TrueOffset;        // Sub-pixel precise position for mouse mode
            public double LastSetOffset;     // Last written ScrollViewer offset for mouse mode
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
            // Elevate Windows scheduler timer resolution to 1ms for smooth rendering
            // Bug 5 fix: Use reference counting to prevent unmatched Begin/End pairs
            if (!_timerResolutionElevated)
            {
                try { TimeBeginPeriod(1); _timerResolutionRefCount++; } catch { }
                _timerResolutionElevated = true;
            }
            bool isTouchpad = (delta % 120 != 0) || (Math.Abs(delta) < 120);

            if (!_states.TryGetValue(sv, out var state))
            {
                state = new ScrollState();
                _states[sv] = state;
            }

            long now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            state.IsTouchpad = isTouchpad;
            state.LastInputTime = now;

            if (isTouchpad)
            {
                // ═══ TOUCHPAD: VS Code target-based animation (unchanged) ═══
                // If switching from mouse velocity mode, cancel it cleanly
                if (state.IsMouseVelocityMode)
                {
                    state.IsMouseVelocityMode = false;
                    state.MouseVelocity = 0;
                    state.PendingMouseImpulse = 0;
                    state.IsAnimating = false; // Force touchpad to re-seed VS Code animation
                }

                // Treat touchpad deltas as raw pixel displacement
                double rawAbs = Math.Abs(delta);
                double capped = Math.Min(rawAbs, DeltaCapTouchpad);
                double speedRatio = Math.Min(capped / ProgressiveThreshold, 1.0);
                double progressiveMul = ProgressiveFloor + (ProgressiveCeiling - ProgressiveFloor) * speedRatio;
                double displacement = -Math.Sign(delta) * capped * progressiveMul * TouchpadScale;

                // Coalesce incoming scroll deltas to prevent micro-stuttering
                state.PendingDelta += displacement;

                if (!state.IsAnimating)
                {
                    // Seed starting values for VS Code animation
                    state.FromOffset = sv.VerticalOffset;
                    state.ToOffset = sv.VerticalOffset;
                    state.ViewportHeight = sv.ViewportHeight;
                    state.LastOffset = sv.VerticalOffset;
                    state.LastFrameTick = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
            else
            {
                // ═══ MOUSE: Velocity-based momentum physics ═══
                // If switching from touchpad VS Code animation, cancel it cleanly
                if (state.IsAnimating && !state.IsMouseVelocityMode)
                {
                    state.IsAnimating = false;
                    state.PendingDelta = 0;
                }
                state.IsMouseVelocityMode = true;

                double notches = delta / 120.0;
                double capped = Math.Sign(notches) * Math.Min(Math.Abs(notches), DeltaCapMouse / 120.0);
                double impulse = -capped * MouseImpulseScale;

                // Coalesce mouse impulses for per-frame drain
                state.PendingMouseImpulse += impulse;

                if (!state.IsAnimating)
                {
                    state.IsAnimating = true;
                    state.TrueOffset = sv.VerticalOffset;
                    state.LastSetOffset = sv.VerticalOffset;
                    state.LastOffset = sv.VerticalOffset;
                    state.LastFrameTick = System.Diagnostics.Stopwatch.GetTimestamp();
                }
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
            long now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            _scrollKeysBuffer.Clear();
            _scrollKeysBuffer.AddRange(_states.Keys);
            _completedBuffer.Clear();

            foreach (var sv in _scrollKeysBuffer)
            {
                if (!_states.TryGetValue(sv, out var state)) continue;

                // ═══ MOUSE VELOCITY PHYSICS MODE ═══
                if (state.IsMouseVelocityMode)
                {
                    if (!state.IsAnimating)
                    {
                        state.IsMouseVelocityMode = false;
                        _completedBuffer.Add(sv);
                        continue;
                    }

                    // Frame-time compensation
                    long mouseTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    double mouseElapsedMs = (double)(mouseTimestamp - state.LastFrameTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (mouseElapsedMs <= 0) mouseElapsedMs = 1.0;
                    double mouseTimeScale = Math.Min(mouseElapsedMs / MouseTargetFrameMs, 3.0);
                    state.LastFrameTick = mouseTimestamp;

                    // Synchronize with WPF layout shifts (same approach as clipboard SmoothScroll)
                    double mouseActualOffset = sv.VerticalOffset;
                    double mouseWpfDelta = mouseActualOffset - state.LastSetOffset;
                    if (Math.Abs(mouseWpfDelta) > 5.0)
                    {
                        state.TrueOffset += mouseWpfDelta;
                        state.LastSetOffset = mouseActualOffset;
                    }
                    else if (Math.Abs(mouseWpfDelta) > 0.001)
                    {
                        state.LastSetOffset = mouseActualOffset;
                    }

                    // Drain coalesced mouse impulse into velocity with smooth blending
                    if (state.PendingMouseImpulse != 0)
                    {
                        double mousePending = state.PendingMouseImpulse;
                        state.PendingMouseImpulse = 0;

                        double mouseTargetV = state.MouseVelocity + mousePending;
                        mouseTargetV = Math.Clamp(mouseTargetV, -MouseMaxVelocity, MouseMaxVelocity);

                        // Smooth direction reversal: 2-phase brake-and-reverse
                        bool mouseReversal = Math.Abs(state.MouseVelocity) > 2.0 &&
                                             Math.Sign(mouseTargetV) != Math.Sign(state.MouseVelocity);

                        if (mouseReversal)
                        {
                            // Phase 1: Fast brake toward zero
                            state.MouseVelocity *= MouseDirectionBrakeMul;
                            if (Math.Abs(state.MouseVelocity) < 0.3)
                                state.MouseVelocity = 0;
                        }
                        else
                        {
                            // Responsive acceleration blend
                            state.MouseVelocity += (mouseTargetV - state.MouseVelocity) * 0.55;
                        }

                        state.MouseVelocity = Math.Clamp(state.MouseVelocity, -MouseMaxVelocity, MouseMaxVelocity);
                    }

                    // Boundary check — stop cleanly at scroll limits
                    bool mouseBound = (state.TrueOffset <= 0 && state.MouseVelocity < 0) ||
                                      (state.TrueOffset >= sv.ScrollableHeight && state.MouseVelocity > 0);

                    if (mouseBound)
                    {
                        state.MouseVelocity = 0;
                        state.TrueOffset = Math.Clamp(Math.Round(state.TrueOffset), 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        state.IsAnimating = false;
                        state.IsMouseVelocityMode = false;
                        _completedBuffer.Add(sv);
                        continue;
                    }

                    // Apply velocity with frame-time compensation
                    double mouseDisplacement = state.MouseVelocity * mouseTimeScale;
                    state.TrueOffset += mouseDisplacement;
                    state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);

                    double mouseNextOffset = Math.Round(state.TrueOffset);
                    // Bug 1 fix: Only write to ScrollViewer if delta >= 0.5px
                    if (Math.Abs(mouseNextOffset - sv.VerticalOffset) >= 0.5)
                    {
                        sv.ScrollToVerticalOffset(mouseNextOffset);
                    }
                    // Bug 4 fix: Store the ROUNDED offset so WPF sync check doesn't
                    // produce false deltas every frame from rounding mismatch
                    state.LastSetOffset = mouseNextOffset;

                    // Apply exponential friction
                    state.MouseVelocity *= Math.Pow(MouseVelocityFriction, mouseTimeScale);

                    // Stop when velocity is imperceptible
                    if (Math.Abs(state.MouseVelocity) < MouseMinVelocity)
                    {
                        state.MouseVelocity = 0;
                        state.TrueOffset = Math.Clamp(state.TrueOffset, 0, sv.ScrollableHeight);
                        sv.ScrollToVerticalOffset(state.TrueOffset);
                        state.LastSetOffset = state.TrueOffset;
                        state.IsAnimating = false;
                        state.IsMouseVelocityMode = false;
                        _completedBuffer.Add(sv);
                    }
                    else
                    {
                        anyAnimating = true;
                    }

                    // Dispatch telemetry for mouse scroll
                    try
                    {
                        double mouseDiff = mouseNextOffset - state.LastOffset;
                        double mouseVelPxSec = Math.Abs(mouseDiff / (mouseElapsedMs / 1000.0));
                        state.LastOffset = mouseNextOffset;
                        double mouseFps = 1000.0 / mouseElapsedMs;
                        string mouseCardsData = Logger.IsEnabled ? ScrollTelemetryClient.GetVisibleItemsTelemetry(sv) : string.Empty;
                        ScrollTelemetryClient.SendTelemetry(mouseNextOffset, state.TrueOffset, mouseVelPxSec, mouseFps, mouseElapsedMs, sv.ViewportHeight, sv.ScrollableHeight, mouseCardsData);
                    }
                    catch { } // Best-effort: failure is acceptable

                    continue; // Skip touchpad VS Code animation below
                }

                // ═══ TOUCHPAD: VS Code Target Animation (unchanged) ═══

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
                    _completedBuffer.Add(sv);
                    continue;
                }

                long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedMs = (double)(currentTimestamp - state.LastFrameTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs <= 0) elapsedMs = 1.0;
                state.LastFrameTick = currentTimestamp;

                // ═══ Calculate Animation Position ═══
                long elapsedAnim = now - state.StartTimeMs;
                double animCompletion = state.DurationMs > 0 ? (double)elapsedAnim / state.DurationMs : 1.0;

                double nextOffset;
                if (animCompletion >= 1.0)
                {
                    // Snap exactly to final target
                    double finalTarget = Math.Clamp(state.ToOffset, 0.0, sv.ScrollableHeight);
                    nextOffset = Math.Round(finalTarget);
                    // Bug 1 fix: Only write if delta >= 0.5px
                    if (Math.Abs(nextOffset - sv.VerticalOffset) >= 0.5)
                        sv.ScrollToVerticalOffset(nextOffset);
                    
                    state.IsAnimating = false;
                    _completedBuffer.Add(sv);
                }
                else
                {
                    double nextPos = GetPositionAtCompletion(state.FromOffset, state.ToOffset, state.ViewportHeight, animCompletion);
                    nextOffset = Math.Clamp(nextPos, 0.0, sv.ScrollableHeight);
                    nextOffset = Math.Round(nextOffset);
                    
                    // Bug 1 fix: Only write if delta >= 0.5px
                    if (Math.Abs(nextOffset - sv.VerticalOffset) >= 0.5)
                        sv.ScrollToVerticalOffset(nextOffset);
                    anyAnimating = true;
                }

                // ═══ Dispatch Real-Time Telemetry ═══
                try
                {
                    double diff = nextOffset - state.LastOffset;
                    double velocityInPixelsSec = Math.Abs(diff / (elapsedMs / 1000.0));
                    state.LastOffset = nextOffset;

                    double fps = 1000.0 / elapsedMs;
                    string cardsData = Logger.IsEnabled ? ScrollTelemetryClient.GetVisibleItemsTelemetry(sv) : string.Empty;
                    ScrollTelemetryClient.SendTelemetry(nextOffset, state.ToOffset, velocityInPixelsSec, fps, elapsedMs, sv.ViewportHeight, sv.ScrollableHeight, cardsData);
                }
                catch { } // Best-effort: failure is acceptable
            }

            foreach (var sv in _completedBuffer)
            {
                _states.Remove(sv);
                DisableStaticCanvas(sv);
            }

            if (!anyAnimating)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
                RestoreUIThreadPriority();

                // Restore system timer resolution when scrolling stops
                // Bug 5 fix: Balanced timer resolution restore
                if (_timerResolutionElevated && _timerResolutionRefCount > 0)
                {
                    try { TimeEndPeriod(1); _timerResolutionRefCount--; } catch { }
                    _timerResolutionElevated = false;
                }
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
            catch { } // Best-effort: failure is acceptable
        }

        private static void RestoreUIThreadPriority()
        {
            try
            {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
            }
            catch { } // Best-effort: failure is acceptable
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
                if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
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
