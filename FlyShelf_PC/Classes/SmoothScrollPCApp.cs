using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Highly optimized, high-priority smooth scroll engine dedicated for the C# PC Dashboard Window.
    /// Supports dynamic thread-priority elevation, Windows scheduling boosts, VSync coalescing,
    /// and direct gesture-snapping trackpad glides.
    /// </summary>
    public static class SmoothScrollPCApp
    {
        // ═══ High-Priority Scrolling Physics Profile ═══
        public static readonly double MouseEase = 0.18;             // Modern web sweeping LERP (Google style)
        public static readonly double MouseScrollStep = 96.0;       // Standard notch step
        public static readonly double TouchpadEase = 0.45;          // Silky smooth LERP trackpad ease (matches Python tuner!)
        public static readonly double TouchpadMultiplier = 0.80;

        private static readonly Dictionary<ScrollViewer, ScrollState> _states = new();
        private static readonly Dictionary<DependencyObject, ScrollViewer> _ancestorCache = new();
        
        private static bool _renderingAttached;
        private static DispatcherTimer? _cleanupTimer;
        private const double TargetFrameMs = 16.667; // 60 FPS standard baseline

        private static UdpClient? _udpClient;
        private static readonly IPEndPoint _telemetryEndPoint = new(IPAddress.Loopback, 5892);

        private static void SendTelemetry(double verticalOffset, double targetOffset, double velocity)
        {
            if (_udpClient == null) return;
            try
            {
                long now = Environment.TickCount64;
                string payload = $"APP:{now},{verticalOffset:F2},{targetOffset:F2},{velocity:F2}";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                _udpClient.Send(bytes, bytes.Length, _telemetryEndPoint);
            }
            catch { }
        }

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
            // 1. Permanently elevate Windows scheduler timer resolution to 1ms to prevent dynamic LERP latency stutters
            try
            {
                TimeBeginPeriod(1);
            }
            catch { }

            // 2. Completely disable Windows 11 EcoQoS/Efficiency Mode (Power Throttling) for this process.
            // This forces the Windows kernel to treat our transparent background window with active execution priority,
            // bypassing background core-parking and delivering buttery-smooth 120Hz scrolling natively!
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

            // 3. Initialize low-latency UDP client for live scroll telemetry diagnostics
            try
            {
                _udpClient = new UdpClient();
            }
            catch { }
        }

        private class ScrollState
        {
            public double TargetOffset;
            public bool IsAnimating;
            public bool IsTouchpad;
            public long LastFrameTick;
            public long LastInputTime;
            public double AccumulatedTouchpadScrollAmount; // Coalesces and accumulates pre-boosted scroll distance
        }

        /// <summary>
        /// Hook window-wide scroll logic at the Window level for HubWindow.
        /// </summary>
        public static void AttachToWindow(Window window)
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
            }

            if (_states.Count == 0 && _renderingAttached)
            {
                _cleanupTimer?.Stop();
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
                RestoreUIThreadPriority();
            }
        }

        /// <summary>
        /// Clears any in-flight smooth scroll animation state for the given ScrollViewer.
        /// </summary>
        public static void ResetScrollState(ScrollViewer? sv)
        {
            if (sv == null) return;
            if (_states.Remove(sv) && _states.Count == 0 && _renderingAttached)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingAttached = false;
                RestoreUIThreadPriority();
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

            if (sv.CanContentScroll)
            {
                // Bypass smooth scroll engine for logical item-based ScrollViewers
                // to let WPF's high-performance native logical scrolling take over.
                // This completely eliminates floaty LERP lag, sub-pixel text rendering jitter, and character shivering!
                return;
            }

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
            ApplyScroll(sv, e.Delta);
        }

        private static void ApplyScroll(ScrollViewer sv, int delta)
        {
            // Cancel cleanup timer since there is active scroll input
            if (_cleanupTimer != null && _cleanupTimer.IsEnabled)
            {
                _cleanupTimer.Stop();
            }

            // Distinguish Precision Touchpad (high frequency, small deltas) vs Mouse Wheel (discrete 120s)
            bool isTouchpad = (delta % 120 != 0) || (Math.Abs(delta) < 120);

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

            // Calculate input packet velocity (delta units per millisecond)
            double dt = timeSinceLastInput > 0 ? timeSinceLastInput : 1.0;
            double inputVelocity = Math.Abs(delta) / dt;

            // ═══ Kinetic Acceleration (Turbo Booster) ═══
            double accelerationMultiplier = 1.0;
            if (state.IsTouchpad && inputVelocity > 2.5)
            {
                accelerationMultiplier = Math.Min(2.5, 1.0 + (inputVelocity - 2.5) * 0.15);
            }

            double scrollAmount = -delta * TouchpadMultiplier * accelerationMultiplier;

            // ═══ Touchpad Input Coalescing/Throttling (VSync-Lock Engine) ═══
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

            if (state.IsTouchpad)
            {
                // Snap target offset to current vertical offset if starting a fresh scroll,
                // changing scroll direction, or if there is a real time gap (>250ms) between events.
                // This ensures instant, buttery response to trackpad swipes!
                bool isReversing = Math.Sign(scrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset);
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
                double mouseScrollAmount = -(delta / 120.0) * MouseScrollStep;
                
                // Snap starting target if starting a fresh scroll or reversing direction
                if (!state.IsAnimating || Math.Sign(mouseScrollAmount) != Math.Sign(state.TargetOffset - sv.VerticalOffset))
                {
                    state.TargetOffset = sv.VerticalOffset;
                }
                
                state.TargetOffset += mouseScrollAmount;
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
                ElevateUIThreadPriority();

                // Suspend theme animations to free up 100% UI thread budget for buttery smooth scrolling!
                try
                {
                    var parentWin = Window.GetWindow(sv) as MainWindow;
                    parentWin?.SuspendThemeAnimations();
                }
                catch { }
            }

            SendTelemetry(sv.VerticalOffset, state.TargetOffset, 0.0);
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
                    state.TargetOffset = Math.Clamp(state.TargetOffset + scrollAmount, 
                                                    sv.VerticalOffset - maxOvershoot, 
                                                    sv.VerticalOffset + maxOvershoot);
                    state.TargetOffset = Math.Clamp(state.TargetOffset, 0, sv.ScrollableHeight);
                }

                long elapsed = now - state.LastFrameTick;
                if (elapsed <= 0) elapsed = 1;
                double timeScale = elapsed / TargetFrameMs;
                
                timeScale = Math.Min(timeScale, 4.0);
                state.LastFrameTick = now;

                double currentOffset = sv.VerticalOffset;
                double diff = state.TargetOffset - currentOffset;

                // Precision snap boundary (0.01px)
                if (Math.Abs(diff) < 0.01)
                {
                    sv.ScrollToVerticalOffset(state.TargetOffset);
                    state.IsAnimating = false;
                    completed.Add(sv);

                    SendTelemetry(state.TargetOffset, state.TargetOffset, 0.0);
                }
                else
                {
                    double baseEase = state.IsTouchpad ? TouchpadEase : MouseEase;
                    double ease = baseEase;

                    if (state.IsTouchpad && baseEase < 1.0)
                    {
                        // ═══ Dynamic Friction Decelerator (Variable Coasting Curve) ═══
                        // Smoothly decay LERP ease when close to target to simulate native trackpad deceleration.
                        double distance = Math.Abs(diff);
                        if (distance < 80.0) // 80px boundary
                        {
                            double ratio = distance / 80.0;
                            ease = 0.04 + (baseEase - 0.04) * ratio; // Whispers down to 0.04 ease at target tail
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

                        SendTelemetry(nextOffset, state.TargetOffset, 0.0);
                    }
                    else
                    {
                        anyAnimating = true;

                        // Calculate normalized scrolling velocity in pixels per second
                        double frameVelocity = Math.Abs(step) / timeScale;
                        double velocitySec = frameVelocity * 60.0;

                        SendTelemetry(nextOffset, state.TargetOffset, velocitySec);
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
                RestoreUIThreadPriority();

                // Resume theme animations now that scrolling has stopped
                try
                {
                    var mainWin = Application.Current.MainWindow as MainWindow;
                    mainWin?.ResumeThemeAnimations();
                }
                catch { }
            }
        }

        private static void ElevateUIThreadPriority()
        {
            try
            {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;
            }
            catch { }
        }

        private static void RestoreUIThreadPriority()
        {
            try
            {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
            }
            catch { }
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
