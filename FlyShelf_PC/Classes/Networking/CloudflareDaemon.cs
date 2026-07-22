using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public class CloudflareDaemon
    {
        private Process _cfProcess;
        private int _localPort;
        private int _consecutiveFailures = 0;
        private bool _useHttp2 = false; // Start with QUIC, fallback to HTTP/2 for restricted networks
        private bool _stopped = false;  // True when Stop() is called — prevents auto-retry
        private const long MIN_EXE_SIZE = 10_000_000; // cloudflared.exe should be >10MB
        private const string TRUSTED_CF_HASH = "c2f4a3c3ea4c62eed562ede027d586a6044d35517e335e642f4e9783e651e4a3";
        private System.Timers.Timer _healthTimer;      // Periodic tunnel health monitor
        private int _quicErrorCount = 0;                 // Track consecutive QUIC/datagram failures for fast auto-restart
        private readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1); // Prevents concurrent StartTunnelCore from QUIC/exit/health triggers
        private int _healthCheckCount = 0;                // Counts health ticks for periodic public URL check
        private long _lastRetryScheduledTicks;             // Debounce: prevents multiple queued retries
        private CancellationTokenSource? _restartCts;      // C-01: Cancel overlapping restart tasks
        private static readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        private volatile string _globalUrl = "Offline";
        public string GlobalUrl { get => _globalUrl; private set => _globalUrl = value; }
        /// <summary>Previous tunnel URL — used to purge stale file entries from Firebase when URL changes.</summary>
        public string PreviousGlobalUrl { get; private set; } = "";
        /// <summary>
        /// True ONLY when the tunnel has been self-verified (HTTP 200 on /api/health).
        /// False if verification was inconclusive (HTTP 400/530/timeout).
        /// CloudDiscoveryManager checks this before using the URL for file downloads.
        /// </summary>
        private volatile bool _isTunnelVerified;
        public bool IsTunnelVerified { get => _isTunnelVerified; private set => _isTunnelVerified = value; }
        public event Action<string> GlobalUrlUpdated;

        public async Task StartAsync(int localPort)
        {
            _localPort = localPort;
            _consecutiveFailures = 0;
            _stopped = false;
            await StartTunnelCore();
        }

        public static string GetCloudflaredExePath()
        {
            // 1. Check bundled paths (perfect for packaged MSIX and portable bundles)
            string bundledAgentExe = Path.Combine(AppContext.BaseDirectory, "agent", "cloudflared.exe");
            if (File.Exists(bundledAgentExe)) return bundledAgentExe;

            string bundledRootExe = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
            if (File.Exists(bundledRootExe)) return bundledRootExe;

            // 2. Fall back to %AppData% folder (where dynamic installer drops/verifies it)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                "FlyShelf", "agent", "cloudflared.exe"
            );
        }

        private async Task StartTunnelCore()
        {
            if (_stopped) return;
            if (!await _startLock.WaitAsync(0)) { Logger.LogAction("CLOUDFLARE", "StartTunnelCore already in progress — skipping duplicate call"); return; }
            try
            {
                try
                {
                string exePath = GetCloudflaredExePath();
                bool isBundled = exePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

                // Verify if cloudflared.exe exists, has valid size, and is cryptographically integral
                bool needsDownload = false;

                if (!isBundled)
                {
                    string agentDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(agentDir))
                    {
                        Directory.CreateDirectory(agentDir);
                    }

                    if (!File.Exists(exePath) || new FileInfo(exePath).Length < MIN_EXE_SIZE)
                    {
                        needsDownload = true;
                    }
                    else
                    {
                        try
                        {
                            using (var sha = System.Security.Cryptography.SHA256.Create())
                            using (var fs = File.OpenRead(exePath))
                            {
                                string existingHash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                                if (existingHash != TRUSTED_CF_HASH)
                                {
                                    Logger.LogAction("CLOUDFLARE", "Existing cloudflared.exe hash mismatch — will re-download to guarantee integrity.");
                                    needsDownload = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("CLOUDFLARE_ERROR", $"Failed to verify existing cloudflared.exe hash: {ex.Message} — forcing secure re-download.");
                            needsDownload = true;
                        }
                    }
                }
                else
                {
                    Logger.LogAction("CLOUDFLARE", $"Using secure bundled agent at: {exePath}");
                }

                if (needsDownload)
                {
                    try { if (File.Exists(exePath)) File.Delete(exePath); } catch { } // Best-effort: failure is acceptable

                    GlobalUrl = "Downloading secure agent...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    Logger.LogAction("CLOUDFLARE", "Downloading cryptographically verified cloudflared.exe...");

                    bool downloaded = await DownloadCloudflaredAsync(exePath);
                    if (!downloaded)
                    {
                        Logger.LogAction("CLOUDFLARE_ERROR", "Failed to download cloudflared.exe — will retry in 30s");
                        GlobalUrl = "Download failed — retrying soon...";
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        ScheduleRetry(30_000);
                        return;
                    }
                }

                KillExisting();

                // Auto-switch protocol after repeated failures:
                // After 2 failures with QUIC, switch to HTTP/2 (TCP 443 — more firewall-friendly)
                // After 2 more failures with HTTP/2, switch back to QUIC
                if (_consecutiveFailures > 0 && _consecutiveFailures % 2 == 0)
                {
                    _useHttp2 = !_useHttp2;
                    Logger.LogAction("CLOUDFLARE", $"Switching protocol to {(_useHttp2 ? "HTTP/2 (TCP 443)" : "QUIC (UDP 7844)")} after {_consecutiveFailures} failures");
                }

                _cfProcess = new Process();
                _cfProcess.StartInfo.FileName = exePath;
                _cfProcess.StartInfo.Arguments = _useHttp2
                    ? $"tunnel --url http://localhost:{_localPort} --no-autoupdate --protocol http2"
                    : $"tunnel --url http://localhost:{_localPort} --no-autoupdate";
                Logger.LogAction("CLOUDFLARE", $"Starting tunnel with protocol: {(_useHttp2 ? "HTTP/2 (TCP 443)" : "QUIC (UDP 7844)")} [attempt {_consecutiveFailures + 1}]");
                _cfProcess.StartInfo.UseShellExecute = false;
                _cfProcess.StartInfo.RedirectStandardError = true;
                _cfProcess.StartInfo.CreateNoWindow = true;
                _cfProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                bool tunnelUrlReceived = false;

                _cfProcess.ErrorDataReceived += (s, e) =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        // Only log errors/warnings, not verbose info lines
                        bool isImportant = e.Data.Contains("ERR", StringComparison.Ordinal) || e.Data.Contains("WRN", StringComparison.Ordinal) || e.Data.Contains("trycloudflare.com", StringComparison.Ordinal) || e.Data.Contains("failed", StringComparison.Ordinal) || e.Data.Contains("error", StringComparison.Ordinal);
                        if (isImportant) Logger.LogAction("CF_STDERR", e.Data);

                        // Track QUIC/datagram failures — if the QUIC connection keeps dying,
                        // restart the tunnel with protocol switch instead of waiting 3 health failures
                        if (e.Data.Contains("failed to run the datagram handler", StringComparison.Ordinal) ||
                            e.Data.Contains("control stream encountered a failure", StringComparison.Ordinal) ||
                            e.Data.Contains("no recent network activity", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _quicErrorCount);
                            if (_quicErrorCount >= 5 && !_stopped)
                            {
                                int quicCount = Interlocked.Exchange(ref _quicErrorCount, 0);
                                Logger.LogAction("CLOUDFLARE", $"⚡ {quicCount} QUIC failures detected — auto-restarting tunnel with protocol switch...");
                                _consecutiveFailures++; // This triggers protocol toggle in StartTunnelCore
                                StopHealthMonitor();
                                GlobalUrl = "QUIC failing — restarting...";
                                GlobalUrlUpdated?.Invoke(GlobalUrl);
                                // C-01: Cancel any previous restart task before starting a new one
                                _restartCts?.Cancel();
                                _restartCts = new CancellationTokenSource();
                                var token = _restartCts.Token;
                                _ = Task.Run(async () =>
                                {
                                    KillExisting();
                                    await Task.Delay(2000, token);
                                    if (!token.IsCancellationRequested)
                                        await StartTunnelCore();
                                });
                                return;
                            }
                        }

                        Match match = Regex.Match(e.Data, @"https://([a-zA-Z0-9-]+)\.trycloudflare\.com");
                        if (match.Success)
                        {
                            string subdomain = match.Groups[1].Value.ToLower(CultureInfo.InvariantCulture);
                            // Skip known Cloudflare system subdomains — NOT tunnel URLs
                            if (subdomain == "api" || subdomain == "dash" || subdomain == "login" || subdomain == "www")
                            {
                                Logger.LogAction("CF_STDERR", $"Ignoring system URL: {match.Value}");
                                return;
                            }
                            // Track old URL for stale entry cleanup
                            if (GlobalUrl.Contains("trycloudflare.com", StringComparison.Ordinal) && GlobalUrl != match.Value)
                            {
                                PreviousGlobalUrl = GlobalUrl;
                            }
                            GlobalUrl = match.Value;
                            tunnelUrlReceived = true;
                            IsTunnelVerified = false; // Not verified until self-ping succeeds
                            _consecutiveFailures = 0; // Reset on success
                            Interlocked.Exchange(ref _quicErrorCount, 0); // Reset QUIC error count on new URL
                            Logger.LogAction("CLOUDFLARE", $"Tunnel URL received: {GlobalUrl} (waiting for DNS propagation before publishing...)");
                            // DON'T fire GlobalUrlUpdated here — URL is published to Firebase
                            // only AFTER DNS verification succeeds (in the verification block below).
                            // Publishing before DNS propagates causes "No such host" on receivers.
                        }
                    }
                    catch (Exception ex) { Logger.LogAction("CF_EVENT_ERROR", ex.Message); }
                };

                _cfProcess.EnableRaisingEvents = true;
                _cfProcess.Exited += (s, e) =>
                {
                    if (_stopped) return; // Don't retry if we intentionally stopped
                    int exitCode = -1;
                    try { exitCode = _cfProcess?.ExitCode ?? -1; } catch { } // Best-effort: failure is acceptable
                    Logger.LogAction("CLOUDFLARE", $"Process exited (code: {exitCode}). Will auto-restart...");
                    _consecutiveFailures++;
                    StopHealthMonitor();
                    int delay = GetRetryDelay();
                    GlobalUrl = $"Reconnecting in {delay / 1000}s...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    ScheduleRetry(delay);
                };

                _cfProcess.Start();
                _cfProcess.BeginErrorReadLine();
                Logger.LogAction("CLOUDFLARE", "Spawned Global Web Tunnel.");

                // Wait up to 30s for the tunnel URL to appear
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    if (tunnelUrlReceived) break;
                    if (_cfProcess.HasExited) break;
                }

                if (tunnelUrlReceived)
                {
                    Logger.LogAction("CLOUDFLARE", $"Tunnel URL received: {GlobalUrl}. Initiating verification in background task...");
                    
                    // Asynchronous fire-and-forget verification to completely avoid blocking application startup
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000); // Give cloudflared time to establish the proxy
                        
                        bool verified = false;
                        // AUDIT Task 5: Use shared pool — do NOT dispose (shared instance)
                        var verifyClient = HttpClientPool.Default;
                        
                        // Phase 1: Verify local server is responding (this is what Cloudflare proxies to)
                        for (int v = 0; v < 3; v++)
                        {
                            try
                            {
                                Logger.LogAction("CLOUDFLARE", $"Verifying local server (attempt {v + 1}/3)...");
                                var localResp = await verifyClient.GetAsync($"http://localhost:{_localPort}/api/health");
                                if (localResp.IsSuccessStatusCode)
                                {
                                    verified = true;
                                    IsTunnelVerified = true;
                                    Logger.LogAction("CLOUDFLARE", $"✅ Local server verified on port {_localPort} — tunnel is live: {GlobalUrl}");
                                    break;
                                }
                                Logger.LogAction("CLOUDFLARE", $"Local verify attempt {v + 1}/3: HTTP {(int)localResp.StatusCode}");
                            }
                            catch (Exception pingEx)
                            {
                                Logger.LogAction("CLOUDFLARE", $"Local verify attempt {v + 1}/3 failed: {pingEx.Message}");
                            }
                            await Task.Delay(2000);
                        }
                        
                        // Phase 2: Wait for DNS propagation before publishing URL to Firebase.
                        // Without this, receivers get "No such host" because Cloudflare's DNS
                        // hasn't propagated the new subdomain yet.
                        if (verified)
                        {
                            bool dnsReady = false;
                            for (int d = 0; d < 15; d++) // Up to 15 attempts × 3s = ~45s max wait
                            {
                                try
                                {
                                    // Extract hostname from URL for DNS check
                                    var uri = new Uri(GlobalUrl);
                                    var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
                                    if (addresses.Length > 0)
                                    {
                                        dnsReady = true;
                                        Logger.LogAction("CLOUDFLARE", $"✅ DNS resolved: {uri.Host} → {addresses[0]} ({d * 3}s wait)");
                                        break;
                                    }
                                }
                                catch (Exception dnsEx)
                                {
                                    Logger.LogAction("CLOUDFLARE", $"DNS not ready (attempt {d + 1}/15): {dnsEx.Message}");
                                }
                                if (d < 14) await Task.Delay(3000); // Wait 3s between DNS checks
                            }
                            
                            if (!dnsReady)
                            {
                                Logger.LogAction("CLOUDFLARE", "⚠️ DNS propagation timeout — publishing URL anyway (receivers will use fallback)");
                            }
                        }
                        
                        // Phase 3: Optional — try the public URL too (works on networks with good DNS)
                        if (!verified)
                        {
                            Logger.LogAction("CLOUDFLARE", "Local server check failed — trying public URL as fallback...");
                            for (int v = 0; v < 2; v++)
                            {
                                try
                                {
                                    await Task.Delay(3000);
                                    var pubResp = await verifyClient.GetAsync($"{GlobalUrl}/api/health");
                                    if (pubResp.IsSuccessStatusCode)
                                    {
                                        verified = true;
                                        IsTunnelVerified = true;
                                        Logger.LogAction("CLOUDFLARE", $"✅ Tunnel verified via public URL: {GlobalUrl}");
                                        break;
                                    }
                                    Logger.LogAction("CLOUDFLARE", $"Public URL verify {v + 1}/2: HTTP {(int)pubResp.StatusCode}");
                                }
                                catch (Exception pubEx)
                                {
                                    Logger.LogAction("CLOUDFLARE", $"Public URL verify {v + 1}/2 failed: {pubEx.Message}");
                                }
                            }
                        }

                        if (!verified)
                        {
                            IsTunnelVerified = false;
                            Logger.LogAction("CLOUDFLARE", $"⚠️ Tunnel verification FAILED — URL exists but local server not responding: {GlobalUrl}");
                            Logger.LogAction("CLOUDFLARE", $"⚠️ File sync will use Firebase Storage fallback instead of Cloudflare tunnel.");
                        }
                        
                        // NOW publish the URL to Firebase — DNS has had time to propagate
                        Logger.LogAction("CLOUDFLARE", $"Publishing tunnel URL to Firebase: {GlobalUrl}");
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        StartHealthMonitor(); // Begin periodic health checks
                    });

                    return;
                }

                if (_cfProcess.HasExited)
                {
                    // The Exited handler will schedule retry
                    Logger.LogAction("CLOUDFLARE", "Process exited before providing tunnel URL.");
                    return;
                }

                // Tunnel still running but no URL yet — could be slow network
                // Wait another 30s for the URL
                Logger.LogAction("CLOUDFLARE", "No URL yet — waiting an extra 30s...");
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    if (tunnelUrlReceived)
                    {
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        StartHealthMonitor();
                        return;
                    }
                    if (_cfProcess.HasExited) return; // Exited handler deals with retry
                }

                // Still no URL — kill and retry with different protocol
                Logger.LogAction("CLOUDFLARE", "Tunnel started but no URL received after 60s — killing and retrying...");
                _consecutiveFailures++;
                KillExisting();
                int retryDelay = GetRetryDelay();
                ScheduleRetry(retryDelay);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOUDFLARE_ERROR", $"Startup error: {ex.Message}");
                    _consecutiveFailures++;
                    ScheduleRetry(GetRetryDelay());
                }
            }
            finally { _startLock.Release(); }
        }

        /// <summary>
        /// Periodic health monitor: every 60s, ping the tunnel.
        /// If 3 consecutive pings fail, kill and restart the tunnel.
        /// </summary>
        private int _healthFailCount = 0;
        private void StartHealthMonitor()
        {
            StopHealthMonitor();
            Interlocked.Exchange(ref _healthFailCount, 0);
            Interlocked.Exchange(ref _healthCheckCount, 0);
            _healthTimer = new System.Timers.Timer(60_000); // Every 60s
            _healthTimer.Elapsed += async (s, e) =>
            {
                try
                {
                if (_stopped || string.IsNullOrEmpty(GlobalUrl) || !GlobalUrl.Contains("trycloudflare.com", StringComparison.Ordinal)) return;
                try
                {
                    // Ping localhost instead of public URL — avoids DNS resolution failures
                    var client = _healthClient;
                    var resp = await client.GetAsync($"http://localhost:{_localPort}/api/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        Interlocked.Exchange(ref _healthFailCount, 0); // Healthy
                        if (!IsTunnelVerified)
                        {
                            IsTunnelVerified = true;
                            Logger.LogAction("CLOUDFLARE HEALTH", $"✅ Tunnel now verified via health check — file downloads enabled: {GlobalUrl}");
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _healthFailCount);
                        if (IsTunnelVerified && _healthFailCount >= 2)
                        {
                            IsTunnelVerified = false;
                            Logger.LogAction("CLOUDFLARE HEALTH", $"⚠️ Tunnel verification lost — file downloads will use Firebase Storage fallback");
                        }
                        Logger.LogAction("CLOUDFLARE HEALTH", $"Ping failed ({_healthFailCount}/3): HTTP {(int)resp.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _healthFailCount);
                    Logger.LogAction("CLOUDFLARE HEALTH", $"Ping failed ({_healthFailCount}/3): {ex.Message}");
                }

                // Every 5th check, verify the public URL too to detect edge failures
                Interlocked.Increment(ref _healthCheckCount);
                if (_healthCheckCount % 5 == 0 && !string.IsNullOrEmpty(GlobalUrl) && GlobalUrl.Contains("trycloudflare.com", StringComparison.Ordinal))
                {
                    try
                    {
                        var publicClient = _healthClient;
                        var publicResp = await publicClient.GetAsync(GlobalUrl + "/api/health");
                        if (!publicResp.IsSuccessStatusCode)
                        {
                            Logger.LogAction("CLOUDFLARE", "Public URL health check failed — tunnel may be stale");
                            Interlocked.Increment(ref _healthFailCount);
                        }
                    }
                    catch
                    {
                        Logger.LogAction("CLOUDFLARE", "Public URL unreachable — scheduling restart");
                        Interlocked.Increment(ref _healthFailCount);
                    }
                }

                if (_healthFailCount >= 3)
                {
                    Logger.LogAction("CLOUDFLARE HEALTH", "🔄 Tunnel appears dead — auto-restarting...");
                    StopHealthMonitor();
                    _consecutiveFailures++;
                    GlobalUrl = "Restarting tunnel...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    KillExisting();
                    await Task.Delay(3000);
                    _ = Task.Run(() => StartTunnelCore());
                }

                // C-02: Manually restart timer at end of handler to prevent overlapping ticks
                if (!_stopped) _healthTimer?.Start();
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOUDFLARE HEALTH", $"Unhandled exception in health timer: {ex.Message}");
                    // Restart timer even on error to prevent health monitoring from stopping permanently
                    if (!_stopped) try { _healthTimer?.Start(); } catch { } // Best-effort: failure is acceptable
                }
            };
            _healthTimer.AutoReset = false;
            _healthTimer.Start();
            Logger.LogAction("CLOUDFLARE HEALTH", "Health monitor started (60s interval)");
        }

        private void StopHealthMonitor()
        {
            try { _healthTimer?.Stop(); _healthTimer?.Dispose(); } catch { } // Best-effort: failure is acceptable
            _healthTimer = null;
        }

        /// <summary>
        /// Forces an immediate tunnel health check — called on wake from sleep
        /// to avoid waiting up to 60s for the periodic health timer.
        /// If the tunnel is dead, triggers a restart immediately.
        /// </summary>
        public async Task ForceCheckTunnelHealth()
        {
            if (_stopped || string.IsNullOrEmpty(GlobalUrl) || !GlobalUrl.Contains("trycloudflare.com", StringComparison.Ordinal))
            {
                Logger.LogAction("CLOUDFLARE HEALTH", "Force check skipped — tunnel not active");
                return;
            }
            Logger.LogAction("CLOUDFLARE HEALTH", "⚡ Force health check triggered (post-sleep)");
            try
            {
                var client = _healthClient;
                var resp = await client.GetAsync($"http://localhost:{_localPort}/api/health");
                if (resp.IsSuccessStatusCode)
                {
                    Logger.LogAction("CLOUDFLARE HEALTH", "✅ Force check passed — tunnel is alive");
                    Interlocked.Exchange(ref _healthFailCount, 0);
                    return;
                }
                Logger.LogAction("CLOUDFLARE HEALTH", $"Force check failed: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("CLOUDFLARE HEALTH", $"Force check failed: {ex.Message}");
            }
            // Tunnel is dead — restart immediately
            try
            {
                Logger.LogAction("CLOUDFLARE HEALTH", "🔄 Post-sleep tunnel dead — force-restarting...");
                StopHealthMonitor();
                _consecutiveFailures++;
                GlobalUrl = "Restarting tunnel...";
                GlobalUrlUpdated?.Invoke(GlobalUrl);
                KillExisting();
                await Task.Delay(2000);
                _ = Task.Run(() => StartTunnelCore());
            }
            catch (Exception ex)
            {
                Logger.LogAction("CLOUDFLARE HEALTH", $"Force-restart failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate retry delay with exponential backoff: 5s, 10s, 20s, 30s… up to 5 min for sustained failures.
        /// Never gives up — tunnel is critical for cross-network file sync.
        /// M-07: After 10 consecutive retries, cap increases to 300s (5 min) to avoid hammering.
        /// </summary>
        private int GetRetryDelay()
        {
            int baseDelay = 5_000;
            int delay = baseDelay * (int)Math.Pow(2, Math.Min(_consecutiveFailures, 6)); // Exponential up to 320s
            int cap = _consecutiveFailures >= 10 ? 300_000 : 30_000; // 5 min cap after 10 failures, else 30s
            return Math.Min(delay, cap);
        }

        private void ScheduleRetry(int delayMs)
        {
            if (_stopped) return;
            // Debounce: ignore retry if one was scheduled within the last 2 seconds
            long now = Environment.TickCount64;
            if (now - _lastRetryScheduledTicks < 2000) return;
            _lastRetryScheduledTicks = now;
            // PERF: LongRunning — retry loop can chain indefinitely, should not occupy ThreadPool thread
            _ = Task.Factory.StartNew(async () =>
            {
                try { await Task.Delay(delayMs); } catch { return; }
                if (!_stopped)
                {
                    Logger.LogAction("CLOUDFLARE", $"Auto-retry #{_consecutiveFailures} after {delayMs}ms...");
                    await StartTunnelCore();
                }
            }, TaskCreationOptions.LongRunning);
        }

        private async Task<bool> DownloadCloudflaredAsync(string exePath)
        {
            // Pin to a known stable release to guarantee signature verification is deterministic and reliable
            string[] downloadUrls = new[]
            {
                "https://github.com/cloudflare/cloudflared/releases/download/2024.12.2/cloudflared-windows-amd64.exe"
            };

            // AUDIT Task 5: Use shared pool — do NOT dispose (shared instance)
            var client = HttpClientPool.Download;

            foreach (string url in downloadUrls)
            {
                try
                {
                    Logger.LogAction("CLOUDFLARE", $"Downloading secure tunnel client from: {url}");
                    var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? -1;
                    Logger.LogAction("CLOUDFLARE", $"Download size: {totalBytes / 1048576.0:F1} MB");

                    string tempPath = exePath + ".tmp";
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576))
                    {
                        byte[] buffer = new byte[1048576];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                        }

                        if (totalRead < MIN_EXE_SIZE)
                        {
                            Logger.LogAction("CLOUDFLARE_ERROR", $"Download too small: {totalRead} bytes");
                            try { File.Delete(tempPath); } catch { } // Best-effort: failure is acceptable
                            continue;
                        }
                    }

                    // SECURITY: Cryptographic integrity check
                    Logger.LogAction("CLOUDFLARE", "Verifying SHA-256 signature of downloaded binary...");
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var fs = File.OpenRead(tempPath))
                    {
                        string downloadedHash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                        if (downloadedHash != TRUSTED_CF_HASH)
                        {
                            Logger.LogAction("CLOUDFLARE_ERROR", $"❌ SHA-256 mismatch! Downloaded: {downloadedHash}, Expected: {TRUSTED_CF_HASH}. Rejecting insecure binary.");
                            try { File.Delete(tempPath); } catch { } // Best-effort: failure is acceptable
                            continue;
                        }
                    }

                    // Atomic rename: only replace after complete download and successful signature verification
                    try { if (File.Exists(exePath)) File.Delete(exePath); } catch { } // Best-effort: failure is acceptable
                    File.Move(tempPath, exePath);
                    Logger.LogAction("CLOUDFLARE", $"✅ Download complete and verified: cloudflared.exe ({new FileInfo(exePath).Length / 1048576.0:F1} MB)");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOUDFLARE_ERROR", $"Download failed from {url}: {ex.Message}");
                }
            }
            return false;
        }

        public void Stop()
        {
            _restartCts?.Cancel(); // C-01: Cancel any pending restart task
            _stopped = true; // Prevents all auto-retry logic
            StopHealthMonitor();
            KillExisting();
            GlobalUrl = "Offline";
            GlobalUrlUpdated?.Invoke(GlobalUrl);
            Logger.LogAction("CLOUDFLARE", "Global Tunnel Terminated.");
        }

        private void KillExisting()
        {
            try
            {
                if (_cfProcess != null && !_cfProcess.HasExited)
                {
                    try { _cfProcess.Kill(); } catch { } // Best-effort: failure is acceptable
                    _cfProcess.Dispose();
                    _cfProcess = null;
                }

                // Kill ALL cloudflared processes owned by FlyShelf (both bundled and AppData paths)
                string appDataAgentPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                    "FlyShelf", "agent", "cloudflared.exe"
                );
                string bundledAgentPath = Path.Combine(AppContext.BaseDirectory, "agent", "cloudflared.exe");
                string bundledRootPath = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");

                int killed = 0;
                foreach (var p in Process.GetProcessesByName("cloudflared"))
                {
                    try
                    {
                        string processPath = p.MainModule?.FileName ?? "";
                        bool isOurs = string.Equals(processPath, appDataAgentPath, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(processPath, bundledAgentPath, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(processPath, bundledRootPath, StringComparison.OrdinalIgnoreCase);
                        if (isOurs)
                        {
                            p.Kill();
                            p.Dispose(); // Prevent handle leak
                            killed++;
                        }
                    }
                    catch
                    {
                        // MainModule might throw if process is 64-bit and we are 32-bit (or access denied)
                        // In that case, do not kill it because it's not ours (ours is always accessible to us).
                    }
                }
                if (killed > 0) Logger.LogAction("CLOUDFLARE", $"Cleaned up {killed} zombie cloudflared process(es)");
            }
            catch { } // Best-effort: failure is acceptable
        }
    }
}
