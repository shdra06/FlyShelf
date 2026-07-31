// ---------------------------------------------------------------
// NetworkFileQueue — Session-only file staging queue for network sending
// Stages files for transfer, sends via LanTransferManager,
// tracks per-file progress. Not persisted across restarts.
// ---------------------------------------------------------------
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// A file staged for network transfer with progress tracking.
    /// </summary>
    public partial class StagedFile : ObservableObject
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.Now;

        private string _status = "Queued";
        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusIcon));
                }
            }
        }

        /// <summary>
        /// Target device ID. Null means "send to all alive peers".
        /// </summary>
        [ObservableProperty]
        private string? _targetDeviceId;

        [ObservableProperty]
        private string? _targetDeviceName;

        [ObservableProperty]
        private string? _errorMessage;

        private double _progress;
        /// <summary>
        /// Transfer progress from 0.0 to 1.0.
        /// </summary>
        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) > 0.001)
                {
                    _progress = value;
                    OnPropertyChanged();
                }
            }
        }

        // ═══ Computed Properties ═══

        public string FileSizeText => FormatBytes(FileSize);

        public string StatusIcon => _status switch
        {
            "Queued" => "",
            "Sending" => "",
            "Sent" => "",
            "Failed" => "",
            _ => ""
        };

        public string AddedAtText
        {
            get
            {
                var elapsed = DateTime.Now - AddedAt;
                if (elapsed.TotalSeconds < 10) return "just now";
                if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
                if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
                if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
                return AddedAt.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
            }
        }

        public bool IsFile => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

        // ═══ Helpers ═══

        // [FIX M-58]: Delegated to shared FormatHelper
        private static string FormatBytes(long bytes) => Classes.FormatHelper.FormatBytes(bytes);
    }

    /// <summary>
    /// Singleton session-only queue for staging files before network transfer.
    /// Files are not persisted — the queue is cleared on app restart.
    /// All ObservableCollection mutations are dispatched to the UI thread.
    /// </summary>
    public class NetworkFileQueue
    {
        public static NetworkFileQueue? Instance { get; private set; }

        /// <summary>
        /// Files waiting to be sent, visible in the UI.
        /// </summary>
        public ObservableCollection<StagedFile> StagedFiles { get; } = new();

        public NetworkFileQueue()
        {
            Instance = this;
            Logger.LogAction("FILEQUEUE", "Network file queue initialized");
        }

        // ═══ Stage Files ═══

        /// <summary>
        /// Stages a single file for transfer. Deduplicates by file path.
        /// </summary>
        public void StageFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            if (!File.Exists(filePath))
            {
                Logger.LogAction("FILEQUEUE", $"File not found, skipping: {filePath}");
                return;
            }

            // Deduplicate by path — don't re-add files already queued
            // Thread safety: snapshot the collection to avoid cross-thread InvalidOperationException
            bool alreadyStaged = false;
            StagedFile[] snapshot = null;
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                snapshot = StagedFiles.ToArray();
            });
            alreadyStaged = snapshot != null && snapshot.Any(f =>
                string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                (f.Status == "Queued" || f.Status == "Sending"));

            if (alreadyStaged)
            {
                Logger.LogAction("FILEQUEUE", $"Already staged, skipping: {Path.GetFileName(filePath)}");
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var staged = new StagedFile
            {
                FileName = fileInfo.Name,
                FilePath = filePath,
                FileSize = fileInfo.Length,
                AddedAt = DateTime.Now,
                Status = "Queued"
            };

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StagedFiles.Add(staged);
            });

            Logger.LogAction("FILEQUEUE", $"Staged: {staged.FileName} ({staged.FileSizeText})");
        }

        /// <summary>
        /// Stages multiple files for transfer.
        /// </summary>
        public void StageFiles(string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0) return;

            foreach (string path in filePaths)
            {
                StageFile(path);
            }

            Logger.LogAction("FILEQUEUE", $"Staged {filePaths.Length} files");
        }

        /// <summary>
        /// Stages a clipboard item that has an associated file path.
        /// </summary>
        public void StageFromClipboard(ClipboardItem item)
        {
            if (item == null) return;

            // Prefer FilePath, which is the primary file location property
            string? path = null;
            if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                path = item.FilePath;
            }

            if (string.IsNullOrEmpty(path))
            {
                Logger.LogAction("FILEQUEUE", $"Clipboard item has no valid file path: {item.FileName}");
                return;
            }

            StageFile(path);
        }

        // ═══ Send Operations ═══

        /// <summary>
        /// Sends a staged file to a specific peer via LanTransferManager.
        /// Updates the StagedFile status throughout the transfer lifecycle.
        /// </summary>
        public async Task SendFile(StagedFile file, PeerConnection peer)
        {
            if (file == null || peer == null) return;
            if (LanTransferManager.Instance == null)
            {
                Logger.LogAction("FILEQUEUE", "LanTransferManager not initialized");
                file.Status = "Failed";
                file.ErrorMessage = "Transfer manager not initialized";
                return;
            }

            if (!File.Exists(file.FilePath))
            {
                Logger.LogAction("FILEQUEUE", $"File no longer exists: {file.FilePath}");
                file.Status = "Failed";
                file.ErrorMessage = "File not found";
                return;
            }

            file.Status = "Sending";
            file.TargetDeviceId = peer.DeviceId;
            file.TargetDeviceName = peer.DeviceName;
            file.Progress = 0.0;

            Logger.LogAction("FILEQUEUE", $"Sending {file.FileName} to {peer.DeviceName}");

            try
            {
                var session = await LanTransferManager.Instance.OfferFile(peer, file.FilePath);
                if (session == null)
                {
                    file.Status = "Failed";
                    file.ErrorMessage = "Failed to initiate transfer";
                    Logger.LogAction("FILEQUEUE", $"Failed to offer {file.FileName} to {peer.DeviceName}");
                    return;
                }

                // Monitor session state for completion
                var tcs = new TaskCompletionSource<bool>();
                EventHandler<TransferState>? stateHandler = null;
                stateHandler = (s, state) =>
                {
                    // Update progress on staged file
                    if (session.FileSize > 0)
                    {
                        file.Progress = (double)session.BytesTransferred / session.FileSize;
                    }

                    if (state == TransferState.Completed)
                    {
                        file.Status = "Sent";
                        file.Progress = 1.0;
                        session.StateChanged -= stateHandler;
                        tcs.TrySetResult(true);
                    }
                    else if (state == TransferState.Failed)
                    {
                        file.Status = "Failed";
                        file.ErrorMessage = session.ErrorMessage ?? "Transfer failed";
                        session.StateChanged -= stateHandler;
                        tcs.TrySetResult(false);
                    }
                    else if (state == TransferState.Cancelled)
                    {
                        file.Status = "Cancelled";
                        file.ErrorMessage = "Cancelled by user";
                        session.StateChanged -= stateHandler;
                        tcs.TrySetResult(false);
                    }
                };
                session.StateChanged += stateHandler;

                // Wait for terminal state with a generous timeout
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(60));
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask && file.Status == "Sending")
                {
                    file.Status = "Failed";
                    file.ErrorMessage = "Transfer timed out";
                    Logger.LogAction("FILEQUEUE", $"Transfer timed out: {file.FileName}");
                }
            }
            catch (Exception ex)
            {
                file.Status = "Failed";
                file.ErrorMessage = ex.Message;
                Logger.LogAction("FILEQUEUE", $"Send error for {file.FileName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a staged file to all currently alive LAN peers.
        /// Waits for ALL sessions to complete before marking status.
        /// </summary>
        public async Task SendFileToAll(StagedFile file)
        {
            if (file == null) return;
            if (PeerManager.Instance == null)
            {
                Logger.LogAction("FILEQUEUE", "PeerManager not initialized");
                file.Status = "Failed";
                file.ErrorMessage = "Peer manager not initialized";
                return;
            }

            var alivePeers = PeerManager.Instance.ConnectedPeers.Values
                .Where(p => p.IsAlive)
                .ToList();

            if (alivePeers.Count == 0)
            {
                Logger.LogAction("FILEQUEUE", $"No alive peers to send {file.FileName} to");
                file.Status = "Failed";
                file.ErrorMessage = "No peers available";
                return;
            }

            Logger.LogAction("FILEQUEUE", $"Sending {file.FileName} to {alivePeers.Count} peers");
            file.Status = "Sending";
            file.Progress = 0.0;

            int totalPeers = alivePeers.Count;
            int successCount = 0;
            int failCount = 0;
            string? lastError = null;
            var completionTasks = new List<Task>();

            foreach (var peer in alivePeers)
            {
                try
                {
                    if (LanTransferManager.Instance == null) break;

                    var session = await LanTransferManager.Instance.OfferFile(peer, file.FilePath);
                    if (session != null)
                    {
                        // Track this session's completion like SendFile does
                        var tcs = new TaskCompletionSource<bool>();
                        session.StateChanged += (s, state) =>
                        {
                            if (session.FileSize > 0)
                                file.Progress = (double)session.BytesTransferred / session.FileSize;

                            if (state == TransferState.Completed)
                            {
                                Interlocked.Increment(ref successCount);
                                tcs.TrySetResult(true);
                            }
                            else if (state == TransferState.Failed || state == TransferState.Cancelled)
                            {
                                Interlocked.Increment(ref failCount);
                                tcs.TrySetResult(false);
                            }
                        };

                        // Timeout after 60 min
                        completionTasks.Add(Task.Run(async () =>
                        {
                            var timeout = Task.Delay(TimeSpan.FromMinutes(60));
                            var completed = await Task.WhenAny(tcs.Task, timeout);
                            if (completed == timeout)
                            {
                                Interlocked.Increment(ref failCount);
                                tcs.TrySetResult(false);
                            }
                        }));
                    }
                    else
                    {
                        failCount++;
                        lastError = $"Failed to offer to {peer.DeviceName}";
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    lastError = ex.Message;
                    Logger.LogAction("FILEQUEUE", $"Failed to send {file.FileName} to {peer.DeviceName}: {ex.Message}");
                }
            }

            // Wait for all sessions to reach terminal state
            if (completionTasks.Count > 0)
                await Task.WhenAll(completionTasks);

            if (successCount > 0)
            {
                file.Status = "Sent";
                file.Progress = 1.0;
            }
            else
            {
                file.Status = "Failed";
                file.ErrorMessage = lastError ?? "All transfers failed";
            }
        }

        /// <summary>
        /// Sends all queued files to all alive peers.
        /// </summary>
        public async Task SendAllToAll()
        {
            var queuedFiles = StagedFiles.Where(f => f.Status == "Queued").ToList();
            if (queuedFiles.Count == 0)
            {
                Logger.LogAction("FILEQUEUE", "No queued files to send");
                return;
            }

            Logger.LogAction("FILEQUEUE", $"Sending {queuedFiles.Count} files to all peers");

            foreach (var file in queuedFiles)
            {
                await SendFileToAll(file);
            }

            Logger.LogAction("FILEQUEUE", "Batch send completed");
        }

        // ═══ Queue Management ═══

        /// <summary>
        /// Removes a specific file from the queue.
        /// </summary>
        public void Remove(StagedFile file)
        {
            if (file == null) return;

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StagedFiles.Remove(file);
            });

            Logger.LogAction("FILEQUEUE", $"Removed from queue: {file.FileName}");
        }

        /// <summary>
        /// Clears all files from the queue regardless of status.
        /// </summary>
        public void ClearAll()
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StagedFiles.Clear();
            });

            Logger.LogAction("FILEQUEUE", "Queue cleared");
        }

        /// <summary>
        /// Clears only files with Status == "Sent".
        /// </summary>
        public void ClearSent()
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var sentFiles = StagedFiles.Where(f => f.Status == "Sent").ToList();
                foreach (var file in sentFiles)
                {
                    StagedFiles.Remove(file);
                }
            });

            Logger.LogAction("FILEQUEUE", "Sent files cleared from queue");
        }

        // ═══ Computed Properties ═══

        public int QueuedCount => StagedFiles.Count(f => f.Status == "Queued");
        public int SendingCount => StagedFiles.Count(f => f.Status == "Sending");
    }
}
