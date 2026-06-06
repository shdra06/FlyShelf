// ═══════════════════════════════════════════════════════════════
// CloudDiscoveryManager — Device Registration, Tunnel URL, Groups
// Split from CloudDiscoveryManager.cs for modularity (<500 lines)
// ═══════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class CloudDiscoveryManager
    {
        // FIX: Only register room membership once per session — it never changes after pairing
        private static bool _roomMembershipRegistered = false;

        /// <summary>
        /// Push device registration to Firebase. Optimized: only writes when URL actually changes
        /// or when going offline. Reduces Firebase writes from ~1440/day to ~2-5/day per user.
        /// </summary>
        /// <param name="forceWrite">If true, bypasses the URL-change check (used for going offline)</param>
        public static async Task PushTunnelUrl(string url, bool isOnline, string localIp = "", bool forceWrite = false)
        {
            try
            {
                // PHASE 2 OPTIMIZATION: Only push to Firebase when URL actually changes or going offline
                string urlFingerprint = $"{url}|{localIp}|{isOnline}";
                if (!forceWrite && urlFingerprint == _lastPushedTunnelUrl)
                {
                    return; // URL hasn't changed — skip Firebase write
                }

                // Encrypt sensitive URLs before writing to Firebase (security: prevents unauthorized access)
                string encryptedGlobalUrl = "";
                string encryptedLocalIp = "";
                string encryptedUrl = "";
                string encryptedTlsUrl = "";
                string tlsUrl = NetworkSyncServer.Instance?.TlsUrl ?? "";
                bool urlsActuallyEncrypted = true;
                try
                {
                    if (url.Contains("trycloudflare.com"))
                        encryptedGlobalUrl = SyncCrypto.Encrypt(url) ?? "";
                    if (!string.IsNullOrEmpty(localIp))
                        encryptedLocalIp = SyncCrypto.Encrypt(localIp) ?? "";
                    string plainUrl = localIp.Contains("http") ? localIp : url;
                    if (!string.IsNullOrEmpty(plainUrl))
                        encryptedUrl = SyncCrypto.Encrypt(plainUrl) ?? "";
                    if (!string.IsNullOrEmpty(tlsUrl))
                        encryptedTlsUrl = SyncCrypto.Encrypt(tlsUrl) ?? "";
                }
                catch
                {
                    // Fallback to plaintext if encryption fails (e.g., no pairing key yet)
                    urlsActuallyEncrypted = false;
                    encryptedGlobalUrl = url.Contains("trycloudflare.com") ? url : "";
                    encryptedLocalIp = localIp;
                    encryptedUrl = localIp.Contains("http") ? localIp : url;
                    encryptedTlsUrl = tlsUrl;
                }

                var payload = new
                {
                    DeviceId = SettingsManager.Current.DeviceId,
                    DeviceName = SettingsManager.Current.DeviceName,
                    DeviceType = "PC",
                    Url = encryptedUrl,
                    LocalIp = encryptedLocalIp,
                    GlobalUrl = encryptedGlobalUrl,
                    TlsUrl = encryptedTlsUrl,
                    TlsThumbprint = NetworkSyncServer.Instance?.TlsThumbprint ?? "",
                    IsOnline = isOnline,
                    Timestamp = NetworkClock.UtcNowMs,
                    UrlsEncrypted = urlsActuallyEncrypted,   // Signal to peers whether URLs need decryption
                    IsPro = LicenseManager.IsPro,
                    LicenseKey = LicenseManager.IsPro ? LicenseManager.MaskedKey : ""
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Use PUT to register or update our specific Device node (scoped to pairing key)
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) { Logger.LogAction("FIREBASE SYNC", "Skipped device registration — no pairing key"); return; }
                // Only register room membership once per session — it never changes
                if (!_roomMembershipRegistered)
                {
                    await RegisterRoomMembershipAsync(pairingKey);
                    _roomMembershipRegistered = true;
                }
                string tunnelNodeUrl = (await AuthUrl($"active_devices/{pairingKey}/{SettingsManager.Current.DeviceId}.json"));
                var response = await _client.PutAsync(tunnelNodeUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    _lastPushedTunnelUrl = urlFingerprint;
                    Logger.LogAction("FIREBASE SYNC", $"Tunnel DNS updated: {url} [{isOnline}]");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE ERROR", $"Tunnel DNS Failure: {ex.Message}");
            }
        }


        /// <summary>
        /// Fetch all active devices from Firebase for the forced sync device picker.
        /// </summary>
        public static async Task<List<(string Id, string Name, string Type, bool IsOnline, string LocalIp, string GlobalUrl)>> GetActiveDevices()
        {
            var devices = new List<(string Id, string Name, string Type, bool IsOnline, string LocalIp, string GlobalUrl)>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string pairingKey = DevicePairingManager.EnsurePairingKey();
                    if (string.IsNullOrEmpty(pairingKey)) return devices;
                    string url = (await AuthUrl($"active_devices/{pairingKey}.json"));
                    var response = await _client.GetAsync(url);

                    // Auto-retry on 401: invalidate token and try once more
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        if (response.Content != null)
                        {
                            string body = await response.Content.ReadAsStringAsync();
                            if (body != null && body.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogAction("FIREBASE", "Permission denied by security rules for GetActiveDevices — token is valid but access is rejected. Skipping invalidation.");
                                break;
                            }
                        }
                        FirebaseAuthManager.InvalidateToken();
                        continue;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json != "null")
                        {
                            using var doc = JsonDocument.Parse(json);
                            string myId = SettingsManager.Current.DeviceId;
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (prop.Name == myId) continue; // Skip self
                                string name = prop.Value.TryGetProperty("DeviceName", out var n) ? n.GetString() ?? "" : "";
                                string type = prop.Value.TryGetProperty("DeviceType", out var dt) ? dt.GetString() ?? "" : "";
                                bool online = prop.Value.TryGetProperty("IsOnline", out var on) && on.GetBoolean();
                                string localIp = prop.Value.TryGetProperty("LocalIp", out var lip) ? lip.GetString() ?? "" : "";
                                string globalUrl = prop.Value.TryGetProperty("GlobalUrl", out var gurl) ? gurl.GetString() ?? "" : "";

                                // TTL check: treat devices with heartbeat older than 2 minutes as offline
                                if (online && prop.Value.TryGetProperty("Timestamp", out var ts))
                                {
                                    long deviceTs = (long)ts.GetDouble();
                                    long nowMs = NetworkClock.UtcNowMs;
                                    if (nowMs - deviceTs > 120_000) online = false;
                                }

                                devices.Add((prop.Name, name, type, online, localIp, globalUrl));
                            }
                        }
                    }
                    break; // Success or non-401 error
                }
                catch (Exception ex)
                {
                    Logger.LogAction("FIREBASE", $"GetActiveDevices error: {ex.Message}");
                    break;
                }
            }
            return devices;
        }

        /// <summary>
        /// Purge old GUID-based device entries from Firebase that were created by the old NewGuid() logic.
        /// Only removes entries that are genuinely stale (offline for 24+ hours).
        /// Active old-version devices are preserved so they remain visible for sync.
        /// </summary>
        public static async Task CleanupStaleDevices()
        {
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;
                string url = (await AuthUrl($"active_devices/{pairingKey}.json"));
                var response = await _client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        using var doc = JsonDocument.Parse(json);
                        long nowMs = NetworkClock.UtcNowMs;
                        const long STALE_THRESHOLD_MS = 24 * 60 * 60_000; // 24 hours

                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            // Old format: a raw GUID like "a1b2c3d4-e5f6-7890-..."
                            // New format: "PC_MACHINENAME_USERNAME"
                            if (prop.Name.Contains('-') && !prop.Name.StartsWith("PC_") && !prop.Name.StartsWith("Mobile_"))
                            {
                                // Only delete if genuinely stale (no heartbeat in 24 hours)
                                long deviceTs = 0;
                                if (prop.Value.TryGetProperty("Timestamp", out var ts))
                                    deviceTs = (long)ts.GetDouble();

                                if (deviceTs > 0 && (nowMs - deviceTs) < STALE_THRESHOLD_MS)
                                {
                                    // Old-format device is still active — keep it
                                    Logger.LogAction("FIREBASE CLEANUP", $"Keeping active old-format device: {prop.Name}");
                                    continue;
                                }

                                // Stale GUID-based entry — safe to remove
                                string deleteUrl = (await AuthUrl($"active_devices/{pairingKey}/{prop.Name}.json"));
                                await _client.DeleteAsync(deleteUrl);
                                Logger.LogAction("FIREBASE CLEANUP", $"Removed stale device: {prop.Name}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"CleanupStaleDevices error: {ex.Message}");
            }
        }

        // ══════ Device Groups CRUD ══════

        public static async Task<List<DeviceGroupInfo>> GetDeviceGroups()
        {
            var result = new List<DeviceGroupInfo>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string url = (await AuthUrl("device_groups.json"));
                    var httpResponse = await _client.GetAsync(url);

                    // Auto-retry on 401: invalidate token and try once more
                    if (httpResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        if (httpResponse.Content != null)
                        {
                            string body = await httpResponse.Content.ReadAsStringAsync();
                            if (body != null && body.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogAction("FIREBASE", "Permission denied by security rules for GetDeviceGroups — token is valid but access is rejected. Skipping invalidation.");
                                break;
                            }
                        }
                        FirebaseAuthManager.InvalidateToken();
                        continue;
                    }
                    if (!httpResponse.IsSuccessStatusCode) break;

                    string response = await httpResponse.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(response) && response != "null")
                    {
                        using var doc = JsonDocument.Parse(response);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var group = new DeviceGroupInfo { Id = prop.Name };
                            if (prop.Value.TryGetProperty("name", out var nameProp))
                                group.Name = nameProp.GetString() ?? "";
                            if (prop.Value.TryGetProperty("deviceNames", out var devsProp) && devsProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var dev in devsProp.EnumerateArray())
                                    group.DeviceNames.Add(dev.GetString() ?? "");
                            }
                            result.Add(group);
                        }
                    }
                    break; // Success
                }
                catch (Exception ex)
                {
                    Logger.LogAction("FIREBASE", $"GetDeviceGroups error: {ex.Message}");
                    break;
                }
            }
            return result;
        }

        public static async Task SaveDeviceGroup(string groupId, string name, List<string> deviceNames)
        {
            try
            {
                string url = (await AuthUrl($"device_groups/{groupId}.json"));
                // SECURITY: Include ownerUid for Firebase rule ownership validation (M-01 hardening)
                string ownerUid = "";
                try { ownerUid = await FirebaseAuthManager.GetUidAsync() ?? ""; } catch { }
                var payload = new { name, deviceNames, ownerUid };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _client.PutAsync(url, content);
                Logger.LogAction("FIREBASE", $"Saved group '{name}' with {deviceNames.Count} devices");
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"SaveDeviceGroup error: {ex.Message}");
            }
        }

        public static async Task DeleteDeviceGroup(string groupId)
        {
            try
            {
                string url = (await AuthUrl($"device_groups/{groupId}.json"));
                await _client.DeleteAsync(url);
                Logger.LogAction("FIREBASE", $"Deleted group {groupId}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"DeleteDeviceGroup error: {ex.Message}");
            }
        }
    }

    public class DeviceGroupInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> DeviceNames { get; set; } = new();
    }
}
