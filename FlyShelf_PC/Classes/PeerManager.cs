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
    /// v5 PeerManager â€” Pure P2P engine.
    /// 
    /// Firebase = phone book ONLY. Stores device URLs, never file data.
    /// Flow: Discover â†’ Handshake â†’ Confirm tick â†’ Talk direct. URLs persist in Firebase as a "phone book".
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

        // â•â•â• Local URL cache â€” survives app restart â•â•â•
        private static readonly string _urlCacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "peer_urls.json");

        // â•â•â• Config â•â•â•
        private const int HEARTBEAT_MS = 5_000;            // 5s heartbeat (fast LAN detection)
        private const int HEARTBEAT_TIMEOUT_MS = 4_000;    // 4s timeout per ping
        private const int MAX_FAILURES = 3;                // 3 misses = dead (quick failover)
        private const int DISCOVERY_MS = 600_000;          // Re-scan Firebase every 10m (safety fallback only - real-time updates use SSE)
        private const int HANDSHAKE_TIMEOUT_LAN_MS = 5_000;   // 5s for LAN
        private const int HANDSHAKE_TIMEOUT_CF_MS = 8_000;    // 8s for Cloudflare tunnels

        // â•â•â• Events â•â•â•
        public event Action<string, string>? PeerConnected;     // (deviceId, transport)
        public event Action<string>? PeerDisconnected;          // (deviceId)
        public event Action<string, string>? TransportSwitched; // (deviceId, newTransport)

        // â•â•â• Shared HttpClient â€” connection pooling eliminates TLS re-handshake per request â•â•â•
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
                Logger.LogAction("PEER", "No pairing key â€” PeerManager idle.");
                return;
            }

            Logger.LogAction("PEER", $"v5 PeerManager starting [device={_myDeviceId}]");
            _cts = new CancellationTokenSource();

            // â•â•â• FIX 1: Re-publish our own URLs to Firebase on startup â•â•â•
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
                        Logger.LogAction("PEER", $"ðŸ“¡ Startup: re-published URLs to Firebase (LAN={localUrl} CF={globalUrl})");
                    }
                }
                catch (Exception ex) { Logger.LogAction("PEER", $"Startup URL publish error: {ex.Message}"); }
            });

            // â•â•â• FIX 2: Try cached URLs first before Firebase â•â•â•
            // If we have locally cached URLs from last session, try them directly.
            // This provides instant reconnection even if Firebase URLs were cleaned.
            await TryCachedUrlsFirst();

            await DiscoverAndHandshake();

            _ = Task.Run(() => HeartbeatLoop(_cts.Token));
            _ = Task.Run(() => DiscoveryLoop(_cts.Token));

            Logger.LogAction("PEER", $"PeerManager running â€” {AliveCount}/{_peers.Count} peer(s) alive");
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

                Logger.LogAction("PEER", $"ðŸ“‹ Loaded {cache.Count} cached peer URL(s) from last session");

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
                        Logger.LogAction("PEER", $"ðŸ“‹ Trying cached URLs for {peer.DeviceName}: LAN={peer.LanUrl} CF={peer.CloudflareUrl}");
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

        public void Stop()
        {
            _cts.Cancel();
            foreach (var p in _peers.Values)
            {
                p.IsAlive = false;
                try { p.WsCts?.Cancel(); } catch { }
                try { p.LiveSocket?.Dispose(); } catch { }
            }
            Logger.LogAction("PEER", "PeerManager stopped.");
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private async Task DiscoverAndHandshake()
        {
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
                            Logger.LogAction("PEER", $"â­ï¸ Skipping unpaired device in Firebase: {name} ({devId}) â€” not in local paired list");

                            // Actively delete the ghost entry from Firebase
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    string deleteUrl = await CloudDiscoveryManager.AuthUrlPublic($"active_devices/{_myPairingKey}/{prop.Name}.json");
                                    using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                                    await delClient.DeleteAsync(deleteUrl);
                                    Logger.LogAction("PEER", $"ðŸ—‘ï¸ Deleted ghost device from Firebase: {name} ({prop.Name})");
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
                            Logger.LogAction("PEER", $"{name}: Cloudflare URL changed â†’ {cf}");
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

                // â•â•â• FIX 3: Send urlRequest whenever we have dead peers â•â•â•
                // Don't gate on _urlRequestSent â€” re-send every 60s if peers are still dead.
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
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

                // â•â•â• FIX 4: Write urlRequest under EACH dead peer's path â•â•â•
                // Previously wrote under our OWN device path â€” the other PC's SSE watcher
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
                Logger.LogAction("PEER", "ðŸ“¡ URL request signal sent + our URLs re-published");
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

            Logger.LogAction("PEER", $"ðŸ“¡ {requestingDeviceId} is requesting URLs â€” re-publishing ours...");

            string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
            string localUrl = CloudDiscoveryManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                _urlCleanedFromFirebase = false;
                await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                Logger.LogAction("PEER", $"ðŸ“¡ Re-published encrypted URLs for {requestingDeviceId}");
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

                Logger.LogAction("PEER", $"â­ï¸ Skipping unpaired device URL update: {deviceName} ({deviceId})");
                // Actively delete the ghost entry from Firebase
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string deleteUrl = await CloudDiscoveryManager.AuthUrlPublic($"active_devices/{_myPairingKey}/{deviceId}.json");
                        using var delClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        await delClient.DeleteAsync(deleteUrl);
                        Logger.LogAction("PEER", $"ðŸ—‘ï¸ Deleted ghost device from Firebase: {deviceName} ({deviceId})");
                    }
                    catch { }
                });
                return;
            }

            Logger.LogAction("PEER", $"ðŸ“¡ Target URL update for {deviceName}: LAN={lan} CF={cf}");

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

        private async Task Handshake(PeerConnection peer)
        {
            bool lanEnabled = SettingsManager.Current.EnableLocalLAN;
            Logger.LogAction("PEER", $"ðŸ”Œ Handshake {peer.DeviceName}: LAN={peer.LanUrl ?? "(empty)"} CF={peer.CloudflareUrl ?? "(empty)"} lanEnabled={lanEnabled}");
            
            // Priority 1: LAN (only if enabled)
            if (lanEnabled && await TryConnect(peer, peer.LanUrl, "LAN")) return;
            // Priority 2: Cloudflare
            if (await TryConnect(peer, peer.CloudflareUrl, "Cloudflare")) return;

            peer.IsAlive = false;
            peer.Transport = "offline";
            Logger.LogAction("PEER", $"âš ï¸ {peer.DeviceName} unreachable (LAN:{(lanEnabled ? "on" : "off")}) tried LAN={peer.LanUrl ?? "null"} CF={peer.CloudflareUrl ?? "null"}");
        }

        private async Task<bool> TryConnect(PeerConnection peer, string testUrl, string transport)
        {
            if (string.IsNullOrEmpty(testUrl)) return false;
            
            if (testUrl.Contains(","))
            {
                var urls = testUrl.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var url in urls)
                {
                    var trimmed = url.Trim();
                    if (await TryConnectSingle(peer, trimmed, transport))
                    {
                        return true;
                    }
                }
                return false;
            }

            return await TryConnectSingle(peer, testUrl.Trim(), transport);
        }

        private async Task<bool> TryConnectSingle(PeerConnection peer, string testUrl, string transport)
        {
            if (string.IsNullOrEmpty(testUrl)) return false;
            // Reject non-URL values like "offline", corrupted decryptions, etc.
            if (!testUrl.StartsWith("http://") && !testUrl.StartsWith("https://")) return false;
            try
            {
                int timeout = (transport == "LAN") ? HANDSHAKE_TIMEOUT_LAN_MS : HANDSHAKE_TIMEOUT_CF_MS;
                using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeout) };
                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(pk)) c.DefaultRequestHeaders.Add("X-Pairing-Key", pk);

                var r = await c.GetAsync($"{testUrl.TrimEnd('/')}/api/health");
                if (r.IsSuccessStatusCode)
                {
                    peer.ActiveUrl = testUrl;
                    peer.Transport = transport;
                    peer.IsAlive = true;
                    peer.LastSeen = DateTime.UtcNow;
                    peer.ConsecutiveFailures = 0;

                    // Parse rich health response for smart transport + version info
                    try
                    {
                        string healthJson = await r.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(healthJson) && healthJson.StartsWith("{"))
                        {
                            using var doc = JsonDocument.Parse(healthJson);
                            var root = doc.RootElement;

                            // Extract version for mismatch detection
                            if (root.TryGetProperty("version", out var ver))
                                peer.Version = ver.GetString() ?? "";

                            // Extract deviceType
                            if (root.TryGetProperty("deviceType", out var dtProp))
                                peer.DeviceType = dtProp.GetString() ?? "";

                            // Extract LAN URL from peer's health response (smart discovery)
                            // If we connected via Cloudflare but peer reports a LAN URL, save it for future LAN fallback
                            if (root.TryGetProperty("transport", out var tr))
                            {
                                if (tr.TryGetProperty("lan", out var lanProp))
                                {
                                    string peerLan = lanProp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(peerLan) && peerLan.StartsWith("http") && peerLan != peer.LanUrl)
                                    {
                                        peer.LanUrl = peerLan;
                                        Logger.LogAction("PEER", $"ðŸ” Discovered {peer.DeviceName} LAN URL from health: {peerLan}");
                                    }
                                }
                                if (tr.TryGetProperty("cloudflare", out var cfProp))
                                {
                                    string peerCf = cfProp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(peerCf) && peerCf.Contains("trycloudflare") && peerCf != peer.CloudflareUrl)
                                    {
                                        peer.CloudflareUrl = peerCf;
                                        Logger.LogAction("PEER", $"ðŸ” Discovered {peer.DeviceName} CF URL from health: {peerCf}");
                                    }
                                }
                            }

                            // Log version mismatch warning
                            string myVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
                            if (!string.IsNullOrEmpty(peer.Version) && peer.Version != myVersion)
                            {
                                Logger.LogAction("PEER", $"âš ï¸ Version mismatch: {peer.DeviceName} is on v{peer.Version}, we are on v{myVersion}");
                            }
                        }
                    }
                    catch { /* Health parsing is optional â€” connection is already confirmed */ }

                    Logger.LogAction("PEER", $"âœ… {peer.DeviceName} connected via {transport}: {testUrl}" +
                        (!string.IsNullOrEmpty(peer.Version) ? $" (v{peer.Version})" : ""));
                    PeerConnected?.Invoke(peer.DeviceId, transport);

                    // â•â•â• FIX 5: Cache URLs locally on successful connection â•â•â•
                    SaveUrlCache();

                    // Establish persistent WebSocket for instant liveness detection
                    _ = Task.Run(() => ConnectWebSocket(peer));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"{transport} handshake {peer.DeviceName} for URL '{testUrl}': {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Opens a persistent WebSocket to the peer.
        /// If it drops â†’ peer is instantly dead (no 50s heartbeat delay).
        /// Sends "ping" every 30s, expects "pong" back.
        /// </summary>
        private async Task ConnectWebSocket(PeerConnection peer)
        {
            // Close any existing WebSocket
            try { peer.WsCts?.Cancel(); } catch { }
            try { peer.LiveSocket?.Dispose(); } catch { }

            try
            {
                var ws = new ClientWebSocket();
                peer.WsCts = new CancellationTokenSource();
                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();

                // Convert http(s):// to ws(s)://
                string wsUrl = peer.ActiveUrl.TrimEnd('/')
                    .Replace("https://", "wss://")
                    .Replace("http://", "ws://");
                wsUrl += $"/ws/peer?key={Uri.EscapeDataString(pk)}&deviceId={Uri.EscapeDataString(_myDeviceId)}";

                ws.Options.SetRequestHeader("X-Pairing-Key", pk);
                ws.Options.SetRequestHeader("X-Device-Id", _myDeviceId);

                await ws.ConnectAsync(new Uri(wsUrl), peer.WsCts.Token);
                peer.LiveSocket = ws;
                Logger.LogAction("WS", $"ðŸ”— WebSocket connected to {peer.DeviceName} via {peer.Transport}");

                // Monitor the WebSocket â€” when it drops, peer is dead
                await MonitorWebSocket(peer);
            }
            catch (Exception ex)
            {
                Logger.LogAction("WS", $"WebSocket to {peer.DeviceName} failed: {ex.Message}");
                // WebSocket is optional â€” HTTP heartbeat still works as fallback
            }
        }

        /// <summary>
        /// Sends ping every 30s over the WebSocket. If the connection drops,
        /// we detect it INSTANTLY and mark the peer as dead.
        /// </summary>
        private async Task MonitorWebSocket(PeerConnection peer)
        {
            var ws = peer.LiveSocket;
            var cts = peer.WsCts;
            if (ws == null || cts == null) return;

            var buf = new byte[64];
            try
            {
                while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                {
                    // Send ping
                    byte[] ping = Encoding.UTF8.GetBytes("ping");
                    await peer.SendSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(ping), WebSocketMessageType.Text, true, cts.Token);
                    }
                    finally
                    {
                        peer.SendSemaphore.Release();
                    }

                    // Wait for pong (with 15s timeout)
                    var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeoutCts.CancelAfter(15_000);
                    try
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), timeoutCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        peer.LastSeen = DateTime.UtcNow;
                        peer.ConsecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
                    {
                        // Pong timeout â€” peer may be dead
                        Logger.LogAction("WS", $"âš ï¸ {peer.DeviceName} WebSocket pong timeout");
                        break;
                    }

                    // Wait 30s before next ping
                    await Task.Delay(30_000, cts.Token);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { return; } // Normal shutdown
            catch (Exception ex)
            {
                Logger.LogAction("WS", $"{peer.DeviceName} WebSocket monitor error: {ex.Message}");
            }

            // WebSocket died â†’ if peer was still marked alive, verify via HTTP health check
            if (peer.IsAlive)
            {
                Logger.LogAction("WS", $"âš ï¸ {peer.DeviceName} WebSocket dropped â€” verifying health via HTTP...");
                
                bool stillAlive = false;
                try
                {
                    using var checkClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(3000) };
                    string testUrl = $"{peer.ActiveUrl.TrimEnd('/')}/api/health";
                    string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                    if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();
                    if (!string.IsNullOrEmpty(pk)) checkClient.DefaultRequestHeaders.Add("X-Pairing-Key", pk);

                    var r = await checkClient.GetAsync(testUrl);
                    if (r.IsSuccessStatusCode)
                    {
                        stillAlive = true;
                    }
                }
                catch { }

                if (stillAlive)
                {
                    Logger.LogAction("WS", $"â„¹ï¸ {peer.DeviceName} is still reachable via HTTP. Keeping connection alive.");
                    try { peer.WsCts?.Cancel(); } catch { }
                    try { peer.LiveSocket?.Dispose(); } catch { }
                    peer.LiveSocket = null;
                }
                else
                {
                    Logger.LogAction("WS", $"ðŸ’€ {peer.DeviceName} WebSocket dropped and HTTP health check failed â€” instant death detection");
                    await HandlePeerDeath(peer);
                }
            }
        }

        /// <summary>
        /// All peers confirmed alive â†’ write tick, then delete URLs from Firebase.
        /// URLs are deleted immediately for SECURITY â€” they expose clipboard data via Cloudflare.
        /// When a peer restarts, it uses the urlRequest signal to ask online peers to re-publish.
        /// </summary>
        private async Task ConfirmAndCleanup()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                // Write confirmation tick: "I have connected to all my peers"
                string tickUrl = await CloudDiscoveryManager.AuthUrlPublic(
                    $"active_devices/{_myPairingKey}/{_myDeviceId}/confirmedPeers.json");
                var peerIds = _peers.Values.Where(p => p.IsAlive).Select(p => p.DeviceId).ToList();
                string tickJson = JsonSerializer.Serialize(new
                {
                    confirmedAt = NetworkClock.UtcNowMs,
                    peers = peerIds
                });
                await client.PutAsync(tickUrl, new StringContent(tickJson, Encoding.UTF8, "application/json"));
                Logger.LogAction("PEER", $"âœ… Confirmation tick written â€” {peerIds.Count} peer(s) confirmed");

                // â•â•â• FIX 6: DON'T delete URLs from Firebase â•â•â•
                // The old code deleted GlobalUrl/LocalIp/Url from Firebase for "security".
                // But this caused a critical bug: after app restart, Firebase had no URLs
                // and PeerManager couldn't reconnect. The URLs are already encrypted,
                // so leaving them is safe. Only clear stale urlRequest signals.
                try
                {
                    string rUrl = await CloudDiscoveryManager.AuthUrlPublic(
                        $"active_devices/{_myPairingKey}/{_myDeviceId}/urlRequest.json");
                    await client.DeleteAsync(rUrl);
                }
                catch { }

                _urlCleanedFromFirebase = true;
                _urlRequestSent = false;
                Logger.LogAction("PEER", "âœ… Peer confirmation written (URLs preserved in Firebase for reconnection)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"Confirm/cleanup error: {ex.Message}");
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    }
}

