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
    {        private async Task Handshake(PeerConnection peer)
        {
            // Prevent concurrent handshakes on the same peer (HeartbeatLoop, DiscoveryLoop, UDP, PeerAnnounce, UrlUpdate)
            if (!await peer.HandshakeLock.WaitAsync(0))
            {
                Logger.LogAction("PEER", $"🔌 Handshake {peer.DeviceName} already in progress — skipping");
                return;
            }
            try
            {
                bool lanEnabled = SettingsManager.Current.EnableLocalLAN;
                Logger.LogAction("PEER", $"🔌 Handshake {peer.DeviceName}: LAN={peer.LanUrl ?? "(empty)"} CF={peer.CloudflareUrl ?? "(empty)"} lanEnabled={lanEnabled}");

                using var cts = new CancellationTokenSource();
                var tasks = new List<Task<(bool success, string transport, string url)>>();

                if (lanEnabled && !string.IsNullOrEmpty(peer.LanUrl))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        bool ok = await TryConnect(peer, peer.LanUrl, "LAN", cts.Token);
                        return (ok, "LAN", peer.LanUrl);
                    }));
                }

                if (!string.IsNullOrEmpty(peer.CloudflareUrl))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        bool ok = await TryConnect(peer, peer.CloudflareUrl, "Cloudflare", cts.Token);
                        return (ok, "Cloudflare", peer.CloudflareUrl);
                    }));
                }

                if (tasks.Count == 0)
                {
                    lock (peer.StateLock) { peer.IsAlive = false; peer.Transport = "offline"; }
                    return;
                }

                var remainingTasks = new List<Task<(bool success, string transport, string url)>>(tasks);
                bool handshakeSucceeded = false;

                while (remainingTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(remainingTasks);
                    remainingTasks.Remove(completedTask);
                    try
                    {
                        var result = await completedTask;
                        if (result.success)
                        {
                            handshakeSucceeded = true;
                            try { cts.Cancel(); } catch { } // Best-effort: failure is acceptable
                            break;
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                }

                if (!handshakeSucceeded)
                {
                    lock (peer.StateLock) { peer.IsAlive = false; peer.Transport = "offline"; }
                    Logger.LogAction("PEER", $"⚠️  {peer.DeviceName} unreachable (LAN:{(lanEnabled ? "on" : "off")}) tried LAN={peer.LanUrl ?? "null"} CF={peer.CloudflareUrl ?? "null"}");
                }
            }
            finally { peer.HandshakeLock.Release(); }
        }

        private async Task<bool> TryConnect(PeerConnection peer, string testUrl, string transport, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(testUrl)) return false;
            
            if (testUrl.Contains(","))
            {
                var urls = testUrl.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var url in urls)
                {
                    var trimmed = url.Trim();
                    if (await TryConnectSingle(peer, trimmed, transport, ct))
                    {
                        return true;
                    }
                }
                return false;
            }

            return await TryConnectSingle(peer, testUrl.Trim(), transport, ct);
        }

        private async Task<bool> TryConnectSingle(PeerConnection peer, string testUrl, string transport, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(testUrl)) return false;
            // Reject non-URL values like "offline", corrupted decryptions, etc.
            if (!testUrl.StartsWith("http://") && !testUrl.StartsWith("https://")) return false;

            // Reject loopback URLs to ourselves
            string myLocalUrl = CloudDiscoveryManager.CachedLocalUrl;
            string myGlobalUrl = CloudDiscoveryManager.CachedGlobalUrl;
            string myDisplayUrl = NetworkSyncServer.Instance?.DisplayUrl;
            string myServerGlobalUrl = NetworkSyncServer.Instance?.GlobalUrl;

            string normalizedTest = testUrl.TrimEnd('/');
            if (!string.IsNullOrEmpty(myLocalUrl) && normalizedTest.Equals(myLocalUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(myGlobalUrl) && normalizedTest.Equals(myGlobalUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(myDisplayUrl) && normalizedTest.Equals(myDisplayUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(myServerGlobalUrl) && normalizedTest.Equals(myServerGlobalUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                int timeout = (transport == "LAN") ? HANDSHAKE_TIMEOUT_LAN_MS : HANDSHAKE_TIMEOUT_CF_MS;
                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{testUrl.TrimEnd('/')}/api/health");
                if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(timeout);
                var r = await _sharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
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

                            // Extract device ID for loopback detection
                            if (root.TryGetProperty("deviceId", out var idProp))
                            {
                                string returnedId = idProp.GetString() ?? "";
                                if (returnedId == _myDeviceId)
                                {
                                    Logger.LogAction("PEER", $"🔌 Loopback connection detected (health returned our own DeviceId '{_myDeviceId}') — rejecting handshake.");
                                    return false;
                                }
                            }

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

                    // Announce ourselves to the peer so they can connect back instantly
                    // (eliminates the need for Firebase SSE round-trip for reverse discovery)
                    _ = Task.Run(() => AnnounceSelfToPeer(peer));
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || ex is OperationCanceledException)
                {
                    return false; // Silently exit on cancellation
                }
                Logger.LogAction("PEER", $"{transport} handshake {peer.DeviceName} for URL '{testUrl}': {ex.Message}");
                if (transport == "Cloudflare")
                {
                    string msg = ex.Message.ToLower();
                    bool isHostError = msg.Contains("no such host is known") || 
                                       msg.Contains("name or service not known") || 
                                       msg.Contains("timed out") || 
                                       msg.Contains("connection refused") || 
                                       msg.Contains("canceled");
                    
                    peer.IncrementFailures();
                    if (peer.ConsecutiveFailures >= 5 || isHostError)
                    {
                        Logger.LogAction("PEER", $"⚠️ Invalidating stale Cloudflare URL for {peer.DeviceName} (failures: {peer.ConsecutiveFailures}, reason: {ex.Message})");
                        peer.CloudflareUrl = "";
                        SaveUrlCache();
                    }
                }
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
            try { peer.WsCts?.Cancel(); } catch { } // Best-effort: failure is acceptable
            try { peer.LiveSocket?.Dispose(); } catch { } // Best-effort: failure is acceptable

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
                peer.WsReconnectAttempts = 0; // Reset backoff on successful connection
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

            var buf = new byte[4096]; // Larger buffer to handle JSON messages (UrlUpdate)
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

                    // Wait for response (pong or JSON message) with 15s timeout
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeoutCts.CancelAfter(15_000);
                    try
                    {
                        // Read full message (may span multiple frames)
                        using var ms = new System.IO.MemoryStream();
                        WebSocketReceiveResult result;
                        bool wsClosed = false;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), timeoutCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) { wsClosed = true; break; }
                            ms.Write(buf, 0, result.Count);
                        } while (!result.EndOfMessage);
                        if (wsClosed) break;

                        peer.LastSeen = DateTime.UtcNow;
                        peer.ConsecutiveFailures = 0;

                        // Parse the message — could be "pong" or a JSON envelope
                        string text = Encoding.UTF8.GetString(ms.ToArray());
                        if (text != "pong" && text.TrimStart().StartsWith("{"))
                        {
                            // JSON message — handle UrlUpdate
                            try
                            {
                                using var doc = JsonDocument.Parse(text);
                                var root = doc.RootElement;
                                string msgType = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                                if (msgType == "UrlUpdate")
                                {
                                    string srcId = root.TryGetProperty("sourceDeviceId", out var si) ? si.GetString() ?? "" : "";
                                    string srcName = root.TryGetProperty("sourceDeviceName", out var sn) ? sn.GetString() ?? "" : "";
                                    string newLan = root.TryGetProperty("lanUrl", out var nl) ? nl.GetString() ?? "" : "";
                                    string newCf = root.TryGetProperty("cfUrl", out var nc) ? nc.GetString() ?? "" : "";
                                    _ = Task.Run(() => HandlePeerUrlUpdateFromWebSocket(srcId, srcName, newLan, newCf));
                                }
                                // ═══ LAN TRANSFER CONTROL MESSAGES ═══
                                else if (msgType == "TransferOffer" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    string fileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
                                    long fileSize = root.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0;
                                    string srcDeviceId = root.TryGetProperty("sourceDeviceId", out var si) ? si.GetString() ?? "" : "";
                                    string srcDeviceName = root.TryGetProperty("sourceDeviceName", out var sn) ? sn.GetString() ?? "" : "";
                                    string xxhash = root.TryGetProperty("xxhash64", out var xh) ? xh.GetString() : null;
                                    // Parse chunk fields (backward compatible — defaults to non-chunked)
                                    bool isChunked = false;
                                    int numChunks = 4;
                                    long chunkSize = 0;
                                    if (root.TryGetProperty("isChunked", out var chunkedProp)) isChunked = chunkedProp.GetBoolean();
                                    if (root.TryGetProperty("numChunks", out var numProp)) numChunks = numProp.GetInt32();
                                    if (root.TryGetProperty("chunkSize", out var sizeProp)) chunkSize = sizeProp.GetInt64();
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                    {
                                        _ = Task.Run(() => LanTransferManager.Instance.HandleTransferOffer(
                                            tid, fileName, fileSize, srcDeviceId, srcDeviceName, xxhash, isChunked, numChunks, chunkSize));
                                    }
                                }
                                else if (msgType == "TransferAccept" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    long resumeFrom = root.TryGetProperty("resumeFrom", out var rf) ? rf.GetInt64() : 0;
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                    {
                                        _ = Task.Run(() => LanTransferManager.Instance.HandleTransferAccepted(tid, resumeFrom, peer.DeviceId));
                                    }
                                }
                                else if (msgType == "TransferPause" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                        _ = Task.Run(() => LanTransferManager.Instance.HandlePeerPause(tid));
                                }
                                else if (msgType == "TransferResume" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    long resumeFrom = root.TryGetProperty("bytesTransferred", out var rf) ? rf.GetInt64() : 0;
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                        LanTransferManager.Instance.HandlePeerResume(tid, resumeFrom);
                                }
                                else if (msgType == "TransferCancel" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                        LanTransferManager.Instance.HandlePeerCancel(tid);
                                }
                                else if (msgType == "TransferComplete" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                        LanTransferManager.Instance.HandlePeerComplete(tid);
                                }
                                // C1 fix: Handle TransferRetryRequest — receiver asking us to re-send a file
                                else if (msgType == "TransferRetryRequest" && LanTransferManager.Instance != null)
                                {
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    string srcDeviceId = peer.DeviceId;
                                    long bytesTransferred = root.TryGetProperty("bytesTransferred", out var bt) ? bt.GetInt64() : 0;
                                    if (Guid.TryParse(tidStr, out Guid tid))
                                        _ = Task.Run(() => LanTransferManager.Instance.HandleTransferRetryRequest(tid, srcDeviceId, bytesTransferred));
                                }
                                else if (msgType == "TransferCheckpoint" && LanTransferManager.Instance != null)
                                {
                                    // Checkpoint ACK from receiver — update our send session progress
                                    string tidStr = root.TryGetProperty("transferId", out var ti) ? ti.GetString() ?? "" : "";
                                    // Checkpoint is informational — we track progress on the send side already
                                }
                            }
                            catch { /* Best-effort JSON parsing */ }
                        }
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
                    // H3b: Reuse shared HttpClient instead of creating a new one per health check
                    // Prevents socket exhaustion under rapid WS reconnection cycles
                    string testUrl = $"{peer.ActiveUrl.TrimEnd('/')}/api/health";
                    string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                    if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();

                    using var req = new HttpRequestMessage(HttpMethod.Get, testUrl);
                    if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);

                    using var healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var r = await _sharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, healthCts.Token);
                    if (r.IsSuccessStatusCode)
                    {
                        stillAlive = true;
                    }
                }
                catch (Exception ex) { Logger.LogAction("PEER", $"Health check failed for {peer.DeviceName}: {ex.Message}"); }

                if (stillAlive)
                {
                    Logger.LogAction("WS", $"ℹ️  {peer.DeviceName} still reachable — reconnecting WebSocket...");
                    try { peer.WsCts?.Cancel(); } catch { } // Best-effort: failure is acceptable
                    try { peer.LiveSocket?.Dispose(); } catch { } // Best-effort: failure is acceptable
                    peer.LiveSocket = null;
                    // Re-establish WebSocket with exponential backoff to prevent tight reconnect loops
                    peer.WsReconnectAttempts++;
                    int delay = Math.Min(1000 * (1 << Math.Min(peer.WsReconnectAttempts, 5)), 30000);
                    Logger.LogAction("WS", $"⏳ Reconnecting WebSocket to {peer.DeviceName} in {delay}ms (attempt #{peer.WsReconnectAttempts})");
                    await Task.Delay(delay);
                    _ = Task.Run(() => ConnectWebSocket(peer));
                }
                else
                {
                    Logger.LogAction("WS", $"💀 {peer.DeviceName} WebSocket dropped and HTTP health check failed — instant death detection");
                    await HandlePeerDeath(peer);
                }
            }
        }

        /// <summary>
        /// After successful handshake, tell the peer about us so they can connect back.
        /// POST /api/peer_announce with our deviceId, name, and URLs.
        /// Fire-and-forget — the handshake already succeeded, this enables reverse connection.
        /// </summary>
        private async Task AnnounceSelfToPeer(PeerConnection peer)
        {
            try
            {
                string myLanUrl = CloudDiscoveryManager.CachedLocalUrl ?? "";
                string myCfUrl = CloudDiscoveryManager.CachedGlobalUrl ?? "";
                string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                string pk = DevicePairingManager.EnsurePairingKey();

                var payload = new
                {
                    deviceId = _myDeviceId,
                    deviceName = myName,
                    lanUrl = myLanUrl,
                    cloudflareUrl = myCfUrl,
                    pairingKey = pk
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string announceUrl = $"{peer.ActiveUrl.TrimEnd('/')}/api/peer_announce";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var resp = await _sharedClient.PostAsync(announceUrl, content, cts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    // Parse response — peer sends back THEIR latest URLs
                    try
                    {
                        string respJson = await resp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(respJson) && respJson.StartsWith("{"))
                        {
                            using var doc = JsonDocument.Parse(respJson);
                            var root = doc.RootElement;

                            // Update peer URLs if they sent back newer ones
                            if (root.TryGetProperty("lanUrl", out var peerLan))
                            {
                                string newLan = peerLan.GetString() ?? "";
                                if (!string.IsNullOrEmpty(newLan) && newLan.StartsWith("http") && newLan != peer.LanUrl)
                                {
                                    peer.LanUrl = newLan;
                                    SaveUrlCache();
                                }
                            }
                            if (root.TryGetProperty("cloudflareUrl", out var peerCf))
                            {
                                string newCf = peerCf.GetString() ?? "";
                                if (!string.IsNullOrEmpty(newCf) && newCf.Contains("trycloudflare") && newCf != peer.CloudflareUrl)
                                {
                                    peer.CloudflareUrl = newCf;
                                    SaveUrlCache();
                                }
                            }
                        }
                    }
                    catch { /* Response parsing is best-effort */ }

                    Logger.LogAction("PEER", $"📢 Announced ourselves to {peer.DeviceName} via {peer.Transport}");
                }
            }
            catch (Exception ex)
            {
                // Non-fatal — the handshake already succeeded, announce is for reverse discovery
                Logger.LogAction("PEER", $"📢 Announce to {peer.DeviceName} failed (non-fatal): {ex.Message}");
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
                // FIX R6: Reuse shared HttpClient instead of creating new one per cleanup
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                // Write confirmation tick: "I have connected to all my peers"
                string tickUrl = await CloudDiscoveryManager.AuthUrlPublic(
                    $"active_devices/{_myPairingKey}/{_myDeviceId}/confirmedPeers.json");
                var peerIds = _peers.Values.Where(p => p.IsAlive).Select(p => p.DeviceId).ToList();
                string tickJson = JsonSerializer.Serialize(new
                {
                    confirmedAt = NetworkClock.UtcNowMs,
                    peers = peerIds
                });
                await _sharedClient.PutAsync(tickUrl, new StringContent(tickJson, Encoding.UTF8, "application/json"), cleanupCts.Token);
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
                    await _sharedClient.DeleteAsync(rUrl);
                }
                catch (Exception ex) { Logger.LogAction("PEER", $"Failed to clean urlRequest from Firebase: {ex.Message}"); }

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

        // ═══════════════════════════════════════════════════════════════
        // P2P URL Exchange via WebSocket — eliminates Firebase SSE
        // When our tunnel URL changes, we broadcast directly to all
        // connected peers. No Firebase connection needed.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Broadcasts our updated URLs to all connected peers via their WebSocket.
        /// Called when the Cloudflare tunnel restarts with a new URL or LAN IP changes.
        /// This replaces Firebase SSE for already-connected peers (&lt;100ms vs ~2s).
        /// </summary>
        public async Task BroadcastUrlUpdate(string lanUrl, string cfUrl)
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = "UrlUpdate",
                sourceDeviceId = _myDeviceId,
                sourceDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                lanUrl = lanUrl ?? "",
                cfUrl = cfUrl ?? "",
                timestamp = NetworkClock.UtcNowMs
            });
            byte[] data = Encoding.UTF8.GetBytes(msg);
            int sent = 0;

            foreach (var peer in _peers.Values)
            {
                if (!peer.IsAlive || peer.LiveSocket == null || peer.LiveSocket.State != WebSocketState.Open)
                    continue;

                try
                {
                    bool acquired = await peer.SendSemaphore.WaitAsync(TimeSpan.FromSeconds(3));
                    if (!acquired)
                    {
                        Logger.LogAction("PEER", $"Skipping URL broadcast to {peer.DeviceName} — send semaphore busy");
                        continue;
                    }
                    try
                    {
                        using var sendCts = new CancellationTokenSource(5000);
                        await peer.LiveSocket.SendAsync(
                            new ArraySegment<byte>(data), WebSocketMessageType.Text, true,
                            sendCts.Token);
                        sent++;
                    }
                    finally { peer.SendSemaphore.Release(); }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("WS", $"Failed to send UrlUpdate to {peer.DeviceName}: {ex.Message}");
                }
            }

            if (sent > 0)
                Logger.LogAction("WS", $"📡 Broadcasted URL update to {sent} peer(s) via WebSocket (LAN={lanUrl} CF={cfUrl})");
        }

        /// <summary>
        /// Called when a connected peer sends us a UrlUpdate message over WebSocket.
        /// Updates the peer's URLs and reconnects if they changed — all without Firebase.
        /// </summary>
        public async Task HandlePeerUrlUpdateFromWebSocket(string sourceDeviceId, string sourceDeviceName, string newLanUrl, string newCfUrl)
        {
            if (sourceDeviceId == _myDeviceId) return;

            Logger.LogAction("WS", $"📡 Received URL update from {sourceDeviceName} via WebSocket: LAN={newLanUrl} CF={newCfUrl}");

            if (_peers.TryGetValue(sourceDeviceId, out var peer))
            {
                bool changed = false;

                if (!string.IsNullOrEmpty(newLanUrl) && newLanUrl.StartsWith("http") && newLanUrl != peer.LanUrl)
                {
                    peer.LanUrl = newLanUrl;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(newCfUrl) && newCfUrl.StartsWith("http") && newCfUrl != peer.CloudflareUrl)
                {
                    peer.CloudflareUrl = newCfUrl;
                    changed = true;
                }

                if (changed)
                {
                    SaveUrlCache();

                    // If we're currently connected via Cloudflare and the CF URL changed,
                    // we need to reconnect with the new URL
                    if (peer.Transport == "Cloudflare" && !string.IsNullOrEmpty(newCfUrl) && newCfUrl != peer.ActiveUrl)
                    {
                        Logger.LogAction("WS", $"📡 {sourceDeviceName} CF URL changed — scheduling reconnect...");
                        // Fire-and-forget reconnect to avoid blocking the WebSocket monitor
                        // that dispatched this handler — prevents deadlock with HandlePeerDeath
                        _ = Task.Run(async () =>
                        {
                            peer.IsAlive = false;
                            peer.ConsecutiveFailures = 0;
                            try { peer.WsCts?.Cancel(); } catch { } // Best-effort: failure is acceptable
                            try { peer.LiveSocket?.Dispose(); } catch { } // Best-effort: failure is acceptable
                            peer.LiveSocket = null;
                            await Handshake(peer);
                        });
                    }
                    else
                    {
                        Logger.LogAction("WS", $"📡 {sourceDeviceName} URLs updated (no reconnect needed — transport={peer.Transport})");
                    }
                }
            }
            else
            {
                Logger.LogAction("WS", $"📡 URL update from unknown peer {sourceDeviceName} ({sourceDeviceId}) — ignoring");
            }
        }
    }
}
