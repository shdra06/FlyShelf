using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight internal clock with multi-server NTP and persistent time anchoring.
    /// Queries multiple NTP servers at startup for resilience. Persists the last known-good
    /// NTP time with Environment.TickCount64 so elapsed time can be computed even when
    /// NTP is blocked, without trusting the OS clock.
    /// </summary>
    public static class NetworkClock
    {
        private static TimeSpan _offset = TimeSpan.Zero;
        private static bool _synced = false;
        private static bool _driftDetected = false;

        // Persistent anchor state
        private static DateTimeOffset _anchorNtpTime;
        private static long _anchorTickCount;
        private static bool _anchorLoaded = false;

        private static readonly string _appDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _anchorFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", ".ntp_anchor");
        private static readonly object _anchorLock = new();

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        /// <summary>NTP servers to try if HTTPS time APIs are unreachable.</summary>
        private static readonly string[] NtpServers = new[]
        {
            "time.google.com",
            "time.windows.com",
            "pool.ntp.org",
            "time.cloudflare.com"
        };

        /// <summary>Corrected UTC time. Uses offset only if OS clock was wrong at startup.</summary>
        public static DateTimeOffset UtcNow
        {
            get
            {
                if (!_driftDetected) return DateTimeOffset.UtcNow;
                return DateTimeOffset.UtcNow + _offset;
            }
        }

        /// <summary>Corrected time as Unix milliseconds (for Firebase timestamps).</summary>
        public static long UtcNowMs => UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Corrected local time (for logging).</summary>
        public static DateTimeOffset Now => UtcNow.ToLocalTime();

        /// <summary>True if network time sync completed successfully.</summary>
        public static bool IsSynced => _synced;

        /// <summary>True if network time synced OR a valid persisted anchor exists (time can be trusted).</summary>
        public static bool IsTimeTrusted => _synced || _anchorLoaded;

        /// <summary>
        /// Returns the best available UTC time with a trust indicator.
        /// - If network time synced: returns corrected time (trusted).
        /// - If not synced but a persisted anchor exists: computes elapsed time from
        ///   TickCount64 delta and adds it to the stored time (trusted).
        /// - If neither: returns OS clock (untrusted).
        /// </summary>
        public static (DateTimeOffset time, bool isTrusted) GetTrustedUtcNow()
        {
            // Best case: live network time sync
            if (_synced)
            {
                return (UtcNow, true);
            }

            // Fallback: persisted anchor + monotonic TickCount64 elapsed time
            if (_anchorLoaded)
            {
                long elapsedMs = Environment.TickCount64 - _anchorTickCount;
                var computedTime = _anchorNtpTime.AddMilliseconds(elapsedMs);
                return (computedTime, true);
            }

            // Fallback: OS system clock
            return (DateTimeOffset.UtcNow, false);
        }

        /// <summary>
        /// One-shot sync at startup. Tries universal HTTPS time APIs first (works across all firewalls),
        /// then UDP NTP servers, and finally falls back to local OS clock.
        /// </summary>
        public static async Task InitializeAsync()
        {
            // Try to load persisted anchor first (available immediately if offline)
            LoadAnchor();

            try
            {
                DateTimeOffset? networkTime = null;
                string syncSource = "None";

                // Step 1: Try universal HTTPS time APIs (Port 443 — never blocked by firewalls)
                (networkTime, syncSource) = await QueryUniversalHttpTimeAsync();

                // Step 2: Fall back to UDP NTP if HTTPS failed
                if (!networkTime.HasValue)
                {
                    foreach (var server in NtpServers)
                    {
                        networkTime = await QueryNtpAsync(server);
                        if (networkTime.HasValue)
                        {
                            syncSource = $"NTP ({server})";
                            break;
                        }
                    }
                }

                // Step 3: If all network sources failed, gracefully fall back to anchor or OS clock
                if (!networkTime.HasValue)
                {
                    _synced = false;
                    Logger.LogAction("CLOCK", "All universal time APIs and NTP servers unreachable — " +
                        (_anchorLoaded ? "using persisted monotonic anchor" : "using OS system clock (fallback)"));
                    StartPeriodicResync();
                    return;
                }

                _offset = networkTime.Value - DateTimeOffset.UtcNow;
                _synced = true;

                if (Math.Abs(_offset.TotalSeconds) > 3)
                {
                    _driftDetected = true;
                    Logger.LogAction("CLOCK", $"✅ Universal time synced via {syncSource}. OS clock drift is {_offset.TotalSeconds:F2}s — applied correction");
                }
                else
                {
                    _driftDetected = false;
                    Logger.LogAction("CLOCK", $"✅ Universal time synced via {syncSource}. OS clock accurate (drift: {_offset.TotalMilliseconds:F0}ms)");
                }

                // Persist the anchor for future offline sessions
                PersistAnchor(networkTime.Value);
                StartPeriodicResync();
            }
            catch (Exception ex)
            {
                _synced = false;
                Logger.LogAction("CLOCK", $"Time sync exception: {ex.Message} — " +
                    (_anchorLoaded ? "using persisted anchor" : "using OS clock"));
            }
        }

        /// <summary>
        /// Starts a background timer to re-sync universal network time every 3 hours.
        /// Detects and logs clock drift over long uptimes.
        /// </summary>
        private static System.Timers.Timer? _resyncTimer;
        public static void StartPeriodicResync()
        {
            _resyncTimer?.Stop();
            _resyncTimer?.Dispose();
            _resyncTimer = new System.Timers.Timer(3 * 60 * 60 * 1000); // 3 hours
            _resyncTimer.Elapsed += async (s, e) =>
            {
                try
                {
                    var previousOffset = _offset;
                    var (networkTime, syncSource) = await QueryUniversalHttpTimeAsync();
                    if (!networkTime.HasValue)
                    {
                        foreach (var server in NtpServers)
                        {
                            networkTime = await QueryNtpAsync(server);
                            if (networkTime.HasValue)
                            {
                                syncSource = $"NTP ({server})";
                                break;
                            }
                        }
                    }

                    if (networkTime.HasValue)
                    {
                        _offset = networkTime.Value - DateTimeOffset.UtcNow;
                        _synced = true;
                        double driftSinceLast = Math.Abs((_offset - previousOffset).TotalSeconds);
                        if (driftSinceLast > 3)
                        {
                            _driftDetected = true;
                            Logger.LogAction("CLOCK", $"Periodic re-sync via {syncSource}: drift of {driftSinceLast:F1}s detected — updated offset");
                        }
                        else
                        {
                            _driftDetected = false;
                            Logger.LogAction("CLOCK", $"Periodic re-sync OK via {syncSource} (drift: {_offset.TotalMilliseconds:F0}ms)");
                        }
                        PersistAnchor(networkTime.Value);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOCK", $"Periodic re-sync failed: {ex.Message}");
                }
            };
            _resyncTimer.AutoReset = true;
            _resyncTimer.Start();
            Logger.LogAction("CLOCK", "Periodic universal time re-sync started (every 3 hours)");
        }

        /// <summary>
        /// Saves NTP time and TickCount64 to disk so future sessions can compute
        /// trusted time even without NTP connectivity.
        /// Format: &lt;NTP_ISO_timestamp&gt;|&lt;TickCount64&gt;
        /// </summary>
        private static void PersistAnchor(DateTimeOffset ntpTime)
        {
            lock (_anchorLock)
            {
                try
                {
                    Directory.CreateDirectory(_appDataDir);
                    string content = $"{ntpTime.ToUniversalTime():O}|{Environment.TickCount64}";
                    string tmpPath = _anchorFilePath + ".tmp";
                    File.WriteAllText(tmpPath, content);
                    File.Move(tmpPath, _anchorFilePath, overwrite: true);
                    Logger.LogAction("CLOCK", "Persisted NTP anchor");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOCK", $"Failed to persist NTP anchor: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Loads persisted NTP anchor from disk. Sets _anchorLoaded if valid.
        /// </summary>
        private static void LoadAnchor()
        {
            lock (_anchorLock)
            {
                try
                {
                    if (!File.Exists(_anchorFilePath)) return;

                    string content = File.ReadAllText(_anchorFilePath).Trim();
                    string[] parts = content.Split('|');
                    if (parts.Length != 2) return;

                    if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var storedTime))
                        return;

                    if (!long.TryParse(parts[1], out long storedTick))
                        return;

                    // Sanity check: stored time should be reasonable (between 2024 and 2100)
                    if (storedTime.Year < 2024 || storedTime.Year > 2100) return;

                    // Sanity check: TickCount64 should have moved forward since the anchor was saved
                    // M-12 FIX: Also detect reboot — if storedTick is unreasonably far from current TickCount64,
                    // the anchor is stale (machine likely rebooted since it was saved)
                    if (Environment.TickCount64 < storedTick || Math.Abs(Environment.TickCount64 - storedTick) > 7 * 24 * 3600 * 1000L)
                        return;

                    _anchorNtpTime = storedTime;
                    _anchorTickCount = storedTick;
                    _anchorLoaded = true;
                    Logger.LogAction("CLOCK", "Loaded persisted NTP anchor");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOCK", $"Failed to load NTP anchor: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Queries universal HTTPS time endpoints (Port 443 — works across all firewalls and mobile hotspots).
        /// Returns calibrated UTC time and source name, with RTT network transit compensation.
        /// </summary>
        private static async Task<(DateTimeOffset? time, string source)> QueryUniversalHttpTimeAsync()
        {
            // 1. Cloudflare Anycast CDN Trace (ts=1725000000.123) — Ultra fast, anycast, millisecond precision
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string trace = await _httpClient.GetStringAsync("https://1.1.1.1/cdn-cgi/trace");
                sw.Stop();
                foreach (var line in trace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("ts=", StringComparison.OrdinalIgnoreCase))
                    {
                        string tsStr = line.Substring(3).Trim();
                        if (double.TryParse(tsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                        {
                            var cfTime = DateTimeOffset.UnixEpoch.AddSeconds(sec).AddMilliseconds(sw.ElapsedMilliseconds / 2.0);
                            if (cfTime.Year >= 2024 && cfTime.Year <= 2100)
                                return (cfTime, "Cloudflare Anycast (1.1.1.1)");
                        }
                    }
                }
            }
            catch { }

            // 2. WorldTimeAPI (JSON)
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string json = await _httpClient.GetStringAsync("https://worldtimeapi.org/api/timezone/Etc/UTC");
                sw.Stop();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("unixtime", out var utProp) && utProp.TryGetInt64(out long unixSec))
                {
                    var wtTime = DateTimeOffset.UnixEpoch.AddSeconds(unixSec).AddMilliseconds(sw.ElapsedMilliseconds / 2.0);
                    if (wtTime.Year >= 2024 && wtTime.Year <= 2100)
                        return (wtTime, "WorldTimeAPI");
                }
            }
            catch { }

            // 3. TimeAPI.io (JSON)
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string json = await _httpClient.GetStringAsync("https://timeapi.io/api/time/current/zone?timeZone=UTC");
                sw.Stop();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("dateTime", out var dtProp))
                {
                    string dtStr = dtProp.GetString();
                    if (!string.IsNullOrEmpty(dtStr) && DateTimeOffset.TryParse(dtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timeApiTime))
                    {
                        var adjusted = timeApiTime.AddMilliseconds(sw.ElapsedMilliseconds / 2.0);
                        if (adjusted.Year >= 2024 && adjusted.Year <= 2100)
                            return (adjusted, "TimeAPI.io");
                    }
                }
            }
            catch { }

            // 4. HTTP HEAD Date header from major reliable CDNs
            string[] headTargets = new[] { "https://www.google.com", "https://www.cloudflare.com", "https://www.microsoft.com" };
            foreach (var target in headTargets)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var req = new HttpRequestMessage(HttpMethod.Head, target);
                    using var resp = await _httpClient.SendAsync(req);
                    sw.Stop();
                    if (resp.Headers.Date.HasValue)
                    {
                        var headTime = resp.Headers.Date.Value.AddMilliseconds(sw.ElapsedMilliseconds / 2.0);
                        if (headTime.Year >= 2024 && headTime.Year <= 2100)
                            return (headTime, $"HTTP Date ({target})");
                    }
                }
                catch { }
            }

            return (null, "None");
        }

        private static async Task<DateTimeOffset?> QueryNtpAsync(string server)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(server);
                if (addresses.Length == 0) return null;

                IPAddress addr = addresses[0];
                foreach (var a in addresses)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
                }

                byte[] ntpData = new byte[48];
                ntpData[0] = 0x1B; // LI=0, VN=3, Mode=3 (client)

                using var socket = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.ReceiveTimeout = 3000;
                socket.SendTimeout = 3000;

                await Task.Run(() =>
                {
                    socket.Connect(new IPEndPoint(addr, 123));
                    socket.Send(ntpData);
                    socket.Receive(ntpData);
                });

                ulong intPart = (ulong)ntpData[40] << 24 | (ulong)ntpData[41] << 16 |
                                (ulong)ntpData[42] << 8 | (ulong)ntpData[43];
                ulong fracPart = (ulong)ntpData[44] << 24 | (ulong)ntpData[45] << 16 |
                                 (ulong)ntpData[46] << 8 | (ulong)ntpData[47];

                const ulong NTP_EPOCH_OFFSET = 2208988800UL;
                double seconds = intPart - NTP_EPOCH_OFFSET + (fracPart / (double)uint.MaxValue);
                var ntpTime = DateTimeOffset.UnixEpoch.AddSeconds(seconds);

                if (ntpTime.Year < 2024 || ntpTime.Year > 2100) return null;
                return ntpTime;
            }
            catch { return null; }
        }
    }
}
