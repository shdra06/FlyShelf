// ---------------------------------------------------------------
// NetworkSyncServer — HTTP Request Routing, WebSocket Peers, Utilities
// ProcessRequest (URL routing), HandlePeerWebSocket, SafeExtractZip
// Split from NetworkSyncServer.Handlers.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class NetworkSyncServer
    {
        // ═══ RATE LIMITING: Per-IP request counter ═══
        // TRUSTED (paired P2P devices): Very high limit — never throttle real sync.
        // UNTRUSTED (web client / external): Strict limit — prevent DoS via public URL.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int count, long windowStart)> _rateLimits = new();
        private const int RATE_LIMIT_TRUSTED_WRITE = 2000;  // Paired devices: effectively unlimited
        private const int RATE_LIMIT_TRUSTED_READ = 2000;
        private const int RATE_LIMIT_EXTERNAL_WRITE = 30;   // Web/external: strict
        private const int RATE_LIMIT_EXTERNAL_READ = 60;
        private const long RATE_WINDOW_MS = 60_000;         // 1 minute window

        /// <summary>
        /// Returns true if the request should be rejected due to rate limiting.
        /// Trusted peers (paired devices, native mobile app) get near-unlimited rates.
        /// External web clients get strict limits to prevent DoS via Cloudflare URL.
        /// </summary>
        private static bool IsRateLimited(string ip, bool isWrite, bool isTrusted)
        {
            int limit = isTrusted
                ? (isWrite ? RATE_LIMIT_TRUSTED_WRITE : RATE_LIMIT_TRUSTED_READ)
                : (isWrite ? RATE_LIMIT_EXTERNAL_WRITE : RATE_LIMIT_EXTERNAL_READ);
            string key = $"{(isTrusted ? "T" : "E")}:{(isWrite ? "W" : "R")}:{ip}";
            long now = Environment.TickCount64;

            var entry = _rateLimits.AddOrUpdate(key,
                _ => (1, now),
                (_, prev) =>
                {
                    if (now - prev.windowStart > RATE_WINDOW_MS)
                        return (1, now); // Reset window
                    return (prev.count + 1, prev.windowStart);
                });

            return entry.count > limit;
        }

        /// <summary>
        /// Computes a safe CORS origin from the request, restricting to trusted domains only.
        /// Allows: localhost, 127.0.0.1, LAN IPs, *.trycloudflare.com, and FlyShelf origins.
        /// Returns null if the origin is untrusted (no CORS header will be added).
        /// </summary>
        private static string GetSafeCorsOrigin(HttpListenerRequest req)
        {
            string origin = req.Headers["Origin"] ?? "";
            if (string.IsNullOrEmpty(origin)) return null;

            try
            {
                var uri = new Uri(origin);
                string host = uri.Host.ToLowerInvariant();

                // Always allow localhost and loopback
                if (host == "localhost" || host == "127.0.0.1" || host == "[::1]") return origin;

                // Allow LAN IPs (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
                if (host.StartsWith("192.168.") || host.StartsWith("10.") || host.StartsWith("172."))
                    return origin;

                // Allow Cloudflare tunnel origins
                if (host.EndsWith(".trycloudflare.com")) return origin;

                // Allow FlyShelf's own global URL if set
                if (!string.IsNullOrEmpty(CloudDiscoveryManager.CachedGlobalUrl))
                {
                    try
                    {
                        var globalUri = new Uri(CloudDiscoveryManager.CachedGlobalUrl);
                        if (host == globalUri.Host.ToLowerInvariant()) return origin;
                    }
                    catch { }
                }
            }
            catch { }

            return null; // Untrusted origin — no CORS header
        }

        private async Task ProcessRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            try
            {
                string path = req.Url.LocalPath.ToLower();
                string remoteAddr = req.RemoteEndPoint?.ToString() ?? "unknown";
                Logger.LogAction("HTTP", $"[{remoteAddr}] {req.HttpMethod} {path}");
                
                // SECURITY: Dynamic CORS — only trusted origins (localhost, LAN, trycloudflare.com)
                string corsOrigin = GetSafeCorsOrigin(req);
                if (!string.IsNullOrEmpty(corsOrigin))
                    res.AddHeader("Access-Control-Allow-Origin", corsOrigin);
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Original-Date, X-FlyShelf-Client, X-Pairing-Key, X-File-Name, X-File-Type, X-Item-Type, X-Source-Device, X-Source-DeviceId, X-Batch-Name, X-Upload-Session, X-Chunk-Index, X-Total-Chunks, X-Device-Id");
                res.AddHeader("Access-Control-Expose-Headers", "X-Global-Url");
                // Enable Keep-Alive to allow socket reuse (crucial for zero-handshake P2P sync and chunked uploads)
                res.KeepAlive = true;
                if (!string.IsNullOrEmpty(GlobalUrl)) res.AddHeader("X-Global-Url", GlobalUrl);

                // ═══ RATE LIMITING ═══
                // Determine trust level: paired devices and native mobile app are trusted.
                string clientIp = req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                string pairingKeyForRateCheck = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                // SECURITY: Trust is established ONLY via cryptographic pairing key — never via spoofable headers
                bool isTrustedPeer = DevicePairingManager.IsDevicePaired(pairingKeyForRateCheck);
                bool isWriteEndpoint = req.HttpMethod == "POST";

                if (IsRateLimited(clientIp, isWriteEndpoint, isTrustedPeer))
                {
                    Logger.LogAction("RATE_LIMIT", $"⛔ Rate limited {clientIp} (trusted={isTrustedPeer}, {(isWriteEndpoint ? "write" : "read")})");
                    res.StatusCode = 429;
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"429 Too Many Requests\"}");
                    res.ContentType = "application/json";
                    try { res.OutputStream.Write(err, 0, err.Length); } catch { }
                    res.Close();
                    return;
                }

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 200;
                    res.Close();
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    ServeHtml(res);
                }
                else if (path == "/ping")
                {
                    byte[] pong = Encoding.UTF8.GetBytes("pong");
                    res.StatusCode = 200;
                    res.ContentType = "text/plain";
                    res.OutputStream.Write(pong, 0, pong.Length);
                    res.Close();
                }
                else if (path == "/api/health" && req.HttpMethod == "GET")
                {
                    // SECURITY: Unauthenticated health endpoint — expose ONLY liveness status.
                    // Full device info (name, ID, URLs, peers) is served behind auth below.
                    try
                    {
                        var healthData = new
                        {
                            status = "online",
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        string json = JsonSerializer.Serialize(healthData);
                        byte[] data = Encoding.UTF8.GetBytes(json);
                        res.StatusCode = 200;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(data, 0, data.Length);
                    }
                    catch { res.StatusCode = 200; }
                    res.Close();
                }
                else if (path == "/ws/peer" && req.IsWebSocketRequest)
                {
                    string wsPairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                    if (string.IsNullOrEmpty(wsPairingKey) || !DevicePairingManager.IsDevicePaired(wsPairingKey))
                    {
                        res.StatusCode = 403;
                        res.Close();
                        return;
                    }
                    string peerDeviceId = req.Headers["X-Device-Id"] ?? req.QueryString["deviceId"] ?? "unknown";
                    if (peerDeviceId == SettingsManager.Current.DeviceId)
                    {
                        Logger.LogAction("WS", $"⛔ Loopback WebSocket connection rejected from self ({peerDeviceId})");
                        res.StatusCode = 403;
                        res.Close();
                        return;
                    }
                    Logger.LogAction("WS", $"✅ Peer WebSocket accepted from {peerDeviceId}");
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = Task.Run(() => HandlePeerWebSocket(wsContext.WebSocket, peerDeviceId));
                }
                else if (path == "/download" && req.HttpMethod == "GET")
                {
                    string dlPairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                    string dlPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"] ?? "";
                    bool dlAuthed = DevicePairingManager.IsDevicePaired(dlPairingKey) ||
                                   (!string.IsNullOrEmpty(dlPin) && dlPin == SettingsManager.Current.WebClientPinToken);
                    if (!dlAuthed)
                    {
                        Logger.LogAction("SECURITY", $"⛔ Rejected unauthenticated /download from {req.RemoteEndPoint}");
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 — Download requires authentication\"}");
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                    }
                    else { await ServeFileDownload(req, res); }
                }
                else if (path == "/api/pair" && req.HttpMethod == "POST")
                {
                    await HandlePairRequest(req, res);
                }
                else if (path == "/api/peer_announce" && req.HttpMethod == "POST")
                {
                    await HandlePeerAnnounce(req, res);
                }
                else if (path == "/api/discover" && req.HttpMethod == "GET")
                {
                    string pairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"];
                    if (DevicePairingManager.IsDevicePaired(pairingKey))
                    {
                        string deviceId = req.Headers["X-Device-Id"] ?? "";
                        string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(deviceId)) DevicePairingManager.TouchDevice(deviceId, remoteIp);

                        var info = new { 
                            status = "ok", 
                            localUrl = DisplayUrl, 
                            globalUrl = GlobalUrl ?? "", 
                            deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                            isPro = LicenseManager.IsPro,
                            licenseKey = LicenseManager.IsPro ? LicenseManager.MaskedKey : ""
                        };
                        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info));
                        res.StatusCode = 200; res.ContentType = "application/json";
                        res.OutputStream.Write(json, 0, json.Length);
                        res.Close();
                    }
                    else
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                        res.StatusCode = 403; res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                    }
                }
                else
                {
                    // HARD SECURE AUTHENTICATION BARRIER
                    // SECURITY: Trust established ONLY via cryptographic pairing key OR valid PIN token.
                    // X-FlyShelf-Client header is NOT used — it's trivially spoofable (just like User-Agent).
                    string providedPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"];
                    string pairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"];
                    bool isPairedDevice = DevicePairingManager.IsDevicePaired(pairingKey);

                    if (!isPairedDevice && (string.IsNullOrEmpty(providedPin) || providedPin != SettingsManager.Current.WebClientPinToken))
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 Unauthorized - Invalid PIN\"}");
                        res.StatusCode = 401; res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                        return;
                    }

                    if (path == "/api/health" && req.HttpMethod == "GET")
                    {
                        try
                        {
                            var healthData = new
                            {
                                status = "online",
                                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                                deviceId = SettingsManager.Current.DeviceId,
                                deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                                deviceType = "PC",
                                uptime = (int)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                                transport = new { lan = CloudDiscoveryManager.CachedLocalUrl ?? "", cloudflare = CloudDiscoveryManager.CachedGlobalUrl ?? "" },
                                peers = PeerManager.Instance?.AliveCount ?? 0,
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            };
                            string json = JsonSerializer.Serialize(healthData);
                            byte[] data = Encoding.UTF8.GetBytes(json);
                            res.StatusCode = 200; res.ContentType = "application/json";
                            res.OutputStream.Write(data, 0, data.Length);
                        }
                        catch { res.StatusCode = 200; }
                        res.Close();
                    }
                    else if (path == "/api/sync" && req.HttpMethod == "GET")
                    {
                        string deviceId = req.Headers["X-Pairing-Key"] ?? req.Headers["X-Device-Id"] ?? req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                        _directDeviceLastSeen[deviceId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        CloudDiscoveryManager.DirectlyConnectedDeviceCount = GetDirectlyConnectedDeviceCount();
                        ServeClipboardData(res);
                    }
                    else if (path == "/api/events" && req.HttpMethod == "GET")
                    {
                        var tcs = new TaskCompletionSource<string>();
                        lock (_longPollLock) { _longPollWaiters.Add(tcs); }
                        try
                        {
                            var timeoutTask = Task.Delay(30000);
                            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                            if (completedTask == tcs.Task)
                            {
                                string payload = await tcs.Task;
                                byte[] data = Encoding.UTF8.GetBytes(payload);
                                res.StatusCode = 200; res.ContentType = "application/json";
                                res.ContentLength64 = data.Length;
                                res.OutputStream.Write(data, 0, data.Length);
                            }
                            else { res.StatusCode = 204; }
                        }
                        catch { res.StatusCode = 500; }
                        finally { lock (_longPollLock) { _longPollWaiters.Remove(tcs); } try { res.Close(); } catch { } }
                    }
                    else if (path == "/api/events/stream" && req.HttpMethod == "GET")
                    {
                        // SSE endpoint — persistent connection, instant push on clipboard change
                        await ServeClipboardEventStream(req, res);
                    }
                    else if (path == "/api/sync_text" && req.HttpMethod == "POST") { await HandleTextUpload(req, res); }
                    else if (path == "/api/sync_file" && req.HttpMethod == "POST") { await HandleFileUpload(req, res); }
                    else if (path == "/api/archive_upload" && req.HttpMethod == "POST") { await HandleArchiveUpload(req, res); }
                    else if (path == "/api/upload_chunk" && req.HttpMethod == "POST") { await HandleChunkUpload(req, res); }
                    else if (path == "/api/upload_finalize" && req.HttpMethod == "POST") { await HandleChunkFinalize(req, res); }
                    else if (path == "/api/relay_upload" && req.HttpMethod == "POST") { await HandleRelayUpload(req, res); }
                    else if (path == "/api/convert_to_pdf" && req.HttpMethod == "POST") { await HandleConvertToPdf(req, res); }
                    else if (path == "/api/merge_pdfs" && req.HttpMethod == "POST") { await HandleMergePdfs(req, res); }
                    else if (path == "/logs" && req.HttpMethod == "GET") { ServeLogDashboard(res); }
                    else if (path == "/api/logs/stream" && req.HttpMethod == "GET") { await ServeLogStream(req, res); }
                    else if (path == "/api/logs" && req.HttpMethod == "GET") { ServeLogsJson(req, res); }
                    else if (path == "/api/logs" && req.HttpMethod == "POST") { await HandleRemoteLogPost(req, res); }
                    else { res.StatusCode = 404; res.Close(); }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SERVER REQUEST FAULT", ex.Message);
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }

        /// <summary>
        /// Holds a WebSocket connection with a peer for instant liveness detection.
        /// Sends ping every 30s, receives pong. If the peer dies or tunnel drops,
        /// the WebSocket closes instantly — no 50s heartbeat delay.
        /// </summary>
        private async Task HandlePeerWebSocket(WebSocket ws, string peerDeviceId)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Logger.LogAction("WS", $"Peer {peerDeviceId} closed WebSocket gracefully");
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    byte[] messageBytes = ms.ToArray();

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string text = Encoding.UTF8.GetString(messageBytes);
                        if (text == "ping")
                        {
                            byte[] pong = Encoding.UTF8.GetBytes("pong");
                            await ws.SendAsync(new ArraySegment<byte>(pong), WebSocketMessageType.Text, true, CancellationToken.None);
                            continue;
                        }

                        if (text.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(text);
                                var root = doc.RootElement;
                                string envelopeType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "";

                                if (envelopeType == "SyncText")
                                {
                                    string sourceDeviceId = root.TryGetProperty("sourceDeviceId", out var idProp) ? idProp.GetString() ?? "" : "";
                                    if (sourceDeviceId == SettingsManager.Current.DeviceId)
                                    {
                                        Logger.LogAction("WS", "Ignored loopback WS SyncText from self");
                                        continue;
                                    }
                                    string itemType = root.TryGetProperty("itemType", out var itProp) ? itProp.GetString() : "Text";
                                    string title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";
                                    string data = root.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : "";
                                    string sourceDeviceName = root.TryGetProperty("sourceDeviceName", out var nameProp) ? nameProp.GetString() : "Remote PC";

                                    Logger.LogAction("WS", $"Received SyncText via WebSocket from {sourceDeviceName}: '{title}'");
                                    if (SettingsManager.Current.EnableIncomingSync)
                                    {
                                        InjectReceivedText(data, sourceDeviceName, "WebSocket", itemType, "PC");
                                    }
                                    else
                                    {
                                        Logger.LogAction("WS", $"Incoming sync paused — discarded text from {sourceDeviceName}");
                                    }
                                }
                                else if (envelopeType == "UrlUpdate")
                                {
                                    // P2P URL exchange — peer is telling us their URLs changed
                                    // This eliminates the need for Firebase SSE for connected peers
                                    string sourceDeviceId = root.TryGetProperty("sourceDeviceId", out var idProp2) ? idProp2.GetString() ?? "" : "";
                                    string sourceDeviceName = root.TryGetProperty("sourceDeviceName", out var nameProp2) ? nameProp2.GetString() ?? "" : "";
                                    string newLanUrl = root.TryGetProperty("lanUrl", out var lanProp) ? lanProp.GetString() ?? "" : "";
                                    string newCfUrl = root.TryGetProperty("cfUrl", out var cfProp) ? cfProp.GetString() ?? "" : "";

                                    if (!string.IsNullOrEmpty(sourceDeviceId) && PeerManager.Instance != null)
                                    {
                                        _ = Task.Run(() => PeerManager.Instance.HandlePeerUrlUpdateFromWebSocket(
                                            sourceDeviceId, sourceDeviceName, newLanUrl, newCfUrl));
                                    }
                                }
                                else if (envelopeType == "SyncFileStart")
                                {
                                    string fileName = root.TryGetProperty("fileName", out var fnProp) ? fnProp.GetString() : "file.dat";
                                    long fileSize = root.TryGetProperty("fileSize", out var fsProp) ? fsProp.GetInt64() : 0;
                                    string itemType = root.TryGetProperty("itemType", out var itProp) ? itProp.GetString() : "File";
                                    string title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";
                                    string sourceDeviceName = root.TryGetProperty("sourceDeviceName", out var nameProp) ? nameProp.GetString() : "Remote PC";
                                    string sourceDeviceId = root.TryGetProperty("sourceDeviceId", out var idProp) ? idProp.GetString() ?? "" : "";

                                    // Loopback check
                                    if (sourceDeviceId == SettingsManager.Current.DeviceId)
                                    {
                                        Logger.LogAction("WS", $"Ignored loopback WS SyncFileStart from self: {fileName}");
                                        // Drain the WebSocket bytes to keep it alive
                                        long bytesSkipped = 0;
                                        while (bytesSkipped < fileSize)
                                        {
                                            long remain = fileSize - bytesSkipped;
                                            int toRead = (int)Math.Min(buffer.Length, remain);
                                            var skipResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, 0, toRead), CancellationToken.None);
                                            if (skipResult.MessageType == WebSocketMessageType.Close) return;
                                            bytesSkipped += skipResult.Count;
                                        }
                                        continue;
                                    }

                                    Logger.LogAction("WS", $"Received SyncFileStart: {fileName} ({fileSize} bytes) from {sourceDeviceName}");

                                    // Check size limit for Free tier
                                    if (fileSize > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                                    {
                                        Logger.LogAction("WS", $"File receipt rejected — {fileName} ({fileSize} bytes) exceeds 50 MB limit on Free tier.");
                                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                            FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Incoming file {fileName} exceeds 50 MB Free tier limit.");
                                        });

                                        // Drain the WebSocket bytes to keep it alive
                                        long bytesSkipped = 0;
                                        while (bytesSkipped < fileSize)
                                        {
                                            long remain = fileSize - bytesSkipped;
                                            int toRead = (int)Math.Min(buffer.Length, remain);
                                            var skipResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, 0, toRead), CancellationToken.None);
                                            if (skipResult.MessageType == WebSocketMessageType.Close) return;
                                            bytesSkipped += skipResult.Count;
                                        }
                                        continue;
                                    }

                                    // ── Incoming Sync Gate ──
                                    if (!SettingsManager.Current.EnableIncomingSync)
                                    {
                                        Logger.LogAction("WS", $"Incoming sync paused — discarding file {fileName} from {sourceDeviceName}");
                                        // Still need to consume the binary data from the WebSocket to keep connection valid
                                        long bytesSkipped = 0;
                                        while (bytesSkipped < fileSize)
                                        {
                                            long remain = fileSize - bytesSkipped;
                                            int toRead = (int)Math.Min(buffer.Length, remain);
                                            var skipResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, 0, toRead), CancellationToken.None);
                                            if (skipResult.MessageType == WebSocketMessageType.Close) return;
                                            bytesSkipped += skipResult.Count;
                                        }
                                        continue;
                                    }

                                    string dateString = DateTime.Now.ToString("dd-MM-yyyy");
                                    string uploadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                                        "FlyShelf", "SyncedFiles", "Clipboard", sourceDeviceName, dateString);
                                    Directory.CreateDirectory(uploadDir);

                                    int counter = 1;
                                    string finalPath = Path.Combine(uploadDir, fileName);
                                    while (File.Exists(finalPath))
                                    {
                                        finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{counter++}{Path.GetExtension(fileName)}");
                                    }

                                    bool isLargeFile = fileSize >= 10 * 1024 * 1024;
                                    ClipboardItem? placeholder = null;

                                    if (isLargeFile)
                                    {
                                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            placeholder = _viewModel.CreateTransferPlaceholder(fileName, fileSize, sourceDeviceName, "WebSocket", "PC");
                                        });
                                    }
                                    else
                                    {
                                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                            FlyShelf.Windows.ToastWindow.ShowToast($"Receiving {fileName} from {sourceDeviceName} (via WS)... 📥");
                                        });
                                    }

                                    long bytesReceived = 0;
                                    var lastProgressUpdate = DateTime.MinValue;
                                    try
                                    {
                                        using (var fileFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                                        {
                                            while (bytesReceived < fileSize)
                                            {
                                                long remain = fileSize - bytesReceived;
                                                int toRead = (int)Math.Min(buffer.Length, remain);
                                                var chunkResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, 0, toRead), CancellationToken.None);
                                                if (chunkResult.MessageType == WebSocketMessageType.Close)
                                                {
                                                    throw new WebSocketException("WebSocket closed during binary file transmission.");
                                                }
                                                await fileFs.WriteAsync(buffer, 0, chunkResult.Count);
                                                bytesReceived += chunkResult.Count;

                                                if (isLargeFile && placeholder != null && (DateTime.Now - lastProgressUpdate).TotalMilliseconds >= 300)
                                                {
                                                    lastProgressUpdate = DateTime.Now;
                                                    double progress = fileSize > 0 ? ((double)bytesReceived / fileSize * 100) : 50;
                                                    if (progress < 1) progress = 1;
                                                    if (progress > 99) progress = 99;
                                                    string speedText = $"{FlyShelfViewModel.FormatBytesStatic(bytesReceived)} of {FlyShelfViewModel.FormatBytesStatic(fileSize)}";
                                                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                                    {
                                                        placeholder.TransferProgress = progress;
                                                        placeholder.TransferStatusText = $"Transferring... {progress:F0}% ({speedText})";
                                                    });
                                                }
                                            }
                                        }

                                        Logger.LogAction("WS", $"SyncFile completed via WS: {fileName} ({bytesReceived} bytes written)");

                                        if (itemType == "Group")
                                        {
                                            string extractDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Extracted", $"{Guid.NewGuid().ToString().Substring(0, 8)}");
                                            Directory.CreateDirectory(extractDir);
                                            SafeExtractZip(finalPath, extractDir);
                                            string[] extractedPaths = Directory.GetFileSystemEntries(extractDir);
                                            InjectReceivedGroup(extractedPaths, sourceDeviceName, "WebSocket", "PC", placeholder);
                                        }
                                        else
                                        {
                                            InjectReceivedFile(finalPath, sourceDeviceName, "WebSocket", "PC", placeholder);
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        if (placeholder != null)
                                        {
                                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.DroppedItems.Remove(placeholder));
                                        }
                                        throw;
                                    }
                                }
                            }
                            catch (Exception jsonEx)
                            {
                                Logger.LogAction("WS ERROR", $"Failed parsing WS JSON payload: {jsonEx.Message}");
                            }
                        }
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                Logger.LogAction("WS", $"Peer {peerDeviceId} WebSocket connection lost/dropped: {wsEx.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("WS", $"Peer {peerDeviceId} WebSocket error: {ex.Message}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                ws.Dispose();
            }
        }

        private static void SafeExtractZip(string zipPath, string extractDir)
        {
            const long MaxAllowedUncompressedSize = 250 * 1024 * 1024;
            const int MaxFileCount = 1000;

            string destinationRoot = Path.GetFullPath(extractDir);
            string destinationRootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? destinationRoot
                : destinationRoot + Path.DirectorySeparatorChar;

            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count > MaxFileCount)
                    throw new InvalidDataException($"Too many files inside zip archive (potential Zip Bomb). Limit is {MaxFileCount} entries.");

                long totalUncompressedSize = 0;
                foreach (var entry in archive.Entries)
                {
                    string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                    if (!destinationPath.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                        !destinationPath.StartsWith(destinationRootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Directory traversal attempt detected in entry: {entry.FullName}");
                    }
                    totalUncompressedSize += entry.Length;
                    if (totalUncompressedSize > MaxAllowedUncompressedSize)
                    {
                        throw new InvalidDataException($"Uncompressed zip payload exceeds safety threshold limits ({MaxAllowedUncompressedSize / (1024 * 1024)}MB).");
                    }
                }
            }
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);
        }
    }
}
