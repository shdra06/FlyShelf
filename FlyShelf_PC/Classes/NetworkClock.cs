using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight internal clock. Single NTP query to time.google.com at startup.
    /// If OS clock is wrong, applies an offset. If OS clock later gets corrected, uses OS time.
    /// No timers, no periodic resync, no extra RAM.
    /// </summary>
    public static class NetworkClock
    {
        private static TimeSpan _offset = TimeSpan.Zero;
        private static bool _synced = false;
        private static bool _driftDetected = false;

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

        /// <summary>
        /// One-shot sync with time.google.com. Call once at startup.
        /// If OS clock is off by more than 5 seconds, stores the offset.
        /// </summary>
        public static async Task InitializeAsync()
        {
            try
            {
                var ntpTime = await QueryNtpAsync("time.google.com");
                if (!ntpTime.HasValue)
                {
                    Logger.LogAction("CLOCK", "NTP query failed — using OS clock");
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
            }
            catch (Exception ex)
            {
                Logger.LogAction("CLOCK", $"NTP failed: {ex.Message} — using OS clock");
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
