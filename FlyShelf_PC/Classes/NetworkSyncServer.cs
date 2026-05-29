using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class NetworkSyncServer
    {
        public static NetworkSyncServer? Instance { get; private set; }
        private HttpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning = false;
        private FlyShelfViewModel _viewModel;
        private CloudflareDaemon _cfDaemon = new CloudflareDaemon();
        private System.Timers.Timer _heartbeatTimer;
        private System.Net.Sockets.TcpListener _proxyListener = null;
        private bool _proxyRunning = false;
        private int _proxyInternalPort = 0;
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly System.Text.RegularExpressions.Regex _rxBase64 = new System.Text.RegularExpressions.Regex(
            @"^[A-Za-z0-9+/=\r\n]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _rxWinPath = new System.Text.RegularExpressions.Regex(
            @"^[a-zA-Z]:[\\/]", System.Text.RegularExpressions.RegexOptions.Compiled);

        // ═══════════════════════════════════════════════════════════════════
        // Phase 3: Track directly-connected devices (via LAN/Cloudflare)
        // When all paired devices are polling /api/sync directly, we can
        // skip pushing clipboard data to Firebase entirely.
        // ═══════════════════════════════════════════════════════════════════
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _directDeviceLastSeen = new();
        private const long DIRECT_DEVICE_STALE_MS = 30_000; // 30s — device must poll at least this often

        /// <summary>
        /// Detect transport method from HTTP request. Returns ("LAN" or "Cloudflare", sourceDeviceLabel).
        /// </summary>
        private static (string transport, string label) DetectTransport(HttpListenerRequest req)
        {
            // Cloudflare tunnel (cloudflared) proxies to localhost, so Host header
            // is always the local address. Detect CF by checking proxy-injected headers:
            //   Cf-Connecting-Ip — real client IP added by cloudflared
            //   Cf-Ray — Cloudflare ray ID for request tracing
            //   X-Forwarded-For — standard proxy header added by CF
            //   X-Forwarded-Proto — "https" when coming through CF tunnel
            string host = req.Headers["Host"] ?? req.Url?.Host ?? "";
            bool isCf = host.Contains(".trycloudflare.com")
                      || !string.IsNullOrEmpty(req.Headers["Cf-Connecting-Ip"])
                      || !string.IsNullOrEmpty(req.Headers["Cf-Ray"])
                      || !string.IsNullOrEmpty(req.Headers["X-Forwarded-For"])
                      || req.Headers["X-Forwarded-Proto"] == "https";
            return isCf ? ("Cloudflare", "☁ Cloud") : ("LAN", "📡 LAN");
        }

        /// <summary>
        /// Returns the count of paired devices that have polled /api/sync within the last 30 seconds.
        /// Used by CloudDiscoveryManager to decide whether Firebase push can be skipped.
        /// </summary>
        public int GetDirectlyConnectedDeviceCount()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int count = 0;
            foreach (var kvp in _directDeviceLastSeen)
            {
                if (now - kvp.Value < DIRECT_DEVICE_STALE_MS)
                    count++;
                else
                    _directDeviceLastSeen.TryRemove(kvp.Key, out _);
            }
            return count;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Long-Poll: Real-time push to mobile clients via blocked HTTP request
        // ═══════════════════════════════════════════════════════════════════
        private readonly List<TaskCompletionSource<string>> _longPollWaiters = new List<TaskCompletionSource<string>>();
        private readonly object _longPollLock = new object();

        /// <summary>
        /// Call this whenever the clipboard changes to instantly unblock all waiting long-poll clients.
        /// </summary>
        public void NotifyClipboardChanged(string itemType = "clipboard", string title = "")
        {
            // Invalidate the sync cache so the next /api/sync poll returns fresh data
            _cachedSyncJson = null;

            string payload = $"{{\"type\":\"{itemType}\",\"title\":\"{title.Replace("\"", "'").Replace("\n", " ")}\",\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
            int waiterCount;
            lock (_longPollLock)
            {
                waiterCount = _longPollWaiters.Count;
                foreach (var tcs in _longPollWaiters)
                {
                    tcs.TrySetResult(payload);
                }
                _longPollWaiters.Clear();
            }
            Logger.LogAction("PUSH", $"NotifyClipboardChanged: {itemType} — unblocked {waiterCount} waiting client(s)");
        }
        
        public string ServerUrl { get; private set; } = "Not Running";
        public string DisplayUrl => ServerUrl.Split(',')[0];
        public string GlobalUrl => _cfDaemon.GlobalUrl;
        public int CurrentPort { get; private set; } = 3000;

        private static readonly string[] _allowedRoots = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            // OneDrive-redirected Desktop & Documents (common on Win10/11)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Desktop"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents"),
            Path.GetTempPath()
        };

        private static readonly DateTime _downloadGracePeriodEnd = DateTime.MinValue; // Auth always enforced

        // ═══════════════════════════════════════════════════════════════════
        // TLS: Self-signed certificate for LAN HTTPS
        // ═══════════════════════════════════════════════════════════════════
        private const int TLS_PORT = 9443;
        private System.Net.Sockets.TcpListener? _tlsListener;
        private bool _tlsRunning = false;
        private X509Certificate2? _tlsCert;
        public string TlsThumbprint { get; private set; } = "";
        public string TlsUrl { get; private set; } = "";

        /// <summary>
        /// Loads or generates a self-signed X509 certificate for HTTPS.
        /// Stored in %AppData%\FlyShelf\server.pfx (persists across restarts).
        /// </summary>
        private X509Certificate2 EnsureTlsCertificate()
        {
            string certDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
            Directory.CreateDirectory(certDir);
            string certPath = Path.Combine(certDir, "server.pfx");

            // Try to load existing cert
            if (File.Exists(certPath))
            {
                try
                {
                    var existing = X509CertificateLoader.LoadPkcs12FromFile(certPath, "advanceclip_tls", X509KeyStorageFlags.Exportable);
                    // Check if cert is still valid (not expired, has at least 30 days left)
                    if (existing.NotAfter > DateTime.Now.AddDays(30))
                    {
                        Logger.LogAction("TLS", $"Loaded existing certificate: {existing.Thumbprint} (expires {existing.NotAfter:yyyy-MM-dd})");
                        return existing;
                    }
                    existing.Dispose();
                    Logger.LogAction("TLS", "Certificate expiring soon — regenerating");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TLS", $"Failed to load cert: {ex.Message} — regenerating");
                }
            }

            // Generate new self-signed cert
            Logger.LogAction("TLS", "Generating new self-signed certificate...");
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=FlyShelf Local Server",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Add Subject Alternative Names for LAN IPs
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(Environment.MachineName);
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
            // Add current LAN IP
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is System.Net.IPEndPoint ep)
                    sanBuilder.AddIpAddress(ep.Address);
            }
            catch { }
            req.CertificateExtensions.Add(sanBuilder.Build());

            // Valid for 2 years
            var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));

            // Export with private key
            var exported = cert.Export(X509ContentType.Pfx, "advanceclip_tls");
            File.WriteAllBytes(certPath, exported);

            // Re-import from file (ensures proper key storage)
            var finalCert = X509CertificateLoader.LoadPkcs12FromFile(certPath, "advanceclip_tls", X509KeyStorageFlags.Exportable);
            Logger.LogAction("TLS", $"Generated certificate: {finalCert.Thumbprint} (expires {finalCert.NotAfter:yyyy-MM-dd})");
            return finalCert;
        }

        /// <summary>
        /// Validates that a requested file path is within an allowed directory
        /// or is currently in the clipboard history. Prevents arbitrary file read.
        /// </summary>
        private bool IsPathAllowed(string requestedPath)
        {
            try
            {
                string resolved = Path.GetFullPath(requestedPath);
                foreach (var root in _allowedRoots)
                {
                    string allowedRoot = Path.GetFullPath(root);
                    string rootWithSeparator = allowedRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                        ? allowedRoot
                        : allowedRoot + Path.DirectorySeparatorChar;

                    if (resolved.Equals(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
                        resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                // Check if file is currently in the clipboard (live items)
                var activePaths = GetAllActiveFilePaths();
                return activePaths.Contains(resolved, StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns all file paths currently held by clipboard items.
        /// </summary>
        private HashSet<string> GetAllActiveFilePaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var item in _viewModel.DroppedItems)
                    {
                        if (!string.IsNullOrEmpty(item.FilePath))
                            paths.Add(Path.GetFullPath(item.FilePath));
                        if (!string.IsNullOrEmpty(item.ZippedArchivePath))
                            paths.Add(Path.GetFullPath(item.ZippedArchivePath));
                    }
                });
            }
            catch { }
            return paths;
        }

        public NetworkSyncServer(FlyShelfViewModel viewModel)
        {
            Instance = this;
            _viewModel = viewModel;
            _cfDaemon.GlobalUrlUpdated += (url) => { 
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.RefreshLocalServerData()); 
                if (!string.IsNullOrEmpty(url) && url.Contains(".trycloudflare.com"))
                {
                    // Purge Firebase entries with the old dead Cloudflare URL before caching the new one
                    string oldUrl = _cfDaemon.PreviousGlobalUrl;
                    if (!string.IsNullOrEmpty(oldUrl) && oldUrl != url)
                    {
                        Logger.LogAction("TUNNEL CHANGE", $"URL changed: {oldUrl.Substring(0, Math.Min(40, oldUrl.Length))}... → {url.Substring(0, Math.Min(40, url.Length))}...");
                        _ = CloudDiscoveryManager.PurgeStaleFileEntries(oldUrl);
                    }
                    CloudDiscoveryManager.CachedGlobalUrl = url; // Cache for file download URL construction
                    CloudDiscoveryManager.CachedTunnelVerified = _cfDaemon.IsTunnelVerified; // Only allow file downloads if verified
                    _ = CloudDiscoveryManager.PushTunnelUrl(url, true, ServerUrl);
                }
            };
            
            SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AdvanceSettings.EnableGlobalCloudflare))
                {
                    bool cfOn = SettingsManager.Current.EnableGlobalCloudflare;
                    
                    if (cfOn && !LicenseManager.CanUseCloudflare())
                    {
                        SettingsManager.Current.EnableGlobalCloudflare = false;
                        SettingsManager.Save();
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            UpgradePrompt.ShowCloudflareLimit());
                        return;
                    }

                    bool lanOn = SettingsManager.Current.EnableLocalLAN;
                    
                    bool serverStartedJustNow = false;
                    // Auto-manage server: if either transport is on, server must be running
                    if (cfOn && !SettingsManager.Current.EnableLocalNetworkSync)
                    {
                        serverStartedJustNow = true;
                        SettingsManager.Current.EnableLocalNetworkSync = true; // starts server (internally launches tunnel)
                    }
                    else if (!cfOn && !lanOn)
                    {
                        SettingsManager.Current.EnableLocalNetworkSync = false; // stops server
                    }
                    
                    if (cfOn && _isRunning)
                    {
                        if (!serverStartedJustNow)
                        {
                            _ = _cfDaemon.StartAsync(CurrentPort);
                        }
                        // When tunnel comes up, ForceResync to broadcast new URL to all peers
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(5000); // Wait for tunnel to establish
                            if (PeerManager.Instance != null)
                            {
                                Logger.LogAction("NETWORK", "Cloudflare ON — triggering peer resync");
                                await PeerManager.Instance.ForceResync();
                            }
                        });
                    }
                    else if (!cfOn)
                    {
                        _cfDaemon.Stop();
                        // Notify peers we're going offline from cloud
                        _ = Task.Run(async () =>
                        {
                            if (PeerManager.Instance != null)
                            {
                                Logger.LogAction("NETWORK", "Cloudflare OFF — triggering peer resync");
                                await PeerManager.Instance.ForceResync();
                            }
                        });
                    }
                }
            };
        }

        // ═══ Server Lifecycle (Start/Stop/TLS/Proxy) moved to NetworkSyncServer.Lifecycle.cs ═══
    }
}
