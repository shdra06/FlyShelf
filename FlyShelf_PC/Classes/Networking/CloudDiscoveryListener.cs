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
        // AUDIT Task 5: Use shared pool instance instead of per-class HttpClient (prevents socket exhaustion)
        private static HttpClient _queryClient => HttpClientPool.Default;
        private static string FIREBASE_BASE => FirebaseAuthManager.FirebaseDatabaseUrl;
        
        /// <summary>Wraps a Firebase REST URL with auth token.</summary>
        private static async Task<string> AuthUrl(string path)
        {
            return await FirebaseAuthManager.AuthenticateUrl($"{FIREBASE_BASE}/{path}");
        }

        private FlyShelfViewModel _viewModel;
        private long _lastProcessedTimestamp = 0;
        private CancellationTokenSource? _cts = null;

        // Debounce: prevent multiple rapid queries (e.g. during network flap)
        private DateTime _lastQueryTime = DateTime.MinValue;
        private const int QUERY_DEBOUNCE_MS = 3000; // 3s minimum between queries
        private readonly SemaphoreSlim _querySemaphore = new(1, 1);

        // H-13 fix: Adaptive polling to prevent Firebase quota exhaustion
        private int _currentPollInterval = 30_000;  // Start at 30s (was 10s)
        private const int POLL_INTERVAL_MIN = 30_000;   // 30s floor
        private const int POLL_INTERVAL_MAX = 300_000;   // 5 min ceiling
        private int _lastPeerCount = -1;  // Track peer changes to reset interval

        public CloudDiscoveryListener(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        /// <summary>
        /// StartPolling() now just triggers an initial on-demand query.
        /// No persistent SSE connection is held — Firebase connections = 0.
        /// Subsequent queries are triggered by:
        ///   - Network changes (App.xaml.cs NetworkAddressChanged)
        ///   - Force Sync button
        ///   - After pairing
        ///   - DiscoveryLoop in PeerManager (30s fallback for dead peers)
        /// </summary>
        public void StartPolling()
        {
            StopPolling();

            // CRITICAL: Do not query Firebase unless device is paired
            if (!DevicePairingManager.HasPairingKey)
            {
                Logger.LogAction("FIREBASE LISTENER", "Blocked — no pairing key. Pair with another device to enable cloud sync.");
                return;
            }

            _cts = new CancellationTokenSource();
            _lastProcessedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

            Logger.LogAction("FIREBASE LISTENER", "Responsive query mode active — checking Firebase periodically & on-demand.");

            // Initial query to discover existing peers
            _ = Task.Run(() => QueryPeersOnce());

            // Responsive background query loop: adaptive polling to avoid Firebase quota exhaustion (H-13)
            // 30s base, doubles when peers are stable (up to 5min), resets to 30s on peer count changes
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_currentPollInterval, token);
                        await QueryPeersOnce();

                        // Adaptive interval: increase when stable, reset on changes
                        int currentAlive = PeerManager.Instance?.AliveCount ?? 0;
                        if (_lastPeerCount >= 0 && currentAlive == _lastPeerCount && currentAlive > 0)
                        {
                            // Peers stable — back off (double interval, capped at 5min)
                            _currentPollInterval = Math.Min(_currentPollInterval * 2, POLL_INTERVAL_MAX);
                        }
                        else
                        {
                            // Peer count changed or no peers — reset to base for faster discovery
                            _currentPollInterval = POLL_INTERVAL_MIN;
                        }
                        _lastPeerCount = currentAlive;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.LogAction("FIREBASE LISTENER", $"Loop error: {ex.Message}");
                    }
                }
            }, token);
        }

        public void StopPolling()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { } // Best-effort: failure is acceptable
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ON-DEMAND PEER QUERY: Single REST call, parse, update, done.
        // Replaces persistent SSE — uses 0 Firebase connections.
        // Called at startup, network changes, force sync, and after pairing.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Performs a single REST query to Firebase to discover/update peer URLs.
        /// This is the replacement for the persistent SSE stream.
        /// Thread-safe with debounce — safe to call from multiple triggers.
        /// </summary>
        public async Task QueryPeersOnce()
        {
            // Debounce: skip if called too soon after last query
            if ((DateTime.UtcNow - _lastQueryTime).TotalMilliseconds < QUERY_DEBOUNCE_MS)
            {
                return;
            }

            // Serialize queries — only one at a time
            if (!await _querySemaphore.WaitAsync(0))
            {
                return;
            }

            try
            {
                _lastQueryTime = DateTime.UtcNow;
                string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey))
                {
                    return;
                }

                string url = await AuthUrl($"active_devices/{pairingKey}.json");
                using var response = await _queryClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogAction("PEER QUERY", $"HTTP {(int)response.StatusCode} — query failed");
                    return;
                }

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    return;
                }

                // Parse and update peers
                await ProcessPeerSnapshot(json, myDeviceId, pairingKey);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER QUERY", $"Error: {ex.Message}");
            }
            finally
            {
                _querySemaphore.Release();
            }
        }

        /// <summary>
        /// Parses a full Firebase snapshot of active_devices/{pairingKey} and
        /// updates PeerManager with any new or changed peer URLs.
        /// </summary>
        private async Task ProcessPeerSnapshot(string jsonData, string myDeviceId, string pairingKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonData) || jsonData == "null")
                    return;

                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object) return;

                var peerManager = PeerManager.Instance;

                // 1. Check for room-level wakeSignal or urlRequest
                if (root.TryGetProperty("urlRequest", out _) || root.TryGetProperty("wakeSignal", out _))
                {
                    Logger.LogAction("URL_REQUEST", "⚡ Room-level URL refresh signal received — publishing fresh endpoints immediately!");
                    _ = CloudDiscoveryManager.PushTunnelUrl(
                        CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                        true,
                        CloudDiscoveryManager.CachedLocalUrl ?? "",
                        forceWrite: true);
                }

                int peerCount = 0;

                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                    // 2. Check for direct urlRequest targeted at this PC
                    if (prop.Name == myDeviceId)
                    {
                        if (prop.Value.TryGetProperty("urlRequest", out _))
                        {
                            Logger.LogAction("URL_REQUEST", $"⚡ Direct URL request received for PC ({myDeviceId}) — publishing fresh endpoints immediately!");
                            _ = CloudDiscoveryManager.PushTunnelUrl(
                                CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                                true,
                                CloudDiscoveryManager.CachedLocalUrl ?? "",
                                forceWrite: true);

                            // Clean up fulfilled request node
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    string delUrl = await AuthUrl($"active_devices/{pairingKey}/{myDeviceId}/urlRequest.json");
                                    await _queryClient.DeleteAsync(delUrl);
                                }
                                catch { }
                            });
                        }
                        continue;
                    }

                    // SECURITY: Skip Firebase entries from blocked (recently-unpaired) devices
                    if (DevicePairingManager.IsDeviceBlocked(prop.Name))
                    {
                        Logger.LogAction("CLOUD", $"Skipped peer update from blocked device: {prop.Name}");
                        continue;
                    }

                    // Check if any peer has a urlRequest for us
                    if (prop.Value.TryGetProperty("urlRequest", out _))
                    {
                        Logger.LogAction("URL_REQUEST", $"⚡ Companion '{prop.Name}' has active urlRequest — publishing fresh endpoints!");
                        _ = CloudDiscoveryManager.PushTunnelUrl(
                            CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                            true,
                            CloudDiscoveryManager.CachedLocalUrl ?? "",
                            forceWrite: true);

                        if (peerManager != null)
                        {
                            _ = Task.Run(() => peerManager.HandlePeerUrlRequest(prop.Name));
                        }
                    }

                    string globalUrl = prop.Value.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                    string localUrl = prop.Value.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                    string deviceName = prop.Value.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? prop.Name : prop.Name;
                    bool isOnline = prop.Value.TryGetProperty("IsOnline", out var on) && on.GetBoolean();

                    // ZERO-TRUST VALIDATION: Strictly sanitize URLs from Firebase
                    if (!IsValidGlobalTunnelUrl(globalUrl)) globalUrl = "";
                    if (!IsValidLocalLanUrl(localUrl)) localUrl = "";

                    if (isOnline)
                    {
                        DevicePairingManager.RecordDeviceActivity(prop.Name, deviceName, !string.IsNullOrEmpty(localUrl) ? "LAN" : "Cloud");
                    }

                    if (peerManager != null && (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl)))
                    {
                        peerCount++;
                        _ = Task.Run(() => peerManager.HandlePeerUrlUpdate(prop.Name, deviceName, localUrl, globalUrl));
                    }
                }

                if (peerCount > 0)
                    Logger.LogAction("PEER QUERY", $"Found {peerCount} peer(s) with validated URLs in Firebase");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER QUERY", $"ProcessPeerSnapshot error: {ex.Message}");
            }
        }

        private static bool IsValidGlobalTunnelUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            return uri.Host.EndsWith("trycloudflare.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidLocalLanUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host;
            return host == "localhost" || host == "127.0.0.1" ||
                   host.StartsWith("192.168.", StringComparison.Ordinal) ||
                   host.StartsWith("10.", StringComparison.Ordinal) ||
                   System.Text.RegularExpressions.Regex.IsMatch(host, @"^172\.(1[6-9]|2[0-9]|3[0-1])\.");
        }

    }
}
