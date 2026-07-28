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
        // ═══ Coast prefetch event (subscribed/unsubscribed by HubWindow) ═══
#pragma warning disable CS0067 // Event is subscribed by HubWindow but invoked via SmoothScroll.CoastPrefetchNeeded
        public static event Action? CoastPrefetchNeeded;
#pragma warning restore CS0067

        // ═══ VS Code target-based animation constants ═══
        private const double TargetDurationMs     = 125.0;    // VS Code uses exactly 125ms duration
        private const double PreAdvanceMs         = 10.0;     // VS Code pretends animation already started for 10ms for instant feel

        // ΓòÉΓòÉΓòÉ Input Scaling ΓòÉΓòÉΓòÉ
        private const double TouchpadScale        = 0.60;     // Sensitivity multiplier for trackpad deltas (flick & drag)
        private const double MouseStepPx          = 96.0;     // Pixels per mouse wheel notch (standard Windows)
        private const double MouseImpulseBoost    = 3.0;      // Mouse wheel step multiplier (increased from 1.5 for faster mouse scrolling)
        private const double DeltaCapTouchpad     = 60.0;     // Clamp raw trackpad delta to absorb driver spikes
        private const double DeltaCapMouse        = 360.0;    // Clamp raw mouse delta
        private const double TouchpadImpulseMul   = 0.09;     // Touchpad impulse multiplier — matched to main clipboard's TouchpadMul

        // ═══ Velocity Physics (unified for mouse + touchpad) ═══
        private const double MouseVelocityFriction   = 0.95;     // Per-frame decay (0.94→0.95 for slightly longer glide, better for content browsing)
        private const double MouseMaxVelocity        = 45.0;     // Maximum velocity cap (px/frame)
        private const double MouseImpulseScale       = 7.2;      // Mouse impulse per notch — matched to clipboard's MouseMul*120 (0.06*120)
        private const double MouseMinVelocity        = 0.15;     // Below this → stop (lowered from 0.20 for Apple-style longer tail)
        private const double MouseDirectionBrakeMul  = 0.35;     // Retained velocity on direction reversal
        private const double MouseTargetFrameMs      = 16.667;   // 60 FPS baseline for frame-time compensation
        private const double MouseBlendFactor        = 0.55;     // Velocity blending factor — matched to main clipboard

        // ΓòÉΓòÉΓòÉ Progressive Touchpad Acceleration ΓòÉΓòÉΓòÉ
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

        static SmoothScrollPCApp()
        {
            // Disable Windows 11 EcoQoS power throttling for unthrottled rendering
            try
            {
                var hProcess = System.Diagnostics.Process.GetCurrentProcess().Handle;
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(state);
                NativeMethods.SetProcessInformation(hProcess, NativeMethods.ProcessPowerThrottling, ref state, size);
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

        // ΓòÉΓòÉΓòÉ GPU Caching ΓÇö Disabled ΓòÉΓòÉΓòÉ
        // BitmapCache degrades ClearType text quality during scroll, causing blurry text.
        // With 3-page virtualization cache + UseLayoutRounding + SnapsToDevicePixels,
        // items don't recycle during scroll, so BitmapCache is unnecessary.

        private static void EnableStaticCanvas(ScrollViewer sv) { }
        private static void DisableStaticCanvas(ScrollViewer sv) { }

        // ΓòÉΓòÉΓòÉ Public API ΓòÉΓòÉΓòÉ

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

        // ΓòÉΓòÉΓòÉ Input Handling ΓòÉΓòÉΓòÉ

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
                try { NativeMethods.TimeBeginPeriod(1); _timerResolutionRefCount++; } catch { }
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

            // ═══ UNIFIED VELOCITY PHYSICS for both touchpad and mouse ═══
            // Both input types feed into the same momentum engine, just with
            // different impulse multipliers. This gives proper momentum scrolling
            // on trackpads (like the main clipboard's SmoothScroll.cs).

            // Cancel any lingering VS Code target animation from old touchpad path
            if (state.IsAnimating && !state.IsMouseVelocityMode)
            {
                state.IsAnimating = false;
                state.PendingDelta = 0;
            }
            state.IsMouseVelocityMode = true;

            double impulse;
            if (isTouchpad)
            {
                // Touchpad: continuous fine-grained deltas → negate (same as mouse) to match scroll direction
                double capped = Math.Sign(delta) * Math.Min(Math.Abs((double)delta), DeltaCapTouchpad);
                impulse = -capped * TouchpadImpulseMul;
            }
            else
            {
                // Mouse: discrete 120-unit notches → scale with MouseImpulseScale
                double notches = delta / 120.0;
                double capped = Math.Sign(notches) * Math.Min(Math.Abs(notches), DeltaCapMouse / 120.0);
                impulse = -capped * MouseImpulseScale;
            }

            // Coalesce impulses for per-frame drain (touchpads fire 200-500 Hz bursts)
            state.PendingMouseImpulse += impulse;

            if (!state.IsAnimating)
            {
                state.IsAnimating = true;
                state.TrueOffset = sv.VerticalOffset;
                state.LastSetOffset = sv.VerticalOffset;
                state.LastOffset = sv.VerticalOffset;
                state.LastFrameTick = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            if (!_renderingAttached)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingAttached = true;
                ElevateUIThreadPriority();
            }
        }

        // ΓòÉΓòÉΓòÉ Render Loop ΓòÉΓòÉΓòÉ

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

                // ΓòÉΓòÉΓòÉ MOUSE VELOCITY PHYSICS MODE ΓòÉΓòÉΓòÉ
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
                        // Touchpad uses higher threshold (4.0) — residual momentum
                        // shouldn't trigger braking or upward scrolling feels sluggish.
                        double mouseReversalThreshold = state.IsTouchpad ? 4.0 : 2.0;
                        bool mouseReversal = Math.Abs(state.MouseVelocity) > mouseReversalThreshold &&
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
                            // Input-appropriate blend factor:
                            // Touchpad uses softer 0.35 for buttery continuity (Chrome/macOS-like).
                            // Mouse uses snappier 0.55 for responsive notch-to-motion feel.
                            double blendFactor = state.IsTouchpad ? 0.35 : MouseBlendFactor;
                            state.MouseVelocity += (mouseTargetV - state.MouseVelocity) * blendFactor;
                        }

                        state.MouseVelocity = Math.Clamp(state.MouseVelocity, -MouseMaxVelocity, MouseMaxVelocity);
                    }

                    // Boundary check ΓÇö stop cleanly at scroll limits
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

                    // Round to pixel boundary — required for VirtualizingStackPanel (sub-pixel causes jitter)
                    double mouseNextOffset = Math.Round(state.TrueOffset);
                    // Only write if delta >= 0.5px to avoid redundant layout passes
                    if (Math.Abs(mouseNextOffset - sv.VerticalOffset) >= 0.5)
                    {
                        sv.ScrollToVerticalOffset(mouseNextOffset);
                    }
                    // Store rounded offset so WPF sync check doesn't produce false deltas
                    state.LastSetOffset = mouseNextOffset;

                    // Velocity-adaptive friction: wider transition band (2-20 px/frame)
                    // eliminates mid-speed resonance — matched to clipboard engine.
                    double mouseAbsV = Math.Abs(state.MouseVelocity);
                    double mouseSlowFriction = 0.96;
                    double mouseFastFriction = 0.93;
                    double mouseFrictionT = Math.Clamp((mouseAbsV - 2.0) / 18.0, 0.0, 1.0);
                    mouseFrictionT = mouseFrictionT * mouseFrictionT * (3.0 - 2.0 * mouseFrictionT);
                    double mouseAdaptiveFriction = mouseSlowFriction + (mouseFastFriction - mouseSlowFriction) * mouseFrictionT;
                    state.MouseVelocity *= Math.Pow(mouseAdaptiveFriction, mouseTimeScale);

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

                // ΓòÉΓòÉΓòÉ TOUCHPAD: VS Code Target Animation (unchanged) ΓòÉΓòÉΓòÉ

                // ΓòÉΓòÉΓòÉ DRAIN COALESCED INPUT ΓòÉΓòÉΓòÉ
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

                // ΓòÉΓòÉΓòÉ Calculate Animation Position ΓòÉΓòÉΓòÉ
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

                // ΓòÉΓòÉΓòÉ Dispatch Real-Time Telemetry ΓòÉΓòÉΓòÉ
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
                    try { NativeMethods.TimeEndPeriod(1); _timerResolutionRefCount--; } catch { }
                    _timerResolutionElevated = false;
                }
            }
        }

        // ΓòÉΓòÉΓòÉ VS Code Mathematical Interpolation with Huge Jump Composed Easing ΓòÉΓòÉΓòÉ

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

        // ΓòÉΓòÉΓòÉ Thread Priority Management ΓòÉΓòÉΓòÉ

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

        // ΓòÉΓòÉΓòÉ Visual Tree Helpers ΓòÉΓòÉΓòÉ

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

        /// <summary>
        /// Returns true if the given element is inside the main clipboard HubListView.
        /// We detect this by walking up the visual tree looking for a ListView named "HubListView".
        /// </summary>
        private static bool IsInsideHubListView(DependencyObject? element)
        {
            var current = element;
            while (current != null)
            {
                if (current is ListView lv && lv.Name == "HubListView")
                    return true;
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
