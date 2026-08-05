using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// High-resolution spawn profiler that measures exact frame timings during clipboard spawn animation.
    /// Hooks CompositionTarget.Rendering to capture per-frame intervals on the WPF render thread.
    /// Detects jitter by flagging frames that exceed the 60fps threshold (16.67ms).
    /// 
    /// Results are written to: %APPDATA%/FlyShelf/Logs/spawn_profile.txt
    /// </summary>
    public sealed class SpawnProfiler
    {
        private static readonly Lazy<SpawnProfiler> _instance = new(() => new SpawnProfiler());
        public static SpawnProfiler Instance => _instance.Value;

        private readonly Stopwatch _sw = new();
        private readonly List<SpawnStep> _steps = new(32);
        private readonly List<FrameTick> _frames = new(60);
        
        private long _lastFrameTicks;
        private bool _isCapturing;
        private int _spawnId;
        private string? _logPath;
        private WeakReference<System.Windows.Window>? _targetWindowRef;
        private System.Windows.Controls.ScrollViewer? _cachedScrollViewer;

        private struct SpawnStep
        {
            public string Name;
            public double ElapsedMs;
        }

        private struct FrameTick
        {
            public int FrameNumber;
            public double DeltaMs;
            public double TotalMs;
            public double Opacity;
            public double SlideY;
            public double WindowTop;
            public double WindowHeight;
            // Win32-level state (what DWM actually sees)
            public bool WsVisible;     // WS_VISIBLE flag in GWL_STYLE
            public int DwmCloaked;     // DwmGetWindowAttribute DWMWA_CLOAKED
            public int Win32Left;      // GetWindowRect left
            public int Win32Top;       // GetWindowRect top
            public int Win32Height;    // GetWindowRect height (bottom - top)
            // Content-level state
            public double RootOpacity; // RootContent.Opacity (inner content opacity)
            public double ScrollOffset;// ScrollViewer.VerticalOffset
        }

        // Win32 interop for DWM-level state inspection — P/Invoke declarations centralized in NativeMethods.cs
        private const int WS_VISIBLE = 0x10000000;
        private const int DWMWA_CLOAKED = 14;
        private IntPtr _hwnd;

        private SpawnProfiler()
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FlyShelf", "Logs");
                Directory.CreateDirectory(logDir);
                _logPath = Path.Combine(logDir, "spawn_profile.txt");
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Call at the very start of the spawn pipeline (before any work).
        /// Resets all timers and starts capturing frame timings.
        /// </summary>
        public void BeginSpawn(System.Windows.Window? window = null)
        {
            _spawnId++;
            _steps.Clear();
            _frames.Clear();
            _targetWindowRef = window != null ? new WeakReference<System.Windows.Window>(window) : null;
            _hwnd = IntPtr.Zero;
            try
            {
                if (window != null)
                    _hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            }
            catch { } // Best-effort: failure is acceptable
            _sw.Restart();
            _lastFrameTicks = _sw.ElapsedTicks;
            _isCapturing = true;
            
            Mark("BEGIN_SPAWN");
            
            // Hook composition thread rendering to measure exact frame intervals
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>
        /// Mark a named checkpoint in the spawn pipeline with nanosecond precision.
        /// Call this at each significant step (e.g., "CLEAR_ANIM_CLOCK", "SET_POSITION", etc.)
        /// </summary>
        public void Mark(string stepName)
        {
            if (!_isCapturing) return;
            _steps.Add(new SpawnStep
            {
                Name = stepName,
                ElapsedMs = _sw.Elapsed.TotalMilliseconds
            });
        }

        /// <summary>
        /// Called by WPF on every rendered frame. Captures delta time between frames.
        /// Any delta > 16.67ms is a dropped frame (jitter).
        /// </summary>
        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isCapturing) return;

            long now = _sw.ElapsedTicks;
            double deltaMs = (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTicks = now;

            // Capture actual visual state + window position
            double opacity = 0;
            double slideY = 0;
            double windowTop = 0;
            double windowHeight = 0;
            bool wsVisible = false;
            int dwmCloaked = 0;
            int win32Left = 0, win32Top = 0, win32Height = 0;
            double rootOpacity = 0;
            double scrollOffset = 0;
            try
            {
                if (_targetWindowRef != null && _targetWindowRef.TryGetTarget(out var targetWin) && targetWin is MainWindow mainWin)
                {
                    opacity = mainWin.Opacity;
                    windowTop = mainWin.Top;
                    windowHeight = mainWin.ActualHeight;
                    var rootContent = mainWin.RootContent;
                    if (rootContent != null)
                    {
                        rootOpacity = rootContent.Opacity;
                        if (rootContent.RenderTransform is System.Windows.Media.TranslateTransform tt)
                            slideY = tt.Y;
                    }
                    // Capture scroll position to detect content shifts
                    try
                    {
                        // Cache ScrollViewer ref — avoid expensive visual tree search on every 60fps tick
                        _cachedScrollViewer ??= FindChild<System.Windows.Controls.ScrollViewer>(mainWin.ShelfListView);
                        if (_cachedScrollViewer != null) scrollOffset = _cachedScrollViewer.VerticalOffset;
                    }
                    catch { } // Best-effort: failure is acceptable
                }
                // Win32-level state: what DWM actually sees
                if (_hwnd != IntPtr.Zero)
                {
                    int style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
                    wsVisible = (style & WS_VISIBLE) != 0;
                    NativeMethods.DwmGetWindowAttribute(_hwnd, DWMWA_CLOAKED, out dwmCloaked, sizeof(int));
                    if (NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT r))
                    {
                        win32Left = r.Left;
                        win32Top = r.Top;
                        win32Height = r.Bottom - r.Top;
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable

            _frames.Add(new FrameTick
            {
                FrameNumber = _frames.Count,
                DeltaMs = deltaMs,
                TotalMs = _sw.Elapsed.TotalMilliseconds,
                Opacity = opacity,
                SlideY = slideY,
                WindowTop = windowTop,
                WindowHeight = windowHeight,
                WsVisible = wsVisible,
                DwmCloaked = dwmCloaked,
                Win32Left = win32Left,
                Win32Top = win32Top,
                Win32Height = win32Height,
                RootOpacity = rootOpacity,
                ScrollOffset = scrollOffset
            });

            // Auto-stop after 1200ms to catch post-animation position jitter
            if (_sw.Elapsed.TotalMilliseconds > 1200)
            {
                EndCapture();
            }
        }

        /// <summary>
        /// Stops frame capture and writes the full profile report to disk.
        /// </summary>
        public void EndCapture()
        {
            if (!_isCapturing) return;
            _isCapturing = false;
            Mark("END_CAPTURE");
            _targetWindowRef = null;

            CompositionTarget.Rendering -= OnRendering;
            
            WriteReport();
        }

        private void WriteReport()
        {
            if (_logPath == null) return;

            try
            {
                using var writer = new StreamWriter(_logPath, append: true);
                writer.WriteLine();
                writer.WriteLine($"═══════════════════════════════════════════════════════════════════");
                writer.WriteLine($"  SPAWN PROFILE #{_spawnId}  —  {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                writer.WriteLine($"═══════════════════════════════════════════════════════════════════");
                writer.WriteLine();

                // ─── Pipeline Steps ───
                writer.WriteLine("  ┌─── PIPELINE STEPS (high-res timestamps) ───");
                double prevMs = 0;
                foreach (var step in _steps)
                {
                    double delta = step.ElapsedMs - prevMs;
                    string flag = delta > 5 ? " ⚠️ SLOW" : "";
                    writer.WriteLine($"  │ {step.ElapsedMs,8:F3}ms (+{delta,6:F3}ms)  {step.Name}{flag}");
                    prevMs = step.ElapsedMs;
                }
                writer.WriteLine($"  └─── Total: {_sw.Elapsed.TotalMilliseconds:F2}ms ───");
                writer.WriteLine();

                // ─── Frame Timings ───
                // Only count drops during VISIBLE animation (first 350ms).
                // Drops after 350ms are post-animation cleanup (DWM border, mascot timer) — invisible.
                int droppedInAnim = 0;   // drops during visible animation (0-350ms)
                int droppedAfter = 0;    // drops after animation (>350ms)
                int positionJitters = 0; // frames where Top changed by >1px
                double maxDelta = 0;
                double sumDelta = 0;
                double prevTop = -1;
                int prevWinH = -1;
                int heightBounces = 0;
                int frameCount = _frames.Count;

                writer.WriteLine("  ┌─── FRAME TIMINGS (CompositionTarget.Rendering) + DWM STATE ───");
                writer.WriteLine("  │  Frame    Delta(ms)   Total(ms)   Opacity   SlideY   WinTop    WinH   Vis  Cloak  W32Top  W32H    Root   Status");
                writer.WriteLine("  │  ─────    ─────────   ─────────   ───────   ──────   ──────    ────   ───  ─────  ──────  ────    ────   ──────");
                
                foreach (var frame in _frames)
                {
                    bool inAnimWindow = frame.TotalMs <= 280;
                    string status;
                    if (frame.DeltaMs > 25)
                    {
                        status = inAnimWindow ? "🔴 DROPPED (>25ms)" : "⚫ POST-ANIM";
                        if (inAnimWindow) droppedInAnim++; else droppedAfter++;
                    }
                    else if (frame.DeltaMs > 16.67)
                    {
                        status = inAnimWindow ? "🟡 LATE" : "⚫ POST-ANIM";
                    }
                    else
                    {
                        status = "🟢 OK";
                    }

                    // Detect position jitter: Top changed by >1px between frames during animation
                    if (prevTop >= 0 && inAnimWindow && Math.Abs(frame.WindowTop - prevTop) > 1.0)
                    {
                        status += " ⚡ POS_JITTER";
                        positionJitters++;
                    }
                    prevTop = frame.WindowTop;

                    // DWM state flags: V=WS_VISIBLE, C=DwmCloaked value
                    string vis = frame.WsVisible ? "V" : ".";
                    string cloak = frame.DwmCloaked != 0 ? $"C{frame.DwmCloaked}" : ".";

                    // Detect content bounce: Win32 height changed during animation
                    if (prevWinH > 0 && inAnimWindow && Math.Abs(frame.Win32Height - prevWinH) > 2)
                    {
                        status += $" 🟠 H_BOUNCE({prevWinH}→{frame.Win32Height})";
                        heightBounces++;
                    }
                    prevWinH = frame.Win32Height;

                    writer.WriteLine($"  │  {frame.FrameNumber,5}    {frame.DeltaMs,9:F3}   {frame.TotalMs,9:F3}   {frame.Opacity,7:F3}   {frame.SlideY,6:F2}   {frame.WindowTop,6:F1}   {frame.WindowHeight,5:F0}    {vis}    {cloak,3}   {frame.Win32Top,6}  {frame.Win32Height,5}   R{frame.RootOpacity:F1}   {status}");
                    
                    if (frame.FrameNumber > 0)
                    {
                        sumDelta += frame.DeltaMs;
                        if (frame.DeltaMs > maxDelta) maxDelta = frame.DeltaMs;
                    }
                }
                writer.WriteLine($"  └─── Frames: {frameCount}, Anim Drops: {droppedInAnim}, Post-Anim Drops: {droppedAfter}, Pos Jitters: {positionJitters}, H Bounces: {heightBounces}, Max: {maxDelta:F2}ms, Avg: {(frameCount > 1 ? sumDelta / (frameCount - 1) : 0):F2}ms ───");
                writer.WriteLine();

                // ─── Verdict (only animation-window drops count) ───
                if (droppedInAnim == 0 && positionJitters == 0)
                    writer.WriteLine($"  ✅ VERDICT: SMOOTH — No dropped frames or position jitter in animation window (0-280ms).{(droppedAfter > 0 ? $" ({droppedAfter} post-anim drops ignored)" : "")}");
                else if (positionJitters > 0)
                    writer.WriteLine($"  ⚡ VERDICT: POSITION BOUNCE — {positionJitters} position jitter(s) detected! Window Top changed >1px between frames during animation.");
                else if (droppedInAnim <= 1)
                    writer.WriteLine($"  ⚠️ VERDICT: MINOR JITTER — {droppedInAnim} dropped frame(s) in animation window. Max gap: {maxDelta:F2}ms.");
                else
                    writer.WriteLine($"  🔴 VERDICT: JITTERY — {droppedInAnim} dropped frame(s) in animation window! Max gap: {maxDelta:F2}ms.");

                writer.WriteLine();
                writer.Flush();
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Finds the first child of the specified type in the visual tree.
        /// </summary>
        private static T? FindChild<T>(System.Windows.DependencyObject? parent) where T : System.Windows.DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
