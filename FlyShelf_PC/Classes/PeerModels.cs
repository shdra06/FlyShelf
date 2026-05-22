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
        public string LanUrl { get; set; } = "";
        public string CloudflareUrl { get; set; } = "";
        public string ActiveUrl { get; set; } = "";
        public string Transport { get; set; } = "offline";  // "LAN", "Cloudflare", "offline"
        public bool IsAlive { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        public int ConsecutiveFailures { get; set; }
        public string Version { get; set; } = "";
        public string DeviceType { get; set; } = "";

        // WebSocket for instant liveness detection
        public ClientWebSocket? LiveSocket { get; set; }
        public CancellationTokenSource? WsCts { get; set; }
        public SemaphoreSlim SendSemaphore { get; } = new(1, 1);

        // Active file transfer tracking (prevents marking peer dead mid-transfer)
        public int ActiveTransfers;
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
