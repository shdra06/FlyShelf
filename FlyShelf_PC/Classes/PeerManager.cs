using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// v5 PeerManager — Pure P2P engine.
    /// 
    /// Firebase = phone book ONLY. Stores device URLs, never file data.
    /// Flow: Discover → Handshake → Confirm tick → Talk direct. URLs persist in Firebase as a "phone book".
    /// All text/files flow device-to-device via LAN or Cloudflare. 5s heartbeat.
    /// </summary>
    public partial class PeerManager
    {
        public static PeerManager? Instance { get; private set; }

        private readonly ConcurrentDictionary<string, PeerConnection> _peers = new();
        private CancellationTokenSource _cts = new();
        private string _myDeviceId = "";
        private string _myPairingKey = "";
        private bool _urlCleanedFromFirebase = false;
        private bool _urlRequestSent = false;             // Have we asked peers for their URLs?
        private DateTime _lastUrlRequestTime = DateTime.MinValue; // Throttle urlRequest to max once per 60s
        private readonly HashSet<string> _prunedGhosts = new(StringComparer.OrdinalIgnoreCase);

        // ═ ═ ═ Local URL cache — survives app restart ═ ═ ═
        private static readonly string _urlCacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "peer_urls.json");

        // ═ ═ ═ Config ═ ═ ═
        private const int HEARTBEAT_MS = 5_000;            // 5s heartbeat (fast LAN detection)
        private const int HEARTBEAT_TIMEOUT_MS = 4_000;    // 4s timeout per ping
        private const int MAX_FAILURES = 3;                // 3 misses = dead (quick failover)
        private const int DISCOVERY_MS = 30_000;           // Re-scan Firebase every 30s when peers are offline (safety fallback + slow DNS retry)
        private const int HANDSHAKE_TIMEOUT_LAN_MS = 5_000;   // 5s for LAN
        private const int HANDSHAKE_TIMEOUT_CF_MS = 8_000;    // 8s for Cloudflare tunnels

        // ═ ═ ═ Events ═ ═ ═
        public event Action<string, string>? PeerConnected;     // (deviceId, transport)
        public event Action<string>? PeerDisconnected;          // (deviceId)
        public event Action<string, string>? TransportSwitched; // (deviceId, newTransport)

        // ═ ═ ═ Shared HttpClient — connection pooling eliminates TLS re-handshake per request ═ ═ ═
        // Critical for Cloudflare: each new HttpClient = new TLS handshake (~800ms via tunnel).
        // Reusing connections saves that on every subsequent request.
        private static readonly SocketsHttpHandler _sharedHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        private static readonly HttpClient _sharedClient = new(_sharedHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        public PeerManager() { Instance = this; }

        public IReadOnlyDictionary<string, PeerConnection> ConnectedPeers => _peers;
        public int AliveCount => _peers.Values.Count(p => p.IsAlive);

        /// <summary>Start: discover peers, handshake, confirm, clean Firebase, begin heartbeat.</summary>
        public async Task StartAsync()
        {
            _myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;
            _myPairingKey = DevicePairingManager.EnsurePairingKey();
            if (string.IsNullOrEmpty(_myPairingKey))
            {
                Logger.LogAction("PEER", "No pairing key — PeerManager idle.");
                return;
            }

            Logger.LogAction("PEER", $"v5 PeerManager starting [device={_myDeviceId}]");
            _cts = new CancellationTokenSource();

            // ═ ═ ═ FIX 1: Re-publish our own URLs to Firebase on startup ═ ═ ═
            // Ensures peers can always find us, even if ConfirmAndCleanup deleted them last session.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000); // Wait for tunnel to start
                    string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
                    string localUrl = CloudDiscoveryManager.CachedLocalUrl;
                    if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
                    {
                        await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                        Logger.LogAction("PEER", $"📡 Startup: re-published URLs to Firebase (LAN={localUrl} CF={globalUrl})");
                    }
                }
                catch (Exception ex) { Logger.LogAction("PEER", $"Startup URL publish error: {ex.Message}"); }
            });

            // ═ ═ ═ FIX 2: Try cached URLs first before Firebase ═ ═ ═
            // If we have locally cached URLs from last session, try them directly.
            // This provides instant reconnection even if Firebase URLs were cleaned.
            await TryCachedUrlsFirst();

            await DiscoverAndHandshake();

            // ═ ═ ═ UDP MULTICAST: Start zero-config offline discovery ═ ═ ═
            StartUdpDiscovery();

            _ = Task.Run(() => HeartbeatLoop(_cts.Token));
            _ = Task.Run(() => DiscoveryLoop(_cts.Token));

            Logger.LogAction("PEER", $"PeerManager running — {AliveCount}/{_peers.Count} peer(s) alive");
        }

        /// <summary>
        /// Try connecting to peers using locally cached URLs from last session.
        /// This provides instant reconnection when Firebase URLs have been cleaned.
        /// </summary>
        private async Task TryCachedUrlsFirst()
        {
            try
            {
                if (!File.Exists(_urlCacheFile)) return;

                string fileContent = await File.ReadAllTextAsync(_urlCacheFile);
                string json = SecureStorage.Decrypt(fileContent);
                var cache = JsonSerializer.Deserialize<Dictionary<string, CachedPeerUrls>>(json);
                if (cache == null || cache.Count == 0) return;

                Logger.LogAction("PEER", $"📋 Loaded {cache.Count} cached peer URL(s) from last session");

                foreach (var (devId, urls) in cache)
                {
                    if (devId == _myDeviceId) continue;

                    var peer = _peers.GetOrAdd(devId, _ => new PeerConnection
                    {
                        DeviceId = devId,
                        DeviceName = urls.DeviceName ?? devId
                    });

                    if (!string.IsNullOrEmpty(urls.LanUrl)) peer.LanUrl = urls.LanUrl;
                    if (!string.IsNullOrEmpty(urls.CloudflareUrl)) peer.CloudflareUrl = urls.CloudflareUrl;

                    if (!peer.IsAlive)
                    {
                        Logger.LogAction("PEER", $"📋 Trying cached URLs for {peer.DeviceName}: LAN={peer.LanUrl} CF={peer.CloudflareUrl}");
                        await Handshake(peer);
                    }
                }
            }
            catch (Exception ex) { Logger.LogAction("PEER", $"Cache load error: {ex.Message}"); }
        }

        /// <summary>Save all known peer URLs to local cache file.</summary>
        private void SaveUrlCache()
        {
            try
            {
                var cache = new Dictionary<string, CachedPeerUrls>();
                foreach (var (devId, peer) in _peers)
                {
                    if (!string.IsNullOrEmpty(peer.LanUrl) || !string.IsNullOrEmpty(peer.CloudflareUrl))
                    {
                        cache[devId] = new CachedPeerUrls
                        {
                            DeviceName = peer.DeviceName,
                            LanUrl = peer.LanUrl,
                            CloudflareUrl = peer.CloudflareUrl,
                            LastSeen = peer.LastSeen
                        };
                    }
                }
                if (cache.Count > 0)
                {
                    string dir = Path.GetDirectoryName(_urlCacheFile)!;
                    Directory.CreateDirectory(dir);
                    string json = JsonSerializer.Serialize(cache);
                    string encrypted = SecureStorage.Encrypt(json);
                    File.WriteAllText(_urlCacheFile, encrypted);
                }
            }
            catch { }
        }

        public bool IsRunning => !_cts.IsCancellationRequested;

        public void Stop()
        {
            _cts.Cancel();
            StopUdpDiscovery();
            foreach (var p in _peers.Values)
            {
                p.IsAlive = false;
                try { p.WsCts?.Cancel(); } catch { }
                try { p.LiveSocket?.Dispose(); } catch { }
            }
            if (Instance == this)
            {
                Instance = null;
            }
            Logger.LogAction("PEER", "PeerManager stopped.");
        }

        // ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═

        private async Task DiscoverAndHandshake()
        {
            if (!SettingsManager.Current.EnableCloudDiscovery)
            {
                Logger.LogAction("PEER", "Cloud discovery is disabled. Skipping Firebase peer scan.");
                return;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string url = await CloudDiscoveryManager.AuthUrlPublic($"active_devices/{_myPairingKey}.json");
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return;

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                // Get locally paired devices to filter out stale/ghost entries in Firebase
                var pairedDevices = DevicePairingManager.GetPairedDevices();
                var pairedDeviceIds = new HashSet<string>(pairedDevices.Select(d => d.DeviceId), StringComparer.OrdinalIgnoreCase);
                var pairedDeviceNames = new HashSet<string>(pairedDevices.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);

                using var doc = JsonDocument.Parse(json);
                int totalPeers = 0;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var dev = prop.Value;
                    string devId = dev.TryGetProperty("DeviceId", out var di) ? di.GetString() ?? prop.Name : prop.Name;
                    if (devId == _myDeviceId) continue;

                    string name = dev.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? "" : "";

                    // Skip devices that are NOT in the local paired devices list
                    // This filters out stale/ghost entries (e.g., unpaired phones still lingering in Firebase)
                    if (pairedDeviceIds.Count > 0 && !pairedDeviceIds.Contains(devId) && !pairedDeviceNames.Contains(name))
                    {
                        // Only log + delete once per unknown device to avoid spam
                        if (_prunedGhosts.Add(devId))
                        {
                            Logger.LogAction("PEER", $"⭐ Skipping unpaired device in Firebase: {name} ({devId}) — not in local paired list");

                            // Actively delete the ghost entry from Firebase
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    string deleteUrl = await CloudDiscoveryManager.AuthUrlPublic($"active_devices/{_myPairingKey}/{prop.Name}.json");
                                    using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                                    await delClient.DeleteAsync(deleteUrl);
                                    Logger.LogAction("PEER", $"🗑️ Deleted ghost device from Firebase: {name} ({prop.Name})");
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogAction("PEER", $"Failed to delete ghost {name}: {ex.Message}");
                                }
                            });
                        }
                        continue;
                    }

                    totalPeers++;
                    string lan = dev.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                    string cf = dev.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                    string direct = dev.TryGetProperty("Url", out var du) ? du.GetString() ?? "" : "";

                    // Decrypt URLs if they were encrypted by the peer
                    lan = DecryptUrlSafe(lan);
                    cf = DecryptUrlSafe(cf);
                    direct = DecryptUrlSafe(direct);

                    // Sanitize: reject non-URL values (e.g., "offline", failed decryption garbage)
                    if (!string.IsNullOrEmpty(lan) && !lan.StartsWith("http")) lan = "";
                    if (!string.IsNullOrEmpty(cf) && !cf.StartsWith("http")) cf = "";
                    if (!string.IsNullOrEmpty(direct) && !direct.StartsWith("http")) direct = "";

                    if (string.IsNullOrEmpty(lan) && !string.IsNullOrEmpty(direct) && !direct.Contains("trycloudflare"))
                        lan = direct;

                    // Track if any known peer has empty URLs (needs urlRequest)

                    if (_peers.TryGetValue(devId, out var existing))
                    {
                        // Update URLs if changed
                        if (!string.IsNullOrEmpty(lan)) existing.LanUrl = lan;
                        if (!string.IsNullOrEmpty(cf) && cf != existing.CloudflareUrl)
                        {
                            Logger.LogAction("PEER", $"{name}: Cloudflare URL changed → {cf}");
                            existing.CloudflareUrl = cf;
                            if (existing.Transport == "Cloudflare") existing.ActiveUrl = cf;
                        }
                    }
                    else
                    {
                        _peers[devId] = new PeerConnection
                        {
                            DeviceId = devId, DeviceName = name,
                            LanUrl = lan, CloudflareUrl = cf
                        };
                        Logger.LogAction("PEER", $"Found: {name} LAN={lan} CF={cf}");
                    }
                }

                // Handshake all non-alive peers
                var tasks = _peers.Values.Where(p => !p.IsAlive).Select(Handshake);
                await Task.WhenAll(tasks);

                // ═ ═ ═ FIX 3: Send urlRequest whenever we have dead peers ═ ═ ═
                // Don't gate on _urlRequestSent — re-send every 60s if peers are still dead.
                // This ensures recovery even if the first request was missed.
                bool hasDeadPeers2 = _peers.Values.Any(p => !p.IsAlive);
                if (hasDeadPeers2 && AliveCount == 0 && totalPeers > 0
                    && (DateTime.UtcNow - _lastUrlRequestTime).TotalSeconds > 60)
                {
                    await SendUrlRequest();
                }

                // CONFIRM + CLEANUP: If all peers are alive, confirm and delete URLs
                if (AliveCount > 0 && AliveCount >= totalPeers && !_urlCleanedFromFirebase)
                {
                    await ConfirmAndCleanup();
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"Discovery error: {ex.Message}");
            }
        }

        private static string DecryptUrlSafe(string value)
        {
            return SyncCrypto.DecryptUrlSafe(value);
        }

        /// <summary>
        /// Write a urlRequest signal to Firebase so that online peers re-publish their URLs.
        /// This is the secure alternative to leaving URLs permanently in Firebase.
        /// </summary>
        private async Task SendUrlRequest()
        {
            if (!SettingsManager.Current.EnableCloudDiscovery) return;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

                // ═ ═ ═ FIX 4: Write urlRequest under EACH dead peer's path ═ ═ ═
                // Previously wrote under our OWN device path — the other PC's SSE watcher
                // was watching for changes to their OWN path, not ours. This fixes the signal.
                foreach (var peer in _peers.Values.Where(p => !p.IsAlive))
                {
                    try
                    {
                        string requestUrl = await CloudDiscoveryManager.AuthUrlPublic(
                            $"active_devices/{_myPairingKey}/{peer.DeviceId}/urlRequest.json");
                        string body = JsonSerializer.Serialize(new
                        {
                            requestedAt = NetworkClock.UtcNowMs,
                            requestedBy = _myDeviceId
                        });
                        await client.PutAsync(requestUrl, new StringContent(body, Encoding.UTF8, "application/json"));
                    }
                    catch { }
                }

                // Also re-publish OUR OWN URLs so the other peer can find us
                string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
                string localUrl = CloudDiscoveryManager.CachedLocalUrl;
                if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
                {
                    _urlCleanedFromFirebase = false;
                    await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                }

                _urlRequestSent = true;
                _lastUrlRequestTime = DateTime.UtcNow;
                Logger.LogAction("PEER", "📡 URL request signal sent + our URLs re-published");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"URL request error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by CloudDiscoveryListener SSE when a peer writes a urlRequest.
        /// Re-publishes our encrypted URLs so the requesting peer can find us.
        /// </summary>
        public async Task HandlePeerUrlRequest(string requestingDeviceId)
        {
            if (requestingDeviceId == _myDeviceId) return;

            Logger.LogAction("PEER", $"📡 {requestingDeviceId} is requesting URLs — re-publishing ours...");

            string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
            string localUrl = CloudDiscoveryManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                _urlCleanedFromFirebase = false;
                await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                Logger.LogAction("PEER", $"📡 Re-published encrypted URLs for {requestingDeviceId}");
            }
        }

        /// <summary>
        /// Called by CloudDiscoveryListener SSE when a peer's URL changes in real-time.
        /// Performs targeted update and handshake for ONLY this peer, preserving other active connections.
        /// </summary>
        public async Task HandlePeerUrlUpdate(string deviceId, string deviceName, string localUrl, string globalUrl)
        {
            if (deviceId == _myDeviceId) return;

            // 1. Decrypt URLs safely (supports multi-key decryption)
            string lan = DecryptUrlSafe(localUrl);
            string cf = DecryptUrlSafe(globalUrl);

            // 2. Sanitize and validate URLs
            if (!string.IsNullOrEmpty(lan) && !lan.StartsWith("http")) lan = "";
            if (!string.IsNullOrEmpty(cf) && !cf.StartsWith("http")) cf = "";

            if (string.IsNullOrEmpty(lan) && string.IsNullOrEmpty(cf))
            {
                return; // Nothing to update
            }

            // 3. Filter out unpaired devices (ghosts)
            var pairedDevices = DevicePairingManager.GetPairedDevices();
            var pairedDeviceIds = new HashSet<string>(pairedDevices.Select(d => d.DeviceId), StringComparer.OrdinalIgnoreCase);
            var pairedDeviceNames = new HashSet<string>(pairedDevices.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);

            if (pairedDeviceIds.Count > 0 && !pairedDeviceIds.Contains(deviceId) && !pairedDeviceNames.Contains(deviceName))
            {
                lock (_prunedGhosts)
                {
                    if (!_prunedGhosts.Add(deviceId))
                        return;
                }

                Logger.LogAction("PEER", $"⭐ Skipping unpaired device URL update: {deviceName} ({deviceId})");
                // Actively delete the ghost entry from Firebase
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string deleteUrl = await CloudDiscoveryManager.AuthUrlPublic($"active_devices/{_myPairingKey}/{deviceId}.json");
                        using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        await delClient.DeleteAsync(deleteUrl);
                        Logger.LogAction("PEER", $"🗑️ Deleted ghost device from Firebase: {deviceName} ({deviceId})");
                    }
                    catch { }
                });
                return;
            }

            Logger.LogAction("PEER", $"📡 Target URL update for {deviceName}: LAN={lan} CF={cf}");

            PeerConnection peer;
            if (_peers.TryGetValue(deviceId, out var existing))
            {
                peer = existing;
                // If URLs didn't change and peer is already alive and connected, skip
                if (peer.LanUrl == lan && peer.CloudflareUrl == cf && peer.IsAlive && peer.LiveSocket?.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    return;
                }
                
                peer.LanUrl = lan;
                peer.CloudflareUrl = cf;
            }
            else
            {
                peer = new PeerConnection
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    LanUrl = lan,
                    CloudflareUrl = cf
                };
                _peers[deviceId] = peer;
            }

            // Reset liveness and attempt targeted handshake
            peer.IsAlive = false;
            peer.ConsecutiveFailures = 0;
            try { peer.WsCts?.Cancel(); } catch { }
            try { peer.LiveSocket?.Dispose(); } catch { }
            peer.LiveSocket = null;

            await Handshake(peer);
        }

        // ═══ Handshake, WebSocket & Cleanup moved to PeerManager.Connection.cs ═══
    }
}
