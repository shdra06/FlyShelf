// ---------------------------------------------------------------
// DeviceRadarControl — Animated radar with peer device blips
// Shows connected peers as pulsing dots positioned by latency/hash.
// Sweep line rotates 360° every 4 seconds via DispatcherTimer.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using FlyShelf.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FlyShelf.Controls
{
    public partial class DeviceRadarControl : UserControl
    {
        private readonly DispatcherTimer _blipRefreshTimer;
        private Path? _sweepWedge;
        private readonly List<FrameworkElement> _blipElements = new();

        // [FIX ANIM-2]: Static frozen easing shared by all blip pulse animations
        private static readonly SineEase s_blipEase = CreateFrozenEase();
        private static SineEase CreateFrozenEase()
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            ease.Freeze();
            return ease;
        }

        // Center and radius of the radar
        private const double CanvasSize = 200;
        private const double CenterX = CanvasSize / 2;
        private const double CenterY = CanvasSize / 2;
        private const double Radius = CanvasSize / 2 - 4;

        // Latency cache: DeviceId → latency ms
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _latencyCache = new();

        public DeviceRadarControl()
        {
            InitializeComponent();

            // Blip refresh timer: every 2 seconds
            _blipRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _blipRefreshTimer.Tick += (_, _) => RefreshBlips();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DrawRadarBackground();
            CreateSweepWedge();
            StartSweepAnimation();
            _blipRefreshTimer.Start();
            RefreshBlips();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopSweepAnimation();
            _blipRefreshTimer.Stop();
        }

        // [FIX ANIM-1]: Composition-thread sweep rotation replaces 60fps DispatcherTimer
        private void StartSweepAnimation()
        {
            if (_sweepWedge?.RenderTransform is RotateTransform sweepRotate)
            {
                var sweepAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(4))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Timeline.SetDesiredFrameRate(sweepAnim, 30);
                sweepAnim.Freeze();
                sweepRotate.BeginAnimation(RotateTransform.AngleProperty, sweepAnim);
            }
        }

        private void StopSweepAnimation()
        {
            if (_sweepWedge?.RenderTransform is RotateTransform sweepRotate)
            {
                sweepRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // RADAR BACKGROUND — Concentric rings + crosshair
        // ═══════════════════════════════════════════════════════════

        private void DrawRadarBackground()
        {
            var ringBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));

            // 3 concentric rings at 33%, 66%, 100% radius
            double[] ringFactors = { 0.33, 0.66, 1.0 };
            foreach (var factor in ringFactors)
            {
                double r = Radius * factor;
                var ring = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Stroke = ringBrush,
                    StrokeThickness = 1,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(ring, CenterX - r);
                Canvas.SetTop(ring, CenterY - r);
                RadarCanvas.Children.Add(ring);
            }

            // Crosshair lines
            var crosshairBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));

            // Horizontal
            var hLine = new Line
            {
                X1 = CenterX - Radius, Y1 = CenterY,
                X2 = CenterX + Radius, Y2 = CenterY,
                Stroke = crosshairBrush, StrokeThickness = 1
            };
            RadarCanvas.Children.Add(hLine);

            // Vertical
            var vLine = new Line
            {
                X1 = CenterX, Y1 = CenterY - Radius,
                X2 = CenterX, Y2 = CenterY + Radius,
                Stroke = crosshairBrush, StrokeThickness = 1
            };
            RadarCanvas.Children.Add(vLine);

            // Center dot
            var centerDot = new Ellipse
            {
                Width = 6, Height = 6,
                Fill = new SolidColorBrush(ThemeColors.IndigoLight)
            };
            Canvas.SetLeft(centerDot, CenterX - 3);
            Canvas.SetTop(centerDot, CenterY - 3);
            RadarCanvas.Children.Add(centerDot);
        }

        // ═══════════════════════════════════════════════════════════
        // SWEEP WEDGE — 60° arc rotated around center
        // ═══════════════════════════════════════════════════════════

        private void CreateSweepWedge()
        {
            _sweepWedge = new Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x81, 0x8C, 0xF8)),
                RenderTransform = new RotateTransform(0, CenterX, CenterY)
            };
            UpdateSweepGeometry();
            RadarCanvas.Children.Add(_sweepWedge);
        }

        private void UpdateSweepGeometry()
        {
            if (_sweepWedge == null) return;

            // Build a 60° pie wedge from center
            const double sweepAngleDeg = 60;
            double startRad = 0;
            double endRad = sweepAngleDeg * Math.PI / 180.0;

            var figure = new PathFigure { StartPoint = new Point(CenterX, CenterY), IsClosed = true };

            var startPoint = new Point(
                CenterX + Radius * Math.Cos(startRad - Math.PI / 2),
                CenterY + Radius * Math.Sin(startRad - Math.PI / 2));

            var endPoint = new Point(
                CenterX + Radius * Math.Cos(endRad - Math.PI / 2),
                CenterY + Radius * Math.Sin(endRad - Math.PI / 2));

            figure.Segments.Add(new LineSegment(startPoint, false));
            figure.Segments.Add(new ArcSegment(
                endPoint, new Size(Radius, Radius), 0,
                sweepAngleDeg > 180, SweepDirection.Clockwise, false));

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            _sweepWedge.Data = geo;
        }



        // ═══════════════════════════════════════════════════════════
        // BLIP REFRESH — Position peer dots on the radar
        // ═══════════════════════════════════════════════════════════

        /// <summary>Refreshes peer blips on the radar. Called by timer and can be called externally.</summary>
        public void RefreshBlips()
        {
            try
            {
                // [FIX ANIM-2]: Stop old animations before removing blips to prevent leaked clocks
                foreach (var blip in _blipElements)
                {
                    if (blip.RenderTransform is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    }
                    RadarCanvas.Children.Remove(blip);
                }
                _blipElements.Clear();

                var peers = PeerManager.Instance?.ConnectedPeers?.Values
                    .Where(p => p.IsAlive)
                    .ToList();

                if (peers == null || peers.Count == 0)
                {
                    UpdateDeviceCount(0);
                    return;
                }

                foreach (var peer in peers)
                {
                    try
                    {
                        // Calculate position: angle from DeviceId hash, distance from latency
                        int hash = (peer.DeviceId ?? "").GetHashCode(StringComparison.Ordinal);
                        double angle = (Math.Abs(hash) % 360) * Math.PI / 180.0;

                        // Get cached latency or default
                        double latencyMs = _latencyCache.TryGetValue(peer.DeviceId, out var cached) ? cached : 5.0;
                        double distance = Math.Min(latencyMs * 2, Radius * 0.9);
                        if (distance < 15) distance = 15 + (Math.Abs(hash) % 20); // Prevent overlap at center

                        double x = CenterX + distance * Math.Cos(angle) - 6;
                        double y = CenterY + distance * Math.Sin(angle) - 6;

                        // Choose color based on transport
                        Color blipColor = peer.Transport switch
                        {
                            "LAN" => ThemeColors.SuccessGreen,
                            "Cloudflare" => ThemeColors.Blue500,
                            _ => ThemeColors.SlateGray
                        };

                        // Create blip dot
                        var blip = new Border
                        {
                            Width = 12, Height = 12,
                            CornerRadius = new CornerRadius(6),
                            Background = new SolidColorBrush(blipColor),
                            ToolTip = $"{peer.DeviceName ?? peer.DeviceId}\n{peer.Transport} · {latencyMs:F0}ms",
                            Cursor = System.Windows.Input.Cursors.Hand,
                            RenderTransformOrigin = new Point(0.5, 0.5)
                        };

                        // Pulse animation (ScaleTransform 1.0 → 1.4 → 1.0 over 1.5s, forever)
                        var scale = new ScaleTransform(1.0, 1.0);
                        blip.RenderTransform = scale;

                        // [FIX ANIM-2]: Use static frozen easing + capped frame rate
                        var pulseX = new DoubleAnimation(1.0, 1.4, TimeSpan.FromMilliseconds(750))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = s_blipEase
                        };
                        Timeline.SetDesiredFrameRate(pulseX, 20);
                        var pulseY = new DoubleAnimation(1.0, 1.4, TimeSpan.FromMilliseconds(750))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = s_blipEase
                        };
                        Timeline.SetDesiredFrameRate(pulseY, 20);
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseX);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseY);

                        Canvas.SetLeft(blip, x);
                        Canvas.SetTop(blip, y);
                        RadarCanvas.Children.Add(blip);
                        _blipElements.Add(blip);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("RADAR", $"Blip error for {peer.DeviceId}: {ex.Message}");
                    }
                }

                UpdateDeviceCount(peers.Count);

                // Fire off latency measurements in the background
                _ = MeasureLatenciesAsync(peers);
            }
            catch (Exception ex)
            {
                Logger.LogAction("RADAR", $"RefreshBlips error: {ex.Message}");
            }
        }

        private void UpdateDeviceCount(int count)
        {
            DeviceCountText.Text = $"{count} device{(count != 1 ? "s" : "")} in range";
        }

        // ═══════════════════════════════════════════════════════════
        // LATENCY MEASUREMENT — Ping /api/health for response time
        // ═══════════════════════════════════════════════════════════

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private async System.Threading.Tasks.Task MeasureLatenciesAsync(List<PeerConnection> peers)
        {
            foreach (var peer in peers)
            {
                try
                {
                    string url = peer.ActiveUrl;
                    if (string.IsNullOrEmpty(url)) continue;

                    var sw = Stopwatch.StartNew();
                    var response = await _httpClient.GetAsync($"{url}/api/health");
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        _latencyCache[peer.DeviceId] = sw.ElapsedMilliseconds;
                    }
                }
                catch
                {
                    // Peer unreachable — keep old cached value or default
                }
            }
        }
    }
}
