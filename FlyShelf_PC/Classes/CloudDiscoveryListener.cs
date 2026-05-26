using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class CloudDiscoveryListener
    {
        // Separate clients: SSE stream needs infinite timeout, forced sync polls use short timeout
        private static readonly HttpClient _streamClient = new HttpClient() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static readonly HttpClient _pollClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
        private static string FIREBASE_BASE => FirebaseAuthManager.FirebaseDatabaseUrl;
        
        /// <summary>Wraps a Firebase REST URL with auth token.</summary>
        private static async Task<string> AuthUrl(string path)
        {
            return await FirebaseAuthManager.AuthenticateUrl($"{FIREBASE_BASE}/{path}");
        }

        private FlyShelfViewModel _viewModel;
        private long _lastProcessedTimestamp = 0;
        private CancellationTokenSource? _cts = null;
        private HashSet<string> _processedIds = new HashSet<string>();

        public CloudDiscoveryListener(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public void StartPolling()
        {
            StopPolling();

            // CRITICAL: Do not listen to Firebase unless device is paired
            if (!DevicePairingManager.HasPairingKey)
            {
                Logger.LogAction("FIREBASE LISTENER", "Blocked Ã¢â‚¬â€ no pairing key. Pair with another device to enable cloud sync.");
                return;
            }

            _cts = new CancellationTokenSource();
            // Backlog: process items from the last 5 minutes (catch-up for devices that connect late)
            _lastProcessedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
            _processedIds.Clear();

            Logger.LogAction("FIREBASE LISTENER", "Starting SSE real-time stream + forced sync poller.");

            // 2. Forced sync SSE: REMOVED — Firebase must never relay content (text, files, URLs).
            //    All content transfer is P2P-only via PeerManager (LAN/Cloudflare direct).
            //    Firebase is strictly for exchanging encrypted device URLs (discovery).

            // 3. Peer URL discovery: SSE stream on active_devices — instant reconnect when any peer comes online
            Task.Run(() => RunPeerDiscoverySSE(_cts.Token));
        }

        public void StopPolling()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                _cts = null;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // FORCED SYNC SSE: REMOVED — Firebase must never relay content.
        // All content transfer is P2P-only via PeerManager.
        // Firebase is strictly for device URL discovery (active_devices).
        // ═══════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════
        // PEER DISCOVERY SSE: Watch active_devices for URL changes
        // When any device posts a new URL, all paired devices instantly
        // pick it up, update PeerManager, and the URL auto-deletes.
        // ═══════════════════════════════════════════════════════════════

        private async Task RunPeerDiscoverySSE(CancellationToken ct)
        {
            const int INITIAL_RECONNECT = 2000;
            const int MAX_RECONNECT = 30000;
            int reconnectDelay = INITIAL_RECONNECT;
            string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string pairingKey = DevicePairingManager.EnsurePairingKey();
                    if (string.IsNullOrEmpty(pairingKey))
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    string url = await AuthUrl($"active_devices/{pairingKey}.json");
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Accept", "text/event-stream");

                    var response = await _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, streamCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.LogAction("PEER SSE", $"HTTP {(int)response.StatusCode} — retrying in {reconnectDelay}ms");
                        await Task.Delay(reconnectDelay, ct);
                        reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT);
                        continue;
                    }

                    Logger.LogAction("PEER SSE", "Watching active_devices for URL changes ✓");
                    reconnectDelay = INITIAL_RECONNECT;

                    // Initialize sliding watchdog timer (65s window - Firebase keepalive is 30s)
                    using var watchdog = new System.Timers.Timer(65000);
                    watchdog.AutoReset = false;
                    watchdog.Elapsed += (s, e) =>
                    {
                        Logger.LogAction("PEER SSE WARN", "⚠️ SSE watchdog expired (no keepalive/data for 65s). Aborting zombie stream to trigger reconnect...");
                        try { streamCts.Cancel(); } catch { }
                    };
                    watchdog.Start();

                    using var stream = await response.Content.ReadAsStreamAsync(streamCts.Token);
                    using var reader = new StreamReader(stream);
                    string currentEvent = "";
                    string currentData = "";

                    while (!streamCts.Token.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(streamCts.Token);
                        if (line == null) break;

                        // Reset sliding watchdog on any line read (event, comment, or keepalive)
                        watchdog.Stop();
                        watchdog.Start();

                        if (line.StartsWith("event:")) currentEvent = line.Substring(6).Trim();
                        else if (line.StartsWith("data:")) currentData = line.Substring(5).Trim();
                        else if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(currentData))
                        {
                            // Process URL change event — only put/patch with valid JSON data
                            if ((currentEvent == "put" || currentEvent == "patch") && 
                                !string.IsNullOrWhiteSpace(currentData) && currentData != "null")
                            {
                                _ = Task.Run(() => ProcessPeerUrlChange(currentData, myDeviceId, ct));
                            }
                            currentEvent = "";
                            currentData = "";
                        }
                    }

                    watchdog.Stop();
                    Logger.LogAction("PEER SSE", "Stream closed — reconnecting...");
                }
                catch (OperationCanceledException) 
                {
                    if (ct.IsCancellationRequested) break; // Normal shutdown
                    // Watchdog cancellation — drop through to catch exception and retry
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER SSE", $"Error: {ex.Message} — retrying in {reconnectDelay}ms");
                    try { await Task.Delay(reconnectDelay, ct); } catch { break; }
                    reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT);
                }
            }

            Logger.LogAction("PEER SSE", "Discovery stream STOPPED.");
        }

        private async Task ProcessPeerUrlChange(string jsonData, string myDeviceId, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonData) || jsonData == "null")
                    return;
                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;

                // Firebase SSE "put" has { "path": "/...", "data": {...} }
                if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                    return;

                // Could be a single device update (path: "/DeviceId") or full snapshot (path: "/")
                string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";

                var peerManager = PeerManager.Instance;
                if (peerManager == null) return;

                // Detect urlRequest on sub-path: /DeviceId/urlRequest
                if (path.Contains("/urlRequest") && data.ValueKind == JsonValueKind.Object)
                {
                    string requestingDevice = path.TrimStart('/').Split('/')[0];
                    if (!string.IsNullOrEmpty(requestingDevice) && requestingDevice != myDeviceId)
                    {
                        _ = Task.Run(() => peerManager.HandlePeerUrlRequest(requestingDevice));
                    }
                    return;
                }

                if (path == "/")
                {
                    // Full snapshot — scan all devices
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in data.EnumerateObject())
                        {
                            if (prop.Name == myDeviceId) continue;

                            // Check if any peer has a urlRequest for us
                            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("urlRequest", out _))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlRequest(prop.Name));
                            }

                            string globalUrl = prop.Value.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                            string localUrl = prop.Value.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                            string deviceName = prop.Value.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? prop.Name : prop.Name;

                            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlUpdate(prop.Name, deviceName, localUrl, globalUrl));
                            }
                        }
                    }
                }
                else
                {
                    // Single device update — path is like "/DeviceId" or "/DeviceId/GlobalUrl"
                    string deviceKey = path.TrimStart('/').Split('/')[0];
                    if (!string.IsNullOrEmpty(deviceKey) && deviceKey != myDeviceId)
                    {
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            // Check if this is a urlRequest
                            if (data.TryGetProperty("urlRequest", out _))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlRequest(deviceKey));
                            }

                            string globalUrl = data.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                            string localUrl = data.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                            string deviceName = data.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? deviceKey : deviceKey;

                            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlUpdate(deviceKey, deviceName, localUrl, globalUrl));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER SSE", $"ProcessPeerUrlChange error: {ex.Message}");
            }
        }

    }
}
