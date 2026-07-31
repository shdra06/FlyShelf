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
        private HashSet<string> _processedIds = new HashSet<string>();

        // Debounce: prevent multiple rapid queries (e.g. during network flap)
        private DateTime _lastQueryTime = DateTime.MinValue;
        private const int QUERY_DEBOUNCE_MS = 3000; // 3s minimum between queries
        private readonly SemaphoreSlim _querySemaphore = new(1, 1);

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
            _processedIds.Clear();

            Logger.LogAction("FIREBASE LISTENER", "On-demand query mode active (no persistent SSE — 0 Firebase connections held).");

            // Initial query to discover existing peers
            _ = Task.Run(() => QueryPeersOnce());
        }

        public void StopPolling()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { } // Best-effort: failure is acceptable
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
                Logger.LogAction("PEER QUERY", "Debounced — skipping (queried < 3s ago)");
                return;
            }

            // Serialize queries — only one at a time
            if (!await _querySemaphore.WaitAsync(0))
            {
                Logger.LogAction("PEER QUERY", "Skipped — another query already in progress");
                return;
            }

            try
            {
                _lastQueryTime = DateTime.UtcNow;
                string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey))
                {
                    Logger.LogAction("PEER QUERY", "No pairing key — skipping query");
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
                    Logger.LogAction("PEER QUERY", "No active devices in Firebase");
                    return;
                }

                // Parse and update peers — same logic as the old SSE ProcessPeerUrlChange
                // but wrapped in a single snapshot parse
                await ProcessPeerSnapshot(json, myDeviceId);

                Logger.LogAction("PEER QUERY", "Peer discovery query completed (single REST call)");
            }
            catch (TaskCanceledException)
            {
                Logger.LogAction("PEER QUERY", "Query timed out");
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
        private async Task ProcessPeerSnapshot(string jsonData, string myDeviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonData) || jsonData == "null")
                    return;

                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object) return;

                var peerManager = PeerManager.Instance;
                if (peerManager == null) return;

                int peerCount = 0;

                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == myDeviceId) continue;

                    // SECURITY: Skip Firebase entries from blocked (recently-unpaired) devices
                    if (DevicePairingManager.IsDeviceBlocked(prop.Name))
                    {
                        Logger.LogAction("CLOUD", $"Skipped peer update from blocked device: {prop.Name}");
                        continue;
                    }
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
                        peerCount++;
                        _ = Task.Run(() => peerManager.HandlePeerUrlUpdate(prop.Name, deviceName, localUrl, globalUrl));
                    }
                }

                if (peerCount > 0)
                    Logger.LogAction("PEER QUERY", $"Found {peerCount} peer(s) with URLs in Firebase");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER QUERY", $"ProcessPeerSnapshot error: {ex.Message}");
            }
        }

    }
}
