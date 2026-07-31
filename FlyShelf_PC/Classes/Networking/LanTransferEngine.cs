// ---------------------------------------------------------------
// LanTransferEngine — Dedicated TCP transfer engine
// Chunked file sends with pause/resume/cancel and real-time progress
// Optimized 1MB buffered receives with checkpoint ACKs
// ---------------------------------------------------------------
using System;
using System.Buffers;
using System.Globalization;
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
        private const int DEFAULT_TRANSFER_PORT = 8998;
        public static int TRANSFER_PORT { get; private set; } = DEFAULT_TRANSFER_PORT;
        private readonly int _port;
        private const int SOCKET_BUFFER_SIZE = 4_194_304;      // 4MB socket buffers (covers BDP for Gigabit LAN)
        private const int RECEIVE_BUFFER_SIZE = 1_048_576;      // 1MB application read buffer
        private const int CHECKPOINT_INTERVAL = 16_777_216;     // 16MB between checkpoint ACKs
        private const int CONNECTION_HEADER_SIZE = 52;          // 16 (GUID) + 32 (HMAC) + 4 (version)
        private const uint PROTOCOL_VERSION = 1;
        private const int ACCEPT_BACKLOG = 20;
        private const int CONNECT_TIMEOUT_MS = 10_000;          // 10s TCP connect timeout
        private const int SPEED_SAMPLE_INTERVAL_MS = 200;       // Speed sample every 200ms

        // Parallel chunked transfer constants
        private const long CHUNKED_THRESHOLD = 100 * 1024 * 1024;  // 100MB — files above this use parallel chunks
        private const int PARALLEL_CHUNKS_DEFAULT = 4;              // 4 parallel streams for 100MB-500MB
        private const int PARALLEL_CHUNKS_LARGE = 6;                // 6 parallel streams for >500MB 
        private const int CHUNK_HEADER_SIZE = 20;                   // 4(chunkIndex) + 8(chunkStart) + 8(chunkEnd)

        private TcpListener? _listener;
        private CancellationTokenSource _cts = new();
        private volatile bool _isRunning;

        public bool IsRunning => _isRunning;
        public int Port => _port;

        public LanTransferEngine(int port = DEFAULT_TRANSFER_PORT)
        {
            _port = port;
            TRANSFER_PORT = port;
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
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start(ACCEPT_BACKLOG);
                _isRunning = true;
                Logger.LogAction("TCP_ENGINE", $"Transfer engine started on port {_port}");

                // Start accept loop
                _ = Task.Run(() => AcceptLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                Logger.LogAction("TCP_ENGINE", $"Failed to start transfer engine: {ex.Message}");
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

        // PC-10 fix: bound concurrent incoming connections (backpressure).
        // Previously every accepted TCP connection spawned an unbounded Task.Run,
        // allowing a LAN peer (or port scanner) to exhaust threads/memory (LAN DoS).
        // Increased from 16→32 to support multiple parallel chunked transfers
        // (6 streams × 2 concurrent transfers = 12, plus headroom for others).
        private const int MAX_CONCURRENT_CONNECTIONS = 32;
        private readonly System.Threading.SemaphoreSlim _connectionGate = new(MAX_CONCURRENT_CONNECTIONS, MAX_CONCURRENT_CONNECTIONS);

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var tcpClient = await _listener!.AcceptTcpClientAsync(ct);
                    if (!await _connectionGate.WaitAsync(0, ct))
                    {
                        // At capacity - reject immediately instead of queueing unbounded work
                        Logger.LogAction("TCP_ENGINE", $"Connection rejected: {MAX_CONCURRENT_CONNECTIONS} concurrent connections already active");
                        try { tcpClient.Dispose(); } catch { }
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        try { await HandleIncomingConnection(tcpClient, ct); }
                        finally { _connectionGate.Release(); }
                    });
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

                // TCP keep-alive: prevents NAT/firewall from killing idle connections during pause
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    byte[] keepAliveValues = new byte[12];
                    BitConverter.GetBytes(1).CopyTo(keepAliveValues, 0);
                    BitConverter.GetBytes(60000).CopyTo(keepAliveValues, 4);
                    BitConverter.GetBytes(10000).CopyTo(keepAliveValues, 8);
                    socket.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
                }
                catch { /* Fallback: OS default keep-alive is fine */ }

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
                    try { await socket.SendAsync(new byte[] { 0xFF }, SocketFlags.None); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                // Validate HMAC using pairing key
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey))
                {
                    Logger.LogAction("TCP_ENGINE", "No pairing key — rejecting TCP connection");
                    try { await socket.SendAsync(new byte[] { 0xFF }, SocketFlags.None); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                byte[] expectedHmac = ComputeHmac(guidBytes, pairingKey);
                if (!CryptographicOperations.FixedTimeEquals(receivedHmac, expectedHmac))
                {
                    Logger.LogAction("TCP_ENGINE", $"HMAC validation failed for transfer {transferId}");
                    try { await socket.SendAsync(new byte[] { 0xFF }, SocketFlags.None); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                Logger.LogAction("TCP_ENGINE", $"Authenticated TCP connection for transfer {transferId}");

                // Look up the pending receive session in the transfer manager
                var session = LanTransferManager.Instance?.GetPendingReceiveSession(transferId);
                if (session == null)
                {
                    Logger.LogAction("TCP_ENGINE", $"No pending session for transfer {transferId} — rejecting");
                    try { await socket.SendAsync(new byte[] { 0xFF }, SocketFlags.None); } catch { } // Best-effort: failure is acceptable
                    return;
                }

                // Check if this is a chunked transfer
                if (session.IsChunked)
                {
                    // Read chunk header: [chunkIndex:4][chunkStart:8][chunkEnd:8]
                    // Read chunk header with timeout
                    byte[] chunkHeader = new byte[CHUNK_HEADER_SIZE];
                    int chunkRead = 0;
                    using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    chunkCts.CancelAfter(CONNECT_TIMEOUT_MS);
                    while (chunkRead < CHUNK_HEADER_SIZE)
                    {
                        int n = await socket.ReceiveAsync(chunkHeader.AsMemory(chunkRead, CHUNK_HEADER_SIZE - chunkRead), SocketFlags.None, chunkCts.Token);
                        if (n == 0) return;
                        chunkRead += n;
                    }
                    int chunkIndex = BitConverter.ToInt32(chunkHeader, 0);
                    long chunkStart = BitConverter.ToInt64(chunkHeader, 4);
                    long chunkEnd = BitConverter.ToInt64(chunkHeader, 12);

                    Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex} connection for transfer {transferId}");
                    await ReceiveChunkAsync(socket, session, chunkIndex, chunkStart, chunkEnd, ct);

                    // Check if ALL chunks are complete — use Interlocked to ensure only ONE thread does hash verification
                    if (session.AllChunksCompleted)
                    {
                        // Race guard: only the first thread to reach here performs verification
                        if (Interlocked.CompareExchange(ref session._hashVerificationStarted, 1, 0) != 0)
                        {
                            Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex} — hash verification already in progress by another thread");
                            return; // Another chunk thread is handling finalization
                        }

                        Logger.LogAction("TCP_ENGINE", $"All {session.NumChunks} chunks received for {session.FileName} — verifying hash");

                        // Hash verification
                        if (!string.IsNullOrEmpty(session.XxHash64))
                        {
                            try
                            {
                                using var hashStream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
                                var hasher = new System.IO.Hashing.XxHash64();
                                byte[] hashBuf = new byte[1024 * 1024];
                                int hashRead;
                                while ((hashRead = await hashStream.ReadAsync(hashBuf, ct)) > 0)
                                    hasher.Append(hashBuf.AsSpan(0, hashRead));
                                string computed = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
                                if (!string.Equals(computed, session.XxHash64, StringComparison.OrdinalIgnoreCase))
                                {
                                    session.MarkFailed($"Integrity check failed: expected {session.XxHash64}, got {computed}");
                                    Logger.LogAction("TCP_ENGINE", $"Hash mismatch for chunked {session.FileName} — deleting");
                                    try { File.Delete(session.FilePath); } catch { }
                                    LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                                    return;
                                }
                                Logger.LogAction("TCP_ENGINE", $"Hash verified for chunked {session.FileName}");
                            }
                            catch (OperationCanceledException) { return; }
                            catch (Exception ex)
                            {
                                Logger.LogAction("TCP_ENGINE", $"Hash verification skipped: {ex.Message}");
                            }
                        }

                        if (session.State == TransferState.Transferring)
                        {
                            session.MarkCompleted();
                            Logger.LogAction("TCP_ENGINE", $"Parallel receive completed: {session.FileName} ({LanTransferSession.FormatBytes(session.FileSize)}) peak {session.PeakSpeedText}");
                            LanTransferManager.Instance?.OnReceiveCompleted(session);
                        }
                    }
                }
                else
                {
                    // Standard single-stream receive
                    await ReceiveFileAsync(socket, session, ct);
                }
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

                // TCP keep-alive: prevents NAT/firewall from killing idle connections during pause
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    // Windows: idle=60s, interval=10s, retries handled by OS
                    byte[] keepAliveValues = new byte[12];
                    BitConverter.GetBytes(1).CopyTo(keepAliveValues, 0);      // on/off
                    BitConverter.GetBytes(60000).CopyTo(keepAliveValues, 4);   // idle time ms
                    BitConverter.GetBytes(10000).CopyTo(keepAliveValues, 8);   // interval ms
                    socket.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
                }
                catch { /* Fallback: OS default keep-alive is fine */ }

                // Connect with timeout
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken);
                connectCts.CancelAfter(CONNECT_TIMEOUT_MS);
                await socket.ConnectAsync(peerIp, peerPort, connectCts.Token);

                Logger.LogAction("TCP_ENGINE", $"Connected to {peerIp}:{peerPort} for transfer {session.TransferId}");

                // Send connection header
                byte[] header = BuildConnectionHeader(session.TransferId);
                await socket.SendAsync(header, SocketFlags.None, session.CancellationToken);

                session.State = TransferState.Transferring;
                session.StartTime = DateTime.UtcNow;

                long resumeFrom = session.BytesTransferred;
                long fileSize = session.FileSize;

                if (resumeFrom > 0)
                {
                    Logger.LogAction("TCP_ENGINE", $"Resuming send from offset {LanTransferSession.FormatBytes(resumeFrom)}");
                }

                // Always use chunked send — supports pause/resume/cancel and provides real-time progress.
                // Zero-copy (SendFileAsync) can't be paused, cancelled, or progress-tracked.
                await SendFileChunkedAsync(socket, session, resumeFrom);

                if (session.State == TransferState.Transferring)
                {
                    session.MarkCompleted();
                    Logger.LogAction("TCP_ENGINE", $"Send completed: {session.FileName} ({LanTransferSession.FormatBytes(fileSize)}) peak {session.PeakSpeedText}");
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
                Logger.LogAction("TCP_ENGINE", $"Send socket error at {LanTransferSession.FormatBytes(session.BytesTransferred)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                session.MarkFailed($"Transfer error: {ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"Send error: {ex.Message}");
            }
            finally
            {
                try { socket?.Shutdown(SocketShutdown.Both); } catch { } // Best-effort: failure is acceptable
                try { socket?.Close(); } catch { } // Best-effort: failure is acceptable
                try { socket?.Dispose(); } catch { } // Best-effort: failure is acceptable
            }
        }

        /// <summary>
        /// Sends a file using N parallel TCP connections, each streaming a different byte range.
        /// Provides ~2-4x throughput improvement on LAN for large files.
        /// </summary>
        public async Task SendFileParallelAsync(string peerIp, int peerPort, LanTransferSession session)
        {
            session.State = TransferState.Connecting;
            int numChunks = session.NumChunks;
            long fileSize = session.FileSize;
            long chunkSize = session.ChunkSize;

            Logger.LogAction("TCP_ENGINE", $"Parallel send: {session.FileName} ({LanTransferSession.FormatBytes(fileSize)}) → {numChunks} chunks of {LanTransferSession.FormatBytes(chunkSize)}");

            var tasks = new Task[numChunks];
            var sockets = new Socket?[numChunks];

            try
            {
                session.State = TransferState.Transferring;
                session.StartTime = DateTime.UtcNow;

                for (int i = 0; i < numChunks; i++)
                {
                    int chunkIndex = i;
                    long start = (long)chunkIndex * chunkSize;
                    long end = Math.Min(start + chunkSize, fileSize);

                    tasks[i] = Task.Run(async () =>
                    {
                        Socket? sock = null;
                        try
                        {
                            sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            sockets[chunkIndex] = sock;
                            sock.NoDelay = false;
                            sock.SendBufferSize = SOCKET_BUFFER_SIZE;
                            sock.ReceiveBufferSize = SOCKET_BUFFER_SIZE;
                            sock.LingerState = new LingerOption(true, 10);
                            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken);
                            connectCts.CancelAfter(CONNECT_TIMEOUT_MS);
                            await sock.ConnectAsync(peerIp, peerPort, connectCts.Token);

                            // Send connection header (same transferId for all chunks)
                            byte[] header = BuildConnectionHeader(session.TransferId);
                            await sock.SendAsync(header, SocketFlags.None, session.CancellationToken);

                            // Send chunk header: [chunkIndex:4][chunkStart:8][chunkEnd:8]
                            byte[] chunkHeader = new byte[CHUNK_HEADER_SIZE];
                            BitConverter.GetBytes(chunkIndex).CopyTo(chunkHeader, 0);
                            BitConverter.GetBytes(start).CopyTo(chunkHeader, 4);
                            BitConverter.GetBytes(end).CopyTo(chunkHeader, 12);
                            await sock.SendAsync(chunkHeader, SocketFlags.None, session.CancellationToken);

                            // Send chunk data
                            await SendChunkDataAsync(sock, session, chunkIndex, start, end);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex} send failed: {ex.Message}");
                            throw;
                        }
                        finally
                        {
                            try { sock?.Shutdown(SocketShutdown.Both); } catch { }
                            try { sock?.Close(); } catch { }
                            try { sock?.Dispose(); } catch { }
                        }
                    });
                }

                await Task.WhenAll(tasks);

                if (session.State == TransferState.Transferring)
                {
                    session.MarkCompleted();
                    Logger.LogAction("TCP_ENGINE", $"Parallel send completed: {session.FileName} peak {session.PeakSpeedText}");
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
            catch (Exception ex)
            {
                session.MarkFailed($"Network error: {ex.InnerException?.Message ?? ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"Parallel send error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a single chunk's data over a TCP connection.
        /// Reads from [chunkStart, chunkEnd) of the file.
        /// </summary>
        private async Task SendChunkDataAsync(Socket socket, LanTransferSession session, int chunkIndex, long chunkStart, long chunkEnd)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(RECEIVE_BUFFER_SIZE);
            try
            {
                using var fs = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, RECEIVE_BUFFER_SIZE,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                long resumeFrom = session.ChunkProgress.GetValueOrDefault(chunkIndex, 0);
                fs.Seek(chunkStart + resumeFrom, SeekOrigin.Begin);
                long totalSent = resumeFrom;
                long chunkLength = chunkEnd - chunkStart;
                long lastSpeedSample = Environment.TickCount64;

                while (totalSent < chunkLength)
                {
                    session.CancellationToken.ThrowIfCancellationRequested();
                    await session.WaitIfPausedAsync();

                    int toRead = (int)Math.Min(buffer.Length, chunkLength - totalSent);
                    int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, toRead), session.CancellationToken);
                    if (bytesRead == 0) break;

                    int bytesSent = 0;
                    while (bytesSent < bytesRead)
                    {
                        int sent = await socket.SendAsync(buffer.AsMemory(bytesSent, bytesRead - bytesSent), SocketFlags.None, session.CancellationToken);
                        if (sent == 0) throw new SocketException((int)System.Net.Sockets.SocketError.ConnectionReset);
                        bytesSent += sent;
                    }

                    totalSent += bytesRead;
                    session.ChunkProgress[chunkIndex] = totalSent;
                    session.AddBytesTransferred(bytesRead); // Atomic add to parent session total

                    long nowMs = Environment.TickCount64;
                    if (nowMs - lastSpeedSample >= SPEED_SAMPLE_INTERVAL_MS)
                    {
                        session.RecordSpeedSample(session.BytesTransferred);
                        lastSpeedSample = nowMs;
                    }
                }

                session.MarkChunkCompleted(chunkIndex);
                Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex}/{session.NumChunks} sent: {LanTransferSession.FormatBytes(chunkLength)}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
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
                    Logger.LogAction("TCP_ENGINE", $"Resuming receive from offset {LanTransferSession.FormatBytes(resumeFrom)}");
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
                            Logger.LogAction("TCP_ENGINE", $"Connection lost during receive: {session.FileName} — checkpoint saved at {LanTransferSession.FormatBytes(totalReceived)}");
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

                        // Update placeholder card in clipboard shelf with live progress
                        if (session.Placeholder != null)
                        {
                            double progress = (double)totalReceived / fileSize * 100.0;
                            session.Placeholder.TransferProgress = Math.Min(progress, 99.0);
                            session.Placeholder.TransferStatusText = $"Receiving... {progress:F0}% ({session.SpeedText})";
                        }
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
                        if (!string.Equals(computed, session.XxHash64, StringComparison.OrdinalIgnoreCase))
                        {
                            session.MarkFailed($"Integrity check failed: expected {session.XxHash64}, got {computed}");
                            Logger.LogAction("TCP_ENGINE", $"Hash mismatch for {session.FileName} — deleting corrupted file");
                            // Delete the corrupted file to prevent it from being injected into clipboard
                            try { File.Delete(session.FilePath); } catch (Exception delEx) { Logger.LogAction("TCP_ENGINE", $"Failed to delete corrupted file: {delEx.Message}"); }
                            // Persist the failure so checkpoint doesn't try to resume from corrupt data
                            LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                            return;
                        }
                        Logger.LogAction("TCP_ENGINE", $"Hash verified for {session.FileName}");
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Logger.LogAction("TCP_ENGINE", $"Hash verification skipped: {ex.Message}");
                    }
                }

                if (session.State == TransferState.Transferring)
                {
                    session.MarkCompleted();
                    Logger.LogAction("TCP_ENGINE", $"Receive completed: {session.FileName} ({LanTransferSession.FormatBytes(fileSize)}) peak {session.PeakSpeedText}");

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
                Logger.LogAction("TCP_ENGINE", $"Receive socket error at {LanTransferSession.FormatBytes(session.BytesTransferred)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                session.MarkFailed($"Receive error: {ex.Message}");
                LanTransferManager.Instance?.PersistCheckpointsIncludingFailed(session);
                Logger.LogAction("TCP_ENGINE", $"Receive error: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Receives a single chunk of a parallel transfer. Writes to the shared file at the correct offset.
        /// Multiple instances run concurrently for different byte ranges of the same file.
        /// </summary>
        public async Task ReceiveChunkAsync(Socket socket, LanTransferSession session, int chunkIndex, long chunkStart, long chunkEnd, CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(RECEIVE_BUFFER_SIZE);
            try
            {
                long chunkLength = chunkEnd - chunkStart;
                long resumeFrom = session.ChunkProgress.GetValueOrDefault(chunkIndex, 0);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);

                // Open file with ReadWrite + share to allow concurrent chunk writers
                using var fs = new FileStream(session.FilePath,
                    FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite,
                    RECEIVE_BUFFER_SIZE,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                fs.Seek(chunkStart + resumeFrom, SeekOrigin.Begin);
                long totalReceived = resumeFrom;
                long lastSpeedSample = Environment.TickCount64;
                long lastCheckpoint = resumeFrom;

                Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex}/{session.NumChunks}: receiving {LanTransferSession.FormatBytes(chunkLength)} (offset {LanTransferSession.FormatBytes(chunkStart)})");

                while (totalReceived < chunkLength)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    if (session.IsPaused)
                    {
                        await fs.FlushAsync(linkedCts.Token);
                    }
                    await session.WaitIfPausedAsync();

                    int toRead = (int)Math.Min(buffer.Length, chunkLength - totalReceived);
                    int bytesRead = await socket.ReceiveAsync(buffer.AsMemory(0, toRead), SocketFlags.None, linkedCts.Token);

                    if (bytesRead == 0)
                    {
                        if (totalReceived < chunkLength)
                        {
                            await fs.FlushAsync();
                            Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex} connection lost at {LanTransferSession.FormatBytes(totalReceived)} of {LanTransferSession.FormatBytes(chunkLength)}");
                            throw new SocketException((int)System.Net.Sockets.SocketError.ConnectionReset);
                        }
                        return;
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), linkedCts.Token);
                    totalReceived += bytesRead;
                    session.ChunkProgress[chunkIndex] = totalReceived;
                    session.AddBytesTransferred(bytesRead);

                    long nowMs = Environment.TickCount64;
                    if (nowMs - lastSpeedSample >= SPEED_SAMPLE_INTERVAL_MS)
                    {
                        session.RecordSpeedSample(session.BytesTransferred);
                        lastSpeedSample = nowMs;

                        if (session.Placeholder != null)
                        {
                            double progress = (double)session.BytesTransferred / session.FileSize * 100.0;
                            session.Placeholder.TransferProgress = Math.Min(progress, 99.0);
                            session.Placeholder.TransferStatusText = $"Receiving... {progress:F0}% ({session.SpeedText}) {session.NumChunks} streams";
                        }
                    }

                    if (totalReceived - lastCheckpoint >= CHECKPOINT_INTERVAL)
                    {
                        await fs.FlushAsync(linkedCts.Token);
                        lastCheckpoint = totalReceived;
                        LanTransferManager.Instance?.PersistCheckpoints();
                    }
                }

                await fs.FlushAsync(linkedCts.Token);
                session.MarkChunkCompleted(chunkIndex);
                Logger.LogAction("TCP_ENGINE", $"Chunk {chunkIndex}/{session.NumChunks} received: {LanTransferSession.FormatBytes(chunkLength)}");
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
