using System;
using System.Globalization;
using System.IO;
using System.Net;
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

        /// <summary>NTP servers to try in order until one succeeds.</summary>
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

        /// <summary>True if NTP sync completed successfully.</summary>
        public static bool IsSynced => _synced;

        /// <summary>True if NTP synced OR a valid persisted anchor exists (time can be trusted).</summary>
        public static bool IsTimeTrusted => _synced || _anchorLoaded;

        /// <summary>
        /// Returns the best available UTC time with a trust indicator.
        /// - If NTP synced: returns NTP-corrected time (trusted).
        /// - If not synced but a persisted anchor exists: computes elapsed time from
        ///   TickCount64 delta (cannot be manipulated by changing OS clock) and adds
        ///   it to the stored NTP time (trusted).
        /// - If neither: returns OS clock (untrusted).
        /// </summary>
        public static (DateTimeOffset time, bool isTrusted) GetTrustedUtcNow()
        {
            // Best case: live NTP sync
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

            // No trusted source available
            return (DateTimeOffset.UtcNow, false);
        }

        /// <summary>
        /// One-shot sync at startup. Tries multiple NTP servers in order.
        /// If OS clock is off by more than 5 seconds, stores the offset.
        /// On success, persists an anchor file for future sessions.
        /// </summary>
        public static async Task InitializeAsync()
        {
            // Try to load persisted anchor first (available immediately if NTP fails)
            LoadAnchor();

            try
            {
                DateTimeOffset? ntpTime = null;

                // Try each NTP server until one succeeds
                foreach (var server in NtpServers)
                {
                    ntpTime = await QueryNtpAsync(server);
                    if (ntpTime.HasValue)
                    {
                        Logger.LogAction("CLOCK", $"NTP synced via {server}");
                        break;
                    }
                }

                if (!ntpTime.HasValue)
                {
                    Logger.LogAction("CLOCK", "All NTP servers failed — " +
                        (_anchorLoaded ? "using persisted anchor" : "using OS clock"));
                    // Still start periodic re-sync — NTP might come online later
                    StartPeriodicResync();
                    return;
                }

                _offset = ntpTime.Value - DateTimeOffset.UtcNow;
                _synced = true;

                if (Math.Abs(_offset.TotalSeconds) > 5)
                {
                    _driftDetected = true;
                    Logger.LogAction("CLOCK", $"⚠️ OS clock is off by {_offset.TotalSeconds:F1}s — using NTP-corrected time");
                }
                else
                {
                    Logger.LogAction("CLOCK", $"✅ OS clock is accurate (drift: {_offset.TotalMilliseconds:F0}ms)");
                }

                // Persist the anchor for future sessions
                PersistAnchor(ntpTime.Value);
                // Start periodic re-sync to detect clock drift over long uptimes
                StartPeriodicResync();
            }
            catch (Exception ex)
            {
                Logger.LogAction("CLOCK", $"NTP failed: {ex.Message} — " +
                    (_anchorLoaded ? "using persisted anchor" : "using OS clock"));
            }
        }

        /// <summary>
        /// Starts a background timer to re-sync NTP every 6 hours.
        /// Detects and logs clock drift over long uptimes.
        /// </summary>
        private static System.Timers.Timer? _resyncTimer;
        public static void StartPeriodicResync()
        {
            _resyncTimer?.Stop();
            _resyncTimer?.Dispose();
            _resyncTimer = new System.Timers.Timer(6 * 60 * 60 * 1000); // 6 hours
            _resyncTimer.Elapsed += async (s, e) =>
            {
                try
                {
                    var previousOffset = _offset;
                    DateTimeOffset? ntpTime = null;
                    foreach (var server in NtpServers)
                    {
                        ntpTime = await QueryNtpAsync(server);
                        if (ntpTime.HasValue) break;
                    }
                    if (ntpTime.HasValue)
                    {
                        _offset = ntpTime.Value - DateTimeOffset.UtcNow;
                        _synced = true;
                        double driftSinceLast = Math.Abs((_offset - previousOffset).TotalSeconds);
                        if (driftSinceLast > 5)
                        {
                            _driftDetected = true;
                            Logger.LogAction("CLOCK", $"⚠️ Periodic re-sync: drift of {driftSinceLast:F1}s detected since last sync");
                        }
                        else
                        {
                            Logger.LogAction("CLOCK", $"✅ Periodic re-sync OK (drift: {_offset.TotalMilliseconds:F0}ms)");
                        }
                        PersistAnchor(ntpTime.Value);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOCK", $"Periodic re-sync failed: {ex.Message}");
                }
            };
            _resyncTimer.AutoReset = true;
            _resyncTimer.Start();
            Logger.LogAction("CLOCK", "Periodic NTP re-sync started (every 6 hours)");
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
