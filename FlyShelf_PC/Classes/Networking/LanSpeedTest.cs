// ---------------------------------------------------------------
// LanSpeedTest — Static class for running LAN speed tests
// Generates 1MB payload, POSTs to peer, measures throughput.
// Caches history per device for trend analysis.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public class SpeedTestResult
    {
        public double UploadMbps { get; set; }
        public double LatencyMs { get; set; }
        public DateTime Timestamp { get; set; }
        public string PeerName { get; set; } = "";
    }

    public static class LanSpeedTest
    {
        private static readonly Dictionary<string, List<SpeedTestResult>> _history = new();
        private static readonly object _lock = new();

        // Reusable HttpClient with 15-second timeout (5s was too aggressive for slow Wi-Fi)
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // 1MB random payload — generated once and reused
        private static readonly byte[] _payload;

        static LanSpeedTest()
        {
            _payload = new byte[1_048_576]; // 1 MB
            Random.Shared.NextBytes(_payload);
        }

        /// <summary>
        /// Runs a speed test against the given peer by uploading 1MB and measuring throughput.
        /// Also measures latency with a quick HEAD request.
        /// </summary>
        public static async Task<SpeedTestResult> RunAsync(PeerConnection peer)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));

            string activeUrl = peer.ActiveUrl;
            if (string.IsNullOrEmpty(activeUrl))
            {
                throw new InvalidOperationException($"Peer {peer.DeviceName} has no active URL");
            }

            var result = new SpeedTestResult
            {
                PeerName = peer.DeviceName ?? peer.DeviceId,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // 1. Measure latency with HEAD /api/health
                var latencySw = Stopwatch.StartNew();
                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, $"{activeUrl}/api/health");
                    using var headRes = await _httpClient.SendAsync(headReq);
                    latencySw.Stop();
                    result.LatencyMs = latencySw.ElapsedMilliseconds;
                }
                catch
                {
                    latencySw.Stop();
                    // Fallback: try GET /api/health
                    var latencySw2 = Stopwatch.StartNew();
                    try
                    {
                        using var res = await _httpClient.GetAsync($"{activeUrl}/api/health");
                        latencySw2.Stop();
                        result.LatencyMs = latencySw2.ElapsedMilliseconds;
                    }
                    catch
                    {
                        latencySw2.Stop();
                        result.LatencyMs = latencySw2.ElapsedMilliseconds;
                    }
                }

                // 2. Upload 1MB payload to /api/speedtest
                var uploadSw = Stopwatch.StartNew();
                using var content = new ByteArrayContent(_payload);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                using var response = await _httpClient.PostAsync($"{activeUrl}/api/speedtest", content);
                uploadSw.Stop();

                double elapsedSeconds = uploadSw.Elapsed.TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    // throughput = (payloadSize * 8 bits) / (elapsedSeconds * 1,000,000) = Mbps
                    result.UploadMbps = (_payload.Length * 8.0) / (elapsedSeconds * 1_000_000.0);
                }

                Logger.LogAction("SPEEDTEST", $"Completed: {result.UploadMbps:F1} Mbps upload, {result.LatencyMs:F0}ms latency to {result.PeerName}");
            }
            catch (TaskCanceledException)
            {
                Logger.LogAction("SPEEDTEST", $"Timeout measuring speed to {result.PeerName}");
                // Return partial result with whatever we have
            }
            catch (HttpRequestException ex)
            {
                Logger.LogAction("SPEEDTEST", $"HTTP error measuring speed to {result.PeerName}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPEEDTEST", $"Speed test error for {result.PeerName}: {ex.Message}");
            }

            // Cache result (max 10 per device)
            lock (_lock)
            {
                string key = peer.DeviceId ?? "";
                if (!_history.TryGetValue(key, out var list))
                {
                    list = new List<SpeedTestResult>();
                    _history[key] = list;
                }
                list.Add(result);
                while (list.Count > 10)
                {
                    list.RemoveAt(0);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the cached speed test history for a given device ID.
        /// </summary>
        public static List<SpeedTestResult> GetHistory(string deviceId)
        {
            lock (_lock)
            {
                return _history.TryGetValue(deviceId, out var list)
                    ? list.ToList()  // Return a copy
                    : new List<SpeedTestResult>();
            }
        }

        /// <summary>
        /// Gets the most recent speed test result for a device, or null if none.
        /// </summary>
        public static SpeedTestResult? GetLatest(string deviceId)
        {
            lock (_lock)
            {
                return _history.TryGetValue(deviceId, out var list) && list.Count > 0
                    ? list[^1]
                    : null;
            }
        }
    }
}
