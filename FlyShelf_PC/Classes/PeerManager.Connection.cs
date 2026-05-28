// ---------------------------------------------------------------
// PeerManager — Handshake, WebSocket & Cleanup
// Split from PeerManager.cs for modularity
// ---------------------------------------------------------------
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
    public partial class PeerManager
    {
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
            Logger.LogAction("PEER", $"⚠️ {peer.DeviceName} unreachable (LAN:{(lanEnabled ? "on" : "off")}) tried LAN={peer.LanUrl ?? "null"} CF={peer.CloudflareUrl ?? "null"}");
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
                                        Logger.LogAction("PEER", $"🔎 Discovered {peer.DeviceName} LAN URL from health: {peerLan}");
                                    }
                                }
                                if (tr.TryGetProperty("cloudflare", out var cfProp))
                                {
                                    string peerCf = cfProp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(peerCf) && peerCf.Contains("trycloudflare") && peerCf != peer.CloudflareUrl)
                                    {
                                        peer.CloudflareUrl = peerCf;
                                        Logger.LogAction("PEER", $"🔎 Discovered {peer.DeviceName} CF URL from health: {peerCf}");
                                    }
                                }
                            }

                            // Log version mismatch warning
                            string myVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
                            if (!string.IsNullOrEmpty(peer.Version) && peer.Version != myVersion)
                            {
                                Logger.LogAction("PEER", $"⚠️  Version mismatch: {peer.DeviceName} is on v{peer.Version}, we are on v{myVersion}");
                            }
                        }
                    }
                    catch { /* Health parsing is optional — connection is already confirmed */ }

                    Logger.LogAction("PEER", $"✅ {peer.DeviceName} connected via {transport}: {testUrl}" +
                        (!string.IsNullOrEmpty(peer.Version) ? $" (v{peer.Version})" : ""));
                    PeerConnected?.Invoke(peer.DeviceId, transport);

                    // ═ ═ ═ FIX 5: Cache URLs locally on successful connection ═ ═ ═
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
                        Logger.LogAction("WS", $"⚠️  {peer.DeviceName} WebSocket pong timeout");
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

            // WebSocket died → if peer was still marked alive, verify via HTTP health check
            if (peer.IsAlive)
            {
                Logger.LogAction("WS", $"⚠️  {peer.DeviceName} WebSocket dropped — verifying health via HTTP...");
                
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
                    Logger.LogAction("WS", $"ℹ️  {peer.DeviceName} is still reachable via HTTP. Keeping connection alive.");
                    try { peer.WsCts?.Cancel(); } catch { }
                    try { peer.LiveSocket?.Dispose(); } catch { }
                    peer.LiveSocket = null;
                }
                else
                {
                    Logger.LogAction("WS", $"💀 {peer.DeviceName} WebSocket dropped and HTTP health check failed — instant death detection");
                    await HandlePeerDeath(peer);
                }
            }
        }

        /// <summary>
        /// All peers confirmed alive → write tick, then delete URLs from Firebase.
        /// URLs are deleted immediately for SECURITY — they expose clipboard data via Cloudflare.
        /// When a peer restarts, it uses the urlRequest signal to ask online peers to re-publish.
        /// </summary>
        private async Task ConfirmAndCleanup()
        {
            if (!SettingsManager.Current.EnableCloudDiscovery) return;

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

                // ═ ═ ═ FIX 6: DON'T delete URLs from Firebase ═ ═ ═ 
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

        // ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ ═ 
    }
}
