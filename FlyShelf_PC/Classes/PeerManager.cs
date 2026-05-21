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

namespace FlyShelf.Classes
{
    /// <summary>
    /// v5 PeerManager — Pure P2P engine.
    /// 
    /// Firebase = phone book ONLY. Stores device URLs, never file data.
    /// Flow: Discover → Handshake → Confirm tick → Talk direct. URLs persist in Firebase as a "phone book".
    /// All text/files flow device-to-device via LAN or Cloudflare. 5s heartbeat.
    /// </summary>
    public class PeerManager
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

        // ═══ Local URL cache — survives app restart ═══
        private static readonly string _urlCacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "peer_urls.json");

        // ═══ Config ═══
        private const int HEARTBEAT_MS = 5_000;            // 5s heartbeat (fast LAN detection)
        private const int HEARTBEAT_TIMEOUT_MS = 4_000;    // 4s timeout per ping
        private const int MAX_FAILURES = 3;                // 3 misses = dead (quick failover)
        private const int DISCOVERY_MS = 15_000;           // Re-scan Firebase every 15s for reconnection
        private const int HANDSHAKE_TIMEOUT_LAN_MS = 2_000;   // 2s for LAN
        private const int HANDSHAKE_TIMEOUT_CF_MS = 8_000;    // 8s for Cloudflare tunnels

        // ═══ Events ═══
        public event Action<string, string>? PeerConnected;     // (deviceId, transport)
        public event Action<string>? PeerDisconnected;          // (deviceId)
        public event Action<string, string>? TransportSwitched; // (deviceId, newTransport)

        // ═══ Shared HttpClient — connection pooling eliminates TLS re-handshake per request ═══
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

            // ═══ FIX 1: Re-publish our own URLs to Firebase on startup ═══
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

            // ═══ FIX 2: Try cached URLs first before Firebase ═══
            // If we have locally cached URLs from last session, try them directly.
            // This provides instant reconnection even if Firebase URLs were cleaned.
            await TryCachedUrlsFirst();

            await DiscoverAndHandshake();

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

        // ═══════════════════════════════════════════════════════════════

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
                            Logger.LogAction("PEER", $"⏭️ Skipping unpaired device in Firebase: {name} ({devId}) — not in local paired list");

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

                // ═══ FIX 3: Send urlRequest whenever we have dead peers ═══
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

        /// <summary>
        /// Safely decrypt a URL. Returns the original string if decryption fails
        /// (meaning it wasn't encrypted, or it's already a plaintext URL).
        /// </summary>
        private static string DecryptUrlSafe(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            // If it already looks like a URL, it's not encrypted
            if (value.StartsWith("http://") || value.StartsWith("https://")) return value;
            try
            {
                string? decrypted = SyncCrypto.Decrypt(value);
                return !string.IsNullOrEmpty(decrypted) ? decrypted : value;
            }
            catch { return value; }
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

                // ═══ FIX 4: Write urlRequest under EACH dead peer's path ═══
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

                Logger.LogAction("PEER", $"⏭️ Skipping unpaired device URL update: {deviceName} ({deviceId})");
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

        private async Task Handshake(PeerConnection peer)
        {
            bool lanEnabled = SettingsManager.Current.EnableLocalLAN;
            Logger.LogAction("PEER", $"🔌 Handshake {peer.DeviceName}: LAN={peer.LanUrl ?? "(empty)"} CF={peer.CloudflareUrl ?? "(empty)"} lanEnabled={lanEnabled}");
            
            // Priority 1: LAN (only if enabled)
            if (lanEnabled && await TryConnect(peer, peer.LanUrl, "LAN")) return;
            // Priority 2: Cloudflare
            if (await TryConnect(peer, peer.CloudflareUrl, "Cloudflare")) return;

            peer.IsAlive = false;
            peer.Transport = "offline";
            Logger.LogAction("PEER", $"⚠️ {peer.DeviceName} unreachable (LAN:{(lanEnabled ? "on" : "off")}) tried LAN={peer.LanUrl ?? "null"} CF={peer.CloudflareUrl ?? "null"}");
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
                                        Logger.LogAction("PEER", $"🔍 Discovered {peer.DeviceName} LAN URL from health: {peerLan}");
                                    }
                                }
                                if (tr.TryGetProperty("cloudflare", out var cfProp))
                                {
                                    string peerCf = cfProp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(peerCf) && peerCf.Contains("trycloudflare") && peerCf != peer.CloudflareUrl)
                                    {
                                        peer.CloudflareUrl = peerCf;
                                        Logger.LogAction("PEER", $"🔍 Discovered {peer.DeviceName} CF URL from health: {peerCf}");
                                    }
                                }
                            }

                            // Log version mismatch warning
                            string myVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
                            if (!string.IsNullOrEmpty(peer.Version) && peer.Version != myVersion)
                            {
                                Logger.LogAction("PEER", $"⚠️ Version mismatch: {peer.DeviceName} is on v{peer.Version}, we are on v{myVersion}");
                            }
                        }
                    }
                    catch { /* Health parsing is optional — connection is already confirmed */ }

                    Logger.LogAction("PEER", $"✅ {peer.DeviceName} connected via {transport}: {testUrl}" +
                        (!string.IsNullOrEmpty(peer.Version) ? $" (v{peer.Version})" : ""));
                    PeerConnected?.Invoke(peer.DeviceId, transport);

                    // ═══ FIX 5: Cache URLs locally on successful connection ═══
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
        /// If it drops → peer is instantly dead (no 50s heartbeat delay).
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
                Logger.LogAction("WS", $"🔗 WebSocket connected to {peer.DeviceName} via {peer.Transport}");

                // Monitor the WebSocket — when it drops, peer is dead
                await MonitorWebSocket(peer);
            }
            catch (Exception ex)
            {
                Logger.LogAction("WS", $"WebSocket to {peer.DeviceName} failed: {ex.Message}");
                // WebSocket is optional — HTTP heartbeat still works as fallback
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
                        // Pong timeout — peer may be dead
                        Logger.LogAction("WS", $"⚠️ {peer.DeviceName} WebSocket pong timeout");
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

            // WebSocket died → if peer was still marked alive, trigger death
            if (peer.IsAlive)
            {
                Logger.LogAction("WS", $"💀 {peer.DeviceName} WebSocket dropped — instant death detection");
                await HandlePeerDeath(peer);
            }
        }

        /// <summary>
        /// All peers confirmed alive → write tick, then delete URLs from Firebase.
        /// URLs are deleted immediately for SECURITY — they expose clipboard data via Cloudflare.
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
                Logger.LogAction("PEER", $"✅ Confirmation tick written — {peerIds.Count} peer(s) confirmed");

                // ═══ FIX 6: DON'T delete URLs from Firebase ═══
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
                Logger.LogAction("PEER", "✅ Peer confirmation written (URLs preserved in Firebase for reconnection)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"Confirm/cleanup error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PUSH — Send data directly to peers (never Firebase)
        // ═══════════════════════════════════════════════════════════════

        public async Task<int> PushTextToAllPeers(string text, string title, string itemType = "Text")
        {
            int delivered = 0;
            var alive = _peers.Values.Where(p => p.IsAlive).ToList();
            if (alive.Count == 0) return 0;

            await Task.WhenAll(alive.Select(async peer =>
            {
                bool sent = await TrySendText(peer, text, title, itemType);
                if (!sent)
                {
                    // First attempt failed — peer's tunnel may have died. Reconnect + retry once.
                    Logger.LogAction("PEER", $"⚡ Text delivery failed — reconnecting {peer.DeviceName}...");
                    peer.IsAlive = false;
                    peer.ConsecutiveFailures = 0;
                    _urlCleanedFromFirebase = false;
                    _urlRequestSent = false;
                    await DiscoverAndHandshake();
                    if (peer.IsAlive)
                    {
                        sent = await TrySendText(peer, text, title, itemType);
                        if (sent) Logger.LogAction("PEER", $"✅ Text delivered on retry to {peer.DeviceName}");
                    }
                }
                if (sent) Interlocked.Increment(ref delivered);
            }));
            return delivered;
        }

        private async Task<bool> TrySendText(PeerConnection peer, string text, string title, string itemType)
        {
            // WebSocket Direct Send Fallback Path
            if (peer.LiveSocket != null && peer.LiveSocket.State == WebSocketState.Open)
            {
                try
                {
                    var envelope = JsonSerializer.Serialize(new
                    {
                        type = "SyncText",
                        itemType = itemType,
                        title = title,
                        data = text,
                        sourceDeviceId = _myDeviceId,
                        sourceDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                        timestamp = NetworkClock.UtcNowMs
                    });

                    byte[] envelopeBytes = Encoding.UTF8.GetBytes(envelope);
                    
                    await peer.SendSemaphore.WaitAsync();
                    try
                    {
                        await peer.LiveSocket.SendAsync(new ArraySegment<byte>(envelopeBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    finally
                    {
                        peer.SendSemaphore.Release();
                    }

                    peer.LastSeen = DateTime.UtcNow;
                    peer.ConsecutiveFailures = 0;
                    Logger.LogAction("PEER", $"→ Text '{title}' to {peer.DeviceName} via WebSocket direct");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER", $"WebSocket Direct Text to {peer.DeviceName} failed: {ex.Message}. Falling back to HTTP...");
                }
            }

            try
            {
                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();
                var payload = JsonSerializer.Serialize(new
                {
                    type = itemType, title, data = text,
                    sourceDeviceId = _myDeviceId,
                    sourceDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                    timestamp = NetworkClock.UtcNowMs
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{peer.ActiveUrl.TrimEnd('/')}/api/sync_text");
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var resp = await _sharedClient.SendAsync(req, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    peer.LastSeen = DateTime.UtcNow;
                    peer.ConsecutiveFailures = 0;
                    Logger.LogAction("PEER", $"→ Text to {peer.DeviceName} via {peer.Transport}");
                    return true;
                }
                Logger.LogAction("PEER", $"Text to {peer.DeviceName}: HTTP {(int)resp.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"Text to {peer.DeviceName} failed: {ex.Message}");
                HandlePeerFailure(peer, ex.Message);
                return false;
            }
        }

        public async Task<int> PushFileToAllPeers(string filePath, string title, string itemType = "Image")
        {
            int delivered = 0;
            var alive = _peers.Values.Where(p => p.IsAlive).ToList();
            if (alive.Count == 0) return 0;

            await Task.WhenAll(alive.Select(async peer =>
            {
                bool sent = await TrySendFile(peer, filePath, title, itemType);
                if (!sent)
                {
                    // First attempt failed — peer's tunnel may have died. Reconnect + retry once.
                    Logger.LogAction("PEER", $"⚡ File delivery failed — reconnecting {peer.DeviceName}...");
                    peer.IsAlive = false;
                    peer.ConsecutiveFailures = 0;
                    _urlCleanedFromFirebase = false;
                    _urlRequestSent = false;
                    await DiscoverAndHandshake();
                    if (peer.IsAlive)
                    {
                        sent = await TrySendFile(peer, filePath, title, itemType);
                        if (sent) Logger.LogAction("PEER", $"✅ File delivered on retry to {peer.DeviceName}");
                    }
                }
                if (sent) Interlocked.Increment(ref delivered);
            }));
            return delivered;
        }

        private async Task<bool> TrySendFile(PeerConnection peer, string filePath, string title, string itemType)
        {
            try
            {
                Interlocked.Increment(ref peer.ActiveTransfers);

                // WebSocket Direct File Send Path
                if (peer.LiveSocket != null && peer.LiveSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        string wsFileName = Path.GetFileName(filePath);
                        long wsFileSize = new FileInfo(filePath).Length;

                        // 1. Send the metadata start frame
                        var startEnvelope = JsonSerializer.Serialize(new
                        {
                            type = "SyncFileStart",
                            fileName = wsFileName,
                            fileSize = wsFileSize,
                            itemType = itemType,
                            title = title,
                            sourceDeviceId = _myDeviceId,
                            sourceDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                            timestamp = NetworkClock.UtcNowMs
                        });

                        byte[] startBytes = Encoding.UTF8.GetBytes(startEnvelope);

                        await peer.SendSemaphore.WaitAsync();
                        try
                        {
                            // Send start frame
                            await peer.LiveSocket.SendAsync(new ArraySegment<byte>(startBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                            // 2. Stream the file in binary chunks (zero-allocation renting)
                            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                byte[] rentBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(262144); // 256KB chunks
                                try
                                {
                                    int readBytes;
                                    long totalSent = 0;
                                    while ((readBytes = await fs.ReadAsync(rentBuffer, 0, rentBuffer.Length)) > 0)
                                    {
                                        totalSent += readBytes;
                                        bool isEnd = totalSent >= wsFileSize;
                                        await peer.LiveSocket.SendAsync(new ArraySegment<byte>(rentBuffer, 0, readBytes), WebSocketMessageType.Binary, isEnd, CancellationToken.None);
                                    }
                                }
                                finally
                                {
                                    System.Buffers.ArrayPool<byte>.Shared.Return(rentBuffer);
                                }
                            }
                        }
                        finally
                        {
                            peer.SendSemaphore.Release();
                        }

                        peer.LastSeen = DateTime.UtcNow;
                        peer.ConsecutiveFailures = 0;
                        Logger.LogAction("PEER", $"→ File '{title}' to {peer.DeviceName} via WebSocket direct");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("PEER", $"WebSocket Direct File to {peer.DeviceName} failed: {ex.Message}. Falling back to HTTP...");
                    }
                }

                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();
                string actualFileName = Path.GetFileName(filePath);
                bool isCf = peer.Transport == "Cloudflare";
                long fileSize = new FileInfo(filePath).Length;

                // ═══ CLOUDFLARE + LARGE FILE → parallel chunked upload ═══
                // Split into 512KB chunks, upload 4 in parallel. Each chunk goes through
                // a separate CF connection, bypassing per-connection throughput limits.
                if (isCf && fileSize > 256 * 1024)
                {
                    return await TrySendFileChunked(peer, filePath, actualFileName, title, itemType, pk);
                }

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{peer.ActiveUrl.TrimEnd('/')}/api/sync_file");
                if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);
                req.Headers.TryAddWithoutValidation("X-Item-Type", itemType);
                req.Headers.TryAddWithoutValidation("X-Source-Device", SettingsManager.Current.DeviceName ?? "");
                req.Headers.TryAddWithoutValidation("X-Source-DeviceId", _myDeviceId);
                req.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(actualFileName));

                if (isCf)
                {
                    // Small file via CF — raw binary (skip multipart overhead)
                    var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var content = new StreamContent(fs);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    req.Content = content;
                }
                else
                {
                    // ═══ LAN PATH — multipart ═══
                    var form = new MultipartFormDataContent();
                    var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    form.Add(new StreamContent(fs), "file", actualFileName);
                    form.Add(new StringContent(title), "title");
                    form.Add(new StringContent(itemType), "type");
                    req.Content = form;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var resp = await _sharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    peer.LastSeen = DateTime.UtcNow;
                    peer.ConsecutiveFailures = 0;
                    Logger.LogAction("PEER", $"→ File '{title}' to {peer.DeviceName} via {peer.Transport}");
                    return true;
                }
                Logger.LogAction("PEER", $"File to {peer.DeviceName}: HTTP {(int)resp.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"File to {peer.DeviceName} failed: {ex.Message}");
                HandlePeerFailure(peer, ex.Message);
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref peer.ActiveTransfers);
            }
        }

        /// <summary>
        /// Parallel chunked upload for Cloudflare. Splits file into 512KB chunks and sends
        /// up to 4 in parallel, then finalizes. This bypasses per-connection throughput limits.
        /// </summary>
        private const int CHUNK_SIZE = 512 * 1024; // 512KB per chunk
        private const int MAX_PARALLEL_CHUNKS = 4;  // 4 concurrent uploads

        private async Task<bool> TrySendFileChunked(PeerConnection peer, string filePath, string fileName, string title, string itemType, string pk)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string sessionId = Guid.NewGuid().ToString("N");
            string baseUrl = peer.ActiveUrl.TrimEnd('/');

            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            int totalChunks = (int)Math.Ceiling((double)fileBytes.Length / CHUNK_SIZE);

            Logger.LogAction("PEER", $"⚡ CF chunked: {fileName} ({fileBytes.Length / 1024}KB) → {totalChunks} chunks × {CHUNK_SIZE / 1024}KB, {MAX_PARALLEL_CHUNKS} parallel");

            // Upload chunks in parallel batches
            var semaphore = new SemaphoreSlim(MAX_PARALLEL_CHUNKS);
            var tasks = new List<Task<bool>>();

            for (int i = 0; i < totalChunks; i++)
            {
                int chunkIndex = i;
                int offset = chunkIndex * CHUNK_SIZE;
                int length = Math.Min(CHUNK_SIZE, fileBytes.Length - offset);

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/upload_chunk");
                        if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);
                        req.Headers.TryAddWithoutValidation("X-Upload-Session", sessionId);
                        req.Headers.TryAddWithoutValidation("X-Chunk-Index", chunkIndex.ToString());

                        var chunkData = new byte[length];
                        Buffer.BlockCopy(fileBytes, offset, chunkData, 0, length);
                        req.Content = new ByteArrayContent(chunkData);
                        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        var resp = await _sharedClient.SendAsync(req, cts.Token);
                        return resp.IsSuccessStatusCode;
                    }
                    finally { semaphore.Release(); }
                }));
            }

            var results = await Task.WhenAll(tasks);
            int successCount = results.Count(r => r);

            if (successCount != totalChunks)
            {
                Logger.LogAction("PEER", $"CF chunked: only {successCount}/{totalChunks} chunks uploaded — aborting");
                return false;
            }

            // Finalize: tell receiver to reassemble
            using var finReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/upload_finalize");
            if (!string.IsNullOrEmpty(pk)) finReq.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);
            finReq.Headers.TryAddWithoutValidation("X-Upload-Session", sessionId);
            finReq.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(fileName));
            finReq.Headers.TryAddWithoutValidation("X-Total-Chunks", totalChunks.ToString());
            finReq.Headers.TryAddWithoutValidation("X-Source-Device", SettingsManager.Current.DeviceName ?? "");
            finReq.Headers.TryAddWithoutValidation("X-Item-Type", itemType);
            finReq.Content = new StringContent("", Encoding.UTF8, "application/json");

            using var finCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var finResp = await _sharedClient.SendAsync(finReq, finCts.Token);

            sw.Stop();
            if (finResp.IsSuccessStatusCode)
            {
                peer.LastSeen = DateTime.UtcNow;
                peer.ConsecutiveFailures = 0;
                double speed = fileBytes.Length / 1024.0 / (sw.ElapsedMilliseconds / 1000.0);
                Logger.LogAction("PEER", $"→ File '{title}' to {peer.DeviceName} via CF chunked ({sw.ElapsedMilliseconds}ms, {speed:F0} KB/s)");
                return true;
            }
            Logger.LogAction("PEER", $"CF chunked finalize failed: HTTP {(int)finResp.StatusCode}");
            return false;
        }

        // ═══════════════════════════════════════════════════════════════
        // HEARTBEAT — HTTP fallback when WebSocket is not available
        // WebSocket provides instant death detection; HTTP heartbeat is backup only.
        // ═══════════════════════════════════════════════════════════════

        private async Task HeartbeatLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(HEARTBEAT_MS, ct); } catch { break; }

                foreach (var peer in _peers.Values.Where(p => p.IsAlive).ToList())
                {
                    // Skip if WebSocket is monitoring this peer (instant detection)
                    if (peer.LiveSocket?.State == WebSocketState.Open)
                    {
                        peer.ConsecutiveFailures = 0;
                        continue;
                    }

                    // Skip heartbeat if peer has an active file transfer in progress
                    // The transfer itself proves the connection is alive
                    if (peer.ActiveTransfers > 0)
                    {
                        peer.ConsecutiveFailures = 0;
                        peer.LastSeen = DateTime.UtcNow;
                        continue;
                    }

                    try
                    {
                        using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(HEARTBEAT_TIMEOUT_MS) };
                        var r = await c.GetAsync($"{peer.ActiveUrl.TrimEnd('/')}/api/health");
                        if (r.IsSuccessStatusCode)
                        {
                            peer.ConsecutiveFailures = 0;
                            peer.LastSeen = DateTime.UtcNow;
                        }
                        else
                        {
                            peer.ConsecutiveFailures++;
                        }
                    }
                    catch
                    {
                        peer.ConsecutiveFailures++;
                    }

                    if (peer.ConsecutiveFailures >= 3)
                        Logger.LogAction("PEER", $"Heartbeat {peer.DeviceName}: {peer.ConsecutiveFailures}/{MAX_FAILURES}");

                    if (peer.ConsecutiveFailures >= MAX_FAILURES)
                        await HandlePeerDeath(peer);
                }
            }
        }

        private async Task DiscoveryLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(DISCOVERY_MS, ct); } catch { break; }

                // Only re-discover if we have dead peers (need new URLs)
                bool hasDeadPeers = _peers.Values.Any(p => !p.IsAlive);
                if (hasDeadPeers || _peers.IsEmpty)
                {
                    _urlCleanedFromFirebase = false; // Allow re-registration
                    _urlRequestSent = false;         // Allow fresh URL requests
                    await DiscoverAndHandshake();
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // RECONNECT — Transport switching + Firebase re-watch
        // ═══════════════════════════════════════════════════════════════

        private async Task HandlePeerDeath(PeerConnection peer)
        {
            string old = peer.Transport;
            peer.IsAlive = false;
            peer.ConsecutiveFailures = 0;

            // Close WebSocket
            try { peer.WsCts?.Cancel(); } catch { }
            try { peer.LiveSocket?.Dispose(); } catch { }
            peer.LiveSocket = null;

            Logger.LogAction("PEER", $"💀 {peer.DeviceName} dead (was {old})");;

            // Try other transport
            bool lanEnabled = SettingsManager.Current.EnableLocalLAN;
            if (old == "LAN" && await TryConnect(peer, peer.CloudflareUrl, "Cloudflare"))
            {
                TransportSwitched?.Invoke(peer.DeviceId, "Cloudflare");
                return;
            }
            if (old == "Cloudflare" && lanEnabled && await TryConnect(peer, peer.LanUrl, "LAN"))
            {
                TransportSwitched?.Invoke(peer.DeviceId, "LAN");
                return;
            }

            // Both dead → re-register our URL in Firebase so peer can find us when it comes back
            PeerDisconnected?.Invoke(peer.DeviceId);
            _urlCleanedFromFirebase = false; // Allow Firebase re-registration

            // Push our current URL back to Firebase for the returning peer
            string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
            string localUrl = CloudDiscoveryManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                Logger.LogAction("PEER", $"Re-registered URLs in Firebase for {peer.DeviceName} to find us");
            }
        }

        private void HandlePeerFailure(PeerConnection peer, string msg)
        {
            bool fatal = msg.Contains("No such host") || msg.Contains("refused");
            if (fatal) { peer.ConsecutiveFailures = MAX_FAILURES; _ = HandlePeerDeath(peer); }
            else peer.ConsecutiveFailures++;
        }

        public async Task RefreshPeer(string deviceId)
        {
            if (_peers.TryGetValue(deviceId, out var p)) { p.IsAlive = false; p.ConsecutiveFailures = 0; }
            await DiscoverAndHandshake();
        }

        /// <summary>
        /// Force Sync: Re-publish own URL to Firebase, mark all peers dead, re-discover fresh URLs, reconnect.
        /// Called from UI "Force Sync" button.
        /// </summary>
        public async Task ForceResync()
        {
            Logger.LogAction("PEER", "═══ FORCE SYNC triggered ═══");

            // 1. Re-publish our own URL to Firebase so other devices can find us
            string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
            string localUrl = CloudDiscoveryManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                Logger.LogAction("PEER", $"Re-published our URLs → LAN={localUrl} CF={globalUrl}");
            }

            // 2. Reset all peers to dead — fresh start
            foreach (var p in _peers.Values)
            {
                p.IsAlive = false;
                p.ConsecutiveFailures = 0;
                try { p.WsCts?.Cancel(); } catch { }
                try { p.LiveSocket?.Dispose(); } catch { }
                p.LiveSocket = null;
            }

            // 3. Clear cleanup flag so we can re-fetch URLs from Firebase
            _urlCleanedFromFirebase = false;
            _urlRequestSent = false;

            // 4. Full discovery + handshake cycle
            await DiscoverAndHandshake();

            Logger.LogAction("PEER", $"Force sync complete — {AliveCount}/{_peers.Count} peer(s) alive");
        }

        public List<PeerStatus> GetPeerStatuses() => _peers.Values.Select(p => new PeerStatus
        {
            DeviceId = p.DeviceId, DeviceName = p.DeviceName, Transport = p.Transport,
            IsAlive = p.IsAlive, LastSeen = p.LastSeen, ActiveUrl = p.ActiveUrl,
            LanUrl = p.LanUrl, CloudflareUrl = p.CloudflareUrl,
            IsWebSocketActive = p.LiveSocket?.State == System.Net.WebSockets.WebSocketState.Open
        }).ToList();
    }

    public class PeerConnection
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string LanUrl { get; set; } = "";
        public string CloudflareUrl { get; set; } = "";
        public string ActiveUrl { get; set; } = "";
        public string Transport { get; set; } = "unknown";
        public string Version { get; set; } = "";   // Peer's app version from /api/health
        public bool IsAlive { get; set; } = false;
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        public int ConsecutiveFailures { get; set; } = 0;
        public int ActiveTransfers = 0; // Interlocked counter — heartbeat skips when > 0
        public System.Net.WebSockets.ClientWebSocket? LiveSocket { get; set; }
        public CancellationTokenSource? WsCts { get; set; }
        public System.Threading.SemaphoreSlim SendSemaphore { get; } = new System.Threading.SemaphoreSlim(1, 1);
    }

    public class PeerStatus
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Transport { get; set; } = "";
        public bool IsAlive { get; set; }
        public bool IsWebSocketActive { get; set; }
        public DateTime LastSeen { get; set; }
        public string ActiveUrl { get; set; } = "";
        public string LanUrl { get; set; } = "";
        public string CloudflareUrl { get; set; } = "";
    }

    /// <summary>UI-bindable peer status for the HubWindow paired devices list.</summary>
    public class PeerStatusItem
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public bool IsAlive { get; set; }
        public string Transport { get; set; } = "offline";
        public bool IsLanActive { get; set; }
        public bool IsCloudActive { get; set; }
        public bool IsWebSocketActive { get; set; }
        public string StatusText { get; set; } = "Offline";
        public string ActiveUrl { get; set; } = "";
    }
    /// <summary>Cached peer URLs for local persistence across restarts.</summary>
    public class CachedPeerUrls
    {
        public string? DeviceName { get; set; }
        public string? LanUrl { get; set; }
        public string? CloudflareUrl { get; set; }
        public DateTime LastSeen { get; set; }
    }
}


