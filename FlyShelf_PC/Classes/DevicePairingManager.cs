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
        public long timestamp { get; set; }
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
    }

    public static class DevicePairingManager
    {
        private static readonly string _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyShelf", "paired_devices.json");

        private static List<PairedDevice> _pairedDevices = new();
        private static readonly object _lock = new();
        
        /// <summary>Fires whenever a device is successfully paired. UI can subscribe to auto-refresh.</summary>
        public static event Action<string> OnDevicePaired;
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
        private const string FIREBASE_BASE = "https://advance-sync-default-rtdb.firebaseio.com";

        /// <summary>Maximum number of paired devices allowed. Remove existing devices to pair new ones.</summary>
        public const int MAX_PAIRED_DEVICES = 10;
        
        /// <summary>Wraps a Firebase REST URL with auth token.</summary>
        private static async Task<string> AuthUrl(string path)
        {
            return await FirebaseAuthManager.AuthenticateUrl($"{FIREBASE_BASE}/{path}");
        }
        
        /// <summary>Current active pairing code for this device (displayed in UI).</summary>
        public static string CurrentPairingCode { get; private set; } = "";


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
            if (string.IsNullOrEmpty(SettingsManager.Current.PairingKey))
            {
                SettingsManager.Current.PairingKey = Guid.NewGuid().ToString("N"); // 32-char hex
                SettingsManager.Save();
                Logger.LogAction("PAIRING", $"Generated new pairing key: {SettingsManager.Current.PairingKey.Substring(0, 8)}...");
            }
            return SettingsManager.Current.PairingKey;
        }

        /// <summary>
        /// Regenerate the pairing key (invalidates all previous QR codes).
        /// </summary>
        public static string RegeneratePairingKey()
        {
            SettingsManager.Current.PairingKey = Guid.NewGuid().ToString("N");
            SettingsManager.Save();
            return SettingsManager.Current.PairingKey;
        }

        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â QR Code Generation Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â

        /// <summary>
        /// Builds the JSON payload for the QR code containing all connection info.
        /// </summary>
        public static string BuildQRPayload(string localUrl, string globalUrl, string pin)
        {
            // This is when the PC becomes the "room creator" Ã¢â‚¬â€ generate key if needed
            string pairingKey = CreatePairingKeyIfNeeded();
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
            lock (_lock) return _pairedDevices.ToList();
        }

        /// <summary>
        /// Validates a pairing key and registers the device if valid.
        /// Returns true if pairing succeeded.
        /// </summary>
        public static bool TryPairDevice(string pairingKey, string deviceId, string deviceName, string deviceType, string remoteIP)
        {
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
                        catch { }
                        return false;
                    }

                    _pairedDevices.Add(new PairedDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        DeviceType = deviceType,
                        PairingKey = pairingKey,
                        PairedAt = DateTime.Now,
                        LastSeen = DateTime.Now,
                        LastKnownIP = remoteIP
                    });
                    Logger.LogAction("PAIR", $"✅ New device paired: {deviceName} ({deviceType}) from {remoteIP}");
                }
            }

            Save();
            OnDevicePaired?.Invoke(deviceName);
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
            lock (_lock)
            {
                _pairedDevices.RemoveAll(d => d.DeviceId == deviceId);
            }
            Save();
            Logger.LogAction("PAIR", $"Removed device: {deviceId}");
        }

        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â Short Pairing Code System Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â

        /// <summary>
        /// Generate a 6-character alphanumeric code (no ambiguous chars like I/1/O/0).
        /// </summary>
        public static string GenerateShortCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var rng = new Random();
            var code = new char[6];
            for (int i = 0; i < 6; i++) code[i] = chars[rng.Next(chars.Length)];
            return new string(code);
        }

        /// <summary>
        /// Publish a pairing code to Firebase so remote devices can find us.
        /// Auto-expires after 5 minutes. Returns the generated code.
        /// </summary>
        public static async Task<string> PublishPairingCode()
        {
            string code = GenerateShortCode();
            try
            {
                // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â Clean up any previous code from this device Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
                // If the PC was killed before cleanup ran, stale codes stay in Firebase
                // and cause "Code Expired" on phones. Delete any previous code first.
                if (!string.IsNullOrEmpty(CurrentPairingCode))
                {
                    try
                    {
                        await _httpClient.DeleteAsync((await AuthUrl($"pairing_codes/{CurrentPairingCode}.json")));
                        Logger.LogAction("PAIR CODE", $"Cleaned up previous code: {CurrentPairingCode}");
                    }
                    catch { }
                }

                // Also scan Firebase for any stale codes from this device ID and remove them
                try
                {
                    string myDeviceId = SettingsManager.Current.DeviceId ?? "";
                    var scanRes = await _httpClient.GetAsync($"{FIREBASE_BASE}/pairing_codes.json?orderBy=\"deviceId\"&equalTo=\"{myDeviceId}\"");
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
                                catch { }
                            }
                        }
                    }
                }
                catch { /* Non-critical Ã¢â‚¬â€ proceed even if scan fails */ }

                // Build JSON manually to use Firebase server timestamp {".sv":"timestamp"}
                // This ensures the timestamp comes from Firebase's server, not the PC clock,
                // so the phone's TTL check always works regardless of clock drift.
                string pairingKey = CreatePairingKeyIfNeeded();
                await CloudDiscoveryManager.RegisterRoomMembershipAsync(pairingKey);
                string jsonPayload = JsonSerializer.Serialize(new
                {
                    deviceId = SettingsManager.Current.DeviceId,
                    deviceName = SettingsManager.Current.DeviceName,
                    deviceType = "PC",
                    pairingKey,
                    localUrl = CloudDiscoveryManager.CachedLocalUrl ?? "",
                    globalUrl = CloudDiscoveryManager.CachedGlobalUrl ?? "",
                    pin = SettingsManager.Current.WebClientPinToken ?? "",
                });
                // Inject Firebase server timestamp Ã¢â‚¬â€ {".sv":"timestamp"} is resolved server-side
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
                    Logger.LogAction("PAIR CODE", $"Published pairing code: {code}");

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
                        catch { }
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
        /// </summary>
        public static async Task<PairingCodeInfo> LookupPairingCode(string code)
        {
            try
            {
                string upperCode = code.Trim().ToUpperInvariant();
                var response = await _httpClient.GetAsync((await AuthUrl($"pairing_codes/{upperCode}.json")));
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return null;

                var info = JsonSerializer.Deserialize<PairingCodeInfo>(json);
                
                // Check if code is still fresh (5 min TTL)
                if (info != null && info.timestamp > 0)
                {
                    long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - info.timestamp;
                    if (ageMs > 5 * 60_000)
                    {
                        Logger.LogAction("PAIR CODE", $"Code {upperCode} expired ({ageMs / 1000}s old)");
                        return null;
                    }
                }

                Logger.LogAction("PAIR CODE", $"Found device via code {upperCode}: {info?.deviceName}");
                return info;
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR CODE", $"Lookup error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Connect to a remote device by its pairing code.
        /// Looks up the code in Firebase, then calls /api/pair on the remote device.
        /// Returns (success, deviceName).
        /// </summary>
        public static async Task<(bool Success, string DeviceName)> ConnectByCode(string code)
        {
            var info = await LookupPairingCode(code);
            if (info == null)
                return (false, "");

            // Try to reach the device and pair Ã¢â‚¬â€ LAN first, then Cloudflare
            string[] urls = new[] { info.localUrl, info.globalUrl }
                .Where(u => !string.IsNullOrEmpty(u) && u.StartsWith("http"))
                .ToArray();

            // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â CASE 1: Mobile device with no HTTP server Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
            // When a mobile generates a code, it has no localUrl/globalUrl.
            // We can't POST /api/pair to it Ã¢â‚¬â€ instead, adopt the shared pairing key
            // directly and register the device locally. The shared key enables cloud sync.
            if (urls.Length == 0)
            {
                Logger.LogAction("PAIR CODE", $"Device {info.deviceName} has no HTTP URLs Ã¢â‚¬â€ performing local-only key adoption");
                
                // Adopt the remote device's pairing key as our own (shared room)
                if (!string.IsNullOrEmpty(info.pairingKey))
                {
                    SettingsManager.Current.PairingKey = info.pairingKey;
                    SettingsManager.Save();
                    Logger.LogAction("PAIR CODE", $"Adopted pairing key from {info.deviceName}: {info.pairingKey.Substring(0, 8)}...");
                    await CloudDiscoveryManager.RegisterRoomMembershipAsync(info.pairingKey);
                }

                // Register the remote device in our paired devices list
                TryPairDevice(info.pairingKey, info.deviceId, info.deviceName, info.deviceType, "cloud");

                // Push our own connection info to Firebase so the mobile can discover us
                _ = CloudDiscoveryManager.PushTunnelUrl(
                    CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                    true,
                    CloudDiscoveryManager.CachedLocalUrl ?? "");

                Logger.LogAction("PAIR CODE", $"Ã¢Å“â€¦ Local-only paired with {info.deviceName} (key adoption)");
                
                // Notify the code-provider that we joined Ã¢â‚¬â€ write a handshake to Firebase
                _ = WriteHandshakeToFirebase(info.pairingKey, info.deviceId);
                
                return (true, info.deviceName);
            }

            // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â CASE 2: Device has HTTP server Ã¢â‚¬â€ try to reach it Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
            foreach (var url in urls)
            {
                try
                {
                    Logger.LogAction("PAIR CODE", $"Trying to pair with {info.deviceName} at {url}...");
                    var pairPayload = new
                    {
                        key = info.pairingKey,
                        deviceId = SettingsManager.Current.DeviceId,
                        deviceName = SettingsManager.Current.DeviceName,
                        deviceType = "PC"
                    };

                    var json = JsonSerializer.Serialize(pairPayload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    
                    using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(8) };
                    var response = await client.PostAsync($"{url}/api/pair", content);

                    if (response.IsSuccessStatusCode)
                    {
                        // CRITICAL: Adopt the remote device's pairing key so both PCs
                        // share the same Firebase scope for clipboard sync and device discovery
                        if (!string.IsNullOrEmpty(info.pairingKey))
                        {
                            SettingsManager.Current.PairingKey = info.pairingKey;
                            SettingsManager.Save();
                            Logger.LogAction("PAIR CODE", $"Adopted pairing key from {info.deviceName}: {info.pairingKey.Substring(0, 8)}...");
                            await CloudDiscoveryManager.RegisterRoomMembershipAsync(info.pairingKey);
                        }

                        // Now register the remote device locally (TryPairDevice checks key match)
                        TryPairDevice(info.pairingKey, info.deviceId, info.deviceName, info.deviceType,
                            url.Contains("trycloudflare") ? "cloudflare" : "lan");
                        
                        // Re-register ourselves in Firebase under the shared pairing key scope
                        _ = CloudDiscoveryManager.PushTunnelUrl(
                            CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                            true,
                            CloudDiscoveryManager.CachedLocalUrl ?? "");

                        Logger.LogAction("PAIR CODE", $"Ã¢Å“â€¦ Paired with {info.deviceName} via {url}");
                        
                        // Notify the code-provider that we joined
                        _ = WriteHandshakeToFirebase(info.pairingKey, info.deviceId);
                        
                        return (true, info.deviceName);
                    }
                    
                    Logger.LogAction("PAIR CODE", $"Pair attempt to {url}: HTTP {(int)response.StatusCode}");
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PAIR CODE", $"Pair attempt to {url} failed: {ex.Message}");
                }
            }

            // ═══ CASE 3: Device has URLs but is unreachable — adopt key anyway ═══ 
            // The device was found in Firebase, so the pairing key is valid.
            // Save it so cloud sync works once the device comes online.
            if (!string.IsNullOrEmpty(info.pairingKey))
            {
                Logger.LogAction("PAIR CODE", $"Device {info.deviceName} unreachable — adopting key for deferred pairing");
                SettingsManager.Current.PairingKey = info.pairingKey;
                SettingsManager.Save();
                await CloudDiscoveryManager.RegisterRoomMembershipAsync(info.pairingKey);
                TryPairDevice(info.pairingKey, info.deviceId, info.deviceName, info.deviceType, "deferred");
                
                _ = CloudDiscoveryManager.PushTunnelUrl(
                    CloudDiscoveryManager.CachedGlobalUrl ?? CloudDiscoveryManager.CachedLocalUrl ?? "",
                    true,
                    CloudDiscoveryManager.CachedLocalUrl ?? "");
                
                _ = WriteHandshakeToFirebase(info.pairingKey, info.deviceId);
                
                return (true, info.deviceName);
            }

            return (false, info.deviceName);
        }

        /// <summary>
        /// Write a handshake notification to Firebase so the code-provider knows this device joined.
        /// The code-provider polls this node and auto-adds the joiner to its paired devices list.
        /// </summary>
        private static async Task WriteHandshakeToFirebase(string pairingKey, string remoteDeviceId)
        {
            try
            {
                string myDeviceId = SettingsManager.Current.DeviceId ?? "";
                string myDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                var handshake = new
                {
                    deviceId = myDeviceId,
                    deviceName = myDeviceName,
                    deviceType = "PC",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                string json = JsonSerializer.Serialize(handshake);
                string url = (await AuthUrl($"pairing_handshake/{pairingKey}/{myDeviceId}.json"));
                await _httpClient.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
                Logger.LogAction("PAIR HANDSHAKE", $"Wrote handshake to Firebase for key {pairingKey.Substring(0, 8)}...");
                
                // Auto-expire handshake after 10 minutes
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10 * 60_000);
                    try { await _httpClient.DeleteAsync(url); } catch { }
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR HANDSHAKE", $"Failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Check for handshake notifications from devices that joined via our pairing code.
        /// Auto-registers them in our paired devices list.
        /// </summary>
        public static async Task CheckForHandshakes()
        {
            try
            {
                string pairingKey = EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;

                string url = (await AuthUrl($"pairing_handshake/{pairingKey}.json"));
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return;

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                string myDeviceId = SettingsManager.Current.DeviceId ?? "";
                bool anyNew = false;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string devId = prop.Value.TryGetProperty("deviceId", out var di) ? di.GetString() ?? "" : "";
                    string devName = prop.Value.TryGetProperty("deviceName", out var dn) ? dn.GetString() ?? "" : "";
                    string devType = prop.Value.TryGetProperty("deviceType", out var dt) ? dt.GetString() ?? "PC" : "PC";

                    if (devId == myDeviceId || string.IsNullOrEmpty(devId)) continue;

                    // Check if we already have this device
                    bool alreadyPaired;
                    lock (_lock)
                    {
                        alreadyPaired = _pairedDevices.Any(d => d.DeviceId == devId);
                    }

                    if (!alreadyPaired)
                    {
                        TryPairDevice(pairingKey, devId, devName, devType, "handshake");
                        Logger.LogAction("PAIR HANDSHAKE", $"Ã¢Å“â€¦ Auto-registered new device from handshake: {devName} ({devType})");
                        anyNew = true;

                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast($"Ã°Å¸â€â€” {devName} joined your sync group!");
                        });
                    }

                    // Clean up processed handshake
                    try { await _httpClient.DeleteAsync((await AuthUrl($"pairing_handshake/{pairingKey}/{prop.Name}.json"))); } catch { }
                }

                if (anyNew) Save();
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR HANDSHAKE", $"Check failed: {ex.Message}");
            }
        }

        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â Persistence Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â


        private static void Load()
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    string fileContent = File.ReadAllText(_storagePath);
                    string json = SecureStorage.Decrypt(fileContent);
                    _pairedDevices = JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? new();
                    Logger.LogAction("PAIR", $"Loaded {_pairedDevices.Count} paired device(s)");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR", $"Load failed: {ex.Message}");
                _pairedDevices = new();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath));
                string json = JsonSerializer.Serialize(_pairedDevices, new JsonSerializerOptions { WriteIndented = true });
                string encrypted = SecureStorage.Encrypt(json);
                File.WriteAllText(_storagePath, encrypted);
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR", $"Save failed: {ex.Message}");
            }
        }
    }
}



