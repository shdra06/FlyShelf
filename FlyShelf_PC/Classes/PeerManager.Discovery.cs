// ---------------------------------------------------------------
// PeerManager — Offline Zero-Config UDP Multicast Discovery
// Broadcasts presence and listens for other instances locally.
// Split from PeerManager.cs for modularity.
// ---------------------------------------------------------------
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public partial class PeerManager
    {
        private const string MULTICAST_GROUP_IP = "239.255.87.41";
        private const int MULTICAST_PORT = 8742;
        
        private UdpClient? _udpListener;
        private CancellationTokenSource? _udpCts;

        /// <summary>
        /// Start UDP Multicast Broadcaster and Listener loops.
        /// </summary>
        private void StartUdpDiscovery()
        {
            if (!SettingsManager.Current.EnableLocalLAN)
            {
                Logger.LogAction("PEER_UDP", "Local LAN sync is disabled. UDP discovery inactive.");
                return;
            }

            _udpCts = new CancellationTokenSource();
            
            // 1. Start the Multicast Listener
            _ = Task.Run(() => ListenMulticastLoop(_udpCts.Token));

            // 2. Start the Broadcaster Loop
            _ = Task.Run(() => BroadcastMulticastLoop(_udpCts.Token));

            Logger.LogAction("PEER_UDP", $"Zero-config local discovery active [group={MULTICAST_GROUP_IP}, port={MULTICAST_PORT}]");
        }

        /// <summary>
        /// Stop UDP Multicast listeners and broadcasters.
        /// </summary>
        private void StopUdpDiscovery()
        {
            try
            {
                _udpCts?.Cancel();
            }
            catch { }

            try
            {
                _udpListener?.Close();
                _udpListener?.Dispose();
            }
            catch { }
            
            _udpListener = null;
            Logger.LogAction("PEER_UDP", "UDP local discovery stopped.");
        }

        /// <summary>
        /// Broadcasts our local connection info to the multicast group periodically.
        /// </summary>
        private async Task BroadcastMulticastLoop(CancellationToken ct)
        {
            var multicastGroup = IPAddress.Parse(MULTICAST_GROUP_IP);
            var targetEp = new IPEndPoint(multicastGroup, MULTICAST_PORT);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Delay at start and between loops (10s intervals for healthy balance of speed vs network traffic)
                    await Task.Delay(10000, ct);

                    if (!SettingsManager.Current.EnableLocalLAN) continue;

                    string pairingKey = DevicePairingManager.EnsurePairingKey();
                    if (string.IsNullOrEmpty(pairingKey)) continue;

                    string localUrl = CloudDiscoveryManager.CachedLocalUrl;
                    if (string.IsNullOrEmpty(localUrl))
                    {
                        // Fall back to server DisplayUrl or dynamic IP bind check
                        localUrl = NetworkSyncServer.Instance?.DisplayUrl ?? "";
                    }

                    if (string.IsNullOrEmpty(localUrl) || localUrl.Equals("Not Running", StringComparison.OrdinalIgnoreCase))
                        continue;

                    long timestamp = NetworkClock.UtcNowMs;
                    string sig = GenerateDiscoverySignature(_myDeviceId, timestamp, localUrl, pairingKey);

                    var packet = new UdpDiscoveryPacket
                    {
                        DeviceId = _myDeviceId,
                        DeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                        LocalUrl = localUrl,
                        Timestamp = timestamp,
                        Sig = sig
                    };

                    byte[] dataBytes = JsonSerializer.SerializeToUtf8Bytes(packet);

                    // Multi-NIC: Broadcast presence out of every active IPv4 network interface explicitly.
                    // This forces Windows to route the UDP multicast frame across Wi-Fi, Ethernet, and local bridges.
                    var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var ni in interfaces)
                    {
                        if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            ni.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4))
                        {
                            var props = ni.GetIPProperties();
                            var unicast = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                            if (unicast != null && !IPAddress.IsLoopback(unicast.Address))
                            {
                                try
                                {
                                    using (var sender = new UdpClient(new IPEndPoint(unicast.Address, 0)))
                                    {
                                        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                                        await sender.SendAsync(dataBytes, dataBytes.Length, targetEp);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER_UDP_WARN", $"Broadcast failed: {ex.Message}");
                    try { await Task.Delay(5000, ct); } catch { break; }
                }
            }
        }

        /// <summary>
        /// Listens to local network UDP broadcasts, parses peer connection URLs and auto-connects.
        /// </summary>
        private async Task ListenMulticastLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _udpListener = new UdpClient();
                    _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpListener.ExclusiveAddressUse = false;
                    
                    var localEp = new IPEndPoint(IPAddress.Any, MULTICAST_PORT);
                    _udpListener.Client.Bind(localEp);

                    var multicastGroup = IPAddress.Parse(MULTICAST_GROUP_IP);
                    
                    // Multi-NIC: Subscribe to the multicast group on every operational IPv4 interface.
                    // This prevents virtual adapters (WSL, Docker, Hyper-V) from blocking physical Wi-Fi/Ethernet discovery.
                    var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                    int joinedInterfacesCount = 0;
                    foreach (var ni in interfaces)
                    {
                        if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            ni.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4))
                        {
                            var props = ni.GetIPProperties();
                            if (props.MulticastAddresses.Count == 0) continue;

                            var ipv4Info = props.GetIPv4Properties();
                            if (ipv4Info == null) continue;

                            var unicast = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                            if (unicast != null)
                            {
                                try
                                {
                                    _udpListener.JoinMulticastGroup(multicastGroup, unicast.Address);
                                    joinedInterfacesCount++;
                                }
                                catch { }
                            }
                        }
                    }

                    Logger.LogAction("PEER_UDP", $"UDP multicast listener bound successfully on {joinedInterfacesCount} interface(s).");

                    while (!ct.IsCancellationRequested)
                    {
                        var result = await _udpListener.ReceiveAsync(ct);
                        if (result.Buffer == null || result.Buffer.Length == 0) continue;

                        try
                        {
                            var packet = JsonSerializer.Deserialize<UdpDiscoveryPacket>(result.Buffer);
                            if (packet == null) continue;

                            // 1. Skip self-echoes
                            if (packet.DeviceId == _myDeviceId) continue;

                            string pairingKey = DevicePairingManager.EnsurePairingKey();
                            if (string.IsNullOrEmpty(pairingKey)) continue;

                            // 2. Verify cryptographically (HMAC-SHA256 validation + 30s sliding time window replay protection)
                            string senderIp = result.RemoteEndPoint?.Address?.ToString() ?? "LAN";
                            bool verified = VerifyDiscoverySignature(packet.DeviceId, packet.Timestamp, packet.LocalUrl, packet.Sig, pairingKey);

                            if (!verified)
                            {
                                // Malicious or outdated discovery packet — silently drop to prevent DoS/spoofing
                                continue;
                            }

                            // 3. Auto-pair the device locally if not already registered
                            // Guard: skip UDP broadcasts with empty DeviceId or DeviceName
                            if (string.IsNullOrWhiteSpace(packet.DeviceId) || string.IsNullOrWhiteSpace(packet.DeviceName)) continue;

                            bool isPaired = DevicePairingManager.IsDevicePaired(pairingKey);
                            if (isPaired)
                            {
                                var pairedList = DevicePairingManager.GetPairedDevices();
                                if (!pairedList.Any(d => d.DeviceId == packet.DeviceId))
                                {
                                    // Fix #1B: Don't auto-re-register devices that were recently unpaired
                                    if (DevicePairingManager.IsRecentlyUnpaired(packet.DeviceId))
                                    {
                                        Logger.LogAction("PEER_UDP", $"⛔ Blocked auto-re-registration of recently unpaired device: {packet.DeviceName} ({packet.DeviceId})");
                                    }
                                    else
                                    {
                                        DevicePairingManager.TryPairDevice(pairingKey, packet.DeviceId, packet.DeviceName, "PC", senderIp);
                                        Logger.LogAction("PEER_UDP", $"✅ Auto-registered device offline from validated broadcast: {packet.DeviceName}");
                                    }
                                }
                            }

                            // 4. Update url properties or insert new peer
                            if (_peers.TryGetValue(packet.DeviceId, out var existing))
                            {
                                if (!string.IsNullOrEmpty(packet.LocalUrl) && existing.LanUrl != packet.LocalUrl)
                                {
                                    Logger.LogAction("PEER_UDP", $"🔎 LAN URL for {packet.DeviceName} changed: {packet.LocalUrl}");
                                    existing.LanUrl = packet.LocalUrl;
                                    SaveUrlCache();
                                }

                                if (!existing.IsAlive)
                                {
                                    Logger.LogAction("PEER_UDP", $"⚡ Reconnecting to {packet.DeviceName} offline...");
                                    _ = Task.Run(() => Handshake(existing));
                                }
                            }
                            else
                            {
                                Logger.LogAction("PEER_UDP", $"⚡ Discovered NEW offline peer: {packet.DeviceName} at {packet.LocalUrl}");
                                var newPeer = new PeerConnection
                                {
                                    DeviceId = packet.DeviceId,
                                    DeviceName = packet.DeviceName,
                                    LanUrl = packet.LocalUrl,
                                    Transport = "offline",
                                    IsAlive = false
                                };
                                _peers[packet.DeviceId] = newPeer;
                                SaveUrlCache();

                                _ = Task.Run(() => Handshake(newPeer));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("PEER_UDP_WARN", $"Packet parse/process failure: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER_UDP_WARN", $"Listener socket failure: {ex.Message}. Re-binding in 10s...");
                    try
                    {
                        _udpListener?.Close();
                        _udpListener?.Dispose();
                    }
                    catch { }
                    _udpListener = null;

                    try { await Task.Delay(10000, ct); } catch { break; }
                }
            }
        }

        /// <summary>
        /// Generates an HMAC-SHA256 signature for zero-config discovery authentication.
        /// </summary>
        private string GenerateDiscoverySignature(string deviceId, long timestamp, string localUrl, string pairingKey)
        {
            string rawData = $"{deviceId}:{timestamp}:{localUrl}";
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pairingKey)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Verifies signature and enforces timestamp safety for packet validity.
        /// </summary>
        private bool VerifyDiscoverySignature(string deviceId, long timestamp, string localUrl, string sig, string pairingKey)
        {
            if (string.IsNullOrEmpty(pairingKey) || string.IsNullOrEmpty(sig)) return false;

            // Replay protection: packet must be signed within a 60-second window
            // (widened from 30s to tolerate clock skew on fresh Windows installs or post-sleep)
            long nowMs = NetworkClock.UtcNowMs;
            long skewMs = Math.Abs(nowMs - timestamp);
            if (skewMs > 60000)
            {
                Logger.LogAction("PEER_UDP_WARN", $"Rejected outdated packet from {deviceId} (skew: {skewMs / 1000.0}s)");
                return false;
            }
            if (skewMs > 30000)
            {
                Logger.LogAction("PEER_UDP_WARN", $"⚠️ High clock skew detected with {deviceId}: {skewMs / 1000.0}s — consider syncing system clocks");
            }

            string expected = GenerateDiscoverySignature(deviceId, timestamp, localUrl, pairingKey);
            return string.Equals(expected, sig, StringComparison.Ordinal);
        }

        /// <summary>
        /// Model representing the discovery packet transmitted over UDP Multicast.
        /// </summary>
        private class UdpDiscoveryPacket
        {
            public string DeviceId { get; set; } = "";
            public string DeviceName { get; set; } = "";
            public string LocalUrl { get; set; } = "";
            public long Timestamp { get; set; }
            public string Sig { get; set; } = "";
        }
    }
}
