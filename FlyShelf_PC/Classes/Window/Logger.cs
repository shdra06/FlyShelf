using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace FlyShelf.Classes
{
    public static class Logger
    {
#if DEBUG
        /// <summary>Logging is active in Debug builds for development diagnostics.</summary>
        internal static bool IsEnabled = true;
#else
        /// <summary>Logging is disabled in Release/Store builds — no disk I/O on user machines.</summary>
        internal static bool IsEnabled = false;
#endif

        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
        private static readonly string LogFile = Path.Combine(LogDirectory, "activity_log.txt");
        private static readonly string NetLogFile = Path.Combine(LogDirectory, "network_diagnostics.txt");
        
        // Async buffered logging — never blocks the UI thread
        private static readonly ConcurrentQueue<string> _buffer = new();
        private static readonly ConcurrentQueue<string> _netBuffer = new();
        private static Timer _flushTimer;
        private static Timer _cleanupTimer;
        private static readonly object _flushLock = new();
        private const int MAX_LOG_LINES = 500; // Keep last 500 lines per file
        private const int CLEANUP_INTERVAL_MS = 5 * 60_000; // 5 minutes
        private const long MAX_LOG_FILE_SIZE = 5 * 1024 * 1024; // 5 MB
        private const int MAX_ROTATED_FILES = 3;

        // Network log categories — any LogAction with these prefixes goes to network_diagnostics.txt
        private static readonly string[] NET_CATEGORIES = {
            "CLOUDFLARE", "CF_", "FIREBASE", "FORCED SYNC", "BIND", "NETWORK",
            "HTTP", "HEARTBEAT", "CLOUDFLARE HEALTH", "CLOUDFLARE_ERROR",
            "DRAG IN", "CLIPBOARD", "FIREBASE SSE", "FIREBASE SYNC",
            "FIREBASE STORAGE", "FIREBASE CLEANUP", "FIREBASE ERROR",
            "LISTENER", "SERVER", "DOWNLOAD",
            "PEER", "PAIR", "PUSH", "TUNNEL CHANGE"
        };

        static Logger()
        {
            if (!IsEnabled) return; // Skip all disk I/O setup in Release builds

            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
            
            // Flush buffer to disk every 2 seconds on a background thread
            _flushTimer = new Timer(_ => FlushBuffer(), null, 2000, 2000);
            
            // Auto-clean logs every 5 minutes — keep only last 500 lines
            _cleanupTimer = new Timer(_ => TruncateLogs(), null, CLEANUP_INTERVAL_MS, CLEANUP_INTERVAL_MS);
        }

        private static void TruncateLogs()
        {
            TruncateLogFile(LogFile);
            TruncateLogFile(NetLogFile);
        }

        private static void TruncateLogFile(string path)
        {
            // LOG-2 FIX: Hold _flushLock so this cannot race with FlushBuffer writing to the same file.
            // Write truncated content to a .tmp then rename atomically so a crash never corrupts the log.
            lock (_flushLock)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > MAX_LOG_LINES)
                    {
                        string tmp = path + ".tmp";
                        File.WriteAllLines(tmp, lines.Skip(lines.Length - MAX_LOG_LINES));
                        File.Move(tmp, path, overwrite: true);
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }
        }

        public static void LogAction(string actionType, string details)
        {
            if (!IsEnabled) return;
            try
            {
                // Use NTP-corrected time if available, otherwise fall back to system time
                string timestamp = (NetworkClock.IsSynced ? NetworkClock.Now.DateTime : DateTime.Now)
                    .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string logEntry = $"[{timestamp}] [{actionType.ToUpper(CultureInfo.InvariantCulture)}] {details}";
                
                // Enqueue to main log — zero-allocation on the hot path, never blocks
                _buffer.Enqueue(logEntry);

                // Also enqueue to network diagnostics log if it's a network-related category
                string upperAction = actionType.ToUpper(CultureInfo.InvariantCulture);
                foreach (var cat in NET_CATEGORIES)
                {
                    if (upperAction.Contains(cat))
                    {
                        _netBuffer.Enqueue(logEntry);
                        break;
                    }
                }

                // Also push to the in-memory live monitor (lightweight, already on correct thread)
                NetworkActivityLog.Instance.Log(upperAction, details);
            }
            catch 
            {
                // Failsafe so logging doesn't crash the app
            }
        }

        /// <summary>
        /// Rotates a log file if it exceeds MAX_LOG_FILE_SIZE.
        /// Shifts existing rotations: .1 → .2, .2 → .3, keeps max MAX_ROTATED_FILES.
        /// Must be called under _flushLock.
        /// </summary>
        private static void RotateLogFileIfNeeded(string logPath)
        {
            try
            {
                if (!File.Exists(logPath)) return;
                var fi = new FileInfo(logPath);
                if (fi.Length < MAX_LOG_FILE_SIZE) return;

                // Shift existing rotations (oldest first to avoid overwrite)
                for (int i = MAX_ROTATED_FILES; i >= 1; i--)
                {
                    string src = i == 1 ? logPath + ".1" : logPath + $".{i - 1}";
                    string dst = logPath + $".{i}";
                    if (i == 1) src = logPath + ".1";
                    try
                    {
                        if (i == MAX_ROTATED_FILES && File.Exists(logPath + $".{i}"))
                            File.Delete(logPath + $".{i}");
                    }
                    catch { } // Best-effort: failure is acceptable
                }
                // Shift .2 → .3, .1 → .2
                for (int i = MAX_ROTATED_FILES - 1; i >= 1; i--)
                {
                    string src = logPath + $".{i}";
                    string dst = logPath + $".{i + 1}";
                    if (File.Exists(src))
                    {
                        try { File.Move(src, dst, true); } catch { } // Best-effort: failure is acceptable
                    }
                }
                // Rename current → .1
                try { File.Move(logPath, logPath + ".1", true); } catch { } // Best-effort: failure is acceptable
            }
            catch { } // Best-effort: failure is acceptable
        }

        private static void FlushBuffer()
        {
            // LOG-1 FIX: Both main log and network log writes are inside _flushLock.
            // This prevents two concurrent FlushBuffer calls (timer + GetRecentNetworkLogs)
            // from interleaving writes and producing garbled lines.
            lock (_flushLock)
            {
                // Drain main log
                if (!_buffer.IsEmpty)
                {
                    try
                    {
                        RotateLogFileIfNeeded(LogFile);
                        using var writer = new StreamWriter(LogFile, append: true);
                        while (_buffer.TryDequeue(out string entry))
                            writer.WriteLine(entry);
                    }
                    catch { } // Best-effort: failure is acceptable
                }

                // Drain network diagnostics log
                if (!_netBuffer.IsEmpty)
                {
                    try
                    {
                        RotateLogFileIfNeeded(NetLogFile);
                        using var writer = new StreamWriter(NetLogFile, append: true);
                        while (_netBuffer.TryDequeue(out string entry))
                            writer.WriteLine(entry);
                    }
                    catch { } // Best-effort: failure is acceptable
                }
            }
        }

        /// <summary>
        /// Dumps complete network state snapshot to the network diagnostics log.
        /// Call on startup and whenever user wants to diagnose sync issues.
        /// </summary>
        public static void DumpNetworkDiagnostics()
        {
            if (!IsEnabled) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
                sb.AppendLine("║              FLYSHELF NETWORK DIAGNOSTICS SNAPSHOT           ║");
                sb.AppendLine(CultureInfo.InvariantCulture, $"║  Time: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}                            ║");
                sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
                sb.AppendLine();

                // Device Identity
                sb.AppendLine("── DEVICE IDENTITY ──");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  DeviceName:   {SettingsManager.Current.DeviceName ?? "(not set)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  DeviceId:     {SettingsManager.Current.DeviceId ?? "(not set)"}");
// [SECURITY FIX v2.1.0]: Hash PII in diagnostics to prevent exposure (M-09)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  MachineName:  {Environment.MachineName[..Math.Min(2, Environment.MachineName.Length)]}***");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  UserName:     {Environment.UserName[..Math.Min(2, Environment.UserName.Length)]}***");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  OS:           {Environment.OSVersion}");
                sb.AppendLine();

                // Network Interfaces
                sb.AppendLine("── NETWORK INTERFACES ──");
                try
                {
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        if (nic.Description.Contains("virtualbox", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("vmware", StringComparison.OrdinalIgnoreCase) ||
                            nic.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("wsl", StringComparison.OrdinalIgnoreCase)) continue;
                        
                        var ipProps = nic.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                // [SECURITY FIX v2.1.0]: Truncate IP addresses in diagnostics to prevent PII exposure (M-11)
                                string ip = addr.Address.ToString();
                                string safeIp = ip.Length > 4 ? ip[..4] + "***" : "***";
                                string mask = addr.IPv4Mask.ToString();
                                string safeMask = mask.Length > 4 ? mask[..4] + "***" : "***";
                                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{nic.NetworkInterfaceType}] {nic.Name}: {safeIp} (Mask: {safeMask})");
                            }
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  Error enumerating NICs: {ex.Message}"); }
                sb.AppendLine();

                // Sync Settings
                sb.AppendLine("── SYNC SETTINGS ──");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  CloudDiscovery:        {SettingsManager.Current.EnableCloudDiscovery}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  GlobalCloudflare:      {SettingsManager.Current.EnableGlobalCloudflare}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  LocalLAN:              {SettingsManager.Current.EnableLocalLAN}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  LocalNetworkSync:      {SettingsManager.Current.EnableLocalNetworkSync}");
                sb.AppendLine();

                // Cloudflare State
                sb.AppendLine("── CLOUDFLARE STATE ──");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  CachedGlobalUrl:  {CloudDiscoveryManager.CachedGlobalUrl ?? "(empty)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  CachedLocalUrl:   {CloudDiscoveryManager.CachedLocalUrl ?? "(empty)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  IsTunnelActive:   {(!string.IsNullOrEmpty(CloudDiscoveryManager.CachedGlobalUrl) && CloudDiscoveryManager.CachedGlobalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))}");
                sb.AppendLine();

                // Cloudflared process check
                sb.AppendLine("── CLOUDFLARED PROCESS ──");
                try
                {
                    var cfProcesses = System.Diagnostics.Process.GetProcessesByName("cloudflared");
                    try
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  Running instances: {cfProcesses.Length}");
                        foreach (var p in cfProcesses)
                        {
                            try { sb.AppendLine(CultureInfo.InvariantCulture, $"    PID {p.Id}: {p.ProcessName} (Started: {p.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}, Memory: {(p.WorkingSet64 / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)}MB)"); }
                            catch { sb.AppendLine(CultureInfo.InvariantCulture, $"    PID {p.Id}: (access denied for details)"); }
                        }
                    }
                    finally
                    {
                        // M-23 FIX: Dispose all Process objects to release native handles
                        foreach (var p in cfProcesses) p.Dispose();
                    }
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  Error checking processes: {ex.Message}"); }
                sb.AppendLine();

                // cloudflared.exe binary check
                sb.AppendLine("── CLOUDFLARED BINARY ──");
                string exePath = CloudflareDaemon.GetCloudflaredExePath();
                bool isBundled = exePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase); // CA1310 already OK
                if (File.Exists(exePath))
                {
                    var fi = new FileInfo(exePath);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Path:     {exePath}{(isBundled ? " (Bundled)" : " (AppData)")}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Size:     {(fi.Length / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)} MB");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Modified: {fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Valid:    {fi.Length > 10_000_000}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  NOT FOUND at {exePath}");
                }
                sb.AppendLine();

                // Firewall / Port Check
                sb.AppendLine("── PORT ACCESSIBILITY ──");
                try
                {
                    int port = 8999;
                    bool portListening = false;
                    var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                    foreach (var ep in listeners)
                    {
                        if (ep.Port == port) { portListening = true; break; }
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Port {port}: {(portListening ? "LISTENING ✓" : "NOT LISTENING ✗")}");
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  Port check error: {ex.Message}"); }
                sb.AppendLine();

                // Internet Connectivity
                sb.AppendLine("── INTERNET CONNECTIVITY ──");
                // M3 FIX: Task.Run avoids SynchronizationContext deadlock, and the 10-second
                // timeout prevents hanging forever if the network is unreachable. Without the
                // timeout, .GetAwaiter().GetResult() would block indefinitely on a dead network.
                try
                {
                    using var client = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(5) };
                    var firebaseTask = Task.Run(async () =>
                    {
                        var authUrl = await FirebaseAuthManager.AuthenticateUrl($"{FirebaseAuthManager.FirebaseDatabaseUrl}/.json?shallow=true").ConfigureAwait(false);
                        return await client.GetAsync(authUrl).ConfigureAwait(false);
                    });
                    if (firebaseTask.Wait(TimeSpan.FromSeconds(10)))
                    {
                        var t = firebaseTask.Result;
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  Firebase RTDB:     HTTP {(int)t.StatusCode} {(t.IsSuccessStatusCode ? "✓" : "✗")}");
                    }
                    else
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  Firebase RTDB:     TIMEOUT (>10s)");
                    }
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  Firebase RTDB:     FAILED — {ex.InnerException?.Message ?? ex.Message}"); }

                // Test Cloudflare tunnel reachability
                if (!string.IsNullOrEmpty(CloudDiscoveryManager.CachedGlobalUrl) && CloudDiscoveryManager.CachedGlobalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var client = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
                        var cfTask = Task.Run(async () => await client.GetAsync($"{CloudDiscoveryManager.CachedGlobalUrl}/api/health").ConfigureAwait(false));
                        if (cfTask.Wait(TimeSpan.FromSeconds(10)))
                        {
                            var t = cfTask.Result;
                            sb.AppendLine(CultureInfo.InvariantCulture, $"  Cloudflare Tunnel: HTTP {(int)t.StatusCode} {(t.IsSuccessStatusCode ? "✓" : "✗")}");
                        }
                        else
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"  Cloudflare Tunnel: TIMEOUT (>10s)");
                        }
                    }
                    catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  Cloudflare Tunnel: FAILED — {ex.InnerException?.Message ?? ex.Message}"); }
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Cloudflare Tunnel: NOT CONFIGURED");
                }
                sb.AppendLine();

                // DNS Resolution check (common cause of cloudflared failure)
                sb.AppendLine("── DNS RESOLUTION ──");
                try
                {
                    var addrs = System.Net.Dns.GetHostAddresses("region1.v2.argotunnel.com");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  argotunnel.com:    {string.Join(", ", addrs.Select(a => a.ToString()))} ✓");
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  argotunnel.com:    FAILED — {ex.Message} (Cloudflare tunnel WILL fail!)"); }
                try
                {
                    var addrs = System.Net.Dns.GetHostAddresses("api.trycloudflare.com");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  trycloudflare.com: {string.Join(", ", addrs.Select(a => a.ToString()))} ✓");
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  trycloudflare.com: FAILED — {ex.Message}"); }
                sb.AppendLine();
                
                sb.AppendLine("══════════════════════════════════════════════════════════════");

                // Write to network log file
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                foreach (var line in sb.ToString().Split('\n'))
                {
                    _netBuffer.Enqueue($"[{timestamp}] [DIAGNOSTICS] {line.TrimEnd('\r')}");
                }
                
                // Also log to main activity log
                LogAction("DIAGNOSTICS", "Network diagnostics snapshot captured → " + NetLogFile);
            }
            catch (Exception ex)
            {
                LogAction("DIAGNOSTICS ERROR", $"Failed to capture network diagnostics: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the path to the network diagnostics log file.
        /// </summary>
        public static string GetNetworkLogPath() => NetLogFile;

        /// <summary>
        /// Returns the last N lines of the network diagnostics log as a string (for clipboard copy).
        /// </summary>
        public static string GetRecentNetworkLogs(int lineCount = 200)
        {
            try
            {
                FlushBuffer(); // Ensure pending entries are written first
                if (!File.Exists(NetLogFile)) return "(No network logs found)";
                
                var lines = File.ReadAllLines(NetLogFile);
                int start = Math.Max(0, lines.Length - lineCount);
                return string.Join(Environment.NewLine, lines.Skip(start));
            }
            catch (Exception ex)
            {
                return $"(Error reading network logs: {ex.Message})";
            }
        }

        /// <summary>
        /// Call on app shutdown to ensure all buffered logs are written.
        /// </summary>
        public static void Shutdown()
        {
            _flushTimer?.Dispose();
            _cleanupTimer?.Dispose();
            FlushBuffer();
        }
    }
}
