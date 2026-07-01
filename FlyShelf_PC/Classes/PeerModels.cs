// ---------------------------------------------------------------
// PeerModels — Data models for P2P networking
// PeerConnection, PeerStatusItem, CachedPeerUrls
// ---------------------------------------------------------------
using System;
using System.Net.WebSockets;
using System.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Represents a single P2P peer connection with its URLs, transport state, and liveness info.
    /// </summary>
    public class PeerConnection
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";

        // Thread-safe: these are read/written from HeartbeatLoop, DiscoveryLoop, Handshake,
        // MonitorWebSocket, Transfer, and URL Update threads concurrently.
        // Using lock(StateLock) for atomic multi-field updates.
        private string _lanUrl = "";
        public string LanUrl
        {
            get { lock (StateLock) return _lanUrl; }
            set { lock (StateLock) _lanUrl = value; }
        }
        private string _cloudflareUrl = "";
        public string CloudflareUrl
        {
            get { lock (StateLock) return _cloudflareUrl; }
            set { lock (StateLock) _cloudflareUrl = value; }
        }
        private string _activeUrl = "";
        public string ActiveUrl
        {
            get { lock (StateLock) return _activeUrl; }
            set { lock (StateLock) _activeUrl = value; }
        }
        private string _transport = "offline";
        public string Transport  // "LAN", "Cloudflare", "offline"
        {
            get { lock (StateLock) return _transport; }
            set { lock (StateLock) _transport = value; }
        }
        // FIX R8: volatile prevents torn reads when accessed from HeartbeatLoop,
        // MonitorWebSocket, HandlePeerDeath, and Transfer threads concurrently
        private volatile bool _isAlive;
        public bool IsAlive { get => _isAlive; set => _isAlive = value; }
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        // Thread-safe: accessed from HeartbeatLoop, HandlePeerFailure, MonitorWebSocket, TryConnectSingle
        private int _consecutiveFailures;
        public int ConsecutiveFailures
        {
            get => Interlocked.CompareExchange(ref _consecutiveFailures, 0, 0);
            set => Interlocked.Exchange(ref _consecutiveFailures, value);
        }
        public int IncrementFailures() => Interlocked.Increment(ref _consecutiveFailures);
        public string Version { get; set; } = "";
        public string DeviceType { get; set; } = "";

        // WebSocket for instant liveness detection — thread-safe via StateLock
        private ClientWebSocket? _liveSocket;
        public ClientWebSocket? LiveSocket
        {
            get { lock (StateLock) return _liveSocket; }
            set { lock (StateLock) _liveSocket = value; }
        }
        private CancellationTokenSource? _wsCts;
        public CancellationTokenSource? WsCts
        {
            get { lock (StateLock) return _wsCts; }
            set
            {
                lock (StateLock)
                {
                    // Dispose old CTS to prevent resource leak on replacement
                    if (_wsCts != null && _wsCts != value)
                    {
                        try { _wsCts.Dispose(); } catch { /* Best-effort cleanup */ }
                    }
                    _wsCts = value;
                }
            }
        }
        public SemaphoreSlim SendSemaphore { get; } = new(1, 1);
        public SemaphoreSlim HandshakeLock { get; } = new(1, 1); // Prevents concurrent handshakes from HeartbeatLoop/DiscoveryLoop/UDP/PeerAnnounce/UrlUpdate
        public readonly object StateLock = new(); // Protects atomic IsAlive + Transport updates

        // Exponential backoff for WebSocket reconnection (prevents tight reconnect loops)
        public int WsReconnectAttempts;

        // Active file transfer tracking (prevents marking peer dead mid-transfer)
        public int ActiveTransfers;

        // Dedicated TCP port for LAN file transfers (discovered via /api/health)
        public int TransferPort { get; set; } = 8998;
    }

    /// <summary>
    /// UI display model for showing peer status in HubWindow paired devices list.
    /// </summary>
    public class PeerStatusItem
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public bool IsAlive { get; set; }
        public string Transport { get; set; } = "offline";
        public bool IsLanActive { get; set; }
        public bool IsCloudActive { get; set; }
        public string StatusText { get; set; } = "Offline";
        public string LanUrl { get; set; } = "";
        public string CloudflareUrl { get; set; } = "";
        public string ActiveUrl { get; set; } = "";
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Serialization model for caching peer URLs locally across app restarts.
    /// </summary>
    public class CachedPeerUrls
    {
        public string? DeviceName { get; set; }
        public string? LanUrl { get; set; }
        public string? CloudflareUrl { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
