using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Advanced per-element position tracker.
    /// Captures the SCREEN position of every named element on every render frame during spawn.
    /// Detects sub-pixel jitter, position jumps, and layout shifts at element level.
    /// </summary>
    public class ElementPositionTracker : IDisposable
    {
        private static ElementPositionTracker? _instance;
        public static ElementPositionTracker Instance => _instance ??= new ElementPositionTracker();

        // Each element being tracked
        public record ElementState
        {
            public string Name { get; init; } = "";
            public double X { get; init; }
            public double Y { get; init; }
            public double Width { get; init; }
            public double Height { get; init; }
            public double Opacity { get; init; }
            public string Visibility { get; init; } = "";
            public bool HasLayoutTransform { get; init; }
            public bool HasRenderTransform { get; init; }
            public double RenderTransformY { get; init; }
        }

        public record FrameData
        {
            public int FrameIndex { get; init; }
            public double TimeSinceStartMs { get; init; }
            public double DeltaMs { get; init; }
            public double WindowTop { get; init; }
            public double WindowLeft { get; init; }
            public double WindowOpacity { get; init; }
            public double SlideTransformY { get; init; }
            public string Phase { get; init; } = "";
            public List<ElementState> Elements { get; init; } = new();
        }

        private readonly List<FrameData> _frames = new();
        private readonly List<(string name, Func<FrameworkElement?> getter)> _trackedElements = new();
        private readonly Stopwatch _timer = new();
        private long _lastTick;
        private int _frameIndex;
        private bool _isRecording;
        private Window? _window;
        private EventHandler? _renderHandler;
        private string _currentPhase = "IDLE";

        // State accessors
        public Func<double>? GetSlideTransformY { get; set; }

        public void RegisterElement(string name, Func<FrameworkElement?> getter)
        {
            _trackedElements.Add((name, getter));
        }

        public void MarkPhase(string phase) => _currentPhase = phase;

        public void BeginRecording(Window window)
        {
            if (_isRecording) StopRecording();

            _window = window;
            _frames.Clear();
            _frameIndex = 0;
            _currentPhase = "SETUP";
            _timer.Restart();
            _lastTick = Stopwatch.GetTimestamp();
            _isRecording = true;

            CaptureFrame(); // Initial state

            _renderHandler = OnRender;
            CompositionTarget.Rendering += _renderHandler;
        }

        public void StopRecording()
        {
            if (!_isRecording) return;
            _isRecording = false;

            if (_renderHandler != null)
            {
                CompositionTarget.Rendering -= _renderHandler;
                _renderHandler = null;
            }

            CaptureFrame(); // Final state
            _timer.Stop();
            WriteReport();
        }

        private void OnRender(object? sender, EventArgs e)
        {
            if (!_isRecording || _window == null) return;
            if (_timer.ElapsedMilliseconds > 1500)
            {
                StopRecording();
                return;
            }
            CaptureFrame();
        }

        private void CaptureFrame()
        {
            if (_window == null) return;

            long now = Stopwatch.GetTimestamp();
            double deltaMs = (now - _lastTick) * 1000.0 / Stopwatch.Frequency;
            _lastTick = now;

            var elements = new List<ElementState>();
            foreach (var (name, getter) in _trackedElements)
            {
                try
                {
                    var el = getter();
                    if (el == null)
                    {
                        elements.Add(new ElementState { Name = name, X = -99999, Y = -99999, Visibility = "NULL" });
                        continue;
                    }

                    // Get position relative to the WINDOW (not screen) to isolate element-level jitter
                    Point pos;
                    try
                    {
                        pos = el.TransformToAncestor(_window).Transform(new Point(0, 0));
                    }
                    catch
                    {
                        pos = new Point(-88888, -88888);
                    }

                    double renderY = 0;
                    bool hasRT = false;
                    if (el.RenderTransform is TranslateTransform tt)
                    {
                        renderY = tt.Y;
                        hasRT = true;
                    }

                    elements.Add(new ElementState
                    {
                        Name = name,
                        X = pos.X,
                        Y = pos.Y,
                        Width = el.ActualWidth,
                        Height = el.ActualHeight,
                        Opacity = el.Opacity,
                        Visibility = el.Visibility.ToString(),
                        HasLayoutTransform = el.LayoutTransform != Transform.Identity,
                        HasRenderTransform = hasRT,
                        RenderTransformY = renderY,
                    });
                }
                catch
                {
                    elements.Add(new ElementState { Name = name, X = -77777, Y = -77777, Visibility = "ERROR" });
                }
            }

            _frames.Add(new FrameData
            {
                FrameIndex = _frameIndex++,
                TimeSinceStartMs = _timer.Elapsed.TotalMilliseconds,
                DeltaMs = deltaMs,
                WindowTop = _window.Top,
                WindowLeft = _window.Left,
                WindowOpacity = _window.Opacity,
                SlideTransformY = GetSlideTransformY?.Invoke() ?? 0,
                Phase = _currentPhase,
                Elements = elements,
            });
        }

        private void WriteReport()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SpawnDiag");
                Directory.CreateDirectory(dir);
                string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");

                // ═══ PER-ELEMENT CSV ═══
                string csvPath = Path.Combine(dir, $"element_pos_{ts}.csv");
                var csv = new StringBuilder();
                csv.Append("Frame,TimeMs,DeltaMs,Phase,WinTop,WinOpacity,SlideY");
                var elementNames = _trackedElements.Select(t => t.name).ToList();
                foreach (var name in elementNames)
                {
                    csv.Append($",{name}_X,{name}_Y,{name}_W,{name}_H,{name}_Op,{name}_Vis,{name}_RTY");
                }
                csv.AppendLine();

                foreach (var f in _frames)
                {
                    csv.Append($"{f.FrameIndex},{f.TimeSinceStartMs:F3},{f.DeltaMs:F3},{f.Phase},{f.WindowTop:F1},{f.WindowOpacity:F4},{f.SlideTransformY:F3}");
                    foreach (var name in elementNames)
                    {
                        var el = f.Elements.FirstOrDefault(e => e.Name == name);
                        if (el != null)
                            csv.Append($",{el.X:F2},{el.Y:F2},{el.Width:F1},{el.Height:F1},{el.Opacity:F3},{el.Visibility},{el.RenderTransformY:F3}");
                        else
                            csv.Append(",,,,,,,");
                    }
                    csv.AppendLine();
                }
                File.WriteAllText(csvPath, csv.ToString());

                // ═══ ANOMALY REPORT ═══
                string reportPath = Path.Combine(dir, $"element_analysis_{ts}.txt");
                var report = new StringBuilder();
                report.AppendLine("╔══════════════════════════════════════════════════════════════════════╗");
                report.AppendLine("║       ELEMENT POSITION TRACKER — PER-ELEMENT SPAWN ANALYSIS         ║");
                report.AppendLine($"║  {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  |  {_frames.Count} frames  |  {_trackedElements.Count} elements    ║");
                report.AppendLine("╚══════════════════════════════════════════════════════════════════════╝");
                report.AppendLine();

                // Detect per-element anomalies
                report.AppendLine("═══ ELEMENT POSITION ANOMALIES ═══");
                int totalAnomalies = 0;

                foreach (var name in elementNames)
                {
                    var positions = _frames
                        .Select(f => f.Elements.FirstOrDefault(e => e.Name == name))
                        .ToList();

                    for (int i = 1; i < positions.Count; i++)
                    {
                        var prev = positions[i - 1];
                        var curr = positions[i];
                        if (prev == null || curr == null) continue;
                        if (curr.Visibility == "Collapsed" || prev.Visibility == "Collapsed") continue;

                        // Position jump (relative to window)
                        double dY = Math.Abs(curr.Y - prev.Y);
                        double dX = Math.Abs(curr.X - prev.X);

                        // Skip the expected slide animation movement (window RenderTransform)
                        double expectedSlideMove = Math.Abs(
                            (_frames[i].SlideTransformY) - (_frames[i - 1].SlideTransformY));

                        // Element-specific Y movement beyond what slide accounts for
                        double unexplainedY = dY - expectedSlideMove;

                        if (unexplainedY > 1.5 && _frames[i].WindowOpacity > 0.01)
                        {
                            totalAnomalies++;
                            report.AppendLine($"  ⚠ {name} Y-JUMP at frame {i} ({_frames[i].TimeSinceStartMs:F1}ms): " +
                                $"Y {prev.Y:F2}→{curr.Y:F2} (Δ{dY:F2}px, unexplained={unexplainedY:F2}px) " +
                                $"opacity={_frames[i].WindowOpacity:F3}");
                        }

                        if (dX > 1.5 && _frames[i].WindowOpacity > 0.01)
                        {
                            totalAnomalies++;
                            report.AppendLine($"  ⚠ {name} X-JUMP at frame {i} ({_frames[i].TimeSinceStartMs:F1}ms): " +
                                $"X {prev.X:F2}→{curr.X:F2} (Δ{dX:F2}px) opacity={_frames[i].WindowOpacity:F3}");
                        }

                        // Size change during animation
                        double dW = Math.Abs(curr.Width - prev.Width);
                        double dH = Math.Abs(curr.Height - prev.Height);
                        if ((dW > 2 || dH > 2) && _frames[i].Phase == "PLAY_SHOW_ANIM")
                        {
                            totalAnomalies++;
                            report.AppendLine($"  ⚠ {name} SIZE-CHANGE during anim at frame {i} ({_frames[i].TimeSinceStartMs:F1}ms): " +
                                $"({prev.Width:F0}x{prev.Height:F0})→({curr.Width:F0}x{curr.Height:F0})");
                        }

                        // Visibility flicker
                        if (curr.Visibility != prev.Visibility && _frames[i].WindowOpacity > 0.01)
                        {
                            totalAnomalies++;
                            report.AppendLine($"  ⚠ {name} VISIBILITY-FLIP at frame {i} ({_frames[i].TimeSinceStartMs:F1}ms): " +
                                $"{prev.Visibility}→{curr.Visibility}");
                        }

                        // Opacity flicker (element-level, not window)
                        double dOp = Math.Abs(curr.Opacity - prev.Opacity);
                        if (dOp > 0.1 && _frames[i].WindowOpacity > 0.1)
                        {
                            totalAnomalies++;
                            report.AppendLine($"  ⚠ {name} OPACITY-FLIP at frame {i} ({_frames[i].TimeSinceStartMs:F1}ms): " +
                                $"{prev.Opacity:F3}→{curr.Opacity:F3}");
                        }
                    }
                }

                if (totalAnomalies == 0)
                    report.AppendLine("  ✓ No element-level anomalies detected.");
                else
                    report.AppendLine($"\n  Total element anomalies: {totalAnomalies}");

                // ═══ ELEMENT TIMELINE ═══
                report.AppendLine();
                report.AppendLine("═══ ELEMENT Y-POSITIONS (relative to window) ═══");
                report.Append("Frame | Time(ms) | Δms   | WinOp   | SlideY ");
                foreach (var name in elementNames)
                    report.Append($" | {name,12}");
                report.AppendLine();
                report.Append("------+----------+-------+---------+--------");
                foreach (var _ in elementNames)
                    report.Append("-+--------------");
                report.AppendLine();

                foreach (var f in _frames)
                {
                    if (f.WindowTop < -1000) continue; // Skip offscreen

                    report.Append($"{f.FrameIndex,5} | {f.TimeSinceStartMs,8:F2} | {f.DeltaMs,5:F1} | {f.WindowOpacity,7:F4} | {f.SlideTransformY,6:F3}");
                    foreach (var name in elementNames)
                    {
                        var el = f.Elements.FirstOrDefault(e => e.Name == name);
                        if (el != null && el.Visibility == "Visible")
                            report.Append($" | {el.Y,12:F2}");
                        else
                            report.Append($" | {"---",12}");
                    }
                    report.AppendLine();
                }

                File.WriteAllText(reportPath, report.ToString());
                Logger.LogAction("ELEM_TRACKER", $"Report: {reportPath} ({_frames.Count} frames, {totalAnomalies} anomalies)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("ELEM_TRACKER_ERR", ex.Message);
            }
        }

        public void Dispose()
        {
            StopRecording();
            _instance = null;
        }
    }
}
