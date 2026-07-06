using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Frame-by-frame spawn diagnostic logger.
    /// Captures window state on EVERY CompositionTarget.Rendering frame during spawn.
    /// Outputs a CSV + human-readable report showing exactly what happened per-frame.
    /// </summary>
    public class SpawnDiagnostic : IDisposable
    {
        // ═══ SINGLETON ═══
        private static SpawnDiagnostic? _instance;
        public static SpawnDiagnostic Instance => _instance ??= new SpawnDiagnostic();

        // ═══ FRAME DATA ═══
        public record FrameSnapshot
        {
            public int FrameIndex { get; init; }
            public double TimeSinceSpawnMs { get; init; }
            public double DeltaFromLastFrameMs { get; init; }

            // Window position
            public double WindowLeft { get; init; }
            public double WindowTop { get; init; }
            public double WindowWidth { get; init; }
            public double WindowHeight { get; init; }
            public double ActualHeight { get; init; }

            // Opacity chain
            public double WindowOpacity { get; init; }
            public double RootContentOpacity { get; init; }

            // Transform state
            public double SlideTransformY { get; init; }
            public string TransformType { get; init; } = "";

            // Render state
            public bool IsVisible { get; init; }
            public string Visibility { get; init; } = "";
            public bool IsHitTestVisible { get; init; }

            // App state flags
            public bool IsShowAnimating { get; init; }
            public bool IsCurrentlySummoned { get; init; }
            public bool IsEdgeLocked { get; init; }
            public bool IsAnimatingHide { get; init; }
            public int SpawnGeneration { get; init; }

            // Notes/Todo state
            public bool IsNotesActive { get; init; }
            public bool IsTodoActive { get; init; }

            // Panel visibility  
            public string NotesPanelVisibility { get; init; } = "";
            public string TodoPanelVisibility { get; init; } = "";
            public double NotesPanelOpacity { get; init; }
            public double TodoPanelOpacity { get; init; }
            public string ShelfListViewVisibility { get; init; } = "";
            public double ShelfListViewOpacity { get; init; }

            // Phase tracking
            public string Phase { get; init; } = "";
            public string Event { get; init; } = "";

            // DWM / Win32
            public double LockedBottomEdge { get; init; }
            public double LastActualHeight { get; init; }
        }

        private readonly List<FrameSnapshot> _frames = new();
        private readonly Stopwatch _spawnTimer = new();
        private long _lastFrameTick;
        private int _frameIndex;
        private bool _isRecording;
        private Window? _targetWindow;
        private EventHandler? _renderHandler;
        private string _currentPhase = "IDLE";
        private string _pendingEvent = "";

        // State accessor delegates (set by MainWindow)
        public Func<bool>? GetIsShowAnimating { get; set; }
        public Func<bool>? GetIsCurrentlySummoned { get; set; }
        public Func<bool>? GetIsEdgeLocked { get; set; }
        public Func<bool>? GetIsAnimatingHide { get; set; }
        public Func<int>? GetSpawnGeneration { get; set; }
        public Func<bool>? GetIsNotesActive { get; set; }
        public Func<bool>? GetIsTodoActive { get; set; }
        public Func<double>? GetLockedBottomEdge { get; set; }
        public Func<double>? GetLastActualHeight { get; set; }

        // UI element accessors
        public Func<FrameworkElement?>? GetRootContent { get; set; }
        public Func<FrameworkElement?>? GetNotesPanel { get; set; }
        public Func<FrameworkElement?>? GetTodoPanel { get; set; }
        public Func<FrameworkElement?>? GetShelfListView { get; set; }

        public void MarkPhase(string phase)
        {
            _currentPhase = phase;
        }

        public void MarkEvent(string evt)
        {
            _pendingEvent = evt;
        }

        public void BeginRecording(Window window)
        {
            if (_isRecording) StopRecording(); // Auto-flush previous

            _targetWindow = window;
            _frames.Clear();
            _frameIndex = 0;
            _currentPhase = "SETUP";
            _pendingEvent = "BEGIN_RECORDING";
            _spawnTimer.Restart();
            _lastFrameTick = Stopwatch.GetTimestamp();
            _isRecording = true;

            // Capture initial state before any render frames
            CaptureFrame();

            _renderHandler = OnRenderFrame;
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

            // Final frame
            _pendingEvent = "STOP_RECORDING";
            CaptureFrame();

            _spawnTimer.Stop();

            // Write report
            WriteReport();
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            if (!_isRecording || _targetWindow == null) return;

            // Stop recording after 2 seconds (enough to capture full spawn + settle)
            if (_spawnTimer.ElapsedMilliseconds > 2000)
            {
                StopRecording();
                return;
            }

            CaptureFrame();
        }

        private void CaptureFrame()
        {
            if (_targetWindow == null) return;

            long now = Stopwatch.GetTimestamp();
            double deltaMsFromLast = (now - _lastFrameTick) * 1000.0 / Stopwatch.Frequency;
            _lastFrameTick = now;

            var rootContent = GetRootContent?.Invoke();
            var notesPanel = GetNotesPanel?.Invoke();
            var todoPanel = GetTodoPanel?.Invoke();
            var shelfListView = GetShelfListView?.Invoke();

            // Get transform info
            double slideY = 0;
            string transformType = "None";
            if (rootContent?.RenderTransform is TranslateTransform tt)
            {
                slideY = tt.Y;
                transformType = $"TranslateY";
            }
            else if (rootContent?.RenderTransform != null)
            {
                transformType = rootContent.RenderTransform.GetType().Name;
            }

            var frame = new FrameSnapshot
            {
                FrameIndex = _frameIndex++,
                TimeSinceSpawnMs = _spawnTimer.Elapsed.TotalMilliseconds,
                DeltaFromLastFrameMs = deltaMsFromLast,

                WindowLeft = _targetWindow.Left,
                WindowTop = _targetWindow.Top,
                WindowWidth = _targetWindow.Width,
                WindowHeight = _targetWindow.Height,
                ActualHeight = _targetWindow.ActualHeight,

                WindowOpacity = _targetWindow.Opacity,
                RootContentOpacity = rootContent?.Opacity ?? -1,

                SlideTransformY = slideY,
                TransformType = transformType,

                IsVisible = _targetWindow.IsVisible,
                Visibility = _targetWindow.Visibility.ToString(),
                IsHitTestVisible = _targetWindow.IsHitTestVisible,

                IsShowAnimating = GetIsShowAnimating?.Invoke() ?? false,
                IsCurrentlySummoned = GetIsCurrentlySummoned?.Invoke() ?? false,
                IsEdgeLocked = GetIsEdgeLocked?.Invoke() ?? false,
                IsAnimatingHide = GetIsAnimatingHide?.Invoke() ?? false,
                SpawnGeneration = GetSpawnGeneration?.Invoke() ?? -1,

                IsNotesActive = GetIsNotesActive?.Invoke() ?? false,
                IsTodoActive = GetIsTodoActive?.Invoke() ?? false,

                NotesPanelVisibility = notesPanel?.Visibility.ToString() ?? "N/A",
                TodoPanelVisibility = todoPanel?.Visibility.ToString() ?? "N/A",
                NotesPanelOpacity = notesPanel?.Opacity ?? -1,
                TodoPanelOpacity = todoPanel?.Opacity ?? -1,
                ShelfListViewVisibility = shelfListView?.Visibility.ToString() ?? "N/A",
                ShelfListViewOpacity = shelfListView?.Opacity ?? -1,

                Phase = _currentPhase,
                Event = _pendingEvent,

                LockedBottomEdge = GetLockedBottomEdge?.Invoke() ?? -1,
                LastActualHeight = GetLastActualHeight?.Invoke() ?? -1,
            };

            _pendingEvent = ""; // Clear one-shot event
            _frames.Add(frame);
        }

        private void WriteReport()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SpawnDiag");
                Directory.CreateDirectory(dir);
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

                // ═══ CSV ═══
                string csvPath = Path.Combine(dir, $"spawn_frames_{timestamp}.csv");
                var csv = new StringBuilder();
                csv.AppendLine("Frame,TimeMs,DeltaMs,Phase,Event," +
                    "WinLeft,WinTop,WinWidth,ActualHeight," +
                    "WinOpacity,RootOpacity,SlideY,TransformType," +
                    "IsVisible,Visibility," +
                    "IsShowAnim,IsSummoned,IsEdgeLocked,IsAnimHide,SpawnGen," +
                    "NotesActive,TodoActive," +
                    "NotesPanelVis,NotesPanelOp,TodoPanelVis,TodoPanelOp," +
                    "ShelfVis,ShelfOp," +
                    "LockedBottom,LastHeight");

                foreach (var f in _frames)
                {
                    csv.AppendLine(CultureInfo.InvariantCulture, $"{f.FrameIndex},{f.TimeSinceSpawnMs:F3},{f.DeltaFromLastFrameMs:F3}," +
                        $"{f.Phase},{f.Event}," +
                        $"{f.WindowLeft:F1},{f.WindowTop:F1},{f.WindowWidth:F1},{f.ActualHeight:F1}," +
                        $"{f.WindowOpacity:F4},{f.RootContentOpacity:F4},{f.SlideTransformY:F3},{f.TransformType}," +
                        $"{f.IsVisible},{f.Visibility}," +
                        $"{f.IsShowAnimating},{f.IsCurrentlySummoned},{f.IsEdgeLocked},{f.IsAnimatingHide},{f.SpawnGeneration}," +
                        $"{f.IsNotesActive},{f.IsTodoActive}," +
                        $"{f.NotesPanelVisibility},{f.NotesPanelOpacity:F4},{f.TodoPanelVisibility},{f.TodoPanelOpacity:F4}," +
                        $"{f.ShelfListViewVisibility},{f.ShelfListViewOpacity:F4}," +
                        $"{f.LockedBottomEdge:F1},{f.LastActualHeight:F1}");
                }
                File.WriteAllText(csvPath, csv.ToString());

                // ═══ HUMAN-READABLE REPORT ═══
                string reportPath = Path.Combine(dir, $"spawn_analysis_{timestamp}.txt");
                var report = new StringBuilder();
                report.AppendLine("╔══════════════════════════════════════════════════════════════╗");
                report.AppendLine("║         FLYSHELF SPAWN DIAGNOSTIC — FRAME-BY-FRAME          ║");
                report.AppendLine(CultureInfo.InvariantCulture, $"║  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  {_frames.Count} frames captured         ║");
                report.AppendLine("╚══════════════════════════════════════════════════════════════╝");
                report.AppendLine();

                // ═══ ANOMALY DETECTION ═══
                report.AppendLine("═══ ANOMALY DETECTION ═══");
                int anomalyCount = 0;

                for (int i = 1; i < _frames.Count; i++)
                {
                    var prev = _frames[i - 1];
                    var curr = _frames[i];

                    // 1. Position jump while visible
                    double posJump = Math.Abs(curr.WindowTop - prev.WindowTop);
                    if (posJump > 5 && curr.WindowOpacity > 0.01 && prev.WindowOpacity > 0.01)
                    {
                        anomalyCount++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ POSITION JUMP at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"Top {prev.WindowTop:F1} → {curr.WindowTop:F1} (Δ{posJump:F1}px) while opacity={curr.WindowOpacity:F3}");
                    }

                    // 2. Opacity jump (not gradual)
                    double opJump = Math.Abs(curr.WindowOpacity - prev.WindowOpacity);
                    if (opJump > 0.15 && curr.WindowOpacity > 0 && prev.WindowOpacity > 0)
                    {
                        anomalyCount++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ OPACITY JUMP at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"{prev.WindowOpacity:F3} → {curr.WindowOpacity:F3} (Δ{opJump:F3})");
                    }

                    // 3. SlideY direction reversal
                    if (i >= 2)
                    {
                        var prevprev = _frames[i - 2];
                        double prevDir = prev.SlideTransformY - prevprev.SlideTransformY;
                        double currDir = curr.SlideTransformY - prev.SlideTransformY;
                        if (prevDir < -0.1 && currDir > 0.5) // Was decreasing, suddenly increased
                        {
                            anomalyCount++;
                            report.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ SLIDE REVERSAL at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                                $"SlideY {prevprev.SlideTransformY:F2} → {prev.SlideTransformY:F2} → {curr.SlideTransformY:F2}");
                        }
                    }

                    // 4. Frame drop (> 25ms gap)
                    if (curr.DeltaFromLastFrameMs > 25)
                    {
                        anomalyCount++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ FRAME DROP at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"gap={curr.DeltaFromLastFrameMs:F1}ms (expected ~16ms)");
                    }

                    // 5. Phase changes
                    if (curr.Phase != prev.Phase)
                    {
                        report.AppendLine(CultureInfo.InvariantCulture, $"  → Phase change at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"{prev.Phase} → {curr.Phase}");
                    }

                    // 6. Visibility/opacity mismatch (visible at wrong time)
                    if (curr.WindowTop > -1000 && curr.WindowOpacity > 0.01 && !curr.IsCurrentlySummoned)
                    {
                        anomalyCount++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ VISIBLE BUT NOT SUMMONED at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"Top={curr.WindowTop:F1} opacity={curr.WindowOpacity:F3}");
                    }

                    // 7. Notes panel state change
                    if (curr.NotesPanelVisibility != prev.NotesPanelVisibility ||
                        Math.Abs(curr.NotesPanelOpacity - prev.NotesPanelOpacity) > 0.05)
                    {
                        report.AppendLine(CultureInfo.InvariantCulture, $"  → NOTES PANEL change at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"Vis={prev.NotesPanelVisibility}→{curr.NotesPanelVisibility} " +
                            $"Op={prev.NotesPanelOpacity:F2}→{curr.NotesPanelOpacity:F2}");
                    }

                    // 8. ActualHeight change during animation
                    double heightDelta = Math.Abs(curr.ActualHeight - prev.ActualHeight);
                    if (heightDelta > 2 && curr.IsShowAnimating)
                    {
                        anomalyCount++;
                        report.AppendLine(CultureInfo.InvariantCulture, $"  ⚠ HEIGHT CHANGE DURING ANIM at frame {i} ({curr.TimeSinceSpawnMs:F1}ms): " +
                            $"{prev.ActualHeight:F1} → {curr.ActualHeight:F1} (Δ{heightDelta:F1}px)");
                    }
                }

                if (anomalyCount == 0)
                    report.AppendLine("  ✓ No anomalies detected.");
                else
                    report.AppendLine(CultureInfo.InvariantCulture, $"\n  Total anomalies: {anomalyCount}");

                // ═══ FRAME-BY-FRAME TIMELINE ═══
                report.AppendLine("\n═══ FRAME-BY-FRAME TIMELINE ═══");
                report.AppendLine("Frame | Time(ms) | Δms   | Phase          | Opacity | SlideY  | Top      | Height  | Event/Notes");
                report.AppendLine("------+----------+-------+----------------+---------+---------+----------+---------+------------------");

                foreach (var f in _frames)
                {
                    string notes = "";
                    if (!string.IsNullOrEmpty(f.Event)) notes = f.Event;
                    if (f.WindowTop < -1000) notes += " [OFFSCREEN]";
                    if (f.IsNotesActive) notes += " [NOTES]";
                    if (f.IsTodoActive) notes += " [TODO]";
                    if (f.DeltaFromLastFrameMs > 25) notes += " [SLOW]";

                    report.AppendLine(CultureInfo.InvariantCulture, $"{f.FrameIndex,5} | {f.TimeSinceSpawnMs,8:F2} | {f.DeltaFromLastFrameMs,5:F1} | " +
                        $"{f.Phase,-14} | {f.WindowOpacity,7:F4} | {f.SlideTransformY,7:F3} | {f.WindowTop,8:F1} | {f.ActualHeight,7:F1} | {notes}");
                }

                File.WriteAllText(reportPath, report.ToString());

                // Log paths
                Logger.LogAction("SPAWN_DIAG", $"CSV: {csvPath}");
                Logger.LogAction("SPAWN_DIAG", $"Report: {reportPath}");
                Logger.LogAction("SPAWN_DIAG", string.Create(CultureInfo.InvariantCulture, $"Frames: {_frames.Count}, Anomalies: {anomalyCount}"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPAWN_DIAG_ERR", ex.Message);
            }
        }

        public void Dispose()
        {
            StopRecording();
            _instance = null;
        }
    }
}
