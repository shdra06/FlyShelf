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
                    // FIX R7: Targeted retry — only reconnect this specific peer instead of
                    // full DiscoverAndHandshake() which reads ALL devices from Firebase
                    Logger.LogAction("PEER", $"⚡ Text delivery failed — reconnecting {peer.DeviceName}...");
                    peer.IsAlive = false;
                    peer.ConsecutiveFailures = 0;
                    await Handshake(peer);
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
                    
                    // FIX R2: 30s timeout prevents permanent SendSemaphore hold if peer stalls
                    using var wsSendCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await peer.SendSemaphore.WaitAsync(wsSendCts.Token);
                    try
                    {
                        await peer.LiveSocket.SendAsync(new ArraySegment<byte>(envelopeBytes), WebSocketMessageType.Text, true, wsSendCts.Token);
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
            if (File.Exists(filePath))
            {
                long fSize = new FileInfo(filePath).Length;

                // ═══ LAN TCP ENGINE: Route large files on LAN through dedicated TCP for zero-copy transfer ═══
                // NOTE: Only PC peers use the dedicated TCP engine (port 8998) — Android/Mobile devices
                // can't open raw TCP sockets from React Native, so they always use HTTP path.
                var aliveLanPcPeers = _peers.Values.Where(p => p.IsAlive && p.Transport == "LAN"
                    && (string.IsNullOrEmpty(p.DeviceType) || p.DeviceType.Equals("PC", StringComparison.OrdinalIgnoreCase))).ToList();
                var aliveLanMobilePeers = _peers.Values.Where(p => p.IsAlive && p.Transport == "LAN"
                    && !string.IsNullOrEmpty(p.DeviceType) && !p.DeviceType.Equals("PC", StringComparison.OrdinalIgnoreCase)).ToList();

                if (fSize > 5 * 1024 * 1024 && aliveLanPcPeers.Count > 0 && LanTransferManager.Instance != null)
                {
                    int tcpDelivered = 0;
                    // Send via TCP engine to LAN PC peers
                    await Task.WhenAll(aliveLanPcPeers.Select(async peer =>
                    {
                        try
                        {
                            var session = await LanTransferManager.Instance.OfferFile(peer, filePath);
                            if (session != null) Interlocked.Increment(ref tcpDelivered);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("PEER", $"TCP offer failed for {peer.DeviceName}: {ex.Message}");
                        }
                    }));

                    // Send via HTTP to non-LAN peers (Cloudflare) + LAN Mobile peers (can't use TCP)
                    var httpPeers = _peers.Values.Where(p => p.IsAlive && p.Transport != "LAN").ToList();
                    httpPeers.AddRange(aliveLanMobilePeers); // Mobile LAN peers use HTTP, not TCP
                    if (httpPeers.Count > 0)
                    {
                        // Cloudflare peers still use old path with size limit (LAN mobile peers skip this check)
                        var cfPeers = httpPeers.Where(p => p.Transport != "LAN").ToList();
                        var lanMobile = httpPeers.Where(p => p.Transport == "LAN").ToList();

                        // LAN Mobile peers: no size limit (same network)
                        await Task.WhenAll(lanMobile.Select(async peer =>
                        {
                            bool sent = await TrySendFile(peer, filePath, title, itemType);
                            if (sent) Interlocked.Increment(ref tcpDelivered);
                        }));

                        // Cloudflare peers: enforce size limit
                        if (cfPeers.Count > 0)
                        {
                            if (fSize > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                            {
                                Logger.LogAction("PEER", "Cloudflare transfer limited to 50 MB on Free tier");
                            }
                            else
                            {
                                await Task.WhenAll(cfPeers.Select(async peer =>
                                {
                                    bool sent = await TrySendFile(peer, filePath, title, itemType);
                                    if (sent) Interlocked.Increment(ref tcpDelivered);
                                }));
                            }
                        }
                    }
                    return tcpDelivered;
                }

                // Non-LAN path: enforce size limit for Cloudflare
                if (fSize > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        Windows.ToastWindow.ShowToast($"⚠️ File transfer limited to 50 MB on Free tier."));
                    return 0;
                }
            }

            int delivered = 0;
            var alive = _peers.Values.Where(p => p.IsAlive).ToList();
            if (alive.Count == 0) return 0;

            await Task.WhenAll(alive.Select(async peer =>
            {
                bool sent = await TrySendFile(peer, filePath, title, itemType);
                if (!sent)
                {
                    // First attempt failed — peer's tunnel may have died. Reconnect + retry once.
                    // FIX R7: Targeted retry — only reconnect this specific peer
                    Logger.LogAction("PEER", $"⚡ File delivery failed — reconnecting {peer.DeviceName}...");
                    peer.IsAlive = false;
                    peer.ConsecutiveFailures = 0;
                    await Handshake(peer);
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

        /// <summary>
        /// Push a file to a specific peer by device ID (used by "Send to Device" UI).
        /// Returns true if delivery succeeded.
        /// </summary>
        public async Task<bool> PushFileToSinglePeer(string targetDeviceId, string filePath, string title, string itemType = "Archive")
        {
            if (File.Exists(filePath))
            {
                long fSize = new FileInfo(filePath).Length;
                if (fSize > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        Windows.ToastWindow.ShowToast($"⚠️ File transfer limited to 50 MB on Free tier."));
                    return false;
                }
            }

            var peer = _peers.Values.FirstOrDefault(p => p.DeviceId == targetDeviceId);
            if (peer == null)
            {
                Logger.LogAction("PEER", $"PushFileToSinglePeer: device '{targetDeviceId}' not found in peers");
                return false;
            }
            if (!peer.IsAlive)
            {
                // FIX R7: Targeted retry — only reconnect this specific peer
                Logger.LogAction("PEER", $"PushFileToSinglePeer: {peer.DeviceName} is not alive — attempting reconnect");
                peer.ConsecutiveFailures = 0;
                await Handshake(peer);
                if (!peer.IsAlive) return false;
            }

            bool sent = await TrySendFile(peer, filePath, title, itemType);
            if (!sent)
            {
                // FIX R7: Targeted retry — only reconnect this specific peer
                peer.IsAlive = false;
                peer.ConsecutiveFailures = 0;
                await Handshake(peer);
                if (peer.IsAlive)
                {
                    sent = await TrySendFile(peer, filePath, title, itemType);
                }
            }
            return sent;
        }


        private async Task<bool> TrySendFile(PeerConnection peer, string filePath, string title, string itemType)
        {
            if (!File.Exists(filePath))
            {
                Logger.LogAction("PEER", $"TrySendFile aborted: local file does not exist: {filePath}");
                return false;
            }

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

                        // FIX R1: 5-minute timeout prevents permanent SendSemaphore hold if peer stalls mid-transfer
                        using var wsFileCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                        await peer.SendSemaphore.WaitAsync(wsFileCts.Token);
                        try
                        {
                            // Send start frame
                            await peer.LiveSocket.SendAsync(new ArraySegment<byte>(startBytes), WebSocketMessageType.Text, true, wsFileCts.Token);

                            // 2. Stream the file in binary chunks (zero-allocation renting)
                            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                byte[] rentBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1_048_576); // 1MB chunks for high-throughput LAN
                                try
                                {
                                    int readBytes;
                                    long totalSent = 0;
                                    while ((readBytes = await fs.ReadAsync(rentBuffer, 0, rentBuffer.Length, wsFileCts.Token)) > 0)
                                    {
                                        totalSent += readBytes;
                                        bool isEnd = totalSent >= wsFileSize;
                                        await peer.LiveSocket.SendAsync(new ArraySegment<byte>(rentBuffer, 0, readBytes), WebSocketMessageType.Binary, isEnd, wsFileCts.Token);
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

                // ═══ HTTP FILE PUSH — tries ActiveUrl first, then fallback transport ═══
                // If LAN is active, try LAN first; if it fails, try CF (and vice versa).
                // This prevents total failure when one transport dies mid-session.
                var urlsToTry = new List<(string url, string transport)>();
                // Primary: whatever transport is currently active
                urlsToTry.Add((peer.ActiveUrl, peer.Transport));
                // Fallback: the OTHER transport (if available)
                bool lanEnabled = SettingsManager.Current.EnableLocalLAN;
                if (peer.Transport == "LAN" && !string.IsNullOrEmpty(peer.CloudflareUrl))
                    urlsToTry.Add((peer.CloudflareUrl, "Cloudflare"));
                else if (peer.Transport == "Cloudflare" && lanEnabled && !string.IsNullOrEmpty(peer.LanUrl))
                    urlsToTry.Add((peer.LanUrl, "LAN"));

                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();
                string actualFileName = Path.GetFileName(filePath);

                foreach (var (tryUrl, tryTransport) in urlsToTry)
                {
                    if (string.IsNullOrEmpty(tryUrl)) continue;
                    
                    // Handle comma-separated URLs (multiple LAN IPs)
                    var splitUrls = tryUrl.Contains(",")
                        ? tryUrl.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToArray()
                        : new[] { tryUrl.Trim() };

                    foreach (var singleUrl in splitUrls)
                    {
                        if (string.IsNullOrEmpty(singleUrl) || !singleUrl.StartsWith("http")) continue;
                        try
                        {
                            bool isCf = tryTransport == "Cloudflare";
                            long fileSize = new FileInfo(filePath).Length;

                            // Cloudflare large file → chunked upload
                            if (isCf && fileSize > 256 * 1024)
                            {
                                // Temporarily set ActiveUrl for chunked upload
                                string savedUrl = peer.ActiveUrl;
                                string savedTransport = peer.Transport;
                                peer.ActiveUrl = singleUrl;
                                peer.Transport = tryTransport;
                                try
                                {
                                    bool chunkedOk = await TrySendFileChunked(peer, filePath, actualFileName, title, itemType, pk);
                                    if (chunkedOk)
                                    {
                                        if (savedUrl != singleUrl)
                                        {
                                            Logger.LogAction("PEER", $"⚡ File delivered via fallback transport {tryTransport}: {singleUrl}");
                                            TransportSwitched?.Invoke(peer.DeviceId, tryTransport);
                                        }
                                        return true;
                                    }
                                }
                                finally
                                {
                                    // Restore only if chunked failed — if it succeeded we keep the new URL
                                    if (!peer.IsAlive || peer.ActiveUrl != singleUrl)
                                    {
                                        peer.ActiveUrl = savedUrl;
                                        peer.Transport = savedTransport;
                                    }
                                }
                                continue; // Try next URL
                            }

                            using var req = new HttpRequestMessage(HttpMethod.Post, $"{singleUrl.TrimEnd('/')}/api/sync_file");
                            if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);
                            // FIX: Send as X-File-Type to match what the receiver reads
                            req.Headers.TryAddWithoutValidation("X-File-Type", itemType);
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
                                // StreamContent disposes the FileStream when request completes
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
                                // MultipartFormDataContent disposes StreamContent which disposes FileStream
                            }

                            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                            var resp = await _sharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                            if (resp.IsSuccessStatusCode)
                            {
                                peer.LastSeen = DateTime.UtcNow;
                                peer.ConsecutiveFailures = 0;
                                if (peer.ActiveUrl != singleUrl)
                                {
                                    Logger.LogAction("PEER", $"⚡ File delivered via fallback {tryTransport}: {singleUrl}");
                                    peer.ActiveUrl = singleUrl;
                                    peer.Transport = tryTransport;
                                    TransportSwitched?.Invoke(peer.DeviceId, tryTransport);
                                }
                                Logger.LogAction("PEER", $"→ File '{title}' to {peer.DeviceName} via {tryTransport}");
                                return true;
                            }
                            Logger.LogAction("PEER", $"File to {peer.DeviceName} via {tryTransport} ({singleUrl}): HTTP {(int)resp.StatusCode}");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("PEER", $"File to {peer.DeviceName} via {tryTransport} ({singleUrl}) failed: {ex.Message}");
                        }
                    }
                }

                // All transports failed
                Logger.LogAction("PEER", $"File to {peer.DeviceName} failed on all transports");
                HandlePeerFailure(peer, "All transports exhausted");
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

            long fileSize = new FileInfo(filePath).Length;
            int totalChunks = (int)Math.Ceiling((double)fileSize / CHUNK_SIZE);

            Logger.LogAction("PEER", $"⚡ CF chunked (streamed): {fileName} ({fileSize / 1024}KB) → {totalChunks} chunks × {CHUNK_SIZE / 1024}KB, {MAX_PARALLEL_CHUNKS} parallel");

            // Upload chunks in parallel batches with unified cancellation source
            using var batchCts = new CancellationTokenSource();
            var semaphore = new SemaphoreSlim(MAX_PARALLEL_CHUNKS);
            var tasks = new List<Task<bool>>();

            for (int i = 0; i < totalChunks; i++)
            {
                int chunkIndex = i;
                long offset = (long)chunkIndex * CHUNK_SIZE;
                int length = (int)Math.Min(CHUNK_SIZE, fileSize - offset);

                tasks.Add(Task.Run(async () =>
                {
                    if (batchCts.IsCancellationRequested) return false;
                    await semaphore.WaitAsync();
                    try
                    {
                        if (batchCts.IsCancellationRequested) return false;

                        // FIX R9: 1 retry per chunk — prevents re-uploading entire file for a single blip
                        for (int attempt = 0; attempt < 2; attempt++)
                        {
                            try
                            {
                                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/upload_chunk");
                                if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);
                                req.Headers.TryAddWithoutValidation("X-Upload-Session", sessionId);
                                req.Headers.TryAddWithoutValidation("X-Chunk-Index", chunkIndex.ToString());

                                // Read exactly 'length' bytes from the file using pooled buffer (avoids LOH pressure)
                                var chunkData = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
                                try
                                {
                                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
                                {
                                    fs.Seek(offset, SeekOrigin.Begin);
                                    int readBytes = 0;
                                    while (readBytes < length)
                                    {
                                        int r = await fs.ReadAsync(chunkData, readBytes, length - readBytes, batchCts.Token);
                                        if (r == 0) break;
                                        readBytes += r;
                                    }
                                }

                                req.Content = new ByteArrayContent(chunkData, 0, length);
                                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(batchCts.Token);
                                linkedCts.CancelAfter(TimeSpan.FromSeconds(60));

                                var resp = await _sharedClient.SendAsync(req, linkedCts.Token);
                                if (resp.IsSuccessStatusCode) return true;

                                Logger.LogAction("PEER_CHUNK", $"Chunk {chunkIndex} attempt {attempt + 1} HTTP {(int)resp.StatusCode}");
                                }
                                finally { System.Buffers.ArrayPool<byte>.Shared.Return(chunkData); }
                            }
                            catch (Exception ex) when (attempt == 0 && !batchCts.IsCancellationRequested)
                            {
                                Logger.LogAction("PEER_CHUNK", $"Chunk {chunkIndex} attempt 1 failed: {ex.Message} — retrying...");
                                await Task.Delay(500, batchCts.Token);
                                continue;
                            }
                        }

                        // Both attempts failed
                        Logger.LogAction("PEER_CHUNK_ERROR", $"Chunk {chunkIndex} failed after 2 attempts — aborting batch");
                        try { batchCts.Cancel(); } catch { } // Best-effort: failure is acceptable
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("PEER_CHUNK_ERROR", $"Chunk {chunkIndex} upload failed: {ex.Message}");
                        try { batchCts.Cancel(); } catch { } // Best-effort: failure is acceptable
                        return false;
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
            finReq.Headers.TryAddWithoutValidation("X-Source-DeviceId", _myDeviceId);
            finReq.Headers.TryAddWithoutValidation("X-Item-Type", itemType);
            finReq.Content = new StringContent("", Encoding.UTF8, "application/json");

            using var finCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var finResp = await _sharedClient.SendAsync(finReq, finCts.Token);

            sw.Stop();
            if (finResp.IsSuccessStatusCode)
            {
                peer.LastSeen = DateTime.UtcNow;
                peer.ConsecutiveFailures = 0;
                double speed = fileSize / 1024.0 / (sw.ElapsedMilliseconds / 1000.0);
                Logger.LogAction("PEER", $"→ File '{title}' to {peer.DeviceName} via CF chunked ({sw.ElapsedMilliseconds}ms, {speed:F0} KB/s)");
                return true;
            }
            Logger.LogAction("PEER", $"CF chunked finalize failed: HTTP {(int)finResp.StatusCode}");
            return false;
        }
        /// <summary>
        /// Returns alive peers that are PC-type (for group file syncing).
        /// Falls back to all alive peers if DeviceType is not populated.
        /// </summary>
        public List<PeerConnection> GetAliveLanPcPeers()
        {
            var pcPeers = _peers.Values.Where(p => p.IsAlive && (string.IsNullOrEmpty(p.DeviceType) || p.DeviceType.Equals("PC", StringComparison.OrdinalIgnoreCase))).ToList();
            return pcPeers;
        }

        /// <summary>
        /// Sends a group item's zipped archive to a specific peer.
        /// </summary>
        public async Task<bool> TrySendGroupToPeer(PeerConnection peer, ClipboardItem groupItem)
        {
            if (string.IsNullOrEmpty(groupItem.ZippedArchivePath) || !File.Exists(groupItem.ZippedArchivePath))
                return false;
            return await TrySendFile(peer, groupItem.ZippedArchivePath, groupItem.FileName ?? "Group", "Archive");
        }
    }
}
