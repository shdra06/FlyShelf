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
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    public class NetworkSyncServer
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
            string host = req.Headers["Host"] ?? req.Url?.Host ?? "";
            if (host.Contains(".trycloudflare.com"))
                return ("Cloudflare", "☁ Cloud");
            else
                return ("LAN", "📡 LAN");
        }

        /// <summary>
        /// Returns the count of paired devices that have polled /api/sync within the last 30 seconds.
        /// Used by FirebaseSyncManager to decide whether Firebase push can be skipped.
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip"),
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
        /// Stored in %AppData%\AdvanceClip\server.pfx (persists across restarts).
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
                    if (resolved.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                        return true;
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
                        _ = FirebaseSyncManager.PurgeStaleFileEntries(oldUrl);
                    }
                    FirebaseSyncManager.CachedGlobalUrl = url; // Cache for file download URL construction
                    FirebaseSyncManager.CachedTunnelVerified = _cfDaemon.IsTunnelVerified; // Only allow file downloads if verified
                    _ = FirebaseSyncManager.PushTunnelUrl(url, true, ServerUrl);
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
                FirebaseSyncManager.CachedLocalUrl = DisplayUrl; // Cache first LAN URL for file download fallback
                string bindMode = needsProxy ? "TCP Proxy" : "Direct";
                Logger.LogAction("NETWORK", $"✅ Web server launched on {ServerUrl} (port {CurrentPort}, mode: {bindMode})");
                NetworkActivityLog.Instance.ServerStatus = "Online";

                // Natively trigger Cloudflare alongside HTTP Socket unconditionally
                // If we used a TCP proxy, Cloudflare tunnels to publicPort which the TcpProxy handles.
                // If we bound directly, Cloudflare tunnels to publicPort which HttpListener handles.
                _ = _cfDaemon.StartAsync(CurrentPort);
                _ = FirebaseSyncManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl);

                // Heartbeat: reduced from 60s to 300s — Firebase writes are now throttled
                // inside PushTunnelUrl (only writes on URL change). Timer is mainly for
                // checking pairing handshakes and keeping the tunnel URL fresh in cache.
                _heartbeatTimer = new System.Timers.Timer(300_000); // 5 minutes
                _heartbeatTimer.Elapsed += (s, e) =>
                {
                    // PushTunnelUrl is now smart — it only writes to Firebase if URL changed
                    _ = FirebaseSyncManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl);
                    // Check for new devices that joined via pairing code
                    _ = DevicePairingManager.CheckForHandshakes();
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
                        int read = await clientStream.ReadAsync(buf, 0, 1, cts.Token);
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
                    var t1 = clientStream.CopyToAsync(targetStream, cts.Token);
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
                        int read = await sslStream.ReadAsync(buf, 0, 1, cts.Token);
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
                    var t1 = sslStream.CopyToAsync(targetStream, cts.Token);
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
            _ = FirebaseSyncManager.PushTunnelUrl("offline", false, "", forceWrite: true);
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

        private async Task ProcessRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            try
            {
                string path = req.Url.LocalPath.ToLower();
                string remoteAddr = req.RemoteEndPoint?.ToString() ?? "unknown";
                Logger.LogAction("HTTP", $"[{remoteAddr}] {req.HttpMethod} {path}");
                
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Original-Date, X-FlyShelf-Client, X-Pairing-Key");
                res.AddHeader("Access-Control-Expose-Headers", "X-Global-Url");
                if (!string.IsNullOrEmpty(GlobalUrl)) res.AddHeader("X-Global-Url", GlobalUrl);

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 200;
                    res.Close();
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    ServeHtml(res);
                }
                else if (path == "/ping")
                {
                    // Unauthenticated ping for LAN reachability detection
                    byte[] pong = Encoding.UTF8.GetBytes("pong");
                    res.StatusCode = 200;
                    res.ContentType = "text/plain";
                    res.OutputStream.Write(pong, 0, pong.Length);
                    res.Close();
                }
                else if (path == "/api/health" && req.HttpMethod == "GET")
                {
                    // Health check is PUBLIC — needed for Cloudflare tunnel self-verification
                    res.StatusCode = 200;
                    res.Close();
                }
                else if (path == "/ws/peer" && req.IsWebSocketRequest)
                {
                    // WebSocket peer liveness — persistent connection for instant death detection
                    string wsPairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                    string expectedKey = DevicePairingManager.EnsurePairingKey();
                    if (string.IsNullOrEmpty(wsPairingKey) || wsPairingKey != expectedKey)
                    {
                        res.StatusCode = 403;
                        res.Close();
                        return;
                    }
                    string peerDeviceId = req.Headers["X-Device-Id"] ?? req.QueryString["deviceId"] ?? "unknown";
                    Logger.LogAction("WS", $"✅ Peer WebSocket accepted from {peerDeviceId}");
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = Task.Run(() => HandlePeerWebSocket(wsContext.WebSocket, peerDeviceId));
                }
                else if (path == "/download" && req.HttpMethod == "GET")
                {
                    // SECURITY: /download requires authentication (pairing key or PIN)
                    string dlPairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                    string dlPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"] ?? "";
                    bool dlAuthed = DevicePairingManager.IsDevicePaired(dlPairingKey) ||
                                   (!string.IsNullOrEmpty(dlPin) && dlPin == SettingsManager.Current.WebClientPinToken);

                    if (!dlAuthed)
                    {
                        Logger.LogAction("SECURITY", $"⛔ Rejected unauthenticated /download from {req.RemoteEndPoint}");
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 — Download requires authentication\"}");
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                    }
                    else
                    {
                        await ServeFileDownload(req, res);
                    }
                }
                else if (path == "/api/pair" && req.HttpMethod == "POST")
                {
                    // QR Code pairing — validates pairing key and registers device
                    await HandlePairRequest(req, res);
                }
                else if (path == "/api/discover" && req.HttpMethod == "GET")
                {
                    // Paired device discovery — returns current connection URLs
                    string pairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"];
                    if (DevicePairingManager.IsDevicePaired(pairingKey))
                    {
                        string deviceId = req.Headers["X-Device-Id"] ?? "";
                        string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(deviceId))
                            DevicePairingManager.TouchDevice(deviceId, remoteIp);

                        var info = new
                        {
                            status = "ok",
                            localUrl = DisplayUrl,
                            globalUrl = GlobalUrl ?? "",
                            pin = SettingsManager.Current.WebClientPinToken,
                            deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName
                        };
                        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info));
                        res.StatusCode = 200;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(json, 0, json.Length);
                        res.Close();
                    }
                    else
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                        res.StatusCode = 403;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                    }
                }
                else
                {
                    // HARD SECURE AUTHENTICATION BARRIER
                    string providedPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"];
                    string pairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"];
                    
                    bool isNativeMobileCompanion = req.Headers["User-Agent"]?.Contains("FlyShelfMobile_Native") == true || req.Headers["X-FlyShelf-Client"] == "MobileCompanion" || req.Headers["X-FlyShelf-Client"] == "DesktopSync";
                    bool isPairedDevice = DevicePairingManager.IsDevicePaired(pairingKey);
                    
                    if (!isNativeMobileCompanion && !isPairedDevice && (string.IsNullOrEmpty(providedPin) || providedPin != SettingsManager.Current.WebClientPinToken))
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 Unauthorized - Invalid PIN\"}");
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                        return;
                    }

                    if (path == "/api/health" && req.HttpMethod == "GET")
                    {
                        res.StatusCode = 200;
                        res.Close();
                    }
                    else if (path == "/api/sync" && req.HttpMethod == "GET")
                    {
                        // Track this device as directly connected (Phase 3)
                        string deviceId = req.Headers["X-Pairing-Key"] ?? req.Headers["X-Device-Id"] ?? req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                        _directDeviceLastSeen[deviceId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        FirebaseSyncManager.DirectlyConnectedDeviceCount = GetDirectlyConnectedDeviceCount();
                        ServeClipboardData(res);
                    }
                    else if (path == "/api/events" && req.HttpMethod == "GET")
                    {
                        // Long-poll endpoint — blocks until clipboard changes or 30s timeout
                        // React Native can't use SSE/ReadableStream, so we use long-polling instead
                        var tcs = new TaskCompletionSource<string>();
                        lock (_longPollLock) { _longPollWaiters.Add(tcs); }
                        try
                        {
                            var timeoutTask = Task.Delay(30000);
                            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                            
                            if (completedTask == tcs.Task)
                            {
                                // Clipboard changed! Return the event data
                                string payload = await tcs.Task;
                                byte[] data = Encoding.UTF8.GetBytes(payload);
                                res.StatusCode = 200;
                                res.ContentType = "application/json";
                                res.ContentLength64 = data.Length;
                                res.OutputStream.Write(data, 0, data.Length);
                            }
                            else
                            {
                                // Timeout — return 204 No Content (no new events)
                                res.StatusCode = 204;
                            }
                        }
                        catch { res.StatusCode = 500; }
                        finally
                        {
                            lock (_longPollLock) { _longPollWaiters.Remove(tcs); }
                            try { res.Close(); } catch { }
                        }
                    }
                    else if (path == "/api/sync_text" && req.HttpMethod == "POST")
                    {
                        await HandleTextUpload(req, res);
                    }
                    else if (path == "/api/sync_file" && req.HttpMethod == "POST")
                    {
                        await HandleFileUpload(req, res);
                    }
                    else if (path == "/api/archive_upload" && req.HttpMethod == "POST")
                    {
                        await HandleArchiveUpload(req, res);
                    }
                    else if (path == "/api/upload_chunk" && req.HttpMethod == "POST")
                    {
                        await HandleChunkUpload(req, res);
                    }
                    else if (path == "/api/upload_finalize" && req.HttpMethod == "POST")
                    {
                        await HandleChunkFinalize(req, res);
                    }
                    else if (path == "/api/relay_upload" && req.HttpMethod == "POST")
                    {
                        await HandleRelayUpload(req, res);
                    }
                    else if (path == "/api/convert_to_pdf" && req.HttpMethod == "POST")
                    {
                        await HandleConvertToPdf(req, res);
                    }
                    else if (path == "/logs" && req.HttpMethod == "GET")
                    {
                        // Serve the live log dashboard page
                        ServeLogDashboard(res);
                    }
                    else if (path == "/api/logs" && req.HttpMethod == "GET")
                    {
                        // Return combined PC + mobile logs as JSON
                        ServeLogsJson(res);
                    }
                    else if (path == "/api/logs" && req.HttpMethod == "POST")
                    {
                        // Accept logs from mobile devices
                        await HandleRemoteLogPost(req, res);
                    }
                    else
                    {
                        res.StatusCode = 404;
                        res.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SERVER REQUEST FAULT", ex.Message);
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }

        /// <summary>
        /// Holds a WebSocket connection with a peer for instant liveness detection.
        /// Sends ping every 30s, receives pong. If the peer dies or tunnel drops,
        /// the WebSocket closes instantly — no 50s heartbeat delay.
        /// </summary>
        private async Task HandlePeerWebSocket(WebSocket ws, string peerDeviceId)
        {
            var buf = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    // Wait for incoming messages (peer sends pings, we just read them)
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.LogAction("WS", $"Peer {peerDeviceId} closed WebSocket gracefully");
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    }
                    // If we receive a text "ping", reply "pong"
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                        if (msg == "ping")
                        {
                            byte[] pong = Encoding.UTF8.GetBytes("pong");
                            await ws.SendAsync(new ArraySegment<byte>(pong), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                }
            }
            catch (WebSocketException)
            {
                Logger.LogAction("WS", $"Peer {peerDeviceId} WebSocket dropped (connection lost)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("WS", $"Peer {peerDeviceId} WebSocket error: {ex.Message}");
            }
            finally
            {
                ws.Dispose();
            }
        }

        private void ServeHtml(HttpListenerResponse res)
        {
            try
            {
                string path = Path.Combine(AdvanceClip.Classes.RuntimeHost.ExecutionDir, "Resources", "WebClient", "index.html");
                Logger.LogAction("HTML", $"Serving from: {path} (exists: {File.Exists(path)})");
                if (File.Exists(path))
                {
                    byte[] buffer = File.ReadAllBytes(path);
                    res.ContentType = "text/html; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.OutputStream.Write(buffer, 0, buffer.Length);
                    Logger.LogAction("HTML", $"Served {buffer.Length} bytes OK");
                }
                else
                {
                    byte[] err = Encoding.UTF8.GetBytes("UI payload not found.");
                    res.StatusCode = 404;
                    res.OutputStream.Write(err, 0, err.Length);
                }
            }
            catch (Exception ex) { Logger.LogAction("HTML ERROR", ex.Message); try { res.StatusCode = 500; } catch { } }
            finally { try { res.Close(); } catch { } }
        }

        // ═══ RESPONSE CACHE: Avoid re-serializing on rapid polls ═══
        private byte[]? _cachedSyncJson = null;
        private long _cachedSyncTimestamp = 0;
        private int _cachedItemCount = 0;
        private const int SYNC_CACHE_TTL_MS = 500; // Cache for 500ms — fast invalidation for real-time sync

        private void ServeClipboardData(HttpListenerResponse res)
        {
            try
            {
                long now = Environment.TickCount64;
                int currentCount = 0;
                System.Windows.Application.Current.Dispatcher.Invoke(() => { currentCount = _viewModel.DroppedItems.Count; });

                // Use cached response if still fresh and item count unchanged
                if (_cachedSyncJson != null && (now - _cachedSyncTimestamp) < SYNC_CACHE_TTL_MS && currentCount == _cachedItemCount)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = _cachedSyncJson.Length;
                    try { res.OutputStream.Write(_cachedSyncJson, 0, _cachedSyncJson.Length); } catch { }
                    res.Close();
                    return;
                }

                // Rebuild cache
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    string deviceId = SettingsManager.Current.DeviceId ?? "PC";
                    var payload = _viewModel.DroppedItems
                        .Where(x => x.Extension != "MOBILE") // Don't echo Mobile items back
                        .Take(15).Select(x => new
                    {
                        id = x.GetHashCode().ToString() + "_" + x.DateCopied.Ticks.ToString(),
                        EventId = $"{deviceId}_{((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds()}_{x.GetHashCode():X4}",
                        Title = string.IsNullOrEmpty(x.FileName) ? (x.RawContent?.Length > 20 ? x.RawContent.Substring(0, 20) + "..." : x.RawContent) : x.FileName,
                        Type = x.ItemType.ToString(),
                        PreviewUrl = (x.ItemType == ClipboardItemType.Image || x.ItemType == ClipboardItemType.QRCode) ? (!string.IsNullOrEmpty(x.FilePath) ? $"/download?path={Uri.EscapeDataString(x.FilePath)}" : (x.RawContent ?? "")) : "",
                        DownloadUrl = !string.IsNullOrEmpty(x.FilePath) ? $"/download?path={Uri.EscapeDataString(x.FilePath)}" : (x.RawContent ?? ""),
                        Raw = x.RawContent ?? x.FileName ?? "",
                        FileName = x.FileName ?? "",
                        Time = x.DateCopied.ToString("HH:mm:ss"),
                        Timestamp = ((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds(),
                        SourceDeviceName = x.Extension == "MOBILE" ? "Mobile" : (SettingsManager.Current.DeviceName ?? Environment.MachineName),
                        SourceDeviceType = x.Extension == "MOBILE" ? "Mobile" : "PC"
                    })
                    // Sort by freshness — bumped items get DateCopied = Now, so they appear first
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();

                    // Filter out encrypted/Base64 blobs that echo back from mobile
                    payload.RemoveAll(x => {
                        var raw = x.Raw ?? x.Title ?? "";
                        return raw.Length > 30 && !raw.Contains(' ') && System.Text.RegularExpressions.Regex.IsMatch(raw, @"^[A-Za-z0-9+/=\r\n]+$");
                    });

                    string json = JsonSerializer.Serialize(payload);
                    _cachedSyncJson = Encoding.UTF8.GetBytes(json);
                    _cachedSyncTimestamp = now;
                    _cachedItemCount = currentCount;
                });

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = _cachedSyncJson!.Length;
                try { res.OutputStream.Write(_cachedSyncJson, 0, _cachedSyncJson.Length); } catch { }
                res.Close();
            }
            catch { try { res.StatusCode = 500; } catch { } try { res.Close(); } catch { } }
        }

        private async Task HandleTextUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // SPEED: Read body first, then respond 200 IMMEDIATELY so the sender isn't blocked
            string text;
            string sourceDevice;
            string itemType = null;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync();
                sourceDevice = req.Headers["X-Source-Device"] ?? "Mobile";
            }

            // v5 PeerManager sends JSON: {"type":"Url","title":"...","data":"actual text","sourceDeviceId":"..."}
            // Parse it to extract the actual content. Fall back to raw body for plain text senders.
            if (text.TrimStart().StartsWith("{"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("data", out var dataProp))
                    {
                        text = dataProp.GetString() ?? text;
                    }
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        itemType = typeProp.GetString();
                    }
                    if (root.TryGetProperty("sourceDeviceName", out var srcProp))
                    {
                        sourceDevice = srcProp.GetString() ?? sourceDevice;
                    }
                }
                catch
                {
                    // Not valid JSON — treat entire body as plain text (legacy sender)
                }
            }

            // Respond instantly — don't make Android wait for UI processing
            res.StatusCode = 200;
            res.Close();

            // Invalidate sync cache so next poll picks up the new item
            _cachedSyncJson = null;

            // Process asynchronously on UI thread (fire-and-forget)
            string capturedText = text;
            string capturedSource = sourceDevice;
            string capturedType = itemType;
            var capturedTransport = DetectTransport(req);
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                // Determine item type from payload or text content
                ClipboardItemType clipType;
                if (!string.IsNullOrEmpty(capturedType) && Enum.TryParse<ClipboardItemType>(capturedType, true, out var parsed))
                    clipType = parsed;
                else
                    clipType = capturedText.StartsWith("http") ? ClipboardItemType.Url : ClipboardItemType.Text;

                var clip = new ClipboardItem
                {
                    RawContent = capturedText,
                    FileName = capturedText.Length > 40 ? capturedText.Substring(0, 40) + "..." : capturedText,
                    Extension = capturedTransport.label,
                    ItemType = clipType,
                    SourceDeviceName = capturedSource,
                    SourceDeviceType = capturedSource.Contains("PC") || capturedSource.Contains("LAPTOP") || capturedSource.Contains("DESKTOP") ? "PC" : "Mobile",
                    TransferMethod = capturedTransport.transport
                };
                clip.EvaluateSmartActions();
                _viewModel.DroppedItems.Insert(0, clip);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                
                // ECHO PREVENTION: Mark this text as cloud-sourced so the clipboard monitor
                // doesn't re-push it to Firebase when we set the Windows clipboard below.
                string txtFp = $"TXT::{capturedText.Substring(0, Math.Min(200, capturedText.Length))}";
                _viewModel.MarkAsCloudSourced(txtFp);
                
                // Suppress clipboard monitor during our write
                try 
                { 
                    MainWindow.SetWritingClipboard(true);
                    System.Windows.Clipboard.SetText(capturedText);
                    await System.Threading.Tasks.Task.Delay(500);
                } 
                catch { }
                finally { MainWindow.SetWritingClipboard(false); }
                
                AdvanceClip.Windows.ToastWindow.ShowToast($"Text from {capturedSource} via {capturedTransport.transport}! 📱");
            });
        }

        private async Task HandleFileUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try 
            {
                string sourceDevice = req.Headers["X-Source-Device"];
                if (string.IsNullOrEmpty(sourceDevice)) sourceDevice = req.QueryString["sourceDevice"];
                if (!string.IsNullOrEmpty(sourceDevice))
                {
                    try { sourceDevice = Uri.UnescapeDataString(sourceDevice); } catch { }
                }
                else
                {
                    sourceDevice = "Mobile";
                }
                var fileTransport = DetectTransport(req);

                string dateString = DateTime.Now.ToString("dd-MM-yyyy");
                string uploadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Clipboard", sourceDevice, dateString);
                Directory.CreateDirectory(uploadDir);

                string encodedName = req.Headers["X-File-Name"] ?? req.QueryString["name"];
                string mappedType = req.Headers["X-File-Type"] ?? req.QueryString["type"] ?? "Document";
                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Receiving {rawName} from {sourceDevice}... 📥");
                });

                int counter = 1;
                string finalPath = Path.Combine(uploadDir, rawName);
                while(File.Exists(finalPath))
                {
                    finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                // Parse multipart/form-data or raw body
                string contentType = req.ContentType ?? "";
                if (contentType.Contains("multipart/form-data") && contentType.Contains("boundary="))
                {
                    // Extract boundary string
                    string boundary = contentType.Substring(contentType.IndexOf("boundary=") + "boundary=".Length).Trim();
                    if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                        boundary = boundary.Substring(1, boundary.Length - 2);
                    
                    byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
                    
                    // Read entire body into memory
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    byte[] body = ms.ToArray();
                    
                    // Find the file content: skip past the first boundary + headers (ends with \r\n\r\n)
                    int headerEnd = -1;
                    for (int i = 0; i < body.Length - 3; i++)
                    {
                        if (body[i] == 0x0D && body[i + 1] == 0x0A && body[i + 2] == 0x0D && body[i + 3] == 0x0A)
                        {
                            // Found \r\n\r\n — content starts after this
                            headerEnd = i + 4;
                            break;
                        }
                    }
                    
                    // ── Extract filename from multipart Content-Disposition if X-File-Name was missing ──
                    if (rawName == "uploaded_file.dat" && headerEnd > 0)
                    {
                        string partHeaders = Encoding.UTF8.GetString(body, 0, headerEnd);
                        // Look for: filename="actual_name.png"  or filename*=UTF-8''encoded_name
                        var fnMatch = System.Text.RegularExpressions.Regex.Match(partHeaders,
                            @"filename=""?([^""\r\n]+)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (fnMatch.Success)
                        {
                            string extracted = fnMatch.Groups[1].Value.Trim();
                            try { extracted = Uri.UnescapeDataString(extracted); } catch { }
                            if (!string.IsNullOrWhiteSpace(extracted) && extracted != "file")
                            {
                                rawName = extracted;
                                // Recalculate path with correct filename
                                counter = 1;
                                finalPath = Path.Combine(uploadDir, rawName);
                                while (File.Exists(finalPath))
                                {
                                    finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                                }
                            }
                        }
                    }

                    if (headerEnd > 0)
                    {
                        // Find trailing boundary
                        int contentEnd = body.Length;
                        byte[] endMarker = Encoding.UTF8.GetBytes("\r\n--" + boundary);
                        for (int i = headerEnd; i < body.Length - endMarker.Length; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < endMarker.Length; j++)
                            {
                                if (body[i + j] != endMarker[j]) { match = false; break; }
                            }
                            if (match) { contentEnd = i; break; }
                        }
                        
                        using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        fs.Write(body, headerEnd, contentEnd - headerEnd);
                    }
                    else
                    {
                        // Fallback: save raw
                        using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        fs.Write(body, 0, body.Length);
                    }
                }
                else
                {
                    // Raw binary body — save directly
                    using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await req.InputStream.CopyToAsync(fs);
                }

                string originalDateStr = req.Headers["X-Original-Date"];
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    try
                    {
                        var originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                        File.SetCreationTime(finalPath, originalDate);
                        File.SetLastWriteTime(finalPath, originalDate);
                    } catch { }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dataObj = new System.Windows.DataObject();
                    var dropList = new System.Collections.Specialized.StringCollection { finalPath };
                    dataObj.SetFileDropList(dropList);
                    // forceClipboardSync=false — DON'T write to OS clipboard, prevents echo loop
                    // (clipboard write → WM_CLIPBOARDUPDATE → syncs back to sender = infinite loop)
                    // skipFirebaseSync=true — file came FROM a peer device, don't echo it back
                    _viewModel.HandleDrop(dataObj, false, skipFirebaseSync: true);
                    
                    // Tag the newly created item with transport + source device info
                    if (_viewModel.DroppedItems.Count > 0)
                    {
                        var newest = _viewModel.DroppedItems[0];
                        newest.SourceDeviceName = sourceDevice;
                        newest.SourceDeviceType = sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile";
                        newest.TransferMethod = fileTransport.transport;
                    }
                    
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Saved: {Path.GetFileName(finalPath)} via {fileTransport.transport} ✅");
                });

                res.StatusCode = 200;
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("SERVER ERR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private DateTime _lastArchiveToastTime = DateTime.MinValue;
        // Track files per batch for auto-clipboard (copy to clipboard if ≤2 files in batch)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _batchFiles = new();

        private async Task HandleArchiveUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string batchName = req.Headers["X-Batch-Name"];
                if (!string.IsNullOrEmpty(batchName))
                {
                    try { batchName = Uri.UnescapeDataString(batchName); } catch { }
                }
                
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "FlyShelf_Mobile_Transfer";
                string archiveSource = req.Headers["X-Source-Device"] ?? req.QueryString["sourceDevice"] ?? "Mobile";
                try { archiveSource = Uri.UnescapeDataString(archiveSource); } catch { }
                var archiveTransport = DetectTransport(req);

                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                string originalDateStr = req.Headers["X-Original-Date"];
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }

                string encodedName = req.Headers["X-File-Name"];
                string rawName = "uploaded_media.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                }

                if ((DateTime.Now - _lastArchiveToastTime).TotalSeconds > 2)
                {
                    _lastArchiveToastTime = DateTime.Now;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Extracting batch data... 📦");
                    });
                }

                int counter = 1;
                string finalPath = Path.Combine(archiveDir, rawName);
                while(File.Exists(finalPath))
                {
                    finalPath = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                if (originalDate.HasValue)
                {
                    try
                    {
                        File.SetCreationTime(finalPath, originalDate.Value);
                        File.SetLastWriteTime(finalPath, originalDate.Value);
                    } catch { }
                }

                res.StatusCode = 200;

                // Track file in batch for auto-clipboard
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }
                
                // Auto-copy to Windows clipboard if ≤2 files in this batch
                if (batchList.Count <= 2)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var fileList = new System.Collections.Specialized.StringCollection();
                            lock (batchList) { foreach (var f in batchList) fileList.Add(f); }
                            System.Windows.Clipboard.SetFileDropList(fileList);
                            AdvanceClip.Windows.ToastWindow.ShowToast($"📋 {rawName} copied to clipboard");
                            
                            // Insert proper file entry into FlyShelf (clickable → opens in default app)
                            var clip = new ClipboardItem
                            {
                                RawContent = finalPath,
                                FileName = rawName,
                                FilePath = finalPath,
                                Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(),
                                ItemType = ClipboardItemType.File,
                                SourceDeviceName = archiveSource,
                                SourceDeviceType = archiveSource.Contains("PC") || archiveSource.Contains("LAPTOP") || archiveSource.Contains("DESKTOP") ? "PC" : "Mobile",
                                TransferMethod = archiveTransport.transport
                            };
                            clip.EvaluateSmartActions();
                            _viewModel.DroppedItems.Insert(0, clip);
                            _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                        }
                        catch { }
                    });
                }
                
                // Clean up old batches after 5 minutes
                _ = Task.Run(async () => { await Task.Delay(300_000); _batchFiles.TryRemove(batchName, out _); });
            }
            catch (Exception ex)
            {
                Logger.LogAction("ARCHIVE UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        // ─── Relay Upload: Android uploads file → PC saves + pushes Cloudflare URL to Firebase ───
        private async Task HandleRelayUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string senderDevice = req.Headers["X-Source-Device"] ?? "Android";
                string originalDateStr = req.Headers["X-Original-Date"];

                string rawName = "relayed_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }

                // Save to Downloads/Synced/Relay_{sender}/
                string relayDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    "Downloads", "FlyShelf", "Relay", senderDevice.Replace(" ", "_"));
                Directory.CreateDirectory(relayDir);

                int counter = 1;
                string finalPath = Path.Combine(relayDir, rawName);
                while (File.Exists(finalPath))
                {
                    finalPath = Path.Combine(relayDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                    try { File.SetCreationTime(finalPath, dt); File.SetLastWriteTime(finalPath, dt); } catch { }
                }

                // Build Cloudflare download URL
                string globalUrl = _cfDaemon.GlobalUrl;
                string downloadUrl = "";
                if (!string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com"))
                {
                    downloadUrl = $"{globalUrl}/download?path={Uri.EscapeDataString(finalPath)}";
                }

                // Push to Firebase so all devices see it
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    var fileInfo = new FileInfo(finalPath);
                    string ext = Path.GetExtension(rawName).ToLower();
                    string fileType = ext switch
                    {
                        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "Video",
                        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "Audio",
                        ".pdf" => "Pdf",
                        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
                        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "ImageLink",
                        ".doc" or ".docx" or ".txt" or ".rtf" => "Document",
                        ".ppt" or ".pptx" => "Presentation",
                        ".apk" => "Archive",
                        _ => "File"
                    };

                    string deviceName = SettingsManager.Current?.DeviceName ?? Environment.MachineName;
                    var payload = new
                    {
                        Title = rawName,
                        Type = fileType,
                        Raw = downloadUrl,
                        PreviewUrl = downloadUrl,
                        DownloadUrl = downloadUrl,
                        FileName = rawName,
                        FileSize = fileInfo.Length,
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SourceDeviceName = senderDevice,
                        SourceDeviceType = "Mobile",
                        RelayedVia = deviceName
                    };

                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    var fbRes = await _httpClient.PostAsync(
                        await FirebaseAuthManager.AuthenticateUrl("https://advance-sync-default-rtdb.firebaseio.com/clipboard.json"), content);

                    if (fbRes.IsSuccessStatusCode)
                    {
                        string fbBody = await fbRes.Content.ReadAsStringAsync();
                        try
                        {
                            var fbObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(fbBody);
                            if (fbObj != null && fbObj.TryGetValue("name", out string? entryKey) && !string.IsNullOrEmpty(entryKey))
                            {
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(24 * 60 * 60_000);
                                    try { await _httpClient.DeleteAsync(await FirebaseAuthManager.AuthenticateUrl($"https://advance-sync-default-rtdb.firebaseio.com/clipboard/{entryKey}.json")); } catch { }
                                });
                            }
                        }
                        catch { }
                    }
                }

                string sizeStr = new FileInfo(finalPath).Length > 1_073_741_824 
                    ? $"{new FileInfo(finalPath).Length / 1_073_741_824.0:F1} GB" 
                    : $"{new FileInfo(finalPath).Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"📡 Relayed {rawName} ({sizeStr}) from {senderDevice}");
                });

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"status\":\"ok\",\"downloadUrl\":\"{downloadUrl}\",\"size\":\"{sizeStr}\"}}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("RELAY UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        // ─── Chunked Upload System (bypasses Cloudflare 100MB limit) ───
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _chunkSessions = new();

        private async Task HandleChunkUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string sessionId = req.Headers["X-Upload-Session"] ?? "";
                string chunkIndexStr = req.Headers["X-Chunk-Index"] ?? "0";
                
                if (string.IsNullOrEmpty(sessionId))
                {
                    res.StatusCode = 400;
                    res.Close();
                    return;
                }

                string chunkDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Chunks", sessionId);
                Directory.CreateDirectory(chunkDir);
                _chunkSessions[sessionId] = chunkDir;

                string chunkPath = Path.Combine(chunkDir, $"chunk_{chunkIndexStr.PadLeft(6, '0')}");
                using (var fs = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CHUNK UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private async Task HandleChunkFinalize(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string sessionId = req.Headers["X-Upload-Session"] ?? "";
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string batchName = req.Headers["X-Batch-Name"] ?? "";
                string originalDateStr = req.Headers["X-Original-Date"];
                string totalChunksStr = req.Headers["X-Total-Chunks"] ?? "0";

                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                if (!string.IsNullOrEmpty(batchName))
                    try { batchName = Uri.UnescapeDataString(batchName); } catch { }
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "AdvanceClip_Chunked_Transfer";
                string sourceDevice = req.Headers["X-Source-Device"] ?? "Remote";
                try { sourceDevice = Uri.UnescapeDataString(sourceDevice); } catch { }
                var chunkTransport = DetectTransport(req);

                if (!_chunkSessions.TryGetValue(sessionId, out string chunkDir) || !Directory.Exists(chunkDir))
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                // Merge all chunks in order
                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                int counter = 1;
                string finalPath = Path.Combine(archiveDir, rawName);
                while (File.Exists(finalPath))
                {
                    finalPath = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                var chunkFiles = Directory.GetFiles(chunkDir, "chunk_*").OrderBy(f => f).ToArray();

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Assembling {rawName} ({chunkFiles.Length} chunks)... 📦");
                });

                using (var outputFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    foreach (var chunkFile in chunkFiles)
                    {
                        using (var chunkFs = new FileStream(chunkFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                        {
                            await chunkFs.CopyToAsync(outputFs);
                        }
                    }
                }

                // Set original timestamps
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }
                if (originalDate.HasValue)
                {
                    try { File.SetCreationTime(finalPath, originalDate.Value); File.SetLastWriteTime(finalPath, originalDate.Value); } catch { }
                }

                // Cleanup temp chunks
                try { Directory.Delete(chunkDir, true); } catch { }
                _chunkSessions.TryRemove(sessionId, out _);

                var fileInfo = new FileInfo(finalPath);
                string sizeStr = fileInfo.Length > 1_073_741_824 ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB" : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"✅ {rawName} ({sizeStr}) received!");
                    // Auto-copy to clipboard + insert into FlyShelf
                    try
                    {
                        var fileList = new System.Collections.Specialized.StringCollection { finalPath };
                        System.Windows.Clipboard.SetFileDropList(fileList);
                        
                        var clip = new ClipboardItem
                        {
                            RawContent = finalPath,
                            FileName = rawName,
                            FilePath = finalPath,
                            Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(),
                            ItemType = ClipboardItemType.File,
                            SourceDeviceName = sourceDevice,
                            SourceDeviceType = sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile",
                            TransferMethod = chunkTransport.transport
                        };
                        clip.EvaluateSmartActions();
                        _viewModel.DroppedItems.Insert(0, clip);
                        _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    }
                    catch { }
                });

                // Also track in batch for consistency 
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes($"{{\"status\":\"ok\",\"size\":\"{sizeStr}\"}}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CHUNK FINALIZE ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private async Task HandleConvertToPdf(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string fileName = req.QueryString["name"] ?? $"document_{DateTime.Now.Ticks}.docx";
                string convertDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Conversions");
                Directory.CreateDirectory(convertDir);

                string inputPath = Path.Combine(convertDir, fileName);
                using (var fs = new FileStream(inputPath, FileMode.Create, FileAccess.Write))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                string pdfName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
                string pdfPath = Path.Combine(convertDir, pdfName);

                // Try LibreOffice conversion first (most reliable cross-platform)
                bool converted = false;
                string[] libreOfficePaths = new[] {
                    @"C:\Program Files\LibreOffice\program\soffice.exe",
                    @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
                };

                string sofficePath = libreOfficePaths.FirstOrDefault(p => File.Exists(p));
                if (sofficePath != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = sofficePath,
                        Arguments = $"--headless --convert-to pdf --outdir \"{convertDir}\" \"{inputPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            await proc.WaitForExitAsync();
                            converted = proc.ExitCode == 0 && File.Exists(pdfPath);
                        }
                    }
                }

                // Fallback: Try Microsoft Word COM automation
                if (!converted)
                {
                    try
                    {
                        Type wordType = Type.GetTypeFromProgID("Word.Application");
                        if (wordType != null)
                        {
                            dynamic word = Activator.CreateInstance(wordType);
                            word.Visible = false;
                            dynamic doc = word.Documents.Open(inputPath);
                            doc.SaveAs2(pdfPath, 17); // 17 = wdFormatPDF
                            doc.Close(false);
                            word.Quit();
                            converted = File.Exists(pdfPath);
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(word);
                        }
                    }
                    catch { }
                }

                if (converted && File.Exists(pdfPath))
                {
                    // Also add the PDF to the clipboard shelf
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        var dropList = new System.Collections.Specialized.StringCollection { pdfPath };
                        dataObj.SetFileDropList(dropList);
                        _viewModel.HandleDrop(dataObj, true);
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Converted: {pdfName} ✅");
                    });

                    string downloadUrl = $"/download?path={Uri.EscapeDataString(pdfPath)}";
                    string json = JsonSerializer.Serialize(new { success = true, downloadUrl, fileName = pdfName });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 200;
                    try { res.OutputStream.Write(buffer, 0, buffer.Length); } catch { }
                }
                else
                {
                    string json = JsonSerializer.Serialize(new { success = false, error = "No converter found. Install LibreOffice or Microsoft Word." });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 500;
                    try { res.OutputStream.Write(buffer, 0, buffer.Length); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("CONVERT PDF ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }
#pragma warning disable CA2022
        private async Task ProcessStreamingMultipartFile(string tempFilePath, string boundary, string destinationDir, DateTime? applyDate = null)
        {
            try
            {
                using (var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    int bufferSize = Math.Min(1024 * 1024, (int)fs.Length);
                    byte[] headBuffer = new byte[bufferSize];
                    int readLen = await fs.ReadAsync(headBuffer, 0, bufferSize);
                    
                    ReadOnlySpan<byte> headSpan = new ReadOnlySpan<byte>(headBuffer, 0, readLen);
                    
                    byte[] filenameSeq = Encoding.ASCII.GetBytes("filename=\"");
                    int filenameIdx = headSpan.IndexOf(filenameSeq);

                    if (filenameIdx != -1)
                    {
                        byte[] headerEndSeq = Encoding.ASCII.GetBytes("\r\n\r\n");
                        int headerEndRel = headSpan.Slice(filenameIdx).IndexOf(headerEndSeq);

                        if (headerEndRel != -1)
                        {
                            long physicalDataStart = filenameIdx + headerEndRel + 4;
                            
                            string headerStr = Encoding.UTF8.GetString(headBuffer, 0, (int)physicalDataStart);
                            int nameIndexStart = headerStr.IndexOf("filename=\"") + 10;
                            int nameEnd = headerStr.IndexOf("\"", nameIndexStart);
                            string fileName = headerStr.Substring(nameIndexStart, nameEnd - nameIndexStart);
                            if (string.IsNullOrWhiteSpace(fileName)) fileName = "uploaded_file.dat";
                            fileName = Path.GetFileName(fileName);
                            
                            int counter = 1;
                            string finalPath = Path.Combine(destinationDir, fileName);
                            while(File.Exists(finalPath))
                            {
                                finalPath = Path.Combine(destinationDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{counter++}{Path.GetExtension(fileName)}");
                            }

                            fs.Seek(0, SeekOrigin.End);
                            long totalLen = fs.Length;
                            int tailSearchSize = Math.Min(8192, (int)totalLen);
                            fs.Seek(totalLen - tailSearchSize, SeekOrigin.Begin);
                            
                            byte[] tailBuffer = new byte[tailSearchSize];
                            int tailReadLen = await fs.ReadAsync(tailBuffer, 0, tailSearchSize);
                            
                            ReadOnlySpan<byte> tailSpan = new ReadOnlySpan<byte>(tailBuffer, 0, tailReadLen);
                            byte[] footerSeq = Encoding.ASCII.GetBytes("\r\n--" + boundary);
                            int footerIdxRel = tailSpan.LastIndexOf(footerSeq);
                            
                            long physicalDataEnd = totalLen;
                            if (footerIdxRel != -1)
                            {
                                physicalDataEnd = (totalLen - tailSearchSize) + footerIdxRel;
                            }

                            fs.Seek(physicalDataStart, SeekOrigin.Begin);
                            long bytesRemaining = physicalDataEnd - physicalDataStart;

                            using (var outFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] transferBuf = new byte[81920];
                                while (bytesRemaining > 0)
                                {
                                    int toRead = (int)Math.Min(transferBuf.Length, bytesRemaining);
                                    int r = await fs.ReadAsync(transferBuf, 0, toRead);
                                    if (r == 0) break;
                                    await outFs.WriteAsync(transferBuf, 0, r);
                                    bytesRemaining -= r;
                                }
                            }

                            if (applyDate.HasValue)
                            {
                                try
                                {
                                    File.SetCreationTime(finalPath, applyDate.Value);
                                    File.SetLastWriteTime(finalPath, applyDate.Value);
                                } catch { }
                            }

                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                var dataObj = new System.Windows.DataObject();
                                var dropList = new System.Collections.Specialized.StringCollection { finalPath };
                                dataObj.SetFileDropList(dropList);
                                _viewModel.HandleDrop(dataObj, true);
                                AdvanceClip.Windows.ToastWindow.ShowToast($"File extracted: {Path.GetFileName(finalPath)} 📱");
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("FILE PARSER", ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            }
        }
#pragma warning restore CA2022

        // Helper: detect if a remote IP is on the same LAN (private range)
        private static bool IsLanAddress(string remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp)) return false;
            // 127.x, 10.x, 192.168.x, 172.16-31.x = local/LAN
            if (remoteIp.StartsWith("127.") || remoteIp.StartsWith("10.") || remoteIp.StartsWith("192.168.")) return true;
            if (remoteIp.StartsWith("172."))
            {
                if (int.TryParse(remoteIp.Split('.').ElementAtOrDefault(1), out int b) && b >= 16 && b <= 31) return true;
            }
            return false;
        }

        private async Task ServeFileDownload(HttpListenerRequest req, HttpListenerResponse res)
        {
            string path = req.QueryString["path"];
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try { res.StatusCode = 404; res.Close(); } catch { }
                return;
            }

            // SECURITY: Path sandbox — reject files outside allowed directories
            if (!IsPathAllowed(path))
            {
                Logger.LogAction("SECURITY", $"🚫 BLOCKED path traversal attempt: {path} from {req.RemoteEndPoint}");
                try
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"403 — Access denied: path not in allowed directory\"}");
                    res.StatusCode = 403;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                    res.Close();
                }
                catch { }
                return;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                long fileSize = fileInfo.Length;
                string ext = Path.GetExtension(path).ToLower();
                string safeFileName = Path.GetFileName(path);
                string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";

                Logger.LogAction("DOWNLOAD", $"Starting: {safeFileName} ({fileSize / 1024}KB) to {remoteIp}");

                // Content-Type
                res.ContentType = ext switch
                {
                    ".png"  => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif"  => "image/gif",
                    ".webp" => "image/webp",
                    ".pdf"  => "application/pdf",
                    ".apk"  => "application/vnd.android.package-archive",
                    ".mp4"  => "video/mp4",
                    ".mkv"  => "video/x-matroska",
                    ".zip"  => "application/zip",
                    ".rar"  => "application/x-rar-compressed",
                    _ => "application/octet-stream"
                };

                bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
                res.AddHeader("Content-Disposition", isImage
                    ? $"inline; filename=\"{safeFileName}\""
                    : $"attachment; filename=\"{safeFileName}\"");
                res.AddHeader("Cache-Control", "no-store");
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Accept-Ranges", "bytes");

                res.StatusCode = 200;
                res.ContentLength64 = fileSize;
                res.SendChunked = false;

                // Fast path: small files (≤5MB) — single read + write for minimal latency
                if (fileSize <= 5 * 1024 * 1024)
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(path);
                    await res.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                    await res.OutputStream.FlushAsync();
                    Logger.LogAction("DOWNLOAD", $"Completed (fast): {safeFileName} ({fileSize / 1024}KB)");
                }
                else
                {
                    // Large files: stream with 1MB buffer for maximum throughput
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1048576, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    byte[] buffer = new byte[1048576]; // 1MB buffer
                    int bytesRead;
                    while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await res.OutputStream.WriteAsync(buffer, 0, bytesRead);
                    }
                    await res.OutputStream.FlushAsync();
                    Logger.LogAction("DOWNLOAD", $"Completed (stream): {safeFileName} ({fileSize / 1024}KB)");
                }
            }
            catch (HttpListenerException ex) { Logger.LogAction("DOWNLOAD", $"Client disconnected: {ex.Message}"); }
            catch (IOException ex) { Logger.LogAction("DOWNLOAD", $"Pipe broken: {ex.Message}"); }
            catch (Exception ex) { Logger.LogAction("DOWNLOAD ERROR", $"{ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        // ═══ QR Code Pairing Handler ═══
        private async Task HandlePairRequest(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                var pairData = JsonSerializer.Deserialize<JsonElement>(body);
                string pairingKey = pairData.TryGetProperty("key", out var k) ? k.GetString() : "";
                string deviceId = pairData.TryGetProperty("deviceId", out var di) ? di.GetString() : "";
                string deviceName = pairData.TryGetProperty("deviceName", out var dn) ? dn.GetString() : "Unknown";
                string deviceType = pairData.TryGetProperty("deviceType", out var dt) ? dt.GetString() : "Mobile";
                string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "unknown";

                if (string.IsNullOrEmpty(deviceId))
                    deviceId = $"{deviceName}_{remoteIp}";

                bool success = DevicePairingManager.TryPairDevice(pairingKey, deviceId, deviceName, deviceType, remoteIp);

                if (success)
                {
                    var response = new
                    {
                        status = "paired",
                        deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                        deviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName,
                        localUrl = DisplayUrl,
                        globalUrl = GlobalUrl ?? "",
                        pin = SettingsManager.Current.WebClientPinToken
                    };
                    byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                    res.StatusCode = 200;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(json, 0, json.Length);

                    // Show toast on PC
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        AdvanceClip.Windows.ToastWindow.ShowToast($"📱 {deviceName} paired successfully!");
                    });
                }
                else
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                    res.StatusCode = 403;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR ERROR", ex.Message);
                byte[] err = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                try { res.OutputStream.Write(err, 0, err.Length); } catch { }
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        // ═══ REMOTE DEVICE LOG STORAGE ═══
        private static readonly ConcurrentQueue<string> _remoteDeviceLogs = new();
        private const int MAX_REMOTE_LOGS = 500;

        private async Task HandleRemoteLogPost(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                string deviceName = req.Headers["X-Device-Name"] ?? req.Headers["X-Source-Device"] ?? "Unknown";
                string deviceTag = deviceName.Replace(" ", "_").Replace("/", "_");
                var collectedLines = new List<string>();

                if (!string.IsNullOrWhiteSpace(body))
                {
                    // Parse as JSON array of log strings, or plain text lines
                    try
                    {
                        var logs = JsonSerializer.Deserialize<string[]>(body);
                        if (logs != null)
                        {
                            foreach (var log in logs)
                            {
                                string entry = $"[📱 {deviceName}] {log}";
                                _remoteDeviceLogs.Enqueue(entry);
                                collectedLines.Add(entry);
                                while (_remoteDeviceLogs.Count > MAX_REMOTE_LOGS) _remoteDeviceLogs.TryDequeue(out _);
                            }
                            Logger.LogAction("NETWORK", $"Received {logs.Length} log entries from {deviceName}");
                        }
                    }
                    catch
                    {
                        // Plain text — split by newlines
                        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string entry = $"[📱 {deviceName}] {line.TrimEnd('\r')}";
                            _remoteDeviceLogs.Enqueue(entry);
                            collectedLines.Add(entry);
                            while (_remoteDeviceLogs.Count > MAX_REMOTE_LOGS) _remoteDeviceLogs.TryDequeue(out _);
                        }
                    }
                }

                // ── Save to a timestamped log file ──
                if (collectedLines.Count > 0)
                {
                    try
                    {
                        string logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
                        Directory.CreateDirectory(logsDir);
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        string fileName = $"diagnostic_{deviceTag}_{timestamp}.log";
                        string filePath = Path.Combine(logsDir, fileName);

                        var sb = new StringBuilder();
                        sb.AppendLine($"═══════════════════════════════════════════════════════════════");
                        sb.AppendLine($"  FlyShelf Diagnostic Log — {deviceName}");
                        sb.AppendLine($"  Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        sb.AppendLine($"  PC Host:  {Environment.MachineName}");
                        sb.AppendLine($"  Entries:  {collectedLines.Count}");
                        sb.AppendLine($"═══════════════════════════════════════════════════════════════");
                        sb.AppendLine();
                        foreach (var line in collectedLines)
                            sb.AppendLine(line);

                        await File.WriteAllTextAsync(filePath, sb.ToString());
                        Logger.LogAction("NETWORK", $"Saved {collectedLines.Count} log entries to {fileName}");
                    }
                    catch (Exception fileEx)
                    {
                        Logger.LogAction("NETWORK", $"Failed to save log file: {fileEx.Message}");
                    }
                }

                res.StatusCode = 200;
                byte[] ok = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(ok, 0, ok.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"HandleRemoteLogPost error: {ex.Message}");
                res.StatusCode = 500;
            }
            finally { try { res.Close(); } catch { } }
        }

        private void ServeLogsJson(HttpListenerResponse res)
        {
            try
            {
                // Get PC network logs
                string pcLogs = Logger.GetRecentNetworkLogs(200);
                var pcLines = pcLogs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => $"[💻 PC] {l}")
                    .ToList();

                // Get mobile logs
                var mobileLines = _remoteDeviceLogs.ToArray().ToList();

                // Merge and sort (newest first — both have timestamps)
                var all = new List<string>();
                all.AddRange(pcLines);
                all.AddRange(mobileLines);
                // Keep newest 300 combined
                if (all.Count > 300) all = all.TakeLast(300).ToList();
                all.Reverse(); // newest first

                string json = JsonSerializer.Serialize(new
                {
                    pcName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                    pcLogCount = pcLines.Count,
                    mobileLogCount = mobileLines.Count,
                    totalCount = all.Count,
                    logs = all
                });

                byte[] data = Encoding.UTF8.GetBytes(json);
                res.StatusCode = 200;
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = data.Length;
                res.OutputStream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"ServeLogsJson error: {ex.Message}");
                res.StatusCode = 500;
            }
            finally { try { res.Close(); } catch { } }
        }

        private void ServeLogDashboard(HttpListenerResponse res)
        {
            try
            {
                string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>FlyShelf — Network Logs</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: 'Segoe UI', 'SF Pro', -apple-system, sans-serif; background: #0B0E14; color: #E5E7EB; min-height: 100vh; }
  .header { padding: 20px 24px; background: linear-gradient(135deg, #111827, #1F2937); border-bottom: 1px solid #1F2937; display: flex; justify-content: space-between; align-items: center; }
  .header h1 { font-size: 20px; font-weight: 700; background: linear-gradient(135deg, #60A5FA, #A78BFA); -webkit-background-clip: text; -webkit-text-fill-color: transparent; }
  .header .badge { font-size: 11px; background: #1E293B; color: #94A3B8; padding: 4px 10px; border-radius: 8px; }
  .stats { display: flex; gap: 12px; padding: 16px 24px; flex-wrap: wrap; }
  .stat { flex: 1; min-width: 120px; background: #111827; border: 1px solid #1F2937; border-radius: 12px; padding: 14px 16px; text-align: center; }
  .stat .num { font-size: 24px; font-weight: 700; }
  .stat .label { font-size: 11px; color: #6B7280; text-transform: uppercase; letter-spacing: 1px; margin-top: 4px; }
  .controls { padding: 0 24px 12px; display: flex; gap: 8px; flex-wrap: wrap; }
  .btn { padding: 8px 16px; border-radius: 8px; border: 1px solid #374151; background: #1F2937; color: #E5E7EB; cursor: pointer; font-size: 12px; font-weight: 600; transition: all 0.15s; }
  .btn:hover { background: #374151; }
  .btn.active { background: #4F46E5; border-color: #6366F1; }
  .btn.danger { background: #7F1D1D; border-color: #991B1B; color: #FCA5A5; }
  .log-container { margin: 0 24px 24px; background: #0F1115; border: 1px solid #1A1F2E; border-radius: 14px; overflow: hidden; }
  .log-header { padding: 10px 16px; background: #111827; border-bottom: 1px solid #1A1F2E; display: flex; justify-content: space-between; align-items: center; }
  .log-header span { font-size: 11px; color: #4B5563; font-family: 'Consolas', 'Fira Code', monospace; font-weight: 600; }
  .log-body { max-height: calc(100vh - 280px); overflow-y: auto; padding: 12px 16px; }
  .log-line { font-family: 'Consolas', 'Fira Code', monospace; font-size: 11px; line-height: 1.7; white-space: pre-wrap; word-break: break-all; }
  .log-line.error { color: #EF4444; }
  .log-line.firebase { color: #F59E0B; }
  .log-line.download { color: #10B981; }
  .log-line.http { color: #60A5FA; }
  .log-line.auth { color: #A78BFA; }
  .log-line.cloud { color: #F97316; }
  .log-line.mobile { color: #EC4899; }
  .log-line.default { color: #6B7280; }
  .auto-badge { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #10B981; margin-right: 8px; animation: pulse 2s infinite; }
  @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.3; } }
  .empty { text-align: center; padding: 40px; color: #374151; font-style: italic; }
  ::-webkit-scrollbar { width: 6px; }
  ::-webkit-scrollbar-track { background: transparent; }
  ::-webkit-scrollbar-thumb { background: #374151; border-radius: 3px; }
</style>
</head>
<body>
<div class=""header"">
  <h1>🌐 FlyShelf Network Logs</h1>
  <span class=""badge"" id=""refresh-badge""><span class=""auto-badge""></span>Auto-refresh: 3s</span>
</div>
<div class=""stats"">
  <div class=""stat""><div class=""num"" id=""pc-count"" style=""color:#60A5FA"">-</div><div class=""label"">💻 PC Logs</div></div>
  <div class=""stat""><div class=""num"" id=""mobile-count"" style=""color:#EC4899"">-</div><div class=""label"">📱 Mobile Logs</div></div>
  <div class=""stat""><div class=""num"" id=""total-count"" style=""color:#A78BFA"">-</div><div class=""label"">Total</div></div>
</div>
<div class=""controls"">
  <button class=""btn active"" id=""btn-all"" onclick=""setFilter('all')"">All</button>
  <button class=""btn"" id=""btn-pc"" onclick=""setFilter('pc')"">💻 PC Only</button>
  <button class=""btn"" id=""btn-mobile"" onclick=""setFilter('mobile')"">📱 Mobile Only</button>
  <button class=""btn"" id=""btn-errors"" onclick=""setFilter('errors')"">❌ Errors</button>
  <button class=""btn"" onclick=""copyLogs()"">📋 Copy All</button>
  <button class=""btn danger"" onclick=""location.reload()"">↻ Refresh</button>
</div>
<div class=""log-container"">
  <div class=""log-header"">
    <span>LIVE NETWORK FEED</span>
    <span id=""last-update"">—</span>
  </div>
  <div class=""log-body"" id=""log-body"">
    <div class=""empty"">Loading logs...</div>
  </div>
</div>
<script>
let allLogs = [];
let filter = 'all';
function classify(line) {
  const u = line.toUpperCase();
  if (u.includes('ERROR') || u.includes('FAIL') || u.includes('✗') || u.includes('FAULT')) return 'error';
  if (u.includes('FIREBASE')) return 'firebase';
  if (u.includes('DOWNLOAD') || u.includes('✓') || u.includes('✅')) return 'download';
  if (u.includes('HTTP') || u.includes('PC-POLL')) return 'http';
  if (u.includes('PAIR') || u.includes('AUTH')) return 'auth';
  if (u.includes('CLOUDFLARE') || u.includes('CF_') || u.includes('TUNNEL')) return 'cloud';
  if (u.includes('📱')) return 'mobile';
  return 'default';
}
function setFilter(f) {
  filter = f;
  document.querySelectorAll('.btn').forEach(b => b.classList.remove('active'));
  document.getElementById('btn-' + f).classList.add('active');
  renderLogs();
}
function renderLogs() {
  const body = document.getElementById('log-body');
  let logs = allLogs;
  if (filter === 'pc') logs = logs.filter(l => l.includes('💻'));
  else if (filter === 'mobile') logs = logs.filter(l => l.includes('📱'));
  else if (filter === 'errors') logs = logs.filter(l => { const u = l.toUpperCase(); return u.includes('ERROR') || u.includes('FAIL') || u.includes('FAULT') || u.includes('401') || u.includes('✗'); });
  if (logs.length === 0) { body.innerHTML = '<div class=""empty"">No logs matching filter.</div>'; return; }
  body.innerHTML = logs.map(l => '<div class=""log-line ' + classify(l) + '"">' + l.replace(/</g,'&lt;') + '</div>').join('');
}
function copyLogs() {
  navigator.clipboard.writeText(allLogs.join('\n')).then(() => alert('Copied ' + allLogs.length + ' log entries!'));
}
async function fetchLogs() {
  try {
    const pin = new URLSearchParams(window.location.search).get('pin') || '';
    const res = await fetch('/api/logs?pin=' + encodeURIComponent(pin));
    if (!res.ok) { document.getElementById('log-body').innerHTML = '<div class=""empty"">Auth required. Add ?pin=YOUR_PIN to URL</div>'; return; }
    const data = await res.json();
    allLogs = data.logs || [];
    document.getElementById('pc-count').textContent = data.pcLogCount || 0;
    document.getElementById('mobile-count').textContent = data.mobileLogCount || 0;
    document.getElementById('total-count').textContent = data.totalCount || 0;
    document.getElementById('last-update').textContent = new Date().toLocaleTimeString();
    renderLogs();
  } catch(e) { console.error('Fetch error:', e); }
}
fetchLogs();
setInterval(fetchLogs, 3000);
</script>
</body>
</html>";

                byte[] data = Encoding.UTF8.GetBytes(html);
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = data.Length;
                res.OutputStream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"ServeLogDashboard error: {ex.Message}");
                res.StatusCode = 500;
            }
            finally { try { res.Close(); } catch { } }
        }
    }
}
