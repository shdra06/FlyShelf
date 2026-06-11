using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        }

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
            catch { }
        }

        /// <summary>
        /// Call at the very start of the spawn pipeline (before any work).
        /// Resets all timers and starts capturing frame timings.
        /// </summary>
        public void BeginSpawn()
        {
            _spawnId++;
            _steps.Clear();
            _frames.Clear();
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

            _frames.Add(new FrameTick
            {
                FrameNumber = _frames.Count,
                DeltaMs = deltaMs,
                TotalMs = _sw.Elapsed.TotalMilliseconds
            });

            // Auto-stop after 500ms (30 frames at 60fps is plenty)
            if (_sw.Elapsed.TotalMilliseconds > 500)
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
                int droppedFrames = 0;
                double maxDelta = 0;
                double sumDelta = 0;
                int frameCount = _frames.Count;

                writer.WriteLine("  ┌─── FRAME TIMINGS (CompositionTarget.Rendering) ───");
                writer.WriteLine("  │  Frame    Delta(ms)   Total(ms)   Status");
                writer.WriteLine("  │  ─────    ─────────   ─────────   ──────");
                
                foreach (var frame in _frames)
                {
                    string status = "";
                    if (frame.DeltaMs > 25)
                    {
                        status = "🔴 DROPPED (>25ms)";
                        droppedFrames++;
                    }
                    else if (frame.DeltaMs > 16.67)
                    {
                        status = "🟡 LATE (>16ms)";
                        droppedFrames++;
                    }
                    else
                    {
                        status = "🟢 OK";
                    }

                    writer.WriteLine($"  │  {frame.FrameNumber,5}    {frame.DeltaMs,9:F3}   {frame.TotalMs,9:F3}   {status}");
                    
                    if (frame.FrameNumber > 0) // Skip first frame (delta is from BeginSpawn)
                    {
                        sumDelta += frame.DeltaMs;
                        if (frame.DeltaMs > maxDelta) maxDelta = frame.DeltaMs;
                    }
                }
                writer.WriteLine($"  └─── Frames: {frameCount}, Dropped: {droppedFrames}, Max: {maxDelta:F2}ms, Avg: {(frameCount > 1 ? sumDelta / (frameCount - 1) : 0):F2}ms ───");
                writer.WriteLine();

                // ─── Verdict ───
                if (droppedFrames == 0)
                    writer.WriteLine("  ✅ VERDICT: SMOOTH — No dropped frames detected.");
                else if (droppedFrames <= 2)
                    writer.WriteLine($"  ⚠️ VERDICT: MINOR JITTER — {droppedFrames} dropped frame(s). Max gap: {maxDelta:F2}ms.");
                else
                    writer.WriteLine($"  🔴 VERDICT: JITTERY — {droppedFrames} dropped frame(s)! Max gap: {maxDelta:F2}ms. See frames above for root cause.");

                writer.WriteLine();
                writer.Flush();
            }
            catch { }
        }
    }
}
