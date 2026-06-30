// ---------------------------------------------------------------
// LanTransferEngine — Dedicated TCP transfer engine
// Chunked file sends with pause/resume/cancel and real-time progress
// Optimized 1MB buffered receives with checkpoint ACKs
// ---------------------------------------------------------------
using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// High-performance dedicated TCP engine for PC-to-PC LAN file transfers.
    /// Uses Socket.SendFileAsync (Windows TransmitFile kernel API) for zero-copy sends.
    /// Separate from the HTTP server — no framing overhead, no multipart, no WebSocket frames.
    /// </summary>
    public class LanTransferEngine
    {
        public static LanTransferEngine? Instance { get; private set; }

        // ═══ Config ═══
        public const int TRANSFER_PORT = 8998;
        private const int SOCKET_BUFFER_SIZE = 4_194_304;      // 4MB socket buffers (covers BDP for Gigabit LAN)
        private const int RECEIVE_BUFFER_SIZE = 1_048_576;      // 1MB application read buffer
        private const int CHECKPOINT_INTERVAL = 16_777_216;     // 16MB between checkpoint ACKs
        private const int CONNECTION_HEADER_SIZE = 52;          // 16 (GUID) + 32 (HMAC) + 4 (version)
        private const uint PROTOCOL_VERSION = 1;
        private const int ACCEPT_BACKLOG = 20;
        private const int CONNECT_TIMEOUT_MS = 10_000;          // 10s TCP connect timeout
        private const int SPEED_SAMPLE_INTERVAL_MS = 200;       // Speed sample every 200ms

        private TcpListener? _listener;
        private CancellationTokenSource _cts = new();
        private volatile bool _isRunning;

        public bool IsRunning => _isRunning;
        public int Port => TRANSFER_PORT;

        public LanTransferEngine()
        {
            Instance = this;
        }

        // ═══ Lifecycle ═══

        public async Task StartAsync()
        {
            if (_isRunning) return;

            try
            {
                try { _cts?.Dispose(); } catch { } // Best-effort: failure is acceptable
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, TRANSFER_PORT);
                _listener.Start(ACCEPT_BACKLOG);
                _isRunning = true;
                Logger.LogAction("TCP_ENGINE", $"✅ Transfer engine started on port {TRANSFER_PORT}");

                // Start accept loop
                _ = Task.Run(() => AcceptLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                Logger.LogAction("TCP_ENGINE", $"❌ Failed to start transfer engine: {ex.Message}");
                _isRunning = false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            try { _cts.Cancel(); } catch { } // Best-effort: failure is acceptable
            try { _listener?.Stop(); } catch { } // Best-effort: failure is acceptable
            Logger.LogAction("TCP_ENGINE", "Transfer engine stopped.");
        }

        // ═══ Accept Loop ═══

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleIncomingConnection(tcpClient, ct));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("TCP_ENGINE", $"Accept error: {ex.Message}");
                    if (_isRunning) await Task.Delay(100, ct);
                }
            }
        }

        /// <summary>
        /// Handles an incoming TCP connection: validates header, routes to pending transfer session.
        /// </summary>
        private async Task HandleIncomingConnection(TcpClient client, CancellationToken ct)
        {
            Socket? socket = null;
            try
            {
                socket = client.Client;
                socket.NoDelay = false;
                socket.SendBufferSize = SOCKET_BUFFER_SIZE;
                socket.ReceiveBufferSize = SOCKET_BUFFER_SIZE;

                // Read connection header (52 bytes)
                byte[] header = new byte[CONNECTION_HEADER_SIZE];
                int headerRead = 0;
                while (headerRead < CONNECTION_HEADER_SIZE)
                {
                    using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    headerCts.CancelAfter(CONNECT_TIMEOUT_MS);
                    int n = await socket.ReceiveAsync(header.AsMemory(headerRead, CONNECTION_HEADER_SIZE - headerRead), SocketFlags.None, headerCts.Token);
                    if (n == 0) { Logger.LogAction("TCP_ENGINE", "Connection closed during header read"); return; }
                    headerRead += n;
                }

                // Parse header
                byte[] guidBytes = new byte[16];
                Buffer.BlockCopy(header, 0, guidBytes, 0, 16);
                Guid transferId = new Guid(guidBytes);

                byte[] receivedHmac = new byte[32];
                Buffer.BlockCopy(header, 16, receivedHmac, 0, 32);

                uint version = BitConverter.ToUInt32(header, 48);
                if (version != PROTOCOL_VERSION)
                {
                    Logger.LogAction("TCP_ENGINE", $"Protocol version mismatch: got {version}, expected {PROTOCOL_VERSION}");
                    try { await new NetworkStream(socket, false).WriteAsync(new byte[] { 0xFF }, 0, 1); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                // Validate HMAC using pairing key
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey))
                {
                    Logger.LogAction("TCP_ENGINE", "No pairing key — rejecting TCP connection");
                    try { await new NetworkStream(socket, false).WriteAsync(new byte[] { 0xFF }, 0, 1); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                byte[] expectedHmac = ComputeHmac(guidBytes, pairingKey);
                if (!CryptographicOperations.FixedTimeEquals(receivedHmac, expectedHmac))
                {
                    Logger.LogAction("TCP_ENGINE", $"⛔ HMAC validation failed for transfer {transferId}");
                    try { await new NetworkStream(socket, false).WriteAsync(new byte[] { 0xFF }, 0, 1); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                Logger.LogAction("TCP_ENGINE", $"✅ Authenticated TCP connection for transfer {transferId}");

                // Look up the pending receive session in the transfer manager
                var session = LanTransferManager.Instance?.GetPendingReceiveSession(transferId);
                if (session == null)
                {
                    Logger.LogAction("TCP_ENGINE", $"No pending session for transfer {transferId} — rejecting");
                    try { await new NetworkStream(socket, false).WriteAsync(new byte[] { 0xFF }, 0, 1); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                // Start receiving file data
                await ReceiveFileAsync(socket, session, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogAction("TCP_ENGINE", $"Incoming connection error: {ex.Message}");
            }
            finally
            {
                try { client.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        // ═══ SEND — Chunked transfer with progress ═══

        /// <summary>
        /// Sends a file to a peer using a dedicated TCP connection.
        /// Supports pause/resume/cancel and real-time progress tracking.
        /// </summary>
        public async Task SendFileAsync(string peerIp, int peerPort, LanTransferSession session)
        {
            Socket? socket = null;
            try
            {
                session.State = TransferState.Connecting;

                // Create and configure socket
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = false; // Nagle ON for bulk throughput
                socket.SendBufferSize = SOCKET_BUFFER_SIZE;
                socket.ReceiveBufferSize = SOCKET_BUFFER_SIZE;
                socket.LingerState = new LingerOption(true, 10);

                // Connect with timeout
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken);
                connectCts.CancelAfter(CONNECT_TIMEOUT_MS);
                await socket.ConnectAsync(peerIp, peerPort, connectCts.Token);

                Logger.LogAction("TCP_ENGINE", $"📤 Connected to {peerIp}:{peerPort} for transfer {session.TransferId}");

                // Send connection header
                byte[] header = BuildConnectionHeader(session.TransferId);
                await socket.SendAsync(header, SocketFlags.None, session.CancellationToken);

                session.State = TransferState.Transferring;
                session.StartTime = DateTime.UtcNow;

                long resumeFrom = session.BytesTransferred;
                long fileSize = session.FileSize;

                if (resumeFrom > 0)
                {
                    Logger.LogAction("TCP_ENGINE", $"📤 Resuming send from offset {LanTransferSession.FormatBytes(resumeFrom)}");
                }

                // Always use chunked send — supports pause/resume/cancel and provides real-time progress.
                // Zero-copy (SendFileAsync) can't be paused, cancelled, or progress-tracked.
                await SendFileChunkedAsync(socket, session, resumeFrom);

                if (session.State == TransferState.Transferring)
                {
                    session.MarkCompleted();
                    Logger.LogAction("TCP_ENGINE", $"✅ Send completed: {session.FileName} ({LanTransferSession.FormatBytes(fileSize)}) peak {session.PeakSpeedText}");
                }
            }
            catch (OperationCanceledException)
            {
                if (session.State != TransferState.Cancelled && session.State != TransferState.Paused)
                {
                    session.MarkFailed("Transfer cancelled");
                    LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                }
            }
            catch (SocketException ex)
            {
                session.MarkFailed($"Network error: {ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"❌ Send socket error at {LanTransferSession.FormatBytes(session.BytesTransferred)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                session.MarkFailed($"Transfer error: {ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"❌ Send error: {ex.Message}");
            }
            finally
            {
                try { socket?.Shutdown(SocketShutdown.Both); } catch { } // Best-effort: failure is acceptable
                try { socket?.Close(); } catch { } // Best-effort: failure is acceptable
                try { socket?.Dispose(); } catch { } // Best-effort: failure is acceptable
            }
        }


        /// <summary>
        /// Chunked send with manual FileStream reading.
        /// Supports pause/resume/cancel with real-time progress tracking.
        /// Uses 1MB buffer from ArrayPool for high throughput.
        /// </summary>
        private async Task SendFileChunkedAsync(Socket socket, LanTransferSession session, long resumeFrom)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(RECEIVE_BUFFER_SIZE);
            try
            {
                using var fs = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, RECEIVE_BUFFER_SIZE,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                fs.Seek(resumeFrom, SeekOrigin.Begin);
                long totalSent = resumeFrom;
                long lastSpeedSample = Environment.TickCount64;

                while (totalSent < session.FileSize)
                {
                    session.CancellationToken.ThrowIfCancellationRequested();

                    // Check pause
                    await session.WaitIfPausedAsync();

                    int toRead = (int)Math.Min(buffer.Length, session.FileSize - totalSent);
                    int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, toRead), session.CancellationToken);
                    if (bytesRead == 0) break;

                    // Send all read bytes
                    int bytesSent = 0;
                    while (bytesSent < bytesRead)
                    {
                        int sent = await socket.SendAsync(buffer.AsMemory(bytesSent, bytesRead - bytesSent), SocketFlags.None, session.CancellationToken);
                        if (sent == 0) throw new SocketException((int)SocketError.ConnectionReset);
                        bytesSent += sent;
                    }

                    totalSent += bytesRead;
                    session.BytesTransferred = totalSent;

                    // Speed sample every ~200ms
                    long nowMs = Environment.TickCount64;
                    if (nowMs - lastSpeedSample >= SPEED_SAMPLE_INTERVAL_MS)
                    {
                        session.RecordSpeedSample(totalSent);
                        lastSpeedSample = nowMs;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ═══ RECEIVE — Optimized buffered receive ═══

        /// <summary>
        /// Receives file data from a TCP socket with 1MB buffered writes.
        /// Checkpoints every 16MB for crash-recovery resume support.
        /// </summary>
        public async Task ReceiveFileAsync(Socket socket, LanTransferSession session, CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(RECEIVE_BUFFER_SIZE);
            try
            {
                session.State = TransferState.Transferring;
                session.StartTime = DateTime.UtcNow;

                long resumeFrom = session.BytesTransferred;
                string filePath = session.FilePath;
                long fileSize = session.FileSize;

                // Create directory if needed
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);

                using var fs = new FileStream(filePath,
                    resumeFrom > 0 ? FileMode.OpenOrCreate : FileMode.Create,
                    FileAccess.Write, FileShare.None,
                    RECEIVE_BUFFER_SIZE,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (resumeFrom > 0)
                {
                    fs.Seek(resumeFrom, SeekOrigin.Begin);
                    fs.SetLength(resumeFrom); // Remove stale tail from previous partial write
                    Logger.LogAction("TCP_ENGINE", $"📥 Resuming receive from offset {LanTransferSession.FormatBytes(resumeFrom)}");
                }

                long totalReceived = resumeFrom;
                long lastCheckpoint = resumeFrom;
                long lastSpeedSample = Environment.TickCount64;

                while (totalReceived < fileSize)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    // Flush to disk before blocking on pause — prevents data loss on crash while paused
                    if (session.IsPaused)
                    {
                        await fs.FlushAsync(linkedCts.Token);
                        LanTransferManager.Instance?.PersistCheckpoints();
                    }

                    // Check pause
                    await session.WaitIfPausedAsync();

                    int toRead = (int)Math.Min(buffer.Length, fileSize - totalReceived);
                    int bytesRead = await socket.ReceiveAsync(buffer.AsMemory(0, toRead), SocketFlags.None, linkedCts.Token);

                    if (bytesRead == 0)
                    {
                        // Connection closed prematurely — flush and persist progress for resume
                        if (totalReceived < fileSize)
                        {
                            await fs.FlushAsync();
                            session.MarkFailed($"Connection lost at {LanTransferSession.FormatBytes(totalReceived)} of {LanTransferSession.FormatBytes(fileSize)}");
                            LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                            Logger.LogAction("TCP_ENGINE", $"❌ Connection lost during receive: {session.FileName} — checkpoint saved at {LanTransferSession.FormatBytes(totalReceived)}");
                        }
                        return;
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), linkedCts.Token);
                    totalReceived += bytesRead;
                    session.BytesTransferred = totalReceived;

                    // Speed sample
                    long nowMs = Environment.TickCount64;
                    if (nowMs - lastSpeedSample >= SPEED_SAMPLE_INTERVAL_MS)
                    {
                        session.RecordSpeedSample(totalReceived);
                        lastSpeedSample = nowMs;
                    }

                    // Checkpoint every 16MB — flush to disk and notify manager for persistence
                    if (totalReceived - lastCheckpoint >= CHECKPOINT_INTERVAL)
                    {
                        await fs.FlushAsync(linkedCts.Token);
                        lastCheckpoint = totalReceived;
                        LanTransferManager.Instance?.PersistCheckpoints();

                        // Send checkpoint ACK via WebSocket
                        LanTransferManager.Instance?.SendCheckpointAck(session);
                    }
                }

                // Final flush
                await fs.FlushAsync(linkedCts.Token);

                // Verify file integrity if hash was provided
                if (!string.IsNullOrEmpty(session.XxHash64))
                {
                    try
                    {
                        using var hashStream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
                        var hasher = new System.IO.Hashing.XxHash64();
                        byte[] hashBuf = new byte[1024 * 1024];
                        int hashRead;
                        while ((hashRead = await hashStream.ReadAsync(hashBuf, linkedCts.Token)) > 0)
                            hasher.Append(hashBuf.AsSpan(0, hashRead));
                        string computed = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
                        if (computed != session.XxHash64.ToLowerInvariant())
                        {
                            session.MarkFailed($"Integrity check failed: expected {session.XxHash64}, got {computed}");
                            Logger.LogAction("TCP_ENGINE", $"❌ Hash mismatch for {session.FileName}");
                            return;
                        }
                        Logger.LogAction("TCP_ENGINE", $"✅ Hash verified for {session.FileName}");
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Logger.LogAction("TCP_ENGINE", $"⚠️ Hash verification skipped: {ex.Message}");
                    }
                }

                if (session.State == TransferState.Transferring)
                {
                    session.MarkCompleted();
                    Logger.LogAction("TCP_ENGINE", $"✅ Receive completed: {session.FileName} ({LanTransferSession.FormatBytes(fileSize)}) peak {session.PeakSpeedText}");

                    // Inject received file into clipboard
                    LanTransferManager.Instance?.OnReceiveCompleted(session);
                }
            }
            catch (OperationCanceledException)
            {
                if (session.State != TransferState.Cancelled && session.State != TransferState.Paused)
                {
                    session.MarkFailed("Transfer cancelled");
                    LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                }
            }
            catch (SocketException ex)
            {
                session.MarkFailed($"Network error: {ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"❌ Receive socket error at {LanTransferSession.FormatBytes(session.BytesTransferred)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                session.MarkFailed($"Receive error: {ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"❌ Receive error: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ═══ Protocol Helpers ═══

        /// <summary>
        /// Builds the 52-byte TCP connection header:
        /// [16 bytes GUID] [32 bytes HMAC-SHA256] [4 bytes version]
        /// </summary>
        public static byte[] BuildConnectionHeader(Guid transferId)
        {
            byte[] header = new byte[CONNECTION_HEADER_SIZE];
            byte[] guidBytes = transferId.ToByteArray();
            Buffer.BlockCopy(guidBytes, 0, header, 0, 16);

            string pairingKey = DevicePairingManager.EnsurePairingKey();
            byte[] hmac = ComputeHmac(guidBytes, pairingKey);
            Buffer.BlockCopy(hmac, 0, header, 16, 32);

            byte[] versionBytes = BitConverter.GetBytes(PROTOCOL_VERSION);
            Buffer.BlockCopy(versionBytes, 0, header, 48, 4);

            return header;
        }

        /// <summary>
        /// Computes HMAC-SHA256 of the transfer ID using the pairing key.
        /// </summary>
        private static byte[] ComputeHmac(byte[] data, string key)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            return hmac.ComputeHash(data);
        }

        /// <summary>
        /// Extracts the IP address from a peer URL (e.g., "http://192.168.1.5:8999" → "192.168.1.5").
        /// Handles comma-separated multi-IP URLs by returning the first one.
        /// </summary>
        public static string ExtractIpFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                // Handle comma-separated LAN URLs
                string first = url.Split(',')[0].Trim();
                var uri = new Uri(first);
                return uri.Host;
            }
            catch
            {
                return "";
            }
        }
    }
}
