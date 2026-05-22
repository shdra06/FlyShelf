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
                    bool lanOn = SettingsManager.Current.EnableLocalLAN;
                    
                    // Auto-manage server: if either transport is on, server must be running
                    if (cfOn && !SettingsManager.Current.EnableLocalNetworkSync)
                    {
                        SettingsManager.Current.EnableLocalNetworkSync = true; // starts server
                    }
                    else if (!cfOn && !lanOn)
                    {
                        SettingsManager.Current.EnableLocalNetworkSync = false; // stops server
                    }
                    
                    if (cfOn && _isRunning)
                    {
                        _ = _cfDaemon.StartAsync(CurrentPort);
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

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                // Cleanup chunk directories from previous runs on startup
                try
                {
                    string chunksRoot = Path.Combine(Path.GetTempPath(), "FlyShelf_Chunks");
                    if (Directory.Exists(chunksRoot))
                    {
                        Directory.Delete(chunksRoot, true);
                        Logger.LogAction("CLEANUP", "Purged temporary chunk directories on startup.");
                    }
                }
                catch { }

                // Determine physical Local IP beforehand for bind fallback
                string localIp = "127.0.0.1";
                try
                {
                    using (System.Net.Sockets.Socket socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0))
                    {
                        socket.Connect("8.8.8.8", 65530);
                        if (socket.LocalEndPoint is System.Net.IPEndPoint endPoint)
                        {
                            localIp = endPoint.Address.ToString();
                        }
                    }
                }
                catch { }

                int publicPort = 8999;
                bool needsProxy = false;

                // === PHASE 1: Try to bind HttpListener to ALL interfaces (no proxy needed) ===
                bool allInterfacesBound = false;
                
                // Strategy 1: http://+:port/ (accepts ALL interfaces)
                if (!allInterfacesBound) try {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://+:{publicPort}/");
                    _listener.Start();
                    Logger.LogAction("BIND", $"✅ Bound to http://+:{publicPort}/ (all interfaces — no proxy needed)");
                    allInterfacesBound = true;
                } catch (Exception ex) { 
                    Logger.LogAction("BIND", $"http://+:{publicPort}/ failed: {ex.Message}");
                    if (_listener != null) { try { _listener.Close(); } catch { } } 
                }

                // Strategy 2: http://*:port/
                if (!allInterfacesBound) try {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://*:{publicPort}/");
                    _listener.Start();
                    Logger.LogAction("BIND", $"✅ Bound to http://*:{publicPort}/ (all interfaces — no proxy needed)");
                    allInterfacesBound = true;
                } catch (Exception ex) { 
                    Logger.LogAction("BIND", $"http://*:{publicPort}/ failed: {ex.Message}");
                    if (_listener != null) { try { _listener.Close(); } catch { } } 
                }

                // Strategy 3: http://{localIp}:port/ + http://localhost:port/
                if (!allInterfacesBound) try {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://{localIp}:{publicPort}/");
                    _listener.Prefixes.Add($"http://localhost:{publicPort}/");
                    _listener.Start();
                    Logger.LogAction("BIND", $"✅ Bound to http://{localIp}:{publicPort}/ + http://localhost:{publicPort}/");
                    allInterfacesBound = true;
                } catch (Exception ex) { 
                    Logger.LogAction("BIND", $"Dual-bind failed: {ex.Message}");
                    if (_listener != null) { try { _listener.Close(); } catch { } } 
                }

                // === PHASE 2: Localhost-only + TCP Proxy (works without admin) ===
                if (!allInterfacesBound)
                {
                    // Bind HttpListener to localhost on an INTERNAL port (publicPort + 10000)
                    int internalPort = publicPort + 10000; // e.g., 18999
                    bool localhostBound = false;

                    try {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://localhost:{internalPort}/");
                        _listener.Start();
                        localhostBound = true;
                        Logger.LogAction("BIND", $"✅ HttpListener bound to http://localhost:{internalPort}/ (internal)");
                    } catch (Exception ex) {
                        Logger.LogAction("BIND", $"localhost:{internalPort} failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } }
                    }

                    // Fallback: try 127.0.0.1
                    if (!localhostBound) try {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://127.0.0.1:{internalPort}/");
                        _listener.Start();
                        localhostBound = true;
                        Logger.LogAction("BIND", $"✅ HttpListener bound to http://127.0.0.1:{internalPort}/ (internal)");
                    } catch (Exception ex) {
                        Logger.LogAction("BIND", $"127.0.0.1:{internalPort} failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } }
                    }

                    if (!localhostBound)
                    {
                        throw new Exception("Cannot bind HTTP server to ANY address — check antivirus/firewall.");
                    }

                    // Start TCP Proxy on 0.0.0.0:publicPort → forwards to localhost:internalPort
                    // TcpListener does NOT need admin privileges or URL ACLs!
                    try
                    {
                        _proxyListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, publicPort);
                        _proxyListener.Start();
                        _proxyRunning = true;
                        needsProxy = true;
                        _proxyInternalPort = internalPort;
                        
                        // Accept connections in background
                        _ = Task.Run(async () => await TcpProxyLoop(internalPort));
                        
                        Logger.LogAction("BIND", $"✅ TCP Proxy started: 0.0.0.0:{publicPort} → localhost:{internalPort} (LAN + Cloudflare enabled)");
                    }
                    catch (Exception proxyEx)
                    {
                        Logger.LogAction("BIND", $"❌ TCP Proxy on port {publicPort} failed: {proxyEx.Message} — LAN access will NOT work");
                        // Even without the proxy, the HttpListener on localhost still works for Cloudflare
                        // (cloudflared connects to localhost). So we don't throw here.
                    }
                }

                CurrentPort = publicPort;

                _isRunning = true;
                _listenerThread = new Thread(() =>
                {
                    try { Task.Run(ListenLoopAsync).GetAwaiter().GetResult(); }
                    catch (Exception ex) { Logger.LogAction("LISTENER", $"Thread crash caught: {ex.Message}"); }
                });
                _listenerThread.IsBackground = true;
                _listenerThread.Start();

                UpdateServerUrl();
                CloudDiscoveryManager.CachedLocalUrl = DisplayUrl; // Cache first LAN URL for file download fallback
                string bindMode = needsProxy ? "TCP Proxy" : "Direct";
                Logger.LogAction("NETWORK", $"✅ Web server launched on {ServerUrl} (port {CurrentPort}, mode: {bindMode})");
                NetworkActivityLog.Instance.ServerStatus = "Online";

                // Natively trigger Cloudflare alongside HTTP Socket unconditionally
                // If we used a TCP proxy, Cloudflare tunnels to publicPort which the TcpProxy handles.
                // If we bound directly, Cloudflare tunnels to publicPort which HttpListener handles.
                _ = _cfDaemon.StartAsync(CurrentPort);
                _ = CloudDiscoveryManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl);

                // Heartbeat: reduced from 60s to 300s — Firebase writes are now throttled
                // inside PushTunnelUrl (only writes on URL change). Timer is mainly for
                // checking pairing handshakes and keeping the tunnel URL fresh in cache.
                _heartbeatTimer = new System.Timers.Timer(300_000); // 5 minutes
                _heartbeatTimer.Elapsed += (s, e) =>
                {
                    // PushTunnelUrl is now smart — it only writes to Firebase if URL changed
                    _ = CloudDiscoveryManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl);
                    // Check for new devices that joined via pairing code
                    _ = DevicePairingManager.CheckForHandshakes();

                    // Clean up abandoned chunk upload sessions older than 2 hours
                    try
                    {
                        string chunksRoot = Path.Combine(Path.GetTempPath(), "FlyShelf_Chunks");
                        if (Directory.Exists(chunksRoot))
                        {
                            var dirs = Directory.GetDirectories(chunksRoot);
                            foreach (var dir in dirs)
                            {
                                try
                                {
                                    var dirInfo = new DirectoryInfo(dir);
                                    if (DateTime.Now - dirInfo.LastWriteTime > TimeSpan.FromHours(2))
                                    {
                                        Directory.Delete(dir, true);
                                        _chunkSessions.TryRemove(dirInfo.Name, out _);
                                        Logger.LogAction("CLEANUP", $"Deleted abandoned chunk session directory: {dirInfo.Name}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                };
                _heartbeatTimer.AutoReset = true;
                _heartbeatTimer.Start();
                Logger.LogAction("HEARTBEAT", "Device heartbeat started (300s interval, Firebase writes only on URL change)");
                // ═══════════════════════════════════════════════════════════════════
                // TLS LAYER: Start HTTPS proxy on port 9443 → forwards to HTTP server
                // ═══════════════════════════════════════════════════════════════════
                try
                {
                    _tlsCert = EnsureTlsCertificate();
                    TlsThumbprint = _tlsCert.Thumbprint;

                    _tlsListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, TLS_PORT);
                    _tlsListener.Start();
                    _tlsRunning = true;

                    _ = Task.Run(async () => await TlsProxyLoop());

                    // Determine LAN IP for TLS URL
                    string tlsIp = localIp;
                    TlsUrl = $"https://{tlsIp}:{TLS_PORT}";
                    Logger.LogAction("TLS", $"✅ HTTPS server started on {TlsUrl} (thumbprint: {TlsThumbprint})");
                }
                catch (Exception tlsEx)
                {
                    Logger.LogAction("TLS", $"⚠️ HTTPS server failed to start: {tlsEx.Message} — LAN sync will use HTTP only");
                }

                // ═══════════════════════════════════════════════════════════════════
                // v5 PEER MANAGER: Direct P2P communication engine
                // Discovers peers, handshakes, and pushes data directly via LAN/Cloudflare.
                // Firebase is only used for URL discovery (~5 seconds at startup).
                // ═══════════════════════════════════════════════════════════════════
                _ = Task.Run(async () =>
                {
                    // Wait a bit for Cloudflare tunnel to establish first
                    await Task.Delay(8000);
                    try
                    {
                        var peerManager = new PeerManager();
                        await peerManager.StartAsync();
                        Logger.LogAction("PEER", $"v5 PeerManager initialized — {peerManager.AliveCount} peer(s) connected");
                    }
                    catch (Exception pmEx)
                    {
                        Logger.LogAction("PEER", $"PeerManager startup error: {pmEx.Message}");
                    }

                    // Start cross-device log streaming (bidirectional)
                    try
                    {
                        StartLocalLogCapture();
                        StartRemoteLogPush();
                        Logger.LogAction("SERVER", "Remote log streaming activated — view at /logs");
                    }
                    catch (Exception logEx)
                    {
                        Logger.LogAction("SERVER", $"Remote log streaming startup error: {logEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                ServerUrl = "Fatal Error Bind Failed";
                Logger.LogAction("NETWORK ERROR", $"❌ Server failed to start: {ex.Message}");
            }
            
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.RefreshLocalServerData());
        }

        // ═══════════════════════════════════════════════════════════════════
        // TCP Reverse Proxy: Enables LAN access without admin/URL ACL.
        // TcpListener on 0.0.0.0:publicPort accepts any connection and
        // proxies the raw TCP stream to HttpListener on localhost:internalPort.
        // ═══════════════════════════════════════════════════════════════════

        private async Task TcpProxyLoop(int internalPort)
        {
            while (_proxyRunning && _proxyListener != null)
            {
                try
                {
                    var client = await _proxyListener.AcceptTcpClientAsync();
                    // Handle each connection in parallel — don't block the accept loop
                    _ = Task.Run(() => ProxyConnection(client, internalPort));
                }
                catch (ObjectDisposedException) { break; }
                catch (System.Net.Sockets.SocketException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("TCP PROXY", $"Accept error: {ex.Message}");
                    if (!_proxyRunning) break;
                    await Task.Delay(100);
                }
            }
        }

        private async Task ProxyConnection(System.Net.Sockets.TcpClient client, int targetPort)
        {
            try
            {
                using (client)
                using (var target = new System.Net.Sockets.TcpClient())
                {
                    client.NoDelay = true;
                    await target.ConnectAsync("localhost", targetPort);
                    target.NoDelay = true;

                    var clientStream = client.GetStream();
                    var targetStream = target.GetStream();
                    using var bufferedClient = new System.IO.BufferedStream(clientStream, 8192);

                    // === HTTP-AWARE PROXY: Rewrite the Host header ===
                    // HttpListener validates Host header against its prefix.
                    // Browser sends "Host: 192.168.1.36:8999" but HttpListener
                    // expects "Host: localhost:18999". We MUST rewrite it.
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

                    // Read the initial HTTP headers from the client
                    var headerBytes = new System.Collections.Generic.List<byte>(4096);
                    byte[] buf = new byte[1];
                    int headerEnd = -1;

                    // Read byte-by-byte until we find \r\n\r\n (end of HTTP headers)
                    while (headerBytes.Count < 16384) // 16KB max header size
                    {
                        int read = await bufferedClient.ReadAsync(buf, 0, 1, cts.Token);
                        if (read == 0) return; // Client disconnected
                        headerBytes.Add(buf[0]);

                        int len = headerBytes.Count;
                        if (len >= 4 &&
                            headerBytes[len - 4] == (byte)'\r' &&
                            headerBytes[len - 3] == (byte)'\n' &&
                            headerBytes[len - 2] == (byte)'\r' &&
                            headerBytes[len - 1] == (byte)'\n')
                        {
                            headerEnd = len;
                            break;
                        }
                    }

                    if (headerEnd <= 0) return; // No valid HTTP headers

                    // Parse and rewrite the Host header
                    string headerText = System.Text.Encoding.ASCII.GetString(headerBytes.ToArray(), 0, headerEnd);
                    string rewritten = System.Text.RegularExpressions.Regex.Replace(
                        headerText,
                        @"(?i)Host:\s*[^\r\n]+",
                        $"Host: localhost:{targetPort}");

                    // Send rewritten headers to HttpListener
                    byte[] rewrittenBytes = System.Text.Encoding.ASCII.GetBytes(rewritten);
                    await targetStream.WriteAsync(rewrittenBytes, 0, rewrittenBytes.Length, cts.Token);

                    // Now relay the rest bi-directionally (body + response)
                    var t1 = bufferedClient.CopyToAsync(targetStream, cts.Token);
                    var t2 = targetStream.CopyToAsync(clientStream, cts.Token);
                    await Task.WhenAny(t1, t2);
                }
            }
            catch { } // Connection closed — normal
        }

        // ═══════════════════════════════════════════════════════════════════
        // TLS Proxy: Accepts HTTPS connections on port 9443, terminates TLS,
        // then proxies the decrypted HTTP stream to HttpListener on localhost.
        // This provides encrypted LAN sync without requiring netsh or admin.
        // ═══════════════════════════════════════════════════════════════════
        private async Task TlsProxyLoop()
        {
            while (_tlsRunning && _tlsListener != null)
            {
                try
                {
                    var client = await _tlsListener!.AcceptTcpClientAsync();
                    _ = Task.Run(() => TlsProxyConnection(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (System.Net.Sockets.SocketException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("TLS", $"Accept error: {ex.Message}");
                    if (!_tlsRunning) break;
                    await Task.Delay(100);
                }
            }
        }

        private async Task TlsProxyConnection(System.Net.Sockets.TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    var networkStream = client.GetStream();

                    // Wrap in SslStream — this terminates TLS
                    using var sslStream = new SslStream(networkStream, false);
                    await sslStream.AuthenticateAsServerAsync(_tlsCert!, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                    using var bufferedSsl = new System.IO.BufferedStream(sslStream, 8192);

                    // Connect to the local HTTP server
                    using var target = new System.Net.Sockets.TcpClient();
                    // Route to the internal port if proxy mode, else to the public port
                    int targetPort = _proxyInternalPort > 0 ? _proxyInternalPort : CurrentPort;
                    await target.ConnectAsync("localhost", targetPort);
                    target.NoDelay = true;
                    var targetStream = target.GetStream();

                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

                    // Read HTTP headers from the decrypted stream
                    var headerBytes = new List<byte>(4096);
                    byte[] buf = new byte[1];
                    int headerEnd = -1;

                    while (headerBytes.Count < 16384)
                    {
                        int read = await bufferedSsl.ReadAsync(buf, 0, 1, cts.Token);
                        if (read == 0) return;
                        headerBytes.Add(buf[0]);

                        int len = headerBytes.Count;
                        if (len >= 4 &&
                            headerBytes[len - 4] == (byte)'\r' &&
                            headerBytes[len - 3] == (byte)'\n' &&
                            headerBytes[len - 2] == (byte)'\r' &&
                            headerBytes[len - 1] == (byte)'\n')
                        {
                            headerEnd = len;
                            break;
                        }
                    }

                    if (headerEnd <= 0) return;

                    // Rewrite Host header to match HttpListener's expected prefix
                    string headerText = Encoding.ASCII.GetString(headerBytes.ToArray(), 0, headerEnd);
                    string rewritten = System.Text.RegularExpressions.Regex.Replace(
                        headerText,
                        @"(?i)Host:\s*[^\r\n]+",
                        $"Host: localhost:{targetPort}");

                    byte[] rewrittenBytes = Encoding.ASCII.GetBytes(rewritten);
                    await targetStream.WriteAsync(rewrittenBytes, 0, rewrittenBytes.Length, cts.Token);

                    // Bi-directional relay: sslStream ↔ targetStream
                    var t1 = bufferedSsl.CopyToAsync(targetStream, cts.Token);
                    var t2 = targetStream.CopyToAsync(sslStream, cts.Token);
                    await Task.WhenAny(t1, t2);
                }
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                Logger.LogAction("TLS", $"Client TLS handshake failed: {ex.Message}");
            }
            catch { } // Connection closed — normal
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _proxyRunning = false;
            ServerUrl = "Offline";
            try { _heartbeatTimer?.Stop(); _heartbeatTimer?.Dispose(); } catch { }
            _cfDaemon.Stop();
            _ = CloudDiscoveryManager.PushTunnelUrl("offline", false, "", forceWrite: true);
            try { _listener?.Stop(); } catch { }
            try { _proxyListener?.Stop(); } catch { }
            // Stop TLS proxy
            _tlsRunning = false;
            try { _tlsListener?.Stop(); } catch { }
            try { _tlsCert?.Dispose(); } catch { }
            TlsUrl = "";
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.RefreshLocalServerData());
        }

        private void UpdateServerUrl()
        {
            try
            {
                var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(x => x.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up 
                             && !x.Description.ToLower().Contains("virtualbox") 
                             && !x.Description.ToLower().Contains("vmware") 
                             && !x.Description.ToLower().Contains("hyper-v")
                             && !x.Description.ToLower().Contains("wsl"))
                    .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                    .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork 
                             && !System.Net.IPAddress.IsLoopback(x.Address))
                    .Select(x => x.Address.ToString())
                    .ToList();
                
                if (ips.Count > 0)
                {
                    ServerUrl = string.Join(",", ips.Select(ip => $"http://{ip}:{CurrentPort}"));
                }
                else
                {
                    ServerUrl = $"http://localhost:{CurrentPort}";
                }
            }
            catch { ServerUrl = $"http://localhost:{CurrentPort}"; }
        }

        private async Task ListenLoopAsync()
        {
            while (_isRunning && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try { await ProcessRequest(context); }
                        catch (Exception ex) { Logger.LogAction("HTTP", $"ProcessRequest error: {ex.Message}"); }
                    });
                }
                catch (ObjectDisposedException) { break; } // Listener closed cleanly
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.ErrorCode == 64) { break; } // I/O abort — normal on stop
                catch (Exception ex)
                {
                    Logger.LogAction("HTTP", $"ListenLoop error: {ex.Message}");
                    await Task.Delay(500);
                    if (_listener == null || !_listener.IsListening) break;
                }
            }
        }

    }
}

