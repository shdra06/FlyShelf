using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// v5 PeerManager — Pure P2P engine.
    /// 
    /// Firebase = phone book ONLY. Stores device URLs, never file data.
    /// Flow: Discover → Handshake → Confirm tick → Delete URL from Firebase → Talk direct.
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

        // ═══ Config ═══
        private const int HEARTBEAT_MS = 5_000;          // 5s heartbeat
        private const int HEARTBEAT_TIMEOUT_MS = 4_000;   // 4s timeout per ping
        private const int MAX_FAILURES = 3;               // 3 misses = dead
        private const int DISCOVERY_MS = 30_000;          // Re-scan Firebase every 30s for reconnection
        private const int HANDSHAKE_TIMEOUT_MS = 5_000;

        // ═══ Events ═══
        public event Action<string, string>? PeerConnected;     // (deviceId, transport)
        public event Action<string>? PeerDisconnected;          // (deviceId)
        public event Action<string, string>? TransportSwitched; // (deviceId, newTransport)

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

            await DiscoverAndHandshake();

            _ = Task.Run(() => HeartbeatLoop(_cts.Token));
            _ = Task.Run(() => DiscoveryLoop(_cts.Token));

            Logger.LogAction("PEER", $"PeerManager running — {AliveCount}/{_peers.Count} peer(s) alive");
        }

        public void Stop()
        {
            _cts.Cancel();
            foreach (var p in _peers.Values) p.IsAlive = false;
            Logger.LogAction("PEER", "PeerManager stopped.");
        }

        // ═══════════════════════════════════════════════════════════════
        // DISCOVER → HANDSHAKE → CONFIRM → CLEANUP
        // ═══════════════════════════════════════════════════════════════

        private async Task DiscoverAndHandshake()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string url = await FirebaseSyncManager.AuthUrlPublic($"active_devices/{_myPairingKey}.json");
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return;

                string json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                int totalPeers = 0;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var dev = prop.Value;
                    string devId = dev.TryGetProperty("DeviceId", out var di) ? di.GetString() ?? prop.Name : prop.Name;
                    if (devId == _myDeviceId) continue;

                    totalPeers++;
                    string name = dev.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? "" : "";
                    string lan = dev.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                    string cf = dev.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                    string direct = dev.TryGetProperty("Url", out var du) ? du.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(lan) && !string.IsNullOrEmpty(direct) && !direct.Contains("trycloudflare"))
                        lan = direct;

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

                // CONFIRM + CLEANUP: If all peers are alive, confirm and delete our URL
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

        private async Task Handshake(PeerConnection peer)
        {
            bool lanEnabled = SettingsManager.Current.EnableLocalLAN;
            
            // Priority 1: LAN (only if enabled)
            if (lanEnabled && await TryConnect(peer, peer.LanUrl, "LAN")) return;
            // Priority 2: Cloudflare
            if (await TryConnect(peer, peer.CloudflareUrl, "Cloudflare")) return;

            peer.IsAlive = false;
            peer.Transport = "offline";
            Logger.LogAction("PEER", $"⚠️ {peer.DeviceName} unreachable (LAN:{(lanEnabled ? "on" : "off")})");
        }

        private async Task<bool> TryConnect(PeerConnection peer, string testUrl, string transport)
        {
            if (string.IsNullOrEmpty(testUrl)) return false;
            try
            {
                using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(HANDSHAKE_TIMEOUT_MS) };
                string pk = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(pk)) c.DefaultRequestHeaders.Add("X-Pairing-Key", pk);

                var r = await c.GetAsync($"{testUrl.TrimEnd('/')}/api/health");
                if (r.IsSuccessStatusCode)
                {
                    peer.ActiveUrl = testUrl;
                    peer.Transport = transport;
                    peer.IsAlive = true;
                    peer.LastSeen = DateTime.UtcNow;
                    peer.ConsecutiveFailures = 0;
                    Logger.LogAction("PEER", $"✅ {peer.DeviceName} connected via {transport}: {testUrl}");
                    PeerConnected?.Invoke(peer.DeviceId, transport);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER", $"{transport} handshake {peer.DeviceName}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// All peers confirmed alive → write tick to Firebase, then delete our URL.
        /// Firebase URL is only needed for initial discovery. Once peers are talking, delete it.
        /// </summary>
        private async Task ConfirmAndCleanup()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                // Write confirmation tick: "I have connected to all my peers"
                string tickUrl = await FirebaseSyncManager.AuthUrlPublic(
                    $"active_devices/{_myPairingKey}/{_myDeviceId}/confirmedPeers.json");
                var peerIds = _peers.Values.Where(p => p.IsAlive).Select(p => p.DeviceId).ToList();
                string tickJson = JsonSerializer.Serialize(new
                {
                    confirmedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    peers = peerIds
                });
                await client.PutAsync(tickUrl, new StringContent(tickJson, Encoding.UTF8, "application/json"));
                Logger.LogAction("PEER", $"✅ Confirmation tick written — {peerIds.Count} peer(s) confirmed");

                // Check if ALL devices in the pairing group have confirmed
                string allUrl = await FirebaseSyncManager.AuthUrlPublic($"active_devices/{_myPairingKey}.json");
                var allResp = await client.GetAsync(allUrl);
                if (!allResp.IsSuccessStatusCode) return;

                string allJson = await allResp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(allJson) || allJson == "null") return;

                using var allDoc = JsonDocument.Parse(allJson);
                int totalDevices = 0, confirmedDevices = 0;
                foreach (var prop in allDoc.RootElement.EnumerateObject())
                {
                    totalDevices++;
                    if (prop.Value.TryGetProperty("confirmedPeers", out _))
                        confirmedDevices++;
                }

                if (confirmedDevices >= totalDevices)
                {
                    // ALL devices confirmed — clean sensitive URLs from Firebase
                    // Keep DeviceId/DeviceName/IsOnline but remove GlobalUrl and LocalIp
                    foreach (var prop in allDoc.RootElement.EnumerateObject())
                    {
                        string devId = prop.Value.TryGetProperty("DeviceId", out var di) ? di.GetString() ?? prop.Name : prop.Name;
                        try
                        {
                            // Delete only the URL fields, keep identity
                            string gUrl = await FirebaseSyncManager.AuthUrlPublic(
                                $"active_devices/{_myPairingKey}/{devId}/GlobalUrl.json");
                            await client.DeleteAsync(gUrl);
                            string lUrl = await FirebaseSyncManager.AuthUrlPublic(
                                $"active_devices/{_myPairingKey}/{devId}/LocalIp.json");
                            await client.DeleteAsync(lUrl);
                            string uUrl = await FirebaseSyncManager.AuthUrlPublic(
                                $"active_devices/{_myPairingKey}/{devId}/Url.json");
                            await client.DeleteAsync(uUrl);
                        }
                        catch { }
                    }

                    _urlCleanedFromFirebase = true;
                    Logger.LogAction("PEER", $"🧹 All {totalDevices} devices confirmed — URLs deleted from Firebase");
                }
                else
                {
                    Logger.LogAction("PEER", $"Waiting: {confirmedDevices}/{totalDevices} devices confirmed");
                }
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
                try
                {
                    using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }; // 30s — Cloudflare tunnels can be slow
                    string pk = DevicePairingManager.EnsurePairingKey();
                    if (!string.IsNullOrEmpty(pk)) c.DefaultRequestHeaders.Add("X-Pairing-Key", pk);

                    var payload = JsonSerializer.Serialize(new
                    {
                        type = itemType, title, data = text,
                        sourceDeviceId = _myDeviceId,
                        sourceDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    var resp = await c.PostAsync($"{peer.ActiveUrl.TrimEnd('/')}/api/sync_text",
                        new StringContent(payload, Encoding.UTF8, "application/json"));

                    if (resp.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref delivered);
                        peer.LastSeen = DateTime.UtcNow;
                        Logger.LogAction("PEER", $"→ Text to {peer.DeviceName} via {peer.Transport}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER", $"Text to {peer.DeviceName} failed: {ex.Message}");
                    HandlePeerFailure(peer, ex.Message);
                }
            }));
            return delivered;
        }

        public async Task<int> PushFileToAllPeers(string filePath, string title, string itemType = "Image")
        {
            int delivered = 0;
            var alive = _peers.Values.Where(p => p.IsAlive).ToList();
            if (alive.Count == 0) return 0;

            await Task.WhenAll(alive.Select(async peer =>
            {
                try
                {
                    using var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    string pk = DevicePairingManager.EnsurePairingKey();
                    if (!string.IsNullOrEmpty(pk)) c.DefaultRequestHeaders.Add("X-Pairing-Key", pk);
                    c.DefaultRequestHeaders.Add("X-Item-Type", itemType);
                    c.DefaultRequestHeaders.Add("X-Source-Device", SettingsManager.Current.DeviceName ?? "");
                    c.DefaultRequestHeaders.Add("X-Source-DeviceId", _myDeviceId);

                    using var form = new MultipartFormDataContent();
                    string actualFileName = Path.GetFileName(filePath);
                    var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    form.Add(new StreamContent(fs), "file", actualFileName);
                    form.Add(new StringContent(title), "title");
                    form.Add(new StringContent(itemType), "type");

                    // Ensure receiver always knows the correct filename (even for old receivers)
                    c.DefaultRequestHeaders.Add("X-File-Name", Uri.EscapeDataString(actualFileName));

                    var resp = await c.PostAsync($"{peer.ActiveUrl.TrimEnd('/')}/api/sync_file", form);
                    if (resp.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref delivered);
                        peer.LastSeen = DateTime.UtcNow;
                        Logger.LogAction("PEER", $"→ File '{title}' to {peer.DeviceName} via {peer.Transport}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER", $"File to {peer.DeviceName} failed: {ex.Message}");
                    HandlePeerFailure(peer, ex.Message);
                }
            }));
            return delivered;
        }

        // ═══════════════════════════════════════════════════════════════
        // HEARTBEAT — 5 second keep-alive
        // ═══════════════════════════════════════════════════════════════

        private async Task HeartbeatLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(HEARTBEAT_MS, ct); } catch { break; }

                foreach (var peer in _peers.Values.Where(p => p.IsAlive).ToList())
                {
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

                    if (peer.ConsecutiveFailures >= 2)
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
            Logger.LogAction("PEER", $"💀 {peer.DeviceName} dead (was {old})");

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
            string globalUrl = FirebaseSyncManager.CachedGlobalUrl;
            string localUrl = FirebaseSyncManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                await FirebaseSyncManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
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
            string globalUrl = FirebaseSyncManager.CachedGlobalUrl;
            string localUrl = FirebaseSyncManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                await FirebaseSyncManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                Logger.LogAction("PEER", $"Re-published our URLs → LAN={localUrl} CF={globalUrl}");
            }

            // 2. Reset all peers to dead — fresh start
            foreach (var p in _peers.Values)
            {
                p.IsAlive = false;
                p.ConsecutiveFailures = 0;
            }

            // 3. Clear cleanup flag so we can re-fetch URLs from Firebase
            _urlCleanedFromFirebase = false;

            // 4. Full discovery + handshake cycle
            await DiscoverAndHandshake();

            Logger.LogAction("PEER", $"Force sync complete — {AliveCount}/{_peers.Count} peer(s) alive");
        }

        public List<PeerStatus> GetPeerStatuses() => _peers.Values.Select(p => new PeerStatus
        {
            DeviceId = p.DeviceId, DeviceName = p.DeviceName, Transport = p.Transport,
            IsAlive = p.IsAlive, LastSeen = p.LastSeen, ActiveUrl = p.ActiveUrl,
            LanUrl = p.LanUrl, CloudflareUrl = p.CloudflareUrl
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
        public bool IsAlive { get; set; } = false;
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        public int ConsecutiveFailures { get; set; } = 0;
    }

    public class PeerStatus
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Transport { get; set; } = "";
        public bool IsAlive { get; set; }
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
        public string StatusText { get; set; } = "Offline";
    }
}
