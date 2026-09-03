// ---------------------------------------------------------------
// NetworkSyncServer — Server Lifecycle & TLS Proxy
// Start, Stop, TLS proxy, TCP proxy, Listen loop
// Split from NetworkSyncServer.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Globalization;
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
                catch { } // Best-effort: failure is acceptable

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
                catch { } // Best-effort: failure is acceptable

                int publicPort = 8999;
                bool needsProxy = false;

                // === PHASE 1: Try to bind HttpListener to ALL interfaces (no proxy needed) ===
                bool allInterfacesBound = false;
                
                // Strategy 1: http://+:port/ (accepts ALL interfaces)
                if (!allInterfacesBound) try {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://+:{publicPort}/");
                    _listener.Start();
                    Logger.LogAction("BIND", $"Bound to http://+:{publicPort}/ (all interfaces — no proxy needed)");
                    allInterfacesBound = true;
                } catch (Exception ex) { 
                    Logger.LogAction("BIND", $"http://+:{publicPort}/ failed: {ex.Message}");
                    if (_listener != null) { try { _listener.Close(); } catch { } /* Best-effort: failure is acceptable */ } 
                }

                // Strategy 2: http://*:port/
                if (!allInterfacesBound) try {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://*:{publicPort}/");
                    _listener.Start();
                    Logger.LogAction("BIND", $"Bound to http://*:{publicPort}/ (all interfaces — no proxy needed)");
                    allInterfacesBound = true;
                } catch (Exception ex) { 
                    Logger.LogAction("BIND", $"http://*:{publicPort}/ failed: {ex.Message}");
                    if (_listener != null) { try { _listener.Close(); } catch { } /* Best-effort: failure is acceptable */ } 
                }

                // Strategy 3: http://{localIp}:port/ + http://localhost:port/
                if (!allInterfacesBound) try {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://{localIp}:{publicPort}/");
                    _listener.Prefixes.Add($"http://localhost:{publicPort}/");
                    _listener.Start();
                    Logger.LogAction("BIND", $"Bound to http://{localIp}:{publicPort}/ + http://localhost:{publicPort}/");
                    allInterfacesBound = true;
                } catch (Exception ex) { 
                    Logger.LogAction("BIND", $"Dual-bind failed: {ex.Message}");
                    if (_listener != null) { try { _listener.Close(); } catch { } /* Best-effort: failure is acceptable */ } 
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
                        Logger.LogAction("BIND", $"HttpListener bound to http://localhost:{internalPort}/ (internal)");
                    } catch (Exception ex) {
                        Logger.LogAction("BIND", $"localhost:{internalPort} failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } /* Best-effort: failure is acceptable */ }
                    }

                    // Fallback: try 127.0.0.1
                    if (!localhostBound) try {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://127.0.0.1:{internalPort}/");
                        _listener.Start();
                        localhostBound = true;
                        Logger.LogAction("BIND", $"HttpListener bound to http://127.0.0.1:{internalPort}/ (internal)");
                    } catch (Exception ex) {
                        Logger.LogAction("BIND", $"127.0.0.1:{internalPort} failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } /* Best-effort: failure is acceptable */ }
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
                        
                        Logger.LogAction("BIND", $"TCP Proxy started: 0.0.0.0:{publicPort} → localhost:{internalPort} (LAN + Cloudflare enabled)");
                    }
                    catch (Exception proxyEx)
                    {
                        Logger.LogAction("BIND", $"TCP Proxy on port {publicPort} failed: {proxyEx.Message} — LAN access will NOT work");
                        // Even without the proxy, the HttpListener on localhost still works for Cloudflare
                        // (cloudflared connects to localhost). So we don't throw here.
                    }
                }

                CurrentPort = publicPort;

                _isRunning = true;
                // PERF: Avoid sync-over-async deadlock risk — run async loop directly on dedicated thread
                _listenerThread = new Thread(() =>
                {
                    try { ListenLoopAsync().GetAwaiter().GetResult(); }
                    catch (Exception ex) { Logger.LogAction("LISTENER", $"Thread crash caught: {ex.Message}"); }
                });
                _listenerThread.IsBackground = true;
                _listenerThread.Start();

                UpdateServerUrl();
                CloudDiscoveryManager.CachedLocalUrl = DisplayUrl; // Cache first LAN URL for file download fallback
                string bindMode = needsProxy ? "TCP Proxy" : "Direct";
                Logger.LogAction("NETWORK", $"Web server launched on {ServerUrl} (port {CurrentPort}, mode: {bindMode})");
                NetworkActivityLog.Instance.ServerStatus = "Online";

                // Fix #9: Detect network changes (WiFi reconnect, VPN toggle, IP change)
                // and refresh the LAN URL so peers can still reach us
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

                // Natively trigger Cloudflare alongside HTTP Socket conditionally
                // If we used a TCP proxy, Cloudflare tunnels to publicPort which the TcpProxy handles.
                // If we bound directly, Cloudflare tunnels to publicPort which HttpListener handles.
                if (SettingsManager.Current.EnableGlobalCloudflare)
                {
                    if (LicenseManager.CanUseCloudflare())
                    {
                        _ = _cfDaemon.StartAsync(CurrentPort);
                    }
                    else
                    {
                        SettingsManager.Current.EnableGlobalCloudflare = false;
                        SettingsManager.Save();
                    }
                }
                // Don't push "Offline" to Firebase — wait for the daemon to provide a real URL
                if (GlobalUrl != null && GlobalUrl != "Offline")
                {
                    _ = CloudDiscoveryManager.PushTunnelUrl(GlobalUrl, true, ServerUrl);
                }

                // Immediately check for new devices that paired while we were offline
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000); // Small delay for Firebase auth to initialize
                    await DevicePairingManager.CheckForHandshakes();
                });

                // H-13b: Handshake poll timer is now on-demand — started via StartHandshakePollTimer()
                // when a pairing code is actively published, and stopped via StopHandshakePollTimer().
                // This avoids unnecessary Firebase polling every 3s when no pairing is in progress.

                // Heartbeat: reduced to 900s (15 min) — URL updates are now handled via
                // P2P WebSocket directly to connected peers. Firebase writes only happen
                // at startup and on URL change. Timer is mainly for pairing handshakes.
                _heartbeatTimer = new System.Timers.Timer(900_000); // 15 minutes
                _heartbeatTimer.Elapsed += (s, e) =>
                {
                    // forceWrite: true — always update Firebase timestamp even if URL hasn't changed,
                    // so other devices' TTL check doesn't mark us as offline between heartbeats.
                    _ = CloudDiscoveryManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl, forceWrite: true);
                    // Always check for new devices that joined via QR scan, pairing code, or Firebase handshake
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
                                catch { } // Best-effort: failure is acceptable
                            }
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                };
                _heartbeatTimer.AutoReset = true;
                _heartbeatTimer.Start();
                Logger.LogAction("HEARTBEAT", "Device heartbeat started (900s interval — URL updates via P2P WebSocket)");
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
                    Logger.LogAction("TLS", $"HTTPS server started on {TlsUrl} (thumbprint: {TlsThumbprint})");
                }
                catch (Exception tlsEx)
                {
                    Logger.LogAction("TLS", $"HTTPS server failed to start: {tlsEx.Message} — LAN sync will use HTTP only");
                }

                // ═══════════════════════════════════════════════════════════════════
                // v5 PEER MANAGER: Direct P2P communication engine
                // Discovers peers, handshakes, and pushes data directly via LAN/Cloudflare.
                // Firebase is only used for URL discovery (~5 seconds at startup).
                // ═══════════════════════════════════════════════════════════════════
                _ = Task.Run(async () =>
                {
                    // PeerManager starts instantly to scan LAN/cache, Cloudflare auto-triggers resync when up
                    await Task.Delay(500);
                    try
                    {
                        // ═══ LAN TRANSFER ENGINE: Start dedicated TCP listener for zero-copy file transfers ═══
                        var transferManager = new LanTransferManager();
                        transferManager.SetViewModel(_viewModel);
                        var transferEngine = new LanTransferEngine();
                        await transferEngine.StartAsync();
                        transferManager.CleanupStaleCheckpoints();
                        Logger.LogAction("TCP_ENGINE", $"LAN Transfer Engine initialized on port {LanTransferEngine.TRANSFER_PORT}");

                        var peerManager = new PeerManager();
                        await peerManager.StartAsync();
                        Logger.LogAction("PEER", $"v5 PeerManager initialized — {peerManager.AliveCount} peer(s) connected");

                        // ═══ NEARBY DISCOVERY: Start UDP scanner for cross-network device detection ═══
                        try
                        {
                            var nearbyDiscovery = new NearbyDiscovery();
                            Logger.LogAction("NEARBY", "Nearby device discovery started");
                        }
                        catch (Exception ndEx)
                        {
                            Logger.LogAction("NEARBY", $"Nearby discovery init error: {ndEx.Message}");
                        }

                        // ═══ TRANSFER HISTORY: Initialize persistent history tracker ═══
                        try
                        {
                            if (TransferHistory.Instance == null)
                                _ = new TransferHistory();
                            Logger.LogAction("HISTORY", $"Transfer history loaded — {TransferHistory.Instance?.Entries.Count ?? 0} entries");
                        }
                        catch (Exception thEx)
                        {
                            Logger.LogAction("HISTORY", $"History init error: {thEx.Message}");
                        }
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
                Logger.LogAction("NETWORK ERROR", $"Server failed to start: {ex.Message}");
            }
            
            // [FIX M-35]: Null-conditional to prevent NRE during app shutdown
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => _viewModel.RefreshLocalServerData());
        }

        // ═══════════════════════════════════════════════════════════════════
        // TCP Reverse Proxy: Enables LAN access without admin/URL ACL.
        // TcpListener on 0.0.0.0:publicPort accepts any connection and
        // proxies the raw TCP stream to HttpListener on localhost:internalPort.
        // ═══════════════════════════════════════════════════════════════════

        // Max concurrent proxy connections to prevent resource exhaustion
        private static readonly SemaphoreSlim _proxySemaphore = new SemaphoreSlim(50, 50);

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
            if (!await _proxySemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                // Too many concurrent proxy connections — reject
                try { client.Close(); } catch { } // Best-effort: failure is acceptable
                return;
            }
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
                    // Short timeout for header read to prevent Slowloris
                    using var headerCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                    // Read HTTP headers efficiently using buffered Span-based memory scanning
                    byte[] buffer = new byte[16384]; // 16KB max header size
                    int totalRead = 0;
                    int headerEndIndex = -1;

                    while (totalRead < buffer.Length)
                    {
                        int read = await clientStream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, headerCts.Token);
                        if (read == 0) return; // Client disconnected
                        totalRead += read;

                        ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, totalRead);
                        int idx = span.IndexOf(new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' });
                        if (idx != -1)
                        {
                            headerEndIndex = idx + 4;
                            break;
                        }
                    }

                    if (headerEndIndex <= 0) return; // No valid HTTP headers

                    // Parse and rewrite the Host header to target localhost prefix
                    string headerText = System.Text.Encoding.ASCII.GetString(buffer, 0, headerEndIndex);
                    string rewritten = System.Text.RegularExpressions.Regex.Replace(
                        headerText,
                        @"(?i)Host:\s*[^\r\n]+",
                        $"Host: localhost:{targetPort}");

                    // Send rewritten headers to HttpListener
                    byte[] rewrittenBytes = System.Text.Encoding.ASCII.GetBytes(rewritten);
                    await targetStream.WriteAsync(rewrittenBytes, 0, rewrittenBytes.Length);

                    // Send any body bytes that were read along with the header in the initial packets
                    int leftoverBytes = totalRead - headerEndIndex;
                    if (leftoverBytes > 0)
                    {
                        await targetStream.WriteAsync(buffer, headerEndIndex, leftoverBytes);
                    }

                    // Bi-directional relay with 30-minute timeout to prevent zombie connections
                    using var proxyCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    var t1 = clientStream.CopyToAsync(targetStream, proxyCts.Token);
                    var t2 = targetStream.CopyToAsync(clientStream, proxyCts.Token);
                    await Task.WhenAny(t1, t2);
                }
            }
            catch { } // Best-effort: failure is acceptable � Connection closed — normal
            finally
            {
                _proxySemaphore.Release();
            }
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
            if (!await _proxySemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                try { client.Close(); } catch { } // Best-effort: failure is acceptable
                return;
            }
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    var networkStream = client.GetStream();

                    // Wrap in SslStream — this terminates TLS
                    using var sslStream = new SslStream(networkStream, false);
                    var sslOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCert!,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    };
                    using (var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        await sslStream.AuthenticateAsServerAsync(sslOptions, handshakeCts.Token);
                    }

                    // Connect to the local HTTP server
                    using var target = new System.Net.Sockets.TcpClient();
                    // Route to the internal port if proxy mode, else to the public port
                    int targetPort = _proxyInternalPort > 0 ? _proxyInternalPort : CurrentPort;
                    await target.ConnectAsync("localhost", targetPort);
                    target.NoDelay = true;
                    var targetStream = target.GetStream();

                    // Short timeout for header read to prevent Slowloris
                    using var headerCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                    // Read HTTP headers efficiently using buffered Span-based memory scanning
                    byte[] buffer = new byte[16384]; // 16KB max header size
                    int totalRead = 0;
                    int headerEndIndex = -1;

                    while (totalRead < buffer.Length)
                    {
                        int read = await sslStream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, headerCts.Token);
                        if (read == 0) return; // Client disconnected
                        totalRead += read;

                        ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, totalRead);
                        int idx = span.IndexOf(new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' });
                        if (idx != -1)
                        {
                            headerEndIndex = idx + 4;
                            break;
                        }
                    }

                    if (headerEndIndex <= 0) return;

                    // Rewrite Host header to match HttpListener's expected prefix
                    string headerText = Encoding.ASCII.GetString(buffer, 0, headerEndIndex);
                    string rewritten = System.Text.RegularExpressions.Regex.Replace(
                        headerText,
                        @"(?i)Host:\s*[^\r\n]+",
                        $"Host: localhost:{targetPort}");

                    byte[] rewrittenBytes = Encoding.ASCII.GetBytes(rewritten);
                    await targetStream.WriteAsync(rewrittenBytes, 0, rewrittenBytes.Length);

                    // Send any body bytes that were read along with the header in the initial TLS packets
                    int leftoverBytes = totalRead - headerEndIndex;
                    if (leftoverBytes > 0)
                    {
                        await targetStream.WriteAsync(buffer, headerEndIndex, leftoverBytes);
                    }

                    // Bi-directional relay with 5-minute timeout: sslStream ↔ targetStream
                    using var proxyCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var t1 = sslStream.CopyToAsync(targetStream, proxyCts.Token);
                    var t2 = targetStream.CopyToAsync(sslStream, proxyCts.Token);
                    await Task.WhenAny(t1, t2);
                }
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                Logger.LogAction("TLS", $"Client TLS handshake failed: {ex.Message}");
            }
            catch { } // Best-effort: failure is acceptable � Connection closed — normal
            finally
            {
                _proxySemaphore.Release();
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _proxyRunning = false;
            ServerUrl = "Offline";
            try { _heartbeatTimer?.Stop(); _heartbeatTimer?.Dispose(); } catch { } // Best-effort: failure is acceptable
            StopHandshakePollTimer();
            try { var t = Interlocked.Exchange(ref _wsPingTimer, null); t?.Dispose(); } catch { } // M-8: Dispose WebSocket ping timer
            try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { } // Best-effort: failure is acceptable
            _cfDaemon.Stop();
            try { PeerManager.Instance?.Stop(); } catch { } // Best-effort: failure is acceptable
            // Stop LAN Transfer Engine and persist checkpoints
            try { LanTransferManager.Instance?.PersistCheckpoints(); } catch { } // Best-effort: failure is acceptable
            try { LanTransferEngine.Instance?.Stop(); } catch { } // Best-effort: failure is acceptable
            // Stop Nearby Discovery
            try { NearbyDiscovery.Instance?.Stop(); } catch { } // Best-effort: failure is acceptable
            // Persist transfer history
            try { TransferHistory.Instance?.Save(); } catch { } // Best-effort: failure is acceptable
            _ = CloudDiscoveryManager.PushTunnelUrl("offline", false, "", forceWrite: true);
            try { _listener?.Stop(); } catch { } // Best-effort: failure is acceptable
            try { _proxyListener?.Stop(); } catch { } // Best-effort: failure is acceptable
            // Stop TLS proxy
            _tlsRunning = false;
            try { _tlsListener?.Stop(); } catch { } // Best-effort: failure is acceptable
            try { _tlsCert?.Dispose(); } catch { } // Best-effort: failure is acceptable
            TlsUrl = "";
            // [FIX M-35]: Null-conditional to prevent NRE during app shutdown
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => _viewModel.RefreshLocalServerData());
        }

        /// <summary>
        /// H-13b: Starts the handshake poll timer (3s interval) on demand.
        /// Call when a pairing code is actively published so we poll Firebase for handshakes.
        /// </summary>
        public void StartHandshakePollTimer()
        {
            if (_handshakePollTimer != null) return; // Already running
            _handshakePollTimer = new System.Timers.Timer(3000);
            _handshakePollTimer.Elapsed += async (s, e) =>
            {
                try
                {
                    if (_isRunning && DevicePairingManager.HasPairingKey)
                    {
                        await DevicePairingManager.CheckForHandshakes();
                    }
                }
                catch { }
            };
            _handshakePollTimer.AutoReset = true;
            _handshakePollTimer.Start();
            Logger.LogAction("HANDSHAKE", "Handshake poll timer started (3s interval)");
        }

        /// <summary>
        /// H-13b: Stops and disposes the handshake poll timer.
        /// Call when the pairing code is no longer published.
        /// </summary>
        public void StopHandshakePollTimer()
        {
            try
            {
                _handshakePollTimer?.Stop();
                _handshakePollTimer?.Dispose();
            }
            catch { } // Best-effort: failure is acceptable
            _handshakePollTimer = null;
            Logger.LogAction("HANDSHAKE", "Handshake poll timer stopped");
        }

        private void UpdateServerUrl()
        {
            try
            {
                var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(x => x.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up 
                             && !x.Description.Contains("virtualbox", StringComparison.OrdinalIgnoreCase) 
                             && !x.Description.Contains("vmware", StringComparison.OrdinalIgnoreCase) 
                             && !x.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
                             && !x.Description.Contains("wsl", StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Fix #9: Called when a network adapter address changes (WiFi reconnect, VPN toggle, Ethernet plug).
        /// Refreshes the LAN URL and pushes the updated address to Firebase + connected peers.
        /// </summary>
        private void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            try
            {
                string oldUrl = ServerUrl;
                UpdateServerUrl();
                string newDisplayUrl = DisplayUrl;

                if (newDisplayUrl != CloudDiscoveryManager.CachedLocalUrl && newDisplayUrl != "Not Running" && newDisplayUrl != "Offline")
                {
                    Logger.LogAction("NETWORK", $"IP changed: {CloudDiscoveryManager.CachedLocalUrl} → {newDisplayUrl}");
                    CloudDiscoveryManager.CachedLocalUrl = newDisplayUrl;

                    // Push updated URL to Firebase for offline peers
                    _ = CloudDiscoveryManager.PushTunnelUrl(GlobalUrl ?? newDisplayUrl, true, newDisplayUrl, forceWrite: true);

                    // Broadcast to connected peers via WebSocket
                    _ = Task.Run(async () =>
                    {
                        try { await PeerManager.Instance?.BroadcastUrlUpdate(newDisplayUrl, GlobalUrl ?? ""); }
                        catch { } // Best-effort: failure is acceptable
                    });

                    // M-9: Rebind UDP multicast listeners on network change
                    try { PeerManager.Instance?.RestartMulticastListeners(); }
                    catch { } // Best-effort: failure is acceptable

                    // [FIX M-35]: Null-conditional to prevent NRE during app shutdown
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => _viewModel.RefreshLocalServerData());
                }
            }
            catch { /* Best-effort — don't crash on network change events */ }
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

        // ═══ AUDIT Task 6: IDisposable implementation ═══
        // Delegates to Stop() which already disposes: _heartbeatTimer, _tlsCert,
        // _listener, _proxyListener, _tlsListener, and stops _cfDaemon.
        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop(); // Handles all resource cleanup (HttpListener, timers, TLS, CloudflareDaemon, etc.)

            // Additional cleanup for resources not covered by Stop()
            try { _listener?.Close(); } catch { } // Best-effort: ensure HttpListener is fully released
            
            GC.SuppressFinalize(this);
        }
    }
}
