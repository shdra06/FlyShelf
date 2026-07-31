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
    // AUDIT Task 6: Implement IDisposable to ensure deterministic cleanup of HttpListener,
    // timers, TLS cert, and CloudflareDaemon resources when server is torn down.
    public partial class NetworkSyncServer : IDisposable
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
        // AUDIT Task 5: Use shared pool instance instead of per-class HttpClient (prevents socket exhaustion)
        private static HttpClient _httpClient => HttpClientPool.Sync;

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
            bool isCf = host.Contains(".trycloudflare.com", StringComparison.Ordinal)
                      || !string.IsNullOrEmpty(req.Headers["Cf-Connecting-Ip"])
                      || !string.IsNullOrEmpty(req.Headers["Cf-Ray"])
                      || !string.IsNullOrEmpty(req.Headers["X-Forwarded-For"])
                      || req.Headers["X-Forwarded-Proto"] == "https";
            return isCf ? ("Cloudflare", "Cloud") : ("LAN", "LAN");
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

        // ═══════════════════════════════════════════════════════════════════
        // SSE: Server-Sent Events for instant push to web clients
        // Reuses proven pattern from /api/logs/stream
        // ═══════════════════════════════════════════════════════════════════
        private readonly ConcurrentDictionary<int, HttpListenerResponse> _sseClipboardClients = new();
        private int _sseClipboardClientIdCounter = 0;
        private readonly object _sseBroadcastLock = new();

        /// <summary>
        /// Broadcasts a clipboard change event to all connected SSE web clients.
        /// </summary>
        private void BroadcastClipboardToSSE(string payload)
        {
            if (_sseClipboardClients.IsEmpty) return;
            byte[] bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
            // PERF (copy jitter fix): broadcast on a background task with async writes.
            // The old code did synchronous Write/Flush under a lock on the CALLER's
            // thread (often the UI thread via NotifyClipboardChanged) - one slow
            // Cloudflare client could stall the UI for seconds on every copy.
            _ = Task.Run(async () =>
            {
                var dead = new List<int>();
                foreach (var kvp in _sseClipboardClients)
                {
                    try
                    {
                        await kvp.Value.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        await kvp.Value.OutputStream.FlushAsync();
                    }
                    catch { dead.Add(kvp.Key); }
                }
                foreach (var id in dead) _sseClipboardClients.TryRemove(id, out _);
            });
        }

        /// <summary>
        /// Handles a single SSE connection for clipboard events.
        /// Keeps the connection alive with 20s heartbeats until client disconnects.
        /// </summary>
        private async Task ServeClipboardEventStream(HttpListenerRequest req, HttpListenerResponse res)
        {
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.AddHeader("Cache-Control", "no-cache");
            res.AddHeader("Connection", "keep-alive");
            res.AddHeader("X-Accel-Buffering", "no"); // Disable Cloudflare/nginx buffering

            // Send initial connected event
            byte[] hello = Encoding.UTF8.GetBytes($"data: {{\"type\":\"connected\",\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}\n\n");
            await res.OutputStream.WriteAsync(hello, 0, hello.Length);
            await res.OutputStream.FlushAsync();

            int clientId = Interlocked.Increment(ref _sseClipboardClientIdCounter);
            _sseClipboardClients[clientId] = res;
            Logger.LogAction("SSE", $"Clipboard SSE client #{clientId} connected ({_sseClipboardClients.Count} total)");

            try
            {
                // Keep alive with 20s heartbeat (Cloudflare has ~100s idle timeout).
                // Bounded by _isRunning so client loops exit on server shutdown.
                while (_isRunning)
                {
                    await Task.Delay(20000);
                    if (!_isRunning) break; // M4: Re-check after delay to avoid write after shutdown
                    byte[] heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                    await res.OutputStream.WriteAsync(heartbeat, 0, heartbeat.Length);
                    await res.OutputStream.FlushAsync();
                }
            }
            catch { /* Client disconnected or server shutting down */ }
            finally
            {
                _sseClipboardClients.TryRemove(clientId, out _);
                Logger.LogAction("SSE", $"Clipboard SSE client #{clientId} disconnected ({_sseClipboardClients.Count} remaining)");
                try { res.Close(); } catch { } // M4: Ensure response stream is always closed
            }
        }

        /// <summary>
        /// Call this whenever the clipboard changes to instantly push to all connected clients.
        /// Unblocks long-poll waiters AND broadcasts to SSE clients.
        /// </summary>
        public void NotifyClipboardChanged(string itemType = "clipboard", string title = "")
        {
            // Invalidate the sync cache so the next /api/sync poll returns fresh data
            _syncCache = null;

            var payloadObj = new { type = itemType, title = title, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            string payload = System.Text.Json.JsonSerializer.Serialize(payloadObj);
            
            // 1. Unblock long-poll waiters
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

            // 2. Broadcast to SSE clients (instant push)
            int sseCount = _sseClipboardClients.Count;
            BroadcastClipboardToSSE(payload);

            Logger.LogAction("PUSH", $"NotifyClipboardChanged: {itemType} — {waiterCount} long-poll, {sseCount} SSE client(s)");
        }
        
        public string ServerUrl { get; private set; } = "Not Running";
        public string DisplayUrl => ServerUrl.Split(',')[0];
        public string GlobalUrl => _cfDaemon.GlobalUrl;
        public int CurrentPort { get; private set; } = 3000;

        /// <summary>
        /// Forces an immediate tunnel health check — used on wake from sleep
        /// to avoid waiting up to 60s for the periodic health timer.
        /// </summary>
        public Task ForceCheckTunnelHealth() => _cfDaemon.ForceCheckTunnelHealth();

        // SECURITY (C-01): Restrict downloads to FlyShelf's own directories only.
        // Previously included Downloads, Desktop, Documents, OneDrive — too broad.
        private static readonly string[] _allowedRoots = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles"),
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
            catch { } // Best-effort: failure is acceptable
            req.CertificateExtensions.Add(sanBuilder.Build());

            // Valid for 2 years
            var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));

            // Export with private key (atomic write: tmp → rename to prevent corruption on crash)
            var exported = cert.Export(X509ContentType.Pfx, "advanceclip_tls");
            string certTmpPath = certPath + ".tmp";
            File.WriteAllBytes(certTmpPath, exported);
            File.Move(certTmpPath, certPath, true);

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
                // H22: Decode URL-encoded characters (%2e%2e = ..) before normalization
                string decoded = Uri.UnescapeDataString(requestedPath);
                // Reject null bytes which can truncate paths in native code
                if (decoded.Contains('\0')) return false;
                string resolved = Path.GetFullPath(decoded);
                foreach (var root in _allowedRoots)
                {
                    string allowedRoot = Path.GetFullPath(root);
                    string rootWithSeparator = allowedRoot.EndsWith(Path.DirectorySeparatorChar)
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
        /// PERF: Uses InvokeAsync instead of blocking Invoke to prevent UI thread freeze.
        /// </summary>
        private HashSet<string> GetAllActiveFilePaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource<HashSet<string>>();
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        foreach (var item in _viewModel.DroppedItems)
                        {
                            if (!string.IsNullOrEmpty(item.FilePath))
                                paths.Add(Path.GetFullPath(item.FilePath));
                            if (!string.IsNullOrEmpty(item.ZippedArchivePath))
                                paths.Add(Path.GetFullPath(item.ZippedArchivePath));
                        }
                        tcs.TrySetResult(paths);
                    }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }, System.Windows.Threading.DispatcherPriority.Background);
                // Wait with timeout — non-blocking to the UI thread (only blocks this background thread)
                if (tcs.Task.Wait(TimeSpan.FromSeconds(2)))
                    return tcs.Task.Result;
            }
            catch { } // Best-effort: failure is acceptable
            return paths;
        }

        public NetworkSyncServer(FlyShelfViewModel viewModel)
        {
            Instance = this;
            _viewModel = viewModel;
            // [FIX M-11]: TODO: Store handler and unsubscribe if NetworkSyncServer is ever disposable
            _cfDaemon.GlobalUrlUpdated += (url) => { 
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => _viewModel.RefreshLocalServerData()); 
                if (!string.IsNullOrEmpty(url) && url.Contains(".trycloudflare.com", StringComparison.Ordinal))
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

                    // Step 1: Instantly notify all CONNECTED peers via P2P WebSocket (<100ms, no Firebase)
                    _ = Task.Run(async () =>
                    {
                        try { await PeerManager.Instance?.BroadcastUrlUpdate(ServerUrl, url); }
                        catch { /* PeerManager may not be initialized yet */ }
                    });

                    // Step 2: Write to Firebase for OFFLINE peers to discover later
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

        /// <summary>
        /// Writes a compact JSON response { "ok": ..., "message": "..." } and sets content-type.
        /// Centralises the boilerplate that was duplicated across every HTTP handler.
        /// </summary>
        private static async Task WriteJsonResponse(HttpListenerResponse res, bool ok, string message)
        {
            message = message?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
            var b = Encoding.UTF8.GetBytes($"{{\"ok\":{(ok ? "true" : "false")},\"message\":\"{message}\"}}");
            res.ContentType = "application/json";
            await res.OutputStream.WriteAsync(b, 0, b.Length);
        }

        // ═══ Server Lifecycle (Start/Stop/TLS/Proxy) moved to NetworkSyncServer.Lifecycle.cs ═══
    }
}
