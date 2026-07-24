// ---------------------------------------------------------------
// LanTransferManager — Orchestrator for all PC-to-PC transfers
// Manages active/completed sessions, checkpoint persistence,
// WebSocket control message routing, file injection
// ---------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Singleton orchestrator for all LAN file transfers.
    /// Routes WebSocket control messages, manages sessions, persists checkpoints.
    /// </summary>
    public class LanTransferManager : IDisposable
    {
        public static LanTransferManager? Instance { get; private set; }

        // ═══ Sessions ═══
        private readonly ConcurrentDictionary<Guid, LanTransferSession> _activeSessions = new();
        private readonly ConcurrentDictionary<Guid, LanTransferSession> _pendingReceives = new(); // Awaiting TCP connection

        // Observable collections for UI binding (must be updated on dispatcher thread)
        public ObservableCollection<LanTransferSession> ActiveTransfers { get; } = new();
        public ObservableCollection<LanTransferSession> CompletedTransfers { get; } = new();

        // ═══ Checkpoint persistence ═══
        private static readonly string _checkpointFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyShelf", "transfer_checkpoints.json");

        private readonly object _checkpointLock = new();
        private DateTime _lastCheckpointWrite = DateTime.MinValue;
        private const int MIN_CHECKPOINT_INTERVAL_MS = 2000; // Don't write more than every 2s

        // PH-2 FIX: Periodic timer to clean up stale pending receives that never got a TCP connection
        private Timer? _stalePendingReceivesTimer;

        // ═══ Events ═══
        public event Action<LanTransferSession>? TransferStarted;
        public event Action<LanTransferSession>? TransferCompleted;
        public event Action<LanTransferSession>? TransferFailed;

        private FlyShelfViewModel? _viewModel;

        public LanTransferManager()
        {
            Instance = this;

            // PH-2 FIX: Start a periodic timer to clean up stale pending receives every 30 seconds
            _stalePendingReceivesTimer = new Timer(_ => CleanupStalePendingReceives(), null, 30_000, 30_000);
        }

        public void SetViewModel(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        // ═══ Counts ═══
        public int ActiveCount => _activeSessions.Values.Count(s => s.IsActive || s.IsPaused);
        public double TotalUploadSpeedBps => _activeSessions.Values
            .Where(s => s.Direction == TransferDirection.Send && s.IsActive)
            .Sum(s => s.SpeedBps);
        public double TotalDownloadSpeedBps => _activeSessions.Values
            .Where(s => s.Direction == TransferDirection.Receive && s.IsActive)
            .Sum(s => s.SpeedBps);

        // ═══ SEND: Offer a file to a peer ═══

        /// <summary>
        /// Initiates a file transfer to a peer. Sends TransferOffer via WebSocket,
        /// then waits for TransferAccept before connecting TCP.
        /// </summary>
        public async Task<LanTransferSession?> OfferFile(PeerConnection peer, string filePath)
        {
            if (!File.Exists(filePath))
            {
                Logger.LogAction("TRANSFER", $"File not found: {filePath}");
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            var session = new LanTransferSession(TransferDirection.Send)
            {
                FilePath = filePath,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                PeerDeviceId = peer.DeviceId,
                PeerDeviceName = peer.DeviceName
            };

            // Enable parallel chunked transfer for large files
            const long CHUNKED_THRESHOLD = 100 * 1024 * 1024; // 100MB
            if (fileInfo.Length >= CHUNKED_THRESHOLD)
            {
                session.IsChunked = true;
                session.NumChunks = fileInfo.Length >= 500 * 1024 * 1024 ? 6 : 4;  // 6 chunks for >500MB, 4 for 100-500MB
                session.ChunkSize = (long)Math.Ceiling((double)fileInfo.Length / session.NumChunks);
                Logger.LogAction("TRANSFER", $"⚡ Large file ({LanTransferSession.FormatBytes(fileInfo.Length)}) → parallel chunked transfer with {session.NumChunks} streams");
            }

            _activeSessions[session.TransferId] = session;
            AddToActiveTransfersOnDispatcher(session);

            Logger.LogAction("TRANSFER", $"📤 Offering {session.FileName} ({LanTransferSession.FormatBytes(session.FileSize)}) to {peer.DeviceName}");

            // Send TransferOffer via WebSocket
            bool offered = await SendTransferOffer(peer, session);
            if (!offered)
            {
                session.MarkFailed("Failed to send transfer offer");
                RemoveFromActiveOnDispatcher(session);
                _activeSessions.TryRemove(session.TransferId, out _);
                return null;
            }

            TransferStarted?.Invoke(session);
            return session;
        }

        /// <summary>
        /// Called when peer accepts our transfer offer. Initiates TCP connection and sends file.
        /// </summary>
        public async Task HandleTransferAccepted(Guid transferId, long resumeFrom, string peerDeviceId)
        {
            if (!_activeSessions.TryGetValue(transferId, out var session)) return;
            if (session.Direction != TransferDirection.Send) return;

            session.BytesTransferred = resumeFrom;

            // Get peer's IP from their LAN URL
            var peer = PeerManager.Instance?.ConnectedPeers.Values
                .FirstOrDefault(p => p.DeviceId == peerDeviceId);
            if (peer == null)
            {
                session.MarkFailed("Peer not connected");
                return;
            }

            string peerIp = LanTransferEngine.ExtractIpFromUrl(peer.LanUrl);
            if (string.IsNullOrEmpty(peerIp))
            {
                session.MarkFailed("Could not resolve peer IP");
                return;
            }

            int peerPort = peer.TransferPort > 0 ? peer.TransferPort : LanTransferEngine.TRANSFER_PORT;

            // Track as active transfer on peer (prevents heartbeat from killing it)
            Interlocked.Increment(ref peer.ActiveTransfers);

            try
            {
                Logger.LogAction("TRANSFER", $"📤 Peer accepted — connecting TCP to {peerIp}:{peerPort} (resume from {LanTransferSession.FormatBytes(resumeFrom)})");
                if (session.IsChunked)
                    await LanTransferEngine.Instance!.SendFileParallelAsync(peerIp, peerPort, session);
                else
                    await LanTransferEngine.Instance!.SendFileAsync(peerIp, peerPort, session);
            }
            finally
            {
                int remaining = Interlocked.Decrement(ref peer.ActiveTransfers);
                // FIX: Trigger deferred URL reconnect if this was the last active transfer
                if (remaining <= 0 && peer.PendingUrlReconnect)
                {
                    peer.PendingUrlReconnect = false;
                    Logger.LogAction("PEER", $"🔄 Deferred URL reconnect triggered for {peer.DeviceName} — TCP send completed");
                    _ = Task.Run(() => PeerManager.Instance?.ReconnectPeerAsync(peer));
                }

                if (session.IsCompleted)
                {
                    MoveToCompleted(session);
                    TransferCompleted?.Invoke(session);
                }
                else if (session.IsFailed)
                {
                    MoveToCompleted(session);
                    TransferFailed?.Invoke(session);

                    // Auto-retry on network failure (not user-cancelled)
                    if (session.IsFailed && session.State != TransferState.Cancelled
                        && session.AutoRetryCount < LanTransferSession.MAX_AUTO_RETRIES
                        && (session.ErrorMessage?.Contains("Network") == true 
                            || session.ErrorMessage?.Contains("Connection") == true
                            || session.ErrorMessage?.Contains("lost") == true))
                    {
                        int retryIdx = Math.Min(session.AutoRetryCount, LanTransferSession.AUTO_RETRY_DELAYS_MS.Length - 1);
                        int delayMs = LanTransferSession.AUTO_RETRY_DELAYS_MS[retryIdx];
                        session.AutoRetryCount++;
                        Logger.LogAction("TRANSFER", $"🔄 Auto-retry {session.AutoRetryCount}/{LanTransferSession.MAX_AUTO_RETRIES} for {session.FileName} in {delayMs}ms");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(delayMs);
                            try { await RetryTransfer(session.TransferId); }
                            catch (Exception ex) { Logger.LogAction("TRANSFER", $"Auto-retry failed: {ex.Message}"); }
                        });
                    }
                }

                PersistCheckpoints();
            }
        }

        // ═══ RECEIVE: Handle incoming transfer offers ═══

        /// <summary>
        /// Called when a peer sends us a TransferOffer via WebSocket.
        /// Creates a receive session and sends TransferAccept.
        /// </summary>
        public async Task HandleTransferOffer(Guid transferId, string fileName, long fileSize,
            string peerDeviceId, string peerDeviceName, string? xxHash64,
            bool isChunked = false, int numChunks = 4, long chunkSize = 0)
        {
            // Check for resumable checkpoint — first by transferId, then by content fingerprint
            long resumeFrom = 0;
            string filePath = GetReceiveFilePath(fileName, peerDeviceName);

            // 1) Try exact match by transferId (same session resumed)
            var checkpoint = LoadCheckpointForTransfer(transferId);
            // 2) Fallback: match by content fingerprint (sender reconnected with new session ID)
            //    This handles the critical case: 15.9GB sent, disconnect, reconnect → new GUID but same file
            if (checkpoint == null)
                checkpoint = LoadCheckpointByContent(fileName, fileSize, peerDeviceId);

            if (checkpoint != null && File.Exists(checkpoint.FilePath))
            {
                var existingFile = new FileInfo(checkpoint.FilePath);
                if (existingFile.Length >= checkpoint.BytesTransferred && checkpoint.FileSize == fileSize)
                {
                    resumeFrom = checkpoint.BytesTransferred;
                    filePath = checkpoint.FilePath;
                    Logger.LogAction("TRANSFER", $"📥 Resumable checkpoint found for {fileName}: resume from {LanTransferSession.FormatBytes(resumeFrom)} (matched by {(checkpoint.TransferId == transferId.ToString() ? "ID" : "content")})");
                }
            }

            var session = new LanTransferSession(transferId, TransferDirection.Receive)
            {
                FilePath = filePath,
                FileName = fileName,
                FileSize = fileSize,
                BytesTransferred = resumeFrom,
                PeerDeviceId = peerDeviceId,
                PeerDeviceName = peerDeviceName,
                XxHash64 = xxHash64
            };

            // Configure chunked receive
            if (isChunked && numChunks > 1 && chunkSize > 0)
            {
                session.IsChunked = true;
                session.NumChunks = numChunks;
                session.ChunkSize = chunkSize;

                // Pre-allocate file to full size so chunk writers can seek freely
                // Use OpenOrCreate to preserve partially-received data on resume
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (resumeFrom == 0) // Only pre-allocate fresh files; resumed files already exist
                {
                    using (var preAlloc = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        preAlloc.SetLength(fileSize);
                    }
                }
                else if (!File.Exists(filePath) || new FileInfo(filePath).Length < fileSize)
                {
                    // Resumed but file is missing or truncated — re-create and lose progress
                    using (var preAlloc = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        preAlloc.SetLength(fileSize);
                    }
                    resumeFrom = 0;
                    session.BytesTransferred = 0;
                    Logger.LogAction("TRANSFER", $"⚠️ Chunked resume file missing/truncated — restarting from scratch");
                }
                Logger.LogAction("TRANSFER", $"⚡ Chunked receive: {numChunks} parallel streams, file pre-allocated ({LanTransferSession.FormatBytes(fileSize)})");
            }

            _activeSessions[transferId] = session;
            _pendingReceives[transferId] = session;
            AddToActiveTransfersOnDispatcher(session);

            // Create download progress card in clipboard shelf
            try
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        session.Placeholder = _viewModel?.CreateTransferPlaceholder(
                            fileName, fileSize, peerDeviceName,
                            "LAN TCP", "PC");
                    }
                    catch (Exception ex) { Logger.LogAction("TRANSFER", $"Placeholder creation failed: {ex.Message}"); }
                });
            }
            catch { }

            Logger.LogAction("TRANSFER", $"📥 Accepting transfer: {fileName} ({LanTransferSession.FormatBytes(fileSize)}) from {peerDeviceName} (resume from {LanTransferSession.FormatBytes(resumeFrom)})");

            // Track on peer
            var peer = PeerManager.Instance?.ConnectedPeers.Values
                .FirstOrDefault(p => p.DeviceId == peerDeviceId);
            if (peer != null)
            {
                Interlocked.Increment(ref peer.ActiveTransfers);
                // M11 fix: Use Interlocked guard to prevent double-decrement on retry cycles
                int decremented = 0;
                session.StateChanged += (s, state) =>
                {
                    if ((state == TransferState.Completed || state == TransferState.Failed || state == TransferState.Cancelled)
                        && Interlocked.CompareExchange(ref decremented, 1, 0) == 0)
                    {
                        int remaining = Interlocked.Decrement(ref peer.ActiveTransfers);
                        // FIX: Trigger deferred URL reconnect if this was the last active transfer
                        if (remaining <= 0 && peer.PendingUrlReconnect)
                        {
                            peer.PendingUrlReconnect = false;
                            Logger.LogAction("PEER", $"🔄 Deferred URL reconnect triggered for {peer.DeviceName} — TCP receive completed");
                            _ = Task.Run(() => PeerManager.Instance?.ReconnectPeerAsync(peer));
                        }
                    }

                    // Update placeholder to show error on receive failure, then remove after 3s
                    if (state == TransferState.Failed && session.Placeholder != null)
                    {
                        var placeholder = session.Placeholder;
                        placeholder.TransferStatusText = $"❌ Failed: {session.ErrorMessage}";
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(3000);
                            try
                            {
                                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    _viewModel?.DroppedItems.Remove(placeholder);
                                });
                            }
                            catch { }
                            session.Placeholder = null;
                        });
                    }
                };
            }

            // Send TransferAccept via WebSocket
            await SendTransferAccept(peerDeviceId, transferId, resumeFrom);

            TransferStarted?.Invoke(session);
        }

        /// <summary>
        /// Called by LanTransferEngine when looking for a pending receive session by transfer ID.
        /// </summary>
        public LanTransferSession? GetPendingReceiveSession(Guid transferId)
        {
            if (_pendingReceives.TryGetValue(transferId, out var session))
            {
                // For chunked transfers, keep in pending until all chunks are received
                // (multiple TCP connections will look up the same session)
                if (!session.IsChunked)
                    _pendingReceives.TryRemove(transferId, out _);
                else if (session.AllChunksCompleted)
                    _pendingReceives.TryRemove(transferId, out _);
                return session;
            }
            return _activeSessions.TryGetValue(transferId, out var active) ? active : null;
        }

        /// <summary>
        /// PH-2 FIX: Periodically cleans up pending receives older than 60 seconds that never got a TCP connection.
        /// Runs on a 30-second timer to prevent unbounded memory growth from stale entries.
        /// </summary>
        private void CleanupStalePendingReceives()
        {
            try
            {
                var staleIds = _pendingReceives
                    .Where(kvp => (DateTime.UtcNow - kvp.Value.StartTime).TotalSeconds > 60)
                    .Select(kvp => kvp.Key).ToList();
                foreach (var id in staleIds)
                {
                    if (_pendingReceives.TryRemove(id, out var stale))
                    {
                        stale.MarkFailed("Connection timeout — no TCP connection received");
                        MoveToCompleted(stale);
                        Logger.LogAction("TRANSFER", $"🧹 Cleaned up stale pending receive: {stale.FileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSFER", $"Stale pending receives cleanup error: {ex.Message}");
            }
        }

        // ═══ Control: Pause / Resume / Cancel ═══

        public async Task PauseTransfer(Guid transferId)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                session.PauseTransfer();
                PersistCheckpoints();
                // Notify peer
                await SendControlMessage(session.PeerDeviceId, "TransferPause", transferId);
                Logger.LogAction("TRANSFER", $"⏸ Paused: {session.FileName}");
            }
        }

        public async Task ResumeTransfer(Guid transferId)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                session.ResumeTransfer();
                await SendControlMessage(session.PeerDeviceId, "TransferResume", transferId, session.BytesTransferred);
                Logger.LogAction("TRANSFER", $"▶ Resumed: {session.FileName} from {LanTransferSession.FormatBytes(session.BytesTransferred)}");
            }
        }

        public async Task CancelTransfer(Guid transferId)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                session.CancelTransfer();
                MoveToCompleted(session);
                await SendControlMessage(session.PeerDeviceId, "TransferCancel", transferId);
                Logger.LogAction("TRANSFER", $"🚫 Cancelled: {session.FileName}");
                PersistCheckpoints();
            }
        }

        public async Task PauseAll()
        {
            foreach (var session in _activeSessions.Values.Where(s => s.CanPause).ToList())
                await PauseTransfer(session.TransferId);
        }

        public async Task ResumeAll()
        {
            foreach (var session in _activeSessions.Values.Where(s => s.CanResume).ToList())
                await ResumeTransfer(session.TransferId);
        }

        public async Task CancelAll()
        {
            foreach (var session in _activeSessions.Values.Where(s => s.CanCancel).ToList())
                await CancelTransfer(session.TransferId);
        }

        // ═══ Peer Control Message Handlers ═══

        public Task HandlePeerPause(Guid transferId)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                session.PauseTransfer();
                PersistCheckpoints();
            }
            return Task.CompletedTask;
        }

        public void HandlePeerResume(Guid transferId, long resumeFrom)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                if (resumeFrom > 0) session.BytesTransferred = resumeFrom;
                session.ResumeTransfer();
            }
        }

        public void HandlePeerCancel(Guid transferId)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                session.CancelTransfer();
                MoveToCompleted(session);
            }
        }

        public void HandlePeerComplete(Guid transferId)
        {
            if (_activeSessions.TryGetValue(transferId, out var session))
            {
                session.MarkCompleted();
                MoveToCompleted(session);
                TransferCompleted?.Invoke(session);
            }
        }

        // ═══ Checkpoint ACK ═══

        public void SendCheckpointAck(LanTransferSession session)
        {
            _ = SendControlMessage(session.PeerDeviceId, "TransferCheckpoint", session.TransferId, session.BytesTransferred);
        }

        // ═══ Receive Completed — Inject into clipboard ═══

        public void OnReceiveCompleted(LanTransferSession session)
        {
            MoveToCompleted(session);
            TransferCompleted?.Invoke(session);

            // Inject into FlyShelf clipboard on dispatcher thread
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_viewModel == null) return;

                    // Auto-copy to Windows clipboard
                    var fileList = new System.Collections.Specialized.StringCollection { session.FilePath };
                    ClipboardHelper.SafeSetFileDropList(fileList);

                    // Insert into FlyShelf shelf
                    var clip = new ClipboardItem
                    {
                        RawContent = session.FilePath,
                        FileName = session.FileName,
                        FilePath = session.FilePath,
                        Extension = Path.GetExtension(session.FilePath).TrimStart('.').ToUpperInvariant(),
                        ItemType = ClipboardItemType.File,
                        SourceDeviceName = session.PeerDeviceName,
                        SourceDeviceType = "PC",
                        TransferMethod = "LAN TCP"
                    };
                    clip.EvaluateSmartActions();

                    // Swap placeholder with completed item if placeholder exists
                    if (session.Placeholder != null)
                    {
                        _viewModel.SwapPlaceholderWithCompleted(session.Placeholder, clip);
                        session.Placeholder = null;
                    }
                    else
                    {
                        _viewModel.InsertWithDedup(clip);
                    }
                    _viewModel.SchedulePersistHistoryPublic(); // PERF: throttled — network transfer is non-critical

                    Windows.ToastWindow.ShowToast($"✅ {session.FileName} ({LanTransferSession.FormatBytes(session.FileSize)}) received from {session.PeerDeviceName}");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TRANSFER", $"Failed to inject received file: {ex.Message}");
                }
            });
        }

        // ═══ WebSocket Message Sending ═══

        private async Task<bool> SendTransferOffer(PeerConnection peer, LanTransferSession session)
        {
            var envelope = JsonSerializer.Serialize(new
            {
                type = "TransferOffer",
                transferId = session.TransferId.ToString(),
                fileName = session.FileName,
                fileSize = session.FileSize,
                tcpPort = LanTransferEngine.TRANSFER_PORT,
                sourceDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName,
                sourceDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                xxhash64 = session.XxHash64 ?? "",
                isChunked = session.IsChunked,
                numChunks = session.NumChunks,
                chunkSize = session.ChunkSize
            });
            return await SendWebSocketMessage(peer, envelope);
        }

        private async Task SendTransferAccept(string peerDeviceId, Guid transferId, long resumeFrom)
        {
            var peer = PeerManager.Instance?.ConnectedPeers.Values
                .FirstOrDefault(p => p.DeviceId == peerDeviceId);
            if (peer == null) return;

            var envelope = JsonSerializer.Serialize(new
            {
                type = "TransferAccept",
                transferId = transferId.ToString(),
                resumeFrom = resumeFrom
            });
            await SendWebSocketMessage(peer, envelope);
        }

        private async Task SendControlMessage(string peerDeviceId, string messageType, Guid transferId, long bytesTransferred = 0)
        {
            var peer = PeerManager.Instance?.ConnectedPeers.Values
                .FirstOrDefault(p => p.DeviceId == peerDeviceId);
            if (peer == null) return;

            var envelope = JsonSerializer.Serialize(new
            {
                type = messageType,
                transferId = transferId.ToString(),
                bytesTransferred = bytesTransferred
            });
            await SendWebSocketMessage(peer, envelope);
        }

        private async Task<bool> SendWebSocketMessage(PeerConnection peer, string message)
        {
            if (peer.LiveSocket?.State != System.Net.WebSockets.WebSocketState.Open)
                return false;

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                // Use a short timeout — control messages are tiny
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                // M15 fix: Control messages must get through even during large WS file transfers.
                // 10s timeout (was 2s) to survive semaphore contention from concurrent large sends.
                bool acquired = await peer.SendSemaphore.WaitAsync(10_000, cts.Token);
                if (!acquired) return false;

                try
                {
                    await peer.LiveSocket.SendAsync(new ArraySegment<byte>(data),
                        System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);
                    return true;
                }
                finally
                {
                    peer.SendSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSFER", $"Failed to send WS control message: {ex.Message}");
                return false;
            }
        }

        // ═══ UI Collection Management ═══

        private void AddToActiveTransfersOnDispatcher(LanTransferSession session)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ActiveTransfers.Add(session);
            });
        }

        private void RemoveFromActiveOnDispatcher(LanTransferSession session)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ActiveTransfers.Remove(session);
            });
        }

        private void MoveToCompleted(LanTransferSession session)
        {
            _activeSessions.TryRemove(session.TransferId, out _);
            _pendingReceives.TryRemove(session.TransferId, out _);

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ActiveTransfers.Remove(session);

                // Keep last 100 completed
                if (CompletedTransfers.Count >= 100)
                {
                    var removed = CompletedTransfers[CompletedTransfers.Count - 1];
                    CompletedTransfers.RemoveAt(CompletedTransfers.Count - 1);
                    try { removed.Dispose(); } catch { }
                }
                CompletedTransfers.Insert(0, session);
            });
        }

        public void ClearCompleted()
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CompletedTransfers.Clear();
            });
        }

        // ═══ File Path Generation ═══

        private static string GetReceiveFilePath(string fileName, string sourceName)
        {
            // Sanitize filename
            fileName = Path.GetFileName(fileName) ?? "received_file.dat";
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            string dateString = DateTime.Now.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlyShelf", "SyncedFiles", "LAN_Transfer", sourceName, dateString);
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, fileName);
            int counter = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(fileName)}_{counter++}{Path.GetExtension(fileName)}");
            }
            return path;
        }

        // ═══ Checkpoint Persistence ═══

        /// <summary>Persists checkpoints for active, paused, and failed sessions. Optionally bypasses throttle.</summary>
        public void PersistCheckpoints(bool bypassThrottle = false)
        {
            lock (_checkpointLock)
            {
                // H7 fix: bypass throttle for terminal states (called with bypassThrottle=true)
                if (!bypassThrottle && (DateTime.UtcNow - _lastCheckpointWrite).TotalMilliseconds < MIN_CHECKPOINT_INTERVAL_MS)
                    return;
                _lastCheckpointWrite = DateTime.UtcNow;
            }

            try
            {
                // Include active + paused sessions
                var checkpoints = _activeSessions.Values
                    .Where(s => s.BytesTransferred > 0 && (s.IsActive || s.IsPaused))
                    .Select(SessionToCheckpoint)
                    .ToList();

                // H-10 fix: Fire-and-forget InvokeAsync — no blocking Wait() that could deadlock on shutdown
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    try
                    {
                        app.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                var snapshot = CompletedTransfers
                                    .Where(s => s.IsFailed && s.BytesTransferred > 0)
                                    .ToList();
                                // Persist on the dispatcher thread directly since we can't block
                                var cps = checkpoints.ToList();
                                cps.AddRange(snapshot.Select(SessionToCheckpoint));
                                if (!DiskSpaceHelper.HasSufficientDiskSpace(_checkpointFile, 1_000_000))
                                {
                                    Logger.LogAction("TRANSFER", "Insufficient disk space for checkpoint persist");
                                    return;
                                }
                                string json2 = JsonSerializer.Serialize(cps, new JsonSerializerOptions { WriteIndented = true });
                                string dir2 = Path.GetDirectoryName(_checkpointFile)!;
                                Directory.CreateDirectory(dir2);
                                string tmp2 = _checkpointFile + ".tmp";
                                File.WriteAllText(tmp2, json2, Encoding.UTF8);
                                File.Move(tmp2, _checkpointFile, true);
                            }
                            catch (Exception ex2) { Logger.LogAction("TRANSFER", $"Checkpoint persist (dispatcher) failed: {ex2.Message}"); }
                        });
                        return; // Dispatcher will handle full persistence
                    }
                    catch { /* App shutting down — fall through to non-dispatcher path */ }
                }

                // SHUTDOWN FIX: When the dispatcher is unavailable (app closing), we still
                // need to persist failed sessions. _activeSessions is a ConcurrentDictionary
                // and is safe to read from any thread. Failed sessions that were moved to the
                // UI-bound CompletedTransfers can't be accessed here, but _activeSessions
                // retains sessions that failed during the current transfer cycle.
                // This is the best-effort fallback — captures most failed session data.
                var failedFromActive = _activeSessions.Values
                    .Where(s => s.IsFailed && s.BytesTransferred > 0)
                    .Select(SessionToCheckpoint)
                    .ToList();
                checkpoints.AddRange(failedFromActive);

                if (!DiskSpaceHelper.HasSufficientDiskSpace(_checkpointFile, 1_000_000))
                {
                    Logger.LogAction("TRANSFER", "Insufficient disk space for checkpoint persist");
                    return;
                }
                string json = JsonSerializer.Serialize(checkpoints, new JsonSerializerOptions { WriteIndented = true });
                string dir = Path.GetDirectoryName(_checkpointFile)!;
                Directory.CreateDirectory(dir);

                // Atomic write
                string tmp = _checkpointFile + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, _checkpointFile, true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSFER", $"Checkpoint persist error: {ex.Message}");
            }
        }

        private TransferCheckpoint? LoadCheckpointForTransfer(Guid transferId)
        {
            try
            {
                if (!File.Exists(_checkpointFile)) return null;
                string json = FileRetryHelper.RunWithRetry(() => File.ReadAllText(_checkpointFile));
                var checkpoints = JsonSerializer.Deserialize<TransferCheckpoint[]>(json);
                return checkpoints?.FirstOrDefault(c => c.TransferId == transferId.ToString());
            }
            catch { return null; }
        }

        /// <summary>
        /// Content-based checkpoint matching — finds a checkpoint by fileName + fileSize + peerDeviceId.
        /// This is the critical fallback for when a sender reconnects with a new transfer GUID
        /// but is re-sending the same file. Picks the checkpoint with the most progress.
        /// </summary>
        private TransferCheckpoint? LoadCheckpointByContent(string fileName, long fileSize, string peerDeviceId, string? xxHash64 = null)
        {
            try
            {
                if (!File.Exists(_checkpointFile)) return null;
                string json = FileRetryHelper.RunWithRetry(() => File.ReadAllText(_checkpointFile));
                var checkpoints = JsonSerializer.Deserialize<TransferCheckpoint[]>(json);
                if (checkpoints == null) return null;

                // M12 fix: Include xxHash64 in matching to prevent corrupt resume
                // when same-name/same-size files are different content
                // Require both hashes to be present and matching — prevents resuming
                // a same-name/same-size but different-content file (data corruption)
                if (string.IsNullOrEmpty(xxHash64)) return null;

                return checkpoints
                    .Where(c => c.FileName == fileName
                             && c.FileSize == fileSize
                             && c.PeerDeviceId == peerDeviceId
                             && c.Direction == "Receive"
                             && File.Exists(c.FilePath)
                             && !string.IsNullOrEmpty(c.XxHash64)
                             && string.Equals(c.XxHash64, xxHash64, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.BytesTransferred)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Immediately persists a checkpoint for a specific failed session, bypassing the throttle.
        /// Called from the TCP engine on connection loss to ensure progress is never lost.
        /// </summary>
        public void PersistCheckpointsIncludingFailed(LanTransferSession failedSession)
        {
            // H5 fix: Lock to prevent concurrent read-modify-write races
            lock (_checkpointLock)
            {
                try
                {
                    TransferCheckpoint[] existing = Array.Empty<TransferCheckpoint>();
                    if (File.Exists(_checkpointFile))
                    {
                        string existingJson = FileRetryHelper.RunWithRetry(() => File.ReadAllText(_checkpointFile));
                        existing = JsonSerializer.Deserialize<TransferCheckpoint[]>(existingJson) ?? Array.Empty<TransferCheckpoint>();
                    }

                    var filtered = existing.Where(c =>
                        !(c.FileName == failedSession.FileName
                          && c.FileSize == failedSession.FileSize
                          && c.PeerDeviceId == failedSession.PeerDeviceId)
                        && c.TransferId != failedSession.TransferId.ToString()
                    ).ToList();

                    filtered.Add(SessionToCheckpoint(failedSession));

                    if (!DiskSpaceHelper.HasSufficientDiskSpace(_checkpointFile, 1_000_000))
                    {
                        Logger.LogAction("TRANSFER", "Insufficient disk space for checkpoint persist");
                        return;
                    }
                    string json = JsonSerializer.Serialize(filtered, new JsonSerializerOptions { WriteIndented = true });
                    string dir = Path.GetDirectoryName(_checkpointFile)!;
                    Directory.CreateDirectory(dir);
                    string tmp = _checkpointFile + ".tmp";
                    File.WriteAllText(tmp, json, Encoding.UTF8);
                    File.Move(tmp, _checkpointFile, true);

                    Logger.LogAction("TRANSFER", $"💾 Checkpoint saved for {failedSession.FileName}: {LanTransferSession.FormatBytes(failedSession.BytesTransferred)} of {LanTransferSession.FormatBytes(failedSession.FileSize)}");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TRANSFER", $"Checkpoint persist error: {ex.Message}");
                }
            }
        }

        /// <summary>Converts a session to a checkpoint model for persistence.</summary>
        private static TransferCheckpoint SessionToCheckpoint(LanTransferSession s)
        {
            return new TransferCheckpoint
            {
                TransferId = s.TransferId.ToString(),
                Direction = s.Direction.ToString(),
                FilePath = s.FilePath,
                FileName = s.FileName,
                FileSize = s.FileSize,
                BytesTransferred = s.BytesTransferred,
                PeerDeviceId = s.PeerDeviceId,
                PeerDeviceName = s.PeerDeviceName,
                XxHash64 = s.XxHash64,
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        public void CleanupStaleCheckpoints()
        {
            try
            {
                if (!File.Exists(_checkpointFile)) return;
                string json = FileRetryHelper.RunWithRetry(() => File.ReadAllText(_checkpointFile));
                var checkpoints = JsonSerializer.Deserialize<TransferCheckpoint[]>(json);
                if (checkpoints == null) return;

                // Remove checkpoints for files that no longer exist or are older than 7 days
                var valid = checkpoints.Where(c =>
                {
                    if (!File.Exists(c.FilePath)) return false;
                    if (DateTime.TryParse(c.Timestamp, out var ts) && (DateTime.UtcNow - ts).TotalDays > 7) return false;
                    return true;
                }).ToArray();

                // H6 fix: Also clean up stale pending receives
                var staleReceiveIds = _pendingReceives
                    .Where(kvp => (DateTime.UtcNow - kvp.Value.StartTime).TotalSeconds > 60)
                    .Select(kvp => kvp.Key).ToList();
                foreach (var id in staleReceiveIds)
                {
                    if (_pendingReceives.TryRemove(id, out var stale))
                    {
                        stale.MarkFailed("Connection timeout — no TCP connection received");
                        MoveToCompleted(stale);
                    }
                }

                if (valid.Length != checkpoints.Length)
                {
                    string newJson = JsonSerializer.Serialize(valid, new JsonSerializerOptions { WriteIndented = true });
                    // M13 fix: Atomic write (was using File.WriteAllText directly)
                    string tmp = _checkpointFile + ".tmp";
                    File.WriteAllText(tmp, newJson, Encoding.UTF8);
                    File.Move(tmp, _checkpointFile, true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSFER", $"Checkpoint cleanup error: {ex.Message}");
            }
        }

        // ═══ Retry Failed Transfers ═══

        /// <summary>
        /// Retries a failed transfer by moving it back to active and re-offering to the peer.
        /// For receive transfers: sends a "TransferRetryRequest" to the sender.
        /// For send transfers: re-initiates the offer/accept flow using the saved checkpoint.
        /// </summary>
        public async Task RetryTransfer(Guid transferId)
        {
            // M14 fix: Use InvokeAsync instead of Invoke to avoid deadlock
            LanTransferSession? session = null;
            var app = System.Windows.Application.Current;
            if (app != null)
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    session = CompletedTransfers.FirstOrDefault(s => s.TransferId == transferId && s.CanRetry);
                });
            }

            if (session == null)
            {
                Logger.LogAction("TRANSFER", $"Retry: session {transferId} not found or not retryable");
                return;
            }

            // L9 fix: Verify source file still exists for send retries
            if (session.Direction == TransferDirection.Send && !File.Exists(session.FilePath))
            {
                session.MarkFailed("Source file no longer exists");
                Logger.LogAction("TRANSFER", $"Retry: source file missing: {session.FilePath}");
                return;
            }

            var peer = PeerManager.Instance?.ConnectedPeers.Values
                .FirstOrDefault(p => p.DeviceId == session.PeerDeviceId);
            if (peer == null || !peer.IsAlive)
            {
                Windows.ToastWindow.ShowToast($"⚠️ Cannot retry — {session.PeerDeviceName} is not connected");
                Logger.LogAction("TRANSFER", $"Retry: peer {session.PeerDeviceName} not connected");
                return;
            }

            session.RetryTransfer();

            if (app != null)
            {
                await app.Dispatcher.InvokeAsync(() =>
                {
                    CompletedTransfers.Remove(session);
                    ActiveTransfers.Add(session);
                });
            }
            _activeSessions[session.TransferId] = session;

            Logger.LogAction("TRANSFER", $"↺ Retrying {session.FileName} from {LanTransferSession.FormatBytes(session.BytesTransferred)}");

            if (session.Direction == TransferDirection.Send)
            {
                bool offered = await SendTransferOffer(peer, session);
                if (!offered)
                {
                    session.MarkFailed("Failed to re-send transfer offer");
                    MoveToCompleted(session);
                }
            }
            else // Receive
            {
                await SendControlMessage(session.PeerDeviceId, "TransferRetryRequest",
                    session.TransferId, session.BytesTransferred);
                _pendingReceives[session.TransferId] = session;
            }

            TransferStarted?.Invoke(session);
        }

        /// <summary>
        /// C1 fix: Handler for TransferRetryRequest from a receiver asking us to re-send a file.
        /// Looks up the file from our checkpoint data and re-offers it.
        /// </summary>
        public async Task HandleTransferRetryRequest(Guid transferId, string peerDeviceId, long bytesTransferred)
        {
            // Find the checkpoint for this transfer (we were the sender)
            var checkpoint = LoadCheckpointForTransfer(transferId);
            if (checkpoint == null || !File.Exists(checkpoint.FilePath))
            {
                Logger.LogAction("TRANSFER", $"RetryRequest: no checkpoint or file missing for {transferId}");
                return;
            }

            var peer = PeerManager.Instance?.ConnectedPeers.Values
                .FirstOrDefault(p => p.DeviceId == peerDeviceId);
            if (peer == null || !peer.IsAlive)
            {
                Logger.LogAction("TRANSFER", $"RetryRequest: peer {peerDeviceId} not connected");
                return;
            }

            // Re-offer the file — the receiver's content-based matching will find the checkpoint
            var session = await OfferFile(peer, checkpoint.FilePath);
            if (session != null)
                Logger.LogAction("TRANSFER", $"↺ Re-offering {checkpoint.FileName} per retry request from {peer.DeviceName}");
        }

        // ═══ Checkpoint Model ═══
        private class TransferCheckpoint
        {
            [JsonPropertyName("transferId")] public string TransferId { get; set; } = "";
            [JsonPropertyName("direction")] public string Direction { get; set; } = "";
            [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
            [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
            [JsonPropertyName("fileSize")] public long FileSize { get; set; }
            [JsonPropertyName("bytesTransferred")] public long BytesTransferred { get; set; }
            [JsonPropertyName("peerDeviceId")] public string PeerDeviceId { get; set; } = "";
            [JsonPropertyName("peerDeviceName")] public string PeerDeviceName { get; set; } = "";
            [JsonPropertyName("xxhash64")] public string? XxHash64 { get; set; }
            [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
        }

        // ═══ IDisposable ═══
        // AUDIT: Deterministic cleanup of _stalePendingReceivesTimer.
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _stalePendingReceivesTimer?.Dispose(); } catch { }
            _stalePendingReceivesTimer = null;
            if (Instance == this) Instance = null;

            GC.SuppressFinalize(this);
        }
    }
}
