using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using FlyShelf.Windows;
using ZXing.Windows.Compatibility;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Data model returned when looking up a pairing code from Firebase.
    /// </summary>
    public class PairingCodeInfo
    {
        public string deviceId { get; set; } = "";
        public string deviceName { get; set; } = "";
        public string deviceType { get; set; } = "";
        public string pairingKey { get; set; } = "";
        public string localUrl { get; set; } = "";
        public string globalUrl { get; set; } = "";
        public string pin { get; set; } = "";
        public string encryptedData { get; set; } = "";
        public double timestamp { get; set; }
    }

    public class PairedDevice
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "Mobile"; // Mobile, PC, Browser
        public string PairingKey { get; set; } = "";
        public DateTime PairedAt { get; set; } = DateTime.Now;
        public DateTime LastSeen { get; set; } = DateTime.Now;
        public string LastKnownIP { get; set; } = "";
        public List<string> KnownLanIps { get; set; } = new();
    }

    public static partial class DevicePairingManager
    {
        private static readonly string _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyShelf", "paired_devices.json");

        private static List<PairedDevice> _pairedDevices = new();
        private static readonly object _lock = new();

        // ═══ Recently-unpaired tracking: prevents UDP auto-re-registration of unpaired devices ═══
        private static readonly HashSet<string> _recentlyUnpaired = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _unpairedLock = new();

        /// <summary>
        /// Returns true if the device was recently unpaired (prevents UDP auto-re-registration).
        /// </summary>
        public static bool IsRecentlyUnpaired(string deviceId)
        {
            lock (_unpairedLock) { return _recentlyUnpaired.Contains(deviceId); }
        }

        /// <summary>
        /// Returns true if the device is blocked (recently unpaired).
        /// Used to reject incoming text/file/WebSocket data from devices that
        /// still hold a valid pairing key but have been explicitly unpaired.
        /// </summary>
        public static bool IsDeviceBlocked(string? deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            lock (_unpairedLock) { return _recentlyUnpaired.Contains(deviceId); }
        }
        
        /// <summary>Fires whenever a device is successfully paired. UI can subscribe to auto-refresh.</summary>
        public static event Action<string> OnDevicePaired;

        /// <summary>
        /// Records recent activity/heartbeat from a device so UI displays live online status.
        /// </summary>
        public static void RecordDeviceActivity(string deviceId, string deviceName = "", string transport = "Cloud")
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_lock)
            {
                var match = _pairedDevices.FirstOrDefault(d => 
                    string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(deviceName) && string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)));
                if (match != null)
                {
                    match.LastSeen = DateTime.Now;
                }
            }
        }
        // AUDIT Task 5: Use shared pool instance instead of per-class HttpClient (prevents socket exhaustion)
        private static HttpClient _httpClient => HttpClientPool.Default;
        private static string FIREBASE_BASE => FirebaseAuthManager.FirebaseDatabaseUrl;

        /// <summary>Maximum number of paired devices allowed. Remove existing devices to pair new ones.</summary>
        public const int MAX_PAIRED_DEVICES = 10;
        
        /// <summary>Wraps a Firebase REST URL with auth token.</summary>
        private static async Task<string> AuthUrl(string path)
        {
            return await FirebaseAuthManager.AuthenticateUrl($"{FIREBASE_BASE}/{path}");
        }
        
        private static string _currentPairingCode = "";
        /// <summary>Current active pairing code for this device (displayed in UI).</summary>
        public static string CurrentPairingCode
        {
            get => _currentPairingCode;
            private set => _currentPairingCode = value;
        }

        /// <summary>
        /// Verify a pairing code over LAN — returns device info if the code matches the current active code.
        /// Used by the /api/pair_verify endpoint for Cloudflare-free LAN pairing.
        /// </summary>
        public static PairingCodeInfo? VerifyLocalPairingCode(string code)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(CurrentPairingCode)) return null;

            // Check code matches
            if (!string.Equals(code, CurrentPairingCode, StringComparison.OrdinalIgnoreCase)) return null;

            // Check code hasn't expired (10 min TTL)
            if ((DateTime.UtcNow - LastPairingCodeGeneratedAt).TotalMinutes > 10) return null;

            return new PairingCodeInfo
            {
                deviceId = SettingsManager.Current.DeviceId ?? "",
                deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                deviceType = "PC",
                pairingKey = EnsurePairingKey(),
                localUrl = CloudDiscoveryManager.CachedLocalUrl ?? "",
                globalUrl = CloudDiscoveryManager.CachedGlobalUrl ?? "",
                pin = SettingsManager.Current.WebClientPinToken ?? "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        /// <summary>
        /// Complete a LAN pairing — registers the remote device and invalidates the code.
        /// Called by /api/pair_complete when Android pairs over LAN.
        /// </summary>
        public static async Task<bool> CompleteLanPairing(string code, string remoteDeviceId, string remoteDeviceName, string remoteDeviceType, string remotePairingKey)
        {
            var capturedCode = System.Threading.Interlocked.Exchange(ref _currentPairingCode, "");
            if (string.IsNullOrEmpty(capturedCode) || !string.Equals(capturedCode, code, StringComparison.OrdinalIgnoreCase))
                return false;

            // Register the remote device
            TryPairDevice(remotePairingKey, remoteDeviceId, remoteDeviceName, remoteDeviceType, "lan");

            // Push our info to Firebase if available (best-effort for cloud backup)
            _ = Task.Run(async () =>
            {
                try
                {
                    await CloudDiscoveryManager.PushTunnelUrl(
                        CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                        true,
                        CloudDiscoveryManager.CachedLocalUrl ?? "");
                }
                catch { }
            });

            Logger.LogAction("PAIR LAN", $"\u2705 LAN pairing complete with {remoteDeviceName} ({remoteDeviceType})");

            // Trigger ForceResync to discover the new peer
            _ = Task.Run(async () =>
            {
                try { if (PeerManager.Instance != null) await PeerManager.Instance.ForceResync(); }
                catch { }
            });

            return true;
        }


        static DevicePairingManager()
        {
            Load();
        }

        /// <summary>
        /// Returns the current pairing key, or empty string if not yet paired.
        /// Does NOT auto-generate pairing key is only created when:
        /// 1) User generates a QR code / pairing code (first device creates the room)
        /// 2) User scans/enters a code from another device (joins existing room)
        /// </summary>
        public static string EnsurePairingKey()
        {
            string configKey = SettingsManager.Current.PairingKey ?? "";
            
            // Self-healing alignment: If we have paired devices, but the config key is different or empty,
            // we should adopt the paired devices' key so we stay in the same room!
            lock (_lock)
            {
                if (_pairedDevices != null && _pairedDevices.Count > 0)
                {
                    var firstDevice = _pairedDevices.FirstOrDefault(d => !string.IsNullOrEmpty(d.PairingKey));
                    if (firstDevice != null && configKey != firstDevice.PairingKey)
                    {
                        Logger.LogAction("PAIR", $"⚠️ Config key ({configKey}) mismatched with paired devices. Aligning to: {firstDevice.PairingKey}");
                        SettingsManager.Current.PairingKey = firstDevice.PairingKey;
                        SettingsManager.Save();
                        SyncCrypto.ClearKeyCache();
                        _ = CloudDiscoveryManager.RegisterRoomMembershipAsync(firstDevice.PairingKey);
                        return firstDevice.PairingKey;
                    }
                }
            }
            
            return configKey;
        }

        /// <summary>
        /// Whether this device has been paired (has a pairing key).
        /// Cloud sync ONLY works when this returns true.
        /// </summary>
        public static bool HasPairingKey => !string.IsNullOrEmpty(SettingsManager.Current.PairingKey);

        /// <summary>
        /// Creates a new pairing key if one doesn't exist yet. Called when
        /// this device is the FIRST in the pair (generating a QR/code for others to scan).
        /// </summary>
        public static string CreatePairingKeyIfNeeded()
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(SettingsManager.Current.PairingKey))
                {
                    SettingsManager.Current.PairingKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // 32-char hex
                    SettingsManager.Save();
                    SyncCrypto.ClearKeyCache();
                    Logger.LogAction("PAIRING", $"Generated new pairing key: {SettingsManager.Current.PairingKey.Substring(0, 8)}...");
                }
            }
            // Ensure room membership is registered in Firebase immediately
            _ = CloudDiscoveryManager.RegisterRoomMembershipAsync(SettingsManager.Current.PairingKey);
            return SettingsManager.Current.PairingKey;
        }

        // ═══ Direct LAN Pairing — No Firebase ═══

        /// <summary>
        /// Derives a shared secret for LAN pairing from a nonce and both device IDs.
        /// Both sides can independently compute the same secret.
        /// </summary>
        public static string DeriveLanPairingSecret(string nonce, string deviceIdA, string deviceIdB)
        {
            // Sort device IDs to ensure both sides get the same result regardless of who initiated
            string[] ids = new[] { deviceIdA, deviceIdB };
            Array.Sort(ids, StringComparer.Ordinal);
            string material = $"FlyShelf_LAN_v1:{nonce}:{ids[0]}:{ids[1]}";
            using var hmac = new System.Security.Cryptography.HMACSHA256(
                Encoding.UTF8.GetBytes("FlyShelf_LAN_Pairing_2025"));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Pairs a device via direct LAN handshake — completely offline, no Firebase.
        /// Stores the device in paired_devices.json with PairingSource = "LAN".
        /// </summary>
        public static void PairDeviceViaLan(string deviceId, string deviceName, string deviceType, string ipAddress, string sharedSecret)
        {
            lock (_lock)
            {
                // Check if already paired
                var existing = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (existing != null)
                {
                    // Update existing
                    existing.DeviceName = deviceName;
                    existing.LastKnownIP = ipAddress;
                    existing.LastSeen = DateTime.Now;
                    existing.PairingKey = sharedSecret;
                }
                else
                {
                    if (_pairedDevices.Count >= MAX_PAIRED_DEVICES)
                    {
                        Logger.LogAction("LAN_PAIR", $"Cannot pair — max {MAX_PAIRED_DEVICES} devices reached");
                        return;
                    }

                    _pairedDevices.Add(new PairedDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        DeviceType = deviceType,
                        PairingKey = sharedSecret,
                        PairedAt = DateTime.Now,
                        LastSeen = DateTime.Now,
                        LastKnownIP = ipAddress
                    });
                }

                Save();
            }

            Logger.LogAction("LAN_PAIR", $"✅ Device paired via LAN: {deviceName} ({deviceId}) @ {ipAddress}");
            OnDevicePaired?.Invoke(deviceId);

            // If we don't have a pairing key yet, create one so PeerManager discovery works
            if (string.IsNullOrEmpty(SettingsManager.Current.PairingKey))
            {
                SettingsManager.Current.PairingKey = sharedSecret;
                SettingsManager.Save();
                SyncCrypto.ClearKeyCache();
            }
        }

        /// <summary>
        /// Regenerate the pairing key (invalidates all previous QR codes).
        /// </summary>
        public static string RegeneratePairingKey()
        {
            string oldKey = SettingsManager.Current.PairingKey;
            SettingsManager.Current.PairingKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            SettingsManager.Save();
            SyncCrypto.ClearKeyCache();

            // SECURITY: Clean up all entries under the old pairing key to prevent data leakage.
            // Without this, old active_devices, clipboard, and members entries are orphaned forever.
            if (!string.IsNullOrEmpty(oldKey))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Delete our device entry from the old key scope
                        string myDeviceId = SettingsManager.Current.DeviceId;
                        if (!string.IsNullOrEmpty(myDeviceId))
                        {
                            string url = await AuthUrl($"active_devices/{oldKey}/{myDeviceId}.json");
                            await _httpClient.DeleteAsync(url);
                        }
                        // Remove our room membership from the old key
                        string uid = await FirebaseAuthManager.GetUidAsync();
                        if (!string.IsNullOrEmpty(uid))
                        {
                            string memberUrl = await AuthUrl($"members/{oldKey}/{uid}.json");
                            await _httpClient.DeleteAsync(memberUrl);
                        }
                        Logger.LogAction("PAIR", $"Cleaned up old pairing key scope: {oldKey.Substring(0, 8)}...");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("PAIR", $"Old key cleanup failed: {ex.Message}");
                    }
                });
            }

            // Ensure room membership for the new key immediately
            _ = CloudDiscoveryManager.RegisterRoomMembershipAsync(SettingsManager.Current.PairingKey);

            return SettingsManager.Current.PairingKey;
        }

        // ═══ QR Code Generation ═══

        /// <summary>
        /// Builds the JSON payload for the QR code containing all connection info.
        /// </summary>
        public static string BuildQRPayload(string localUrl, string globalUrl, string pin)
        {
            // This is when the PC becomes the "room creator" — generate key if needed
            string pairingKey = CreatePairingKeyIfNeeded();

            // Proactively ensure room membership and presence are registered in Firebase immediately
            _ = Task.Run(async () =>
            {
                try
                {
                    await CloudDiscoveryManager.RegisterRoomMembershipAsync(pairingKey);
                    await CloudDiscoveryManager.PushTunnelUrl(
                        globalUrl ?? CloudDiscoveryManager.CachedGlobalUrl ?? localUrl ?? "",
                        true,
                        localUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "");
                }
                catch { }
            });

            var payload = new
            {
                app = "FlyShelf",
                v = 1,
                key = pairingKey,
                local = localUrl ?? "",
                global = globalUrl ?? "",
                pin = pin ?? "",
                name = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                id = SettingsManager.Current.DeviceId ?? Environment.MachineName
            };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Generates a QR code BitmapSource from the pairing payload.
        /// </summary>
        public static BitmapSource GenerateQRCode(string localUrl, string globalUrl, string pin, int size = 250)
        {
            try
            {
                string payload = BuildQRPayload(localUrl, globalUrl, pin);
                Logger.LogAction("QR", $"Generating QR with payload: {payload.Substring(0, Math.Min(80, payload.Length))}...");

                var writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new EncodingOptions
                    {
                        Width = size,
                        Height = size,
                        Margin = 1,
                        PureBarcode = false
                    }
                };

                // ZXing.Net generates a System.Drawing.Bitmap
                using var bitmap = writer.Write(payload);

                // Convert System.Drawing.Bitmap Ã¢â€ â€™ WPF BitmapSource
                var bitmapData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                var bmpSource = BitmapSource.Create(
                    bitmapData.Width, bitmapData.Height,
                    96, 96,
                    PixelFormats.Bgra32,
                    null,
                    bitmapData.Scan0,
                    bitmapData.Stride * bitmapData.Height,
                    bitmapData.Stride);

                bitmap.UnlockBits(bitmapData);
                bmpSource.Freeze();
                return bmpSource;
            }
            catch (Exception ex)
            {
                Logger.LogAction("QR ERROR", $"Failed to generate QR: {ex.Message}");
                return null;
            }
        }

        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â Paired Device Management Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â

        public static List<PairedDevice> GetPairedDevices()
        {
            lock (_lock) return _pairedDevices.Select(d => new PairedDevice {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                DeviceType = d.DeviceType,
                PairingKey = d.PairingKey,
                PairedAt = d.PairedAt,
                LastSeen = d.LastSeen,
                LastKnownIP = d.LastKnownIP,
                KnownLanIps = d.KnownLanIps.ToList()
            }).ToList();
        }

        /// <summary>
        /// Validates a pairing key and registers the device if valid.
        /// Returns true if pairing succeeded.
        /// </summary>
        public static bool TryPairDevice(string pairingKey, string deviceId, string deviceName, string deviceType, string remoteIP)
        {
            // Guard: reject pairing attempts with empty/whitespace DeviceId or DeviceName
            // This prevents ghost entries from stale Firebase room presences being persisted locally
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceName))
            {
                Logger.LogAction("PAIR", $"❌ Rejected pairing attempt with empty/whitespace DeviceId='{deviceId}' or DeviceName='{deviceName}'");
                return false;
            }

            if (deviceId == (SettingsManager.Current.DeviceId ?? ""))
            {
                Logger.LogAction("PAIR", $"❌ Rejected pairing attempt from self (DeviceId='{deviceId}')");
                return false;
            }

            string expectedKey = EnsurePairingKey();
            if (pairingKey != expectedKey)
            {
                Logger.LogAction("PAIR", $"Ã¢ÂÅ’ Invalid pairing key from {deviceName} ({remoteIP})");
                return false;
            }

            lock (_lock)
            {
                // Update if already paired, otherwise add
                var existing = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (existing != null)
                {
                    existing.LastSeen = DateTime.Now;
                    existing.LastKnownIP = remoteIP;
                    existing.DeviceName = deviceName;
                    if (!string.IsNullOrEmpty(existing.LastKnownIP) && !existing.KnownLanIps.Contains(existing.LastKnownIP))
                    {
                        existing.KnownLanIps.Insert(0, existing.LastKnownIP);
                        if (existing.KnownLanIps.Count > 5) existing.KnownLanIps.RemoveAt(existing.KnownLanIps.Count - 1);
                    }
                    Logger.LogAction("PAIR", $"Ã°Å¸â€ â€ž Re-paired existing device: {deviceName}");
                }
                else
                {
                    // Enforce pairing limit
                    if (_pairedDevices.Count >= MAX_PAIRED_DEVICES)
                    {
                        Logger.LogAction("PAIR", $"⚠️ Pairing limit reached ({MAX_PAIRED_DEVICES} devices). Rejected: {deviceName}");
                        try
                        {
                            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                                ToastWindow.ShowToast($"⚠️ Pairing limit reached ({MAX_PAIRED_DEVICES} devices)\nRemove existing devices to add new ones."));
                        }
                        catch { } // Best-effort: failure is acceptable
                        return false;
                    }

                    var newDevice = new PairedDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        DeviceType = deviceType,
                        PairingKey = pairingKey,
                        PairedAt = DateTime.Now,
                        LastSeen = DateTime.Now,
                        LastKnownIP = remoteIP
                    };
                    if (!string.IsNullOrEmpty(remoteIP) && !newDevice.KnownLanIps.Contains(remoteIP))
                    {
                        newDevice.KnownLanIps.Insert(0, remoteIP);
                    }
                    _pairedDevices.Add(newDevice);
                    Logger.LogAction("PAIR", $"✅ New device paired: {deviceName} ({deviceType}) from {remoteIP}");
                }
            }

            // Auto-align and enable incoming/outgoing sync gates when pairing completes
            SettingsManager.Current.EnableIncomingSync = true;
            SettingsManager.Current.EnableOutgoingSync = true;
            SettingsManager.Save();

            Save();
            OnDevicePaired?.Invoke(deviceName);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (PeerManager.Instance != null)
                    {
                        if (deviceType == "Mobile" || string.Equals(remoteIP, "cloud", StringComparison.OrdinalIgnoreCase))
                        {
                            PeerManager.Instance.TouchMobilePeer(deviceId, deviceName, remoteIP, "LAN");
                        }
                        await PeerManager.Instance.ForceResync();
                    }
                }
                catch { }
            });

            return true;
        }

        /// <summary>
        /// Check if a device with this pairing key is trusted (bypass PIN).
        /// </summary>
        public static bool IsDevicePaired(string pairingKey)
        {
            if (string.IsNullOrEmpty(pairingKey)) return false;
            string expectedKey = EnsurePairingKey();
            if (pairingKey == expectedKey) return true;
            lock (_lock)
            {
                return _pairedDevices != null && _pairedDevices.Any(d => d.PairingKey == pairingKey);
            }
        }

        /// <summary>
        /// AUDIT FIX #1: Validate HMAC-based auth token (X-Auth-Token) with timestamp.
        /// Returns true if the HMAC matches any known pairing key within a 5-minute window.
        /// This prevents sending the raw pairing key over the wire.
        /// </summary>
        public static bool ValidateHmacAuth(string hmacToken, string timestampStr)
        {
            if (string.IsNullOrEmpty(hmacToken) || string.IsNullOrEmpty(timestampStr)) return false;
            if (!long.TryParse(timestampStr, out long timestamp)) return false;

            // Validate timestamp is within ±5 minutes to prevent replay attacks
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Math.Abs(nowMs - timestamp) > 300_000) return false;

            // Check against our own key
            string myKey = EnsurePairingKey();
            if (VerifyHmac(myKey, timestampStr, hmacToken)) return true;

            // Check against all paired device keys
            lock (_lock)
            {
                if (_pairedDevices != null)
                {
                    foreach (var dev in _pairedDevices)
                    {
                        if (VerifyHmac(dev.PairingKey, timestampStr, hmacToken)) return true;
                    }
                }
            }
            return false;
        }

        private static bool VerifyHmac(string key, string message, string expectedHmac)
        {
            try
            {
                using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(key));
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
                string computed = Convert.ToHexString(hash).ToLowerInvariant();
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(expectedHmac.ToLowerInvariant()));
            }
            catch { return false; }
        }

        /// <summary>
        /// Gets the pairing key for a given deviceId from the paired devices list.
        /// </summary>
        public static string GetPairingKeyForDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            lock (_lock)
            {
                if (_pairedDevices == null) return "";
                var dev = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                return dev?.PairingKey ?? "";
            }
        }

        /// <summary>
        /// Update the last-seen timestamp for a paired device.
        /// </summary>
        public static void TouchDevice(string deviceId, string remoteIP)
        {
            lock (_lock)
            {
                var device = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device != null)
                {
                    device.LastSeen = DateTime.Now;
                    device.LastKnownIP = remoteIP;
                }
            }
            Save();
        }

        public static void RemoveDevice(string deviceId)
        {
            string devName = "";
            lock (_lock)
            {
                var match = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId || d.DeviceName == deviceId);
                if (match != null) devName = match.DeviceName;
                _pairedDevices.RemoveAll(d => d.DeviceId == deviceId || d.DeviceName == deviceId);
            }
            Save();
            Logger.LogAction("PAIR", $"Removed device: {deviceId} ({devName})");

            // Fix #1A: Disconnect and remove the peer from PeerManager to prevent ghost connections
            try
            {
                PeerManager.Instance?.DisconnectPeer(deviceId);
            }
            catch { } // Best-effort: failure is acceptable

            // Fix #1B: Track recently-unpaired device to prevent UDP auto-re-registration
            lock (_unpairedLock)
            {
                _recentlyUnpaired.Add(deviceId);
                if (!string.IsNullOrEmpty(devName)) _recentlyUnpaired.Add(devName);
            }

            // SECURITY: Also delete the ghost entry from Firebase active_devices
            // Without this, unpaired devices persist in the Cloud topology indefinitely.
            _ = Task.Run(async () =>
            {
                try
                {
                    string pairingKey = SettingsManager.Current.PairingKey;
                    if (!string.IsNullOrEmpty(pairingKey))
                    {
                        string urlId = await AuthUrl($"active_devices/{pairingKey}/{deviceId}.json");
                        await _httpClient.DeleteAsync(urlId);
                        if (!string.IsNullOrEmpty(devName) && devName != deviceId)
                        {
                            string urlName = await AuthUrl($"active_devices/{pairingKey}/{devName}.json");
                            await _httpClient.DeleteAsync(urlName);
                        }
                        Logger.LogAction("PAIR", $"Deleted ghost entry from Firebase: active_devices/{pairingKey}/{deviceId}");
                    }
                    await CloudDiscoveryManager.CleanupStaleDevices();
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PAIR", $"Firebase ghost cleanup failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Update the last known IP for a paired device (called on successful connection).
        /// </summary>
        public static void UpdateDeviceIp(string deviceId, string ip)
        {
            lock (_lock)
            {
                var device = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device != null)
                {
                    device.LastKnownIP = ip;
                    device.LastSeen = DateTime.Now;
                    if (!device.KnownLanIps.Contains(ip))
                    {
                        device.KnownLanIps.Insert(0, ip);
                        if (device.KnownLanIps.Count > 5) device.KnownLanIps.RemoveAt(device.KnownLanIps.Count - 1);
                    }
                    Save();
                }
            }
        }

        // ═══ Short Pairing Code System ═══

        /// <summary>
        /// Generate a 6-character alphanumeric code using CSPRNG (no ambiguous chars like I/1/O/0).
        /// </summary>
        public static string GenerateShortCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var code = new char[6];
            for (int i = 0; i < 6; i++)
            {
                code[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
            }
            return new string(code);
        }

        /// <summary>
        /// Publish a pairing code to Firebase so remote devices can find us.
        /// Auto-expires after 5 minutes. Returns the generated code.
        /// </summary>
        public static async Task<string> PublishPairingCode()
        {
            LastPairingCodeGeneratedAt = DateTime.UtcNow;
            string code = GenerateShortCode();
            try
            {
                // ─── Clean up any previous code from this device ───
                // If the PC was killed before cleanup ran, stale codes stay in Firebase
                // and cause "Code Expired" on phones. Delete any previous code first.
                if (!string.IsNullOrEmpty(CurrentPairingCode))
                {
                    try
                    {
                        await _httpClient.DeleteAsync((await AuthUrl($"pairing_codes/{CurrentPairingCode}.json")));
                        Logger.LogAction("PAIR CODE", $"Cleaned up previous code: {CurrentPairingCode}");
                    }
                    catch (Exception ex) { Logger.LogAction("PAIR CODE", $"Failed to clean previous code: {ex.Message}"); }
                }

                // Also scan Firebase for any stale codes from this device ID and remove them
                try
                {
                    string myDeviceId = SettingsManager.Current.DeviceId ?? "";
                    string scanUrl = await FirebaseAuthManager.AuthenticateUrl($"{FIREBASE_BASE}/pairing_codes.json?orderBy=\"deviceId\"&equalTo=\"{myDeviceId}\"");
                    var scanRes = await _httpClient.GetAsync(scanUrl);
                    if (scanRes.IsSuccessStatusCode)
                    {
                        string scanJson = await scanRes.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(scanJson) && scanJson != "null")
                        {
                            using var doc = JsonDocument.Parse(scanJson);
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                try
                                {
                                    await _httpClient.DeleteAsync((await AuthUrl($"pairing_codes/{prop.Name}.json")));
                                    Logger.LogAction("PAIR CODE", $"Purged stale code: {prop.Name}");
                                }
                                catch (Exception ex) { Logger.LogAction("PAIR CODE", $"Failed to purge stale code: {ex.Message}"); }
                            }
                        }
                    }
                }
                catch { /* Non-critical Ã¢â‚¬â€ proceed even if scan fails */ }

                // Build JSON manually to use Firebase server timestamp {".sv":"timestamp"}
                // This ensures the timestamp comes from Firebase's server, not the PC clock,
                // so the phone's TTL check always works.
                string pairingKey = CreatePairingKeyIfNeeded();
                await CloudDiscoveryManager.RegisterRoomMembershipAsync(pairingKey);

                // SECURITY: Include uid for Firebase rule ownership validation (M-01 hardening)
                string uid = "";
                try { uid = await FirebaseAuthManager.GetUidAsync() ?? ""; } catch (Exception ex) { Logger.LogAction("PAIR", $"Firebase UID fetch failed: {ex.Message}"); }

                // SECURITY (SEC-SRV-02): Encrypt sensitive pairing secrets (pairingKey, pin, localUrl, globalUrl)
                // using an AES-256-GCM key derived from the ephemeral 6-character code.
                // Zero plaintext secrets are stored in Firebase.
                string sensitiveJson = JsonSerializer.Serialize(new
                {
                    pairingKey,
                    localUrl = CloudDiscoveryManager.CachedLocalUrl ?? "",
                    globalUrl = CloudDiscoveryManager.CachedGlobalUrl ?? "",
                    pin = SettingsManager.Current.WebClientPinToken ?? "",
                });
                string encryptedData = SyncCrypto.Encrypt(sensitiveJson, code);

                string jsonPayload = JsonSerializer.Serialize(new
                {
                    deviceId = SettingsManager.Current.DeviceId,
                    deviceName = SettingsManager.Current.DeviceName,
                    deviceType = "PC",
                    encryptedData,
                    uid,
                });
                // Inject Firebase server timestamp — {".sv":"timestamp"} is resolved server-side
                var jsonObj = System.Text.Json.Nodes.JsonNode.Parse(jsonPayload).AsObject();
                var svTimestamp = new System.Text.Json.Nodes.JsonObject();
                svTimestamp[".sv"] = "timestamp";
                jsonObj["timestamp"] = svTimestamp;
                string json = jsonObj.ToJsonString();

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync((await AuthUrl($"pairing_codes/{code}.json")), content);

                if (response.IsSuccessStatusCode)
                {
                    CurrentPairingCode = code;
                    Logger.LogAction("PAIR CODE", $"Published encrypted pairing code: {code}");

                    // Auto-expire after 5 minutes
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5 * 60_000);
                        try
                        {
                            await _httpClient.DeleteAsync((await AuthUrl($"pairing_codes/{code}.json")));
                            if (CurrentPairingCode == code) CurrentPairingCode = "";
                            Logger.LogAction("PAIR CODE", $"Expired pairing code: {code}");
                        }
                        catch (Exception ex) { Logger.LogAction("PAIR CODE", $"Failed to expire code: {ex.Message}"); }
                    });
                }
                else
                {
                    Logger.LogAction("PAIR CODE", $"Failed to publish code: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR CODE", $"Publish error: {ex.Message}");
            }
            return code;
        }

        /// <summary>
        /// Look up a pairing code from Firebase. Returns device info or null if not found/expired.
        /// Decrypts payload using the entered 6-character code.
        /// </summary>
        public static async Task<PairingCodeInfo> LookupPairingCode(string code)
        {
            try
            {
                string upperCode = code.Trim().ToUpperInvariant();
                Logger.LogAction("PAIR CODE", $"[STEP 4/6: CODE PAIRING] Looking up code {upperCode} in Firebase...");
                var response = await _httpClient.GetAsync((await AuthUrl($"pairing_codes/{upperCode}.json")));
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogAction("PAIR CODE", $"[STEP 4/6: CODE PAIRING ERROR] ❌ Firebase lookup for {upperCode} returned HTTP {(int)response.StatusCode}");
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    Logger.LogAction("PAIR CODE", $"[STEP 4/6: CODE PAIRING ERROR] ❌ Code {upperCode} not found in Firebase (empty response)");
                    return null;
                }

                var info = JsonSerializer.Deserialize<PairingCodeInfo>(json);

                // Decrypt encryptedData if present (Zero-Trust pairing protocol)
                if (info != null && !string.IsNullOrEmpty(info.encryptedData))
                {
                    try
                    {
                        string? decrypted = SyncCrypto.Decrypt(info.encryptedData, upperCode);
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            using var decDoc = JsonDocument.Parse(decrypted);
                            var decRoot = decDoc.RootElement;
                            if (decRoot.TryGetProperty("pairingKey", out var pk)) info.pairingKey = pk.GetString() ?? "";
                            if (decRoot.TryGetProperty("pin", out var pinProp)) info.pin = pinProp.GetString() ?? "";
                            if (decRoot.TryGetProperty("localUrl", out var lu)) info.localUrl = lu.GetString() ?? "";
                            if (decRoot.TryGetProperty("globalUrl", out var gu)) info.globalUrl = gu.GetString() ?? "";
                        }
                    }
                    catch (Exception decEx)
                    {
                        Logger.LogAction("PAIR CODE", $"Decryption failed for code {upperCode}: {decEx.Message}");
                    }
                }
                
                // Check if code is still fresh (5 min TTL)
                if (info != null && info.timestamp > 0)
                {
                    double ageMs = NetworkClock.UtcNowMs - info.timestamp;
                    if (Math.Abs(ageMs) > 5 * 60_000)
                    {
                        Logger.LogAction("PAIR CODE", $"[STEP 4/6: CODE PAIRING ERROR] ❌ Code {upperCode} expired/drifted ({ageMs / 1000:F1}s offset)");
                        return null;
                    }
                }

                Logger.LogAction("PAIR CODE", $"[STEP 4/6: CODE PAIRING] ✅ Found device via code {upperCode}: '{info?.deviceName}' (Type: {info?.deviceType})");
                return info;
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR CODE", $"[STEP 4/6: CODE PAIRING ERROR] ❌ Lookup error for {code}: {ex.Message}");
                return null;
            }
        }

        // ═══ Code-Based Pairing, Handshake, Load/Save moved to DevicePairingManager.CodePairing.cs ═══
    }
}
