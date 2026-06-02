// ---------------------------------------------------------------
// DevicePairingManager — Code-Based Pairing, Handshake & Persistence
// ConnectByCode, WriteHandshakeToFirebase, CheckForHandshakes, Load, Save
// Split from DevicePairingManager.cs for modularity
// ---------------------------------------------------------------
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
    public static partial class DevicePairingManager
    {
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

                    if (devId == myDeviceId || string.IsNullOrWhiteSpace(devId)) continue;

                    // Guard: skip handshake entries with empty DeviceName
                    if (string.IsNullOrWhiteSpace(devName)) continue;

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
                            FlyShelf.Windows.ToastWindow.ShowToast($"Ã°Å¸â€— {devName} joined your sync group!");
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
                    var loaded = JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? new();

                    // Self-healing: purge any invalid/nameless devices that were persisted by older versions
                    int originalCount = loaded.Count;
                    _pairedDevices = loaded.Where(d =>
                        !string.IsNullOrWhiteSpace(d.DeviceId) &&
                        !string.IsNullOrWhiteSpace(d.DeviceName)).ToList();

                    if (_pairedDevices.Count < originalCount)
                    {
                        Logger.LogAction("PAIR", $"🧹 Purged {originalCount - _pairedDevices.Count} invalid/nameless device(s) from storage.");
                        Save();
                    }

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
                string tempPath = _storagePath + ".tmp";
                File.WriteAllText(tempPath, encrypted);
                File.Move(tempPath, _storagePath, true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR", $"Save failed: {ex.Message}");
            }
        }
    }
}
