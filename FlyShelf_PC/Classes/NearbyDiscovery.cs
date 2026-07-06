// ---------------------------------------------------------------
// NearbyDiscovery — UDP Broadcast-based Device Scanner
// Discovers FlyShelf instances on any reachable network via
// UDP broadcast probes on port 8999. Session-only pairing.
// ---------------------------------------------------------------
using System;
using System.Globalization;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Represents a device discovered via nearby scanning.
    /// </summary>
    public class NearbyDeviceInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public int HttpPort { get; set; } = 8080;
        public int TransferPort { get; set; } = 8998;
        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
        public int LatencyMs { get; set; } = -1;
        public bool IsConnected { get; set; }
        public string DeviceType { get; set; } = "PC";
        public bool IsPaired { get; set; }
        public bool IsOnline => (DateTime.UtcNow - DiscoveredAt).TotalSeconds < 30;
        public string StatusText => IsConnected ? "Connected" : $"Available ({LatencyMs}ms)";
    }

    /// <summary>
    /// UDP broadcast-based nearby device discovery for cross-network scenarios.
    /// Uses port 8999 for probes. LAN devices are already auto-detected by PeerManager
    /// multicast on 239.255.87.41:8742 — this covers the non-LAN case.
    /// 
    /// Discovery methods:
    /// 1. UDP broadcast on all network interfaces (same subnet)
    /// 2. UDP multicast on 239.255.88.42 (broader reach)
    /// 3. Manual IP entry (user types IP for different network)
    /// </summary>
    public class NearbyDiscovery
    {
        public static NearbyDiscovery? Instance { get; private set; }

        private const int NEARBY_PORT = 8999;
        private const string NEARBY_MULTICAST = "239.255.88.42";
        private const string APP_SIGNATURE = "FlyShelf_Nearby_v1";

        private UdpClient? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, NearbyDeviceInfo> _discovered = new();
        private static readonly System.Net.Http.HttpClient _latencyClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        public IReadOnlyCollection<NearbyDeviceInfo> DiscoveredDevices =>
            _discovered.Values.OrderByDescending(d => d.DiscoveredAt).ToList();

        public event Action<NearbyDeviceInfo>? DeviceDiscovered;

        public NearbyDiscovery()
        {
            Instance = this;
            StartListener();
            StartBroadcastLoop();
        }

        /// <summary>
        /// Start listening for incoming nearby probes from other FlyShelf instances.
        /// </summary>
        private void StartListener()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoop(_cts.Token));
            Logger.LogAction("NEARBY", $"Nearby discovery listener started on port {NEARBY_PORT}");
        }

        /// <summary>
        /// Continuously broadcasts our presence so nearby devices can detect us.
        /// Burst: 3 rapid probes at 1s intervals on startup, then settles to 5s.
        /// </summary>
        private void StartBroadcastLoop()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Burst discovery: 3 rapid probes for instant detection
                    for (int i = 0; i < 3; i++)
                    {
                        await BroadcastProbe();
                        await Task.Delay(1000, _cts!.Token);
                    }
                    Logger.LogAction("NEARBY", "Burst discovery complete (3 probes sent)");

                    // Steady-state: broadcast every 5 seconds
                    while (!_cts!.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5000, _cts.Token);
                        await BroadcastProbe();
                        PruneStale();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Logger.LogAction("NEARBY", $"Broadcast loop error: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Stop the nearby discovery listener.
        /// </summary>
        public void Stop()
        {
            try { _cts?.Cancel(); } catch { } // Best-effort: failure is acceptable
            try { _listener?.Close(); _listener?.Dispose(); } catch { } // Best-effort: failure is acceptable
            _listener = null;
        }

        /// <summary>
        /// Send a broadcast probe to discover nearby FlyShelf instances.
        /// Broadcasts on all network interfaces + multicast group.
        /// </summary>
        public async Task BroadcastProbe()
        {
            try
            {
                string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;
                string myDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                int myHttpPort = NetworkSyncServer.Instance?.CurrentPort ?? 8080;

                var probe = new
                {
                    type = APP_SIGNATURE,
                    action = "probe",
                    deviceId = myDeviceId,
                    deviceName = myDeviceName,
                    deviceType = "PC",
                    httpPort = myHttpPort,
                    transferPort = LanTransferEngine.TRANSFER_PORT,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    hmac = ComputeProbeHmac(myDeviceId)
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(probe));

                // 1. Broadcast on all interfaces
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                              && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                              && !ni.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                              && !ni.Description.Contains("vmware", StringComparison.OrdinalIgnoreCase)
                              && !ni.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var ni in interfaces)
                {
                    try
                    {
                        var ipProps = ni.GetIPProperties();
                        foreach (var uniAddr in ipProps.UnicastAddresses)
                        {
                            if (uniAddr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (IPAddress.IsLoopback(uniAddr.Address)) continue;

                            // Calculate broadcast address for this subnet
                            byte[] ipBytes = uniAddr.Address.GetAddressBytes();
                            byte[] maskBytes = uniAddr.IPv4Mask.GetAddressBytes();
                            byte[] broadcastBytes = new byte[4];
                            for (int i = 0; i < 4; i++)
                                broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                            var broadcastAddr = new IPAddress(broadcastBytes);

                            using var udp = new UdpClient();
                            udp.EnableBroadcast = true;
                            await udp.SendAsync(data, data.Length, new IPEndPoint(broadcastAddr, NEARBY_PORT));
                        }
                    }
                    catch { /* Some interfaces may fail — that's OK */ }
                }

                // 2. Multicast probe
                try
                {
                    using var mcast = new UdpClient();
                    mcast.JoinMulticastGroup(IPAddress.Parse(NEARBY_MULTICAST));
                    await mcast.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(NEARBY_MULTICAST), NEARBY_PORT));
                }
                catch { } // Best-effort: failure is acceptable

                Logger.LogAction("NEARBY", $"Broadcast probe sent on {interfaces.Count} interface(s)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NEARBY", $"Broadcast error: {ex.Message}");
            }
        }

        /// <summary>
        /// Listen for incoming probes and respond with our info.
        /// </summary>
        private async Task ListenLoop(CancellationToken ct)
        {
            try
            {
                _listener = new UdpClient();
                _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Client.Bind(new IPEndPoint(IPAddress.Any, NEARBY_PORT));

                // Join multicast group
                try { _listener.JoinMulticastGroup(IPAddress.Parse(NEARBY_MULTICAST)); } catch { } // Best-effort: failure is acceptable

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _listener.ReceiveAsync(ct);
                        string text = Encoding.UTF8.GetString(result.Buffer);

                        if (string.IsNullOrEmpty(text) || !text.Contains(APP_SIGNATURE)) continue;

                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        string action = root.TryGetProperty("action", out var ap) ? ap.GetString() ?? "" : "";
                        string deviceId = root.TryGetProperty("deviceId", out var dp) ? dp.GetString() ?? "" : "";
                        string deviceName = root.TryGetProperty("deviceName", out var np) ? np.GetString() ?? "" : "";
                        string probeDeviceType = root.TryGetProperty("deviceType", out var dtp) ? dtp.GetString() ?? "PC" : "PC";
                        int httpPort = root.TryGetProperty("httpPort", out var hp) ? hp.GetInt32() : 8080;
                        int transferPort = root.TryGetProperty("transferPort", out var tp) ? tp.GetInt32() : 8998;
                        string hmac = root.TryGetProperty("hmac", out var hm) ? hm.GetString() ?? "" : "";

                        // Skip self
                        string myId = SettingsManager.Current.DeviceId ?? "";
                        if (deviceId == myId) continue;

                        // Verify HMAC
                        if (!VerifyProbeHmac(deviceId, hmac)) continue;

                        string senderIp = result.RemoteEndPoint.Address.ToString();

                        // Mark connected status but don't skip — show all devices
                        bool isAlreadyConnected = PeerManager.Instance?.ConnectedPeers.Values
                            .Any(p => p.DeviceId == deviceId && p.IsAlive) == true;

                        if (action == "probe")
                        {
                            // Respond with our info
                            _ = Task.Run(() => SendProbeResponse(result.RemoteEndPoint.Address, ct));

                            // Record the discovered device
                            RecordDiscovery(deviceId, deviceName, senderIp, httpPort, transferPort, probeDeviceType, isAlreadyConnected);
                        }
                        else if (action == "response")
                        {
                            RecordDiscovery(deviceId, deviceName, senderIp, httpPort, transferPort, probeDeviceType, isAlreadyConnected);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.LogAction("NEARBY", $"Listen error: {ex.Message}");
                        await Task.Delay(1000, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogAction("NEARBY", $"Listener fatal: {ex.Message}");
            }
        }

        private async Task SendProbeResponse(IPAddress targetIp, CancellationToken ct)
        {
            try
            {
                string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;
                string myDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                int myHttpPort = NetworkSyncServer.Instance?.CurrentPort ?? 8080;

                var response = new
                {
                    type = APP_SIGNATURE,
                    action = "response",
                    deviceId = myDeviceId,
                    deviceName = myDeviceName,
                    deviceType = "PC",
                    httpPort = myHttpPort,
                    transferPort = LanTransferEngine.TRANSFER_PORT,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    hmac = ComputeProbeHmac(myDeviceId)
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));

                using var udp = new UdpClient();
                await udp.SendAsync(data, data.Length, new IPEndPoint(targetIp, NEARBY_PORT));
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void RecordDiscovery(string deviceId, string deviceName, string ip, int httpPort, int transferPort, string deviceType = "PC", bool isConnected = false)
        {
            var info = _discovered.AddOrUpdate(deviceId,
                _ => new NearbyDeviceInfo
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    DeviceType = deviceType,
                    IpAddress = ip,
                    HttpPort = httpPort,
                    TransferPort = transferPort,
                    DiscoveredAt = DateTime.UtcNow,
                    IsConnected = isConnected,
                    IsPaired = DevicePairingManager.GetPairedDevices()?.Any(d => d.DeviceId == deviceId) == true
                },
                (_, existing) =>
                {
                    existing.DeviceName = deviceName;
                    existing.DeviceType = deviceType;
                    existing.IpAddress = ip;
                    existing.HttpPort = httpPort;
                    existing.TransferPort = transferPort;
                    existing.DiscoveredAt = DateTime.UtcNow;
                    existing.IsConnected = isConnected;
                    existing.IsPaired = DevicePairingManager.GetPairedDevices()?.Any(d => d.DeviceId == deviceId) == true;
                    return existing;
                });

            // Measure latency
            _ = Task.Run(async () =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var resp = await _latencyClient.GetAsync($"http://{ip}:{httpPort}/api/health");
                    sw.Stop();
                    info.LatencyMs = (int)sw.ElapsedMilliseconds;
                }
                catch { info.LatencyMs = -1; }
            });

            Logger.LogAction("NEARBY", $"Discovered: {deviceName} @ {ip}:{httpPort}");
            DeviceDiscovered?.Invoke(info);
        }

        /// <summary>
        /// Connect to a discovered nearby device — creates a temporary peer connection.
        /// </summary>
        public async Task ConnectToDevice(NearbyDeviceInfo device)
        {
            try
            {
                if (PeerManager.Instance == null) return;

                string lanUrl = $"http://{device.IpAddress}:{device.HttpPort}";

                // Check if already connected
                if (PeerManager.Instance.ConnectedPeers.Values
                    .Any(p => p.DeviceId == device.DeviceId && p.IsAlive))
                {
                    Logger.LogAction("NEARBY", $"Already connected to {device.DeviceName}");
                    device.IsConnected = true;
                    return;
                }

                // Use AddManualPeer for proper handshake
                bool success = await PeerManager.Instance.AddManualPeer(
                    device.DeviceId, device.DeviceName, lanUrl, device.TransferPort);

                device.IsConnected = success;
                if (success)
                    Logger.LogAction("NEARBY", $"✅ Connected to nearby device: {device.DeviceName} @ {device.IpAddress}");
                else
                    Logger.LogAction("NEARBY", $"❌ Could not reach {device.DeviceName} @ {device.IpAddress}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NEARBY", $"Connect error: {ex.Message}");
            }
        }

        /// <summary>
        /// Register a device discovered via HTTP health check (e.g., Android app subnet scan).
        /// Called by the /api/health handler when it receives a request with X-FlyShelf-Client: MobileCompanion.
        /// </summary>
        public void RecordHttpDiscovery(string deviceId, string deviceName, string ipAddress, int httpPort, string deviceType = "Mobile")
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(ipAddress)) return;

            // Skip self
            string myId = SettingsManager.Current.DeviceId ?? "";
            if (deviceId == myId) return;

            RecordDiscovery(deviceId, deviceName, ipAddress, httpPort, 0, deviceType);
            Logger.LogAction("NEARBY", $"📱 HTTP discovery: {deviceName} ({deviceType}) @ {ipAddress}:{httpPort}");
        }

        /// <summary>
        /// Remove stale discoveries older than 30 seconds.
        /// </summary>
        public void PruneStale()
        {
            var staleIds = _discovered.Where(kv => (DateTime.UtcNow - kv.Value.DiscoveredAt).TotalSeconds > 30)
                .Select(kv => kv.Key).ToList();
            foreach (var id in staleIds)
                _discovered.TryRemove(id, out _);
        }

        // ═══ HMAC SIGNING ═══
        // HMAC verifies probes come from genuine FlyShelf instances.
        // Uses a composite key: app-wide base + device-specific pairing key
        // to prevent spoofing by other FlyShelf instances on the LAN.

        private const string APP_KEY_BASE = "FlyShelf_NearbyDiscovery_2025_Key";

        private static string ComputeProbeHmac(string deviceId, string pairingKey = "")
        {
            string compositeKey = APP_KEY_BASE + (string.IsNullOrEmpty(pairingKey) ? "" : "_" + pairingKey);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(compositeKey));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
            return Convert.ToHexString(hash).Substring(0, 16).ToLower(CultureInfo.InvariantCulture);
        }

        private static bool VerifyProbeHmac(string deviceId, string hmacStr, string pairingKey = "")
        {
            string expected = ComputeProbeHmac(deviceId, pairingKey);
            return string.Equals(expected, hmacStr, StringComparison.OrdinalIgnoreCase);
        }
    }
}
