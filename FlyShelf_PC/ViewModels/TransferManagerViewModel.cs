// ---------------------------------------------------------------
// TransferManagerViewModel — ViewModel for the Transfer Manager window
// Wraps LanTransferManager sessions with filtering, speed aggregation,
// and command bindings for pause/resume/cancel/send operations
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlyShelf.Classes;
using Microsoft.Win32;

namespace FlyShelf.ViewModels
{
    public class TransferManagerViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // ═══ Transfer Collections (bound from LanTransferManager) ═══
        public ObservableCollection<LanTransferSession> ActiveTransfers { get; }
        public ObservableCollection<LanTransferSession> CompletedTransfers { get; }
        public ObservableCollection<LanTransferSession> AllTransfers { get; } = new();

        // ═══ Filtered view ═══
        private string _filterMode = "All";
        public string FilterMode
        {
            get => _filterMode;
            set
            {
                if (_filterMode != value)
                {
                    _filterMode = value;
                    OnPropertyChanged();
                    RebuildFilteredTransfers();
                }
            }
        }

        public ObservableCollection<LanTransferSession> FilteredTransfers { get; } = new();

        // ═══ Selected transfer ═══
        private LanTransferSession? _selectedTransfer;
        public LanTransferSession? SelectedTransfer
        {
            get => _selectedTransfer;
            set
            {
                if (_selectedTransfer != value)
                {
                    _selectedTransfer = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelection));
                }
            }
        }

        public bool HasSelection => _selectedTransfer != null;

        // ═══ Dashboard properties ═══
        private string _totalUploadSpeedText = "—";
        public string TotalUploadSpeedText
        {
            get => _totalUploadSpeedText;
            private set { _totalUploadSpeedText = value; OnPropertyChanged(); }
        }

        private string _totalDownloadSpeedText = "—";
        public string TotalDownloadSpeedText
        {
            get => _totalDownloadSpeedText;
            private set { _totalDownloadSpeedText = value; OnPropertyChanged(); }
        }

        private int _activeCount;
        public int ActiveCount
        {
            get => _activeCount;
            private set
            {
                if (_activeCount != value)
                {
                    _activeCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsEmpty));
                    OnPropertyChanged(nameof(HasActiveTransfers));
                }
            }
        }

        public bool IsEmpty => AllTransfers.Count == 0;
        public bool HasActiveTransfers => _activeCount > 0;

        // ═══ Filter counts ═══
        private int _allCount;
        public int AllCount { get => _allCount; private set { _allCount = value; OnPropertyChanged(); } }

        private int _activeFilterCount;
        public int ActiveFilterCount { get => _activeFilterCount; private set { _activeFilterCount = value; OnPropertyChanged(); } }

        private int _completedCount;
        public int CompletedCount { get => _completedCount; private set { _completedCount = value; OnPropertyChanged(); } }

        private int _failedCount;
        public int FailedCount { get => _failedCount; private set { _failedCount = value; OnPropertyChanged(); } }

        // ═══ Connected peers ═══
        public ObservableCollection<PeerConnection> ConnectedPeers { get; } = new();

        private PeerConnection? _selectedPeer;
        public PeerConnection? SelectedPeer
        {
            get => _selectedPeer;
            set { _selectedPeer = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSendFile)); }
        }

        public bool CanSendFile => _selectedPeer != null;

        // ═══ Status bar ═══
        private string _tcpStatusText = "Initializing...";
        public string TcpStatusText
        {
            get => _tcpStatusText;
            private set { _tcpStatusText = value; OnPropertyChanged(); }
        }

        private string _peerCountText = "No peers";
        public string PeerCountText
        {
            get => _peerCountText;
            private set { _peerCountText = value; OnPropertyChanged(); }
        }

        // ═══ Commands ═══
        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand PauseAllCommand { get; }
        public ICommand ResumeAllCommand { get; }
        public ICommand CancelAllCommand { get; }
        public ICommand ClearCompletedCommand { get; }
        public ICommand OpenFileLocationCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand SetFilterCommand { get; }

        // ═══ Timer ═══
        private readonly DispatcherTimer _refreshTimer;

        // ═══ Constructor ═══
        public TransferManagerViewModel()
        {
            var manager = LanTransferManager.Instance;

            ActiveTransfers = manager?.ActiveTransfers ?? new ObservableCollection<LanTransferSession>();
            CompletedTransfers = manager?.CompletedTransfers ?? new ObservableCollection<LanTransferSession>();

            // Sync AllTransfers from both source collections
            RebuildAllTransfers();

            ActiveTransfers.CollectionChanged += OnSourceCollectionChanged;
            CompletedTransfers.CollectionChanged += OnSourceCollectionChanged;

            // Commands
            PauseCommand = new RelayCommand<LanTransferSession>(s =>
            {
                if (s != null && s.CanPause && manager != null)
                    _ = manager.PauseTransfer(s.TransferId);
            });

            ResumeCommand = new RelayCommand<LanTransferSession>(s =>
            {
                if (s != null && s.CanResume && manager != null)
                    _ = manager.ResumeTransfer(s.TransferId);
            });

            CancelCommand = new RelayCommand<LanTransferSession>(s =>
            {
                if (s != null && s.CanCancel && manager != null)
                    _ = manager.CancelTransfer(s.TransferId);
            });

            PauseAllCommand = new RelayCommand(() =>
            {
                if (manager != null) _ = manager.PauseAll();
            });

            ResumeAllCommand = new RelayCommand(() =>
            {
                if (manager != null) _ = manager.ResumeAll();
            });

            CancelAllCommand = new RelayCommand(() =>
            {
                if (manager != null) _ = manager.CancelAll();
            });

            ClearCompletedCommand = new RelayCommand(() =>
            {
                manager?.ClearCompleted();
            });

            OpenFileLocationCommand = new RelayCommand<LanTransferSession>(s =>
            {
                if (s != null && File.Exists(s.FilePath))
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{s.FilePath}\"");
                    }
                    catch { }
                }
            });

            SendFileCommand = new RelayCommand(() =>
            {
                if (manager == null || _selectedPeer == null) return;

                var dialog = new OpenFileDialog
                {
                    Title = $"Send file to {_selectedPeer.DeviceName}",
                    Filter = "All files (*.*)|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
                {
                    _ = manager.OfferFile(_selectedPeer, dialog.FileName);
                }
            });

            SetFilterCommand = new RelayCommand<string>(mode =>
            {
                if (!string.IsNullOrEmpty(mode))
                    FilterMode = mode;
            });

            // Speed/ETA refresh timer (500ms)
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }

        // ═══ Timer Tick — refresh speeds, ETAs, counts ═══
        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            var manager = LanTransferManager.Instance;
            if (manager == null) return;

            // Speeds
            double upSpeed = manager.TotalUploadSpeedBps;
            double downSpeed = manager.TotalDownloadSpeedBps;
            TotalUploadSpeedText = upSpeed > 0 ? LanTransferSession.FormatSpeed(upSpeed) : "—";
            TotalDownloadSpeedText = downSpeed > 0 ? LanTransferSession.FormatSpeed(downSpeed) : "—";

            // Counts
            ActiveCount = manager.ActiveCount;
            UpdateFilterCounts();

            // TCP status
            var engine = LanTransferEngine.Instance;
            TcpStatusText = engine?.IsRunning == true
                ? $"TCP engine listening on port {LanTransferEngine.TRANSFER_PORT}"
                : "TCP engine not running";

            // Peer count
            var peers = PeerManager.Instance?.ConnectedPeers;
            int alive = peers?.Values.Count(p => p.IsAlive) ?? 0;
            int total = peers?.Count ?? 0;
            PeerCountText = total > 0 ? $"{alive}/{total} peers online" : "No peers";

            // Refresh connected peers list for dropdown
            RefreshPeers();
        }

        // ═══ Collection management ═══
        private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildAllTransfers();
            RebuildFilteredTransfers();
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void RebuildAllTransfers()
        {
            AllTransfers.Clear();

            // Active first (newest on top)
            foreach (var s in ActiveTransfers)
                AllTransfers.Add(s);

            // Then completed/failed
            foreach (var s in CompletedTransfers)
                AllTransfers.Add(s);
        }

        private void RebuildFilteredTransfers()
        {
            FilteredTransfers.Clear();

            IEnumerable<LanTransferSession> source = _filterMode switch
            {
                "Active" => AllTransfers.Where(s => s.IsActive || s.IsPaused || s.State == TransferState.Queued || s.State == TransferState.Connecting),
                "Completed" => AllTransfers.Where(s => s.IsCompleted),
                "Failed" => AllTransfers.Where(s => s.IsFailed || s.IsCancelled),
                _ => AllTransfers // "All"
            };

            foreach (var s in source)
                FilteredTransfers.Add(s);

            UpdateFilterCounts();
        }

        private void UpdateFilterCounts()
        {
            AllCount = AllTransfers.Count;
            ActiveFilterCount = AllTransfers.Count(s => s.IsActive || s.IsPaused || s.State == TransferState.Queued || s.State == TransferState.Connecting);
            CompletedCount = AllTransfers.Count(s => s.IsCompleted);
            FailedCount = AllTransfers.Count(s => s.IsFailed || s.IsCancelled);
        }

        private void RefreshPeers()
        {
            var peers = PeerManager.Instance?.ConnectedPeers.Values
                .Where(p => p.IsAlive)
                .ToList() ?? new List<PeerConnection>();

            // Only update if changed
            if (peers.Count != ConnectedPeers.Count || !peers.All(p => ConnectedPeers.Any(cp => cp.DeviceId == p.DeviceId)))
            {
                ConnectedPeers.Clear();
                foreach (var p in peers)
                    ConnectedPeers.Add(p);

                // Auto-select first peer if none selected
                if (_selectedPeer == null && ConnectedPeers.Count > 0)
                    SelectedPeer = ConnectedPeers[0];
            }
        }

        // ═══ Public Methods ═══
        public void HandleFileDrop(string[] files)
        {
            if (files == null || files.Length == 0) return;
            var manager = LanTransferManager.Instance;
            if (manager == null || _selectedPeer == null) return;

            foreach (var file in files)
            {
                if (File.Exists(file))
                {
                    _ = manager.OfferFile(_selectedPeer, file);
                }
            }
        }

        public void Cleanup()
        {
            _refreshTimer.Stop();
            ActiveTransfers.CollectionChanged -= OnSourceCollectionChanged;
            CompletedTransfers.CollectionChanged -= OnSourceCollectionChanged;
        }

        // ═══ INotifyPropertyChanged ═══
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
