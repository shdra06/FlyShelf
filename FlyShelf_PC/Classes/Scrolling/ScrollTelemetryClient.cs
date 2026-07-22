using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight, asynchronous local UDP telemetry client and built-in CSV logger.
    /// Sends real-time scroll updates, frame rates, and visual item coordinates to the Python dashboard,
    /// and asynchronously records them locally to scroll_cards_timeline_local.csv without external programs.
    /// </summary>
    public static class ScrollTelemetryClient
    {
        private static UdpClient? _udpClient;
        private static IPEndPoint? _endPoint;
        private static bool _initialized = false;
        private static bool _failed = false;

        static ScrollTelemetryClient()
        {
            EnsureInitialized();
        }

        private static readonly ConcurrentQueue<string> _logQueue = new();
        private static readonly Dictionary<int, double> _prevCardPositions = new();
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
        private static readonly string LocalCsvFile = Path.Combine(LogDirectory, "scroll_cards_timeline_local.csv");
        private static System.Threading.Timer? _csvFlushTimer;
        private static readonly object _fileLock = new object();

        private static void EnsureInitialized()
        {
            // M4 FIX: Skip telemetry infrastructure entirely in Release builds —
            // no UdpClient, no CSV file, no timer overhead.
            if (!Logger.IsEnabled) return;
            if (_initialized || _failed) return;
            try
            {
                _udpClient = new UdpClient();
                _endPoint = new IPEndPoint(IPAddress.Loopback, 5892);

                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                if (!File.Exists(LocalCsvFile))
                {
                    File.WriteAllText(LocalCsvFile, "Timestamp_Ms,App_Offset,Card_Index,Card_Y,Card_Height,Card_Movement\n", Encoding.UTF8);
                }

                // Flush CSV buffer every 1 second in a background thread
                _csvFlushTimer = new System.Threading.Timer(_ => FlushCsvBuffer(), null, 1000, 1000);

                _initialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[-] Telemetry client init failed: {ex.Message}");
                _failed = true;
            }
        }

        public static void SendTelemetry(double verticalOffset, double targetOffset, double velocity, double fps, double frameTimeMs, double viewportHeight, double scrollableHeight, string cardsData = "")
        {
            EnsureInitialized();
            if (!Logger.IsEnabled) return; // M4 FIX: No-op in Release builds

            // Process local CSV logging regardless of socket initialization success
            try
            {
                long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!string.IsNullOrEmpty(cardsData))
                {
                    string[] cardStrings = cardsData.Split(';');
                    foreach (var cardStr in cardStrings)
                    {
                        string[] parts = cardStr.Split(':');
                        if (parts.Length == 3 &&
                            int.TryParse(parts[0], out int idx) &&
                            double.TryParse(parts[1], out double y) &&
                            double.TryParse(parts[2], out double h))
                        {
                            double prevY = y;
                            lock (_prevCardPositions)
                            {
                                // [FIX M-46]: Cap _prevCardPositions to prevent unbounded growth
                                if (_prevCardPositions.Count > 500)
                                    _prevCardPositions.Clear();

                                if (_prevCardPositions.TryGetValue(idx, out double py))
                                {
                                    prevY = py;
                                }
                                _prevCardPositions[idx] = y;
                            }
                            double move = y - prevY;

                            // Format: Timestamp_Ms,App_Offset,Card_Index,Card_Y,Card_Height,Card_Movement
                            string row = $"{timestampMs},{verticalOffset:F2},{idx},{y:F1},{h:F1},{move:F1}";
                            _logQueue.Enqueue(row);
                        }
                    }
                }
            }
            catch
            {
                // Silent fail to prevent any application crash during local logging
            }

            if (_failed || _udpClient == null || _endPoint == null) return;

            try
            {
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                // Format: "APP:{TimestampMs},{VerticalOffset},{TargetOffset},{Velocity},{FPS},{FrameTimeMs},{ViewportHeight},{ScrollableHeight}"
                string payload = $"APP:{timestamp},{verticalOffset:F2},{targetOffset:F2},{velocity:F2},{fps:F1},{frameTimeMs:F1},{viewportHeight:F2},{scrollableHeight:F2}";
                if (!string.IsNullOrEmpty(cardsData))
                {
                    payload += $"|CARDS:{cardsData}";
                }

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                // BeginSend is completely asynchronous and does not block the UI render thread
                _udpClient.BeginSend(bytes, bytes.Length, _endPoint, null, null);
            }
            catch (Exception)
            {
                // Silent fail to prevent any application crash during telemetry errors
            }
        }

        private static void FlushCsvBuffer()
        {
            if (_logQueue.IsEmpty) return;
            lock (_fileLock)
            {
                try
                {
                    var lines = new List<string>();
                    while (_logQueue.TryDequeue(out string? line))
                    {
                        if (line != null)
                        {
                            lines.Add(line);
                        }
                    }
                    if (lines.Count > 0)
                    {
                        File.AppendAllLines(LocalCsvFile, lines, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Silent fail
                }
            }
        }

        // ═══ Visual Tree Card Extraction Helpers ═══

        public static string GetVisibleItemsTelemetry(ScrollViewer sv)
        {
            try
            {
                var itemsControl = FindItemsControlParent(sv);
                if (itemsControl == null) return string.Empty;

                var panel = FindVisualPanel(sv);
                if (panel == null) return string.Empty;

                var telemetryList = new List<string>();
                int childrenCount = VisualTreeHelper.GetChildrenCount(panel);

                for (int i = 0; i < childrenCount; i++)
                {
                    var child = VisualTreeHelper.GetChild(panel, i) as FrameworkElement;
                    if (child != null && child.IsVisible && child.IsLoaded)
                    {
                        try
                        {
                            // Translate child coordinates relative to the ScrollViewer viewport
                            var transform = child.TransformToVisual(sv);
                            var relativePoint = transform.Transform(new Point(0, 0));
                            double y = relativePoint.Y;
                            double height = child.ActualHeight;

                            // Find the logical index of this container within the parent ItemsControl
                            int index = itemsControl.ItemContainerGenerator.IndexFromContainer(child);
                            if (index >= 0)
                            {
                                // Format: index:Y:height
                                telemetryList.Add($"{index}:{y:F1}:{height:F1}");
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // Visual is not connected to a presentation source, safe to ignore
                        }
                    }
                }
                return string.Join(";", telemetryList);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static ItemsControl? FindItemsControlParent(DependencyObject child)
        {
            DependencyObject current = child;
            while (current != null)
            {
                if (current is ItemsControl ic)
                {
                    return ic;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target) return target;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static Panel? FindVisualPanel(DependencyObject parent)
        {
            // First locate ScrollContentPresenter to skip ScrollViewer's control template Grid
            var presenter = FindVisualChild<ScrollContentPresenter>(parent);
            if (presenter != null)
            {
                return FindVisualChild<Panel>(presenter);
            }
            return FindVisualChild<Panel>(parent);
        }

        /// <summary>
        /// [FIX M-44, M-45]: Disposes the UdpClient and CSV flush timer.
        /// Should be called on application exit.
        /// </summary>
        public static void Cleanup()
        {
            _csvFlushTimer?.Dispose();
            _csvFlushTimer = null;
            _udpClient?.Dispose();
            _udpClient = null;
        }
    }
}
