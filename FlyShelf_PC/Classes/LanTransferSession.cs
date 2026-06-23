// ---------------------------------------------------------------
// LanTransferSession — Single file transfer state model
// Tracks progress, speed, pause/resume for one transfer
// ---------------------------------------------------------------
using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public enum TransferDirection { Send, Receive }

    public enum TransferState
    {
        Queued,
        Connecting,
        Transferring,
        Paused,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Represents a single PC-to-PC LAN file transfer with full pause/resume/cancel support.
    /// Thread-safe for concurrent updates from TCP engine and UI.
    /// </summary>
    public class LanTransferSession : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // ═══ Identity ═══
        public Guid TransferId { get; }
        public TransferDirection Direction { get; }

        // ═══ File Info ═══
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public string? XxHash64 { get; set; } // Full-file hash for integrity verification

        // ═══ Peer Info ═══
        public string PeerDeviceId { get; set; } = "";
        public string PeerDeviceName { get; set; } = "";

        // ═══ Timing ═══
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }

        // ═══ Error ═══
        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        // ═══ State Machine ═══
        private TransferState _state = TransferState.Queued;
        public TransferState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsActive));
                    OnPropertyChanged(nameof(IsPaused));
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(IsFailed));
                    OnPropertyChanged(nameof(IsCancelled));
                    OnPropertyChanged(nameof(CanPause));
                    OnPropertyChanged(nameof(CanResume));
                    OnPropertyChanged(nameof(CanCancel));
                    OnPropertyChanged(nameof(CanRetry));
                    OnPropertyChanged(nameof(StateDisplayText));
                    OnPropertyChanged(nameof(StateIcon));
                    StateChanged?.Invoke(this, value);
                }
            }
        }

        public bool IsActive => _state == TransferState.Transferring || _state == TransferState.Connecting;
        public bool IsPaused => _state == TransferState.Paused;
        public bool IsCompleted => _state == TransferState.Completed;
        public bool IsFailed => _state == TransferState.Failed;
        public bool IsCancelled => _state == TransferState.Cancelled;
        public bool CanPause => _state == TransferState.Transferring;
        public bool CanResume => _state == TransferState.Paused;
        public bool CanCancel => _state == TransferState.Transferring || _state == TransferState.Paused || _state == TransferState.Queued || _state == TransferState.Connecting;
        /// <summary>True if this transfer failed with partial progress and can be retried from checkpoint.</summary>
        public bool CanRetry => _state == TransferState.Failed && BytesTransferred > 0;

        public string StateDisplayText => _state switch
        {
            TransferState.Queued => "Queued",
            TransferState.Connecting => "Connecting...",
            TransferState.Transferring => Direction == TransferDirection.Send ? "Sending..." : "Receiving...",
            TransferState.Paused => "Paused",
            TransferState.Completed => "Completed",
            TransferState.Failed => "Failed",
            TransferState.Cancelled => "Cancelled",
            _ => "Unknown"
        };

        public string StateIcon => _state switch
        {
            TransferState.Queued => "⏳",
            TransferState.Connecting => "🔗",
            TransferState.Transferring => Direction == TransferDirection.Send ? "📤" : "📥",
            TransferState.Paused => "⏸",
            TransferState.Completed => "✅",
            TransferState.Failed => "❌",
            TransferState.Cancelled => "🚫",
            _ => "❓"
        };

        public string DirectionIcon => Direction == TransferDirection.Send ? "→" : "←";
        public string DirectionText => Direction == TransferDirection.Send ? "Sending to" : "Receiving from";

        // ═══ Progress Tracking ═══
        private long _bytesTransferred;
        public long BytesTransferred
        {
            get => Interlocked.Read(ref _bytesTransferred);
            set
            {
                Interlocked.Exchange(ref _bytesTransferred, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ElapsedTime));
            }
        }

        public double ProgressPercent => FileSize > 0 ? Math.Min(100.0, (double)BytesTransferred / FileSize * 100.0) : 0;

        public string ProgressText
        {
            get
            {
                if (FileSize <= 0) return "0%";
                return $"{FormatBytes(BytesTransferred)} / {FormatBytes(FileSize)}";
            }
        }

        public TimeSpan ElapsedTime => (EndTime ?? DateTime.UtcNow) - StartTime;

        // ═══ Speed Measurement (Rolling 2-second window) ═══
        private const int SPEED_SAMPLES = 10;
        private const int SPEED_INTERVAL_MS = 200;
        private readonly (long bytes, long ticksMs)[] _speedSamples = new (long, long)[SPEED_SAMPLES];
        private int _speedSampleIndex;
        private int _speedSampleCount;
        private readonly object _speedLock = new();

        private double _speedBps;
        public double SpeedBps
        {
            get => _speedBps;
            private set
            {
                _speedBps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpeedText));
                OnPropertyChanged(nameof(EstimatedSecondsLeft));
                OnPropertyChanged(nameof(EtaText));
            }
        }

        private double _peakSpeedBps;
        public double PeakSpeedBps
        {
            get => _peakSpeedBps;
            set { _peakSpeedBps = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeakSpeedText)); }
        }

        public string SpeedText => FormatSpeed(SpeedBps);
        public string PeakSpeedText => FormatSpeed(PeakSpeedBps);

        public double EstimatedSecondsLeft
        {
            get
            {
                if (SpeedBps <= 0 || FileSize <= 0) return double.MaxValue;
                long remaining = FileSize - BytesTransferred;
                if (remaining <= 0) return 0;
                return remaining / SpeedBps;
            }
        }

        public string EtaText
        {
            get
            {
                double seconds = EstimatedSecondsLeft;
                if (seconds <= 0 || _state == TransferState.Completed) return "";
                if (seconds == double.MaxValue || _state != TransferState.Transferring) return "";
                if (seconds < 60) return "less than a minute";
                if (seconds < 3600) return $"~{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
                return $"~{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
            }
        }

        /// <summary>
        /// Call this periodically (~200ms) from the transfer loop to update rolling speed.
        /// </summary>
        public void RecordSpeedSample(long currentBytesTransferred)
        {
            long nowMs = Environment.TickCount64;
            lock (_speedLock)
            {
                _speedSamples[_speedSampleIndex] = (currentBytesTransferred, nowMs);
                _speedSampleIndex = (_speedSampleIndex + 1) % SPEED_SAMPLES;
                if (_speedSampleCount < SPEED_SAMPLES) _speedSampleCount++;

                if (_speedSampleCount >= 2)
                {
                    int oldestIdx = _speedSampleCount < SPEED_SAMPLES
                        ? 0
                        : _speedSampleIndex; // Circular: oldest is next to be overwritten
                    var oldest = _speedSamples[oldestIdx];
                    var newest = _speedSamples[(_speedSampleIndex - 1 + SPEED_SAMPLES) % SPEED_SAMPLES];

                    double elapsedSec = (newest.ticksMs - oldest.ticksMs) / 1000.0;
                    if (elapsedSec > 0.05)
                    {
                        double speed = (newest.bytes - oldest.bytes) / elapsedSec;
                        SpeedBps = Math.Max(0, speed);
                        if (speed > PeakSpeedBps) PeakSpeedBps = speed;
                    }
                }
            }
        }

        // ═══ Pause/Resume/Cancel ═══
        private CancellationTokenSource _cts = new();
        public CancellationToken CancellationToken => _cts.Token;

        // SemaphoreSlim used to block the transfer loop when paused
        private readonly SemaphoreSlim _pauseGate = new(1, 1);

        /// <summary>
        /// Pauses the transfer. The TCP connection stays open — sender/receiver loop blocks on _pauseGate.
        /// </summary>
        public async Task PauseAsync()
        {
            if (_state != TransferState.Transferring) return;
            State = TransferState.Paused;
            // Acquire the gate — transfer loop will block trying to acquire it
            await _pauseGate.WaitAsync();
        }

        /// <summary>
        /// Resumes a paused transfer by releasing the pause gate.
        /// </summary>
        public void ResumeTransfer()
        {
            if (_state != TransferState.Paused) return;
            State = TransferState.Transferring;
            try { _pauseGate.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Cancels the transfer. Triggers CancellationToken and closes the TCP connection.
        /// </summary>
        public void CancelTransfer()
        {
            if (!CanCancel) return;
            State = TransferState.Cancelled;
            try { _cts.Cancel(); } catch { }
            // Release pause gate in case we're paused
            try { _pauseGate.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Resets a failed transfer for retry. Creates a fresh CancellationTokenSource
        /// and resets state to Queued while preserving BytesTransferred for resume.
        /// </summary>
        public void RetryTransfer()
        {
            if (!CanRetry) return;
            _cts = new CancellationTokenSource();
            ErrorMessage = null;
            EndTime = null;
            State = TransferState.Queued;
        }

        /// <summary>
        /// Called from the transfer loop to check if paused. Blocks until resumed or cancelled.
        /// </summary>
        public async Task WaitIfPausedAsync()
        {
            if (_state != TransferState.Paused) return;
            // Wait to acquire the gate (will block until Resume releases it)
            await _pauseGate.WaitAsync(_cts.Token);
            _pauseGate.Release(); // Release immediately so future checks don't block
        }

        /// <summary>
        /// Marks the transfer as failed with an error message.
        /// </summary>
        public void MarkFailed(string error)
        {
            ErrorMessage = error;
            EndTime = DateTime.UtcNow;
            State = TransferState.Failed;
            try { _cts.Cancel(); } catch { }
            try { _pauseGate.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Marks the transfer as completed successfully.
        /// </summary>
        public void MarkCompleted()
        {
            EndTime = DateTime.UtcNow;
            SpeedBps = 0; // Stop showing speed
            State = TransferState.Completed;
        }

        // ═══ Events ═══
        public event EventHandler<TransferState>? StateChanged;

        // ═══ Constructors ═══
        public LanTransferSession(Guid transferId, TransferDirection direction)
        {
            TransferId = transferId;
            Direction = direction;
        }

        public LanTransferSession(TransferDirection direction) : this(Guid.NewGuid(), direction) { }

        // ═══ Formatting Helpers ═══
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1_048_576) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1_073_741_824) return $"{bytes / 1_048_576.0:F1} MB";
            return $"{bytes / 1_073_741_824.0:F2} GB";
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "—";
            if (bytesPerSecond < 1_048_576) return $"{bytesPerSecond / 1024.0:F0} KB/s";
            if (bytesPerSecond < 1_073_741_824) return $"{bytesPerSecond / 1_048_576.0:F1} MB/s";
            return $"{bytesPerSecond / 1_073_741_824.0:F2} GB/s";
        }

        // ═══ INotifyPropertyChanged ═══
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
