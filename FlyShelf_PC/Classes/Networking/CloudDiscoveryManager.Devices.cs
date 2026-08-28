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

        // H4: Firebase quota/rate-limit backoff — prevents hammering when 429/402 is returned
        private static DateTime _firebaseBackoffUntil = DateTime.MinValue;
        private static bool _firebaseQuotaWarningShown = false;

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
                    if (url.Contains("trycloudflare.com", StringComparison.Ordinal))
                        encryptedGlobalUrl = SyncCrypto.Encrypt(url) ?? "";
                    if (!string.IsNullOrEmpty(localIp))
                        encryptedLocalIp = SyncCrypto.Encrypt(localIp) ?? "";
                    string plainUrl = localIp.Contains("http", StringComparison.Ordinal) ? localIp : url;
                    if (!string.IsNullOrEmpty(plainUrl))
                        encryptedUrl = SyncCrypto.Encrypt(plainUrl) ?? "";
                    if (!string.IsNullOrEmpty(tlsUrl))
                        encryptedTlsUrl = SyncCrypto.Encrypt(tlsUrl) ?? "";
                }
                catch
                {
                    // Fallback to plaintext if encryption fails (e.g., no pairing key yet)
                    urlsActuallyEncrypted = false;
                    encryptedGlobalUrl = url.Contains("trycloudflare.com", StringComparison.Ordinal) ? url : "";
                    encryptedLocalIp = localIp;
                    encryptedUrl = localIp.Contains("http", StringComparison.Ordinal) ? localIp : url;
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
                // Register room membership concurrently — don't delay the tunnel URL push
                if (!_roomMembershipRegistered)
                {
                    _roomMembershipRegistered = true;
                    _ = RegisterRoomMembershipAsync(pairingKey);
                }
                string tunnelNodeUrl = (await AuthUrl($"active_devices/{pairingKey}/{SettingsManager.Current.DeviceId}.json"));
                // H4: Skip Firebase writes during backoff period
                if (DateTime.UtcNow < _firebaseBackoffUntil)
                {
                    Logger.LogAction("FIREBASE SYNC", $"Skipping write — in backoff until {_firebaseBackoffUntil:HH:mm:ss}Z");
                    return;
                }

                using var response = await _client.PutAsync(tunnelNodeUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    _lastPushedTunnelUrl = urlFingerprint;
                    _firebaseQuotaWarningShown = false; // Reset on success
                    Logger.LogAction("FIREBASE SYNC", $"Tunnel DNS updated: {url} [{isOnline}]");
                }
                else
                {
                    int statusCode = (int)response.StatusCode;
                    // H4: Detect Firebase quota exceeded (402) and rate limiting (429)
                    if (statusCode == 429 || statusCode == 402)
                    {
                        _firebaseBackoffUntil = DateTime.UtcNow.AddMinutes(5);
                        Logger.LogAction("FIREBASE QUOTA", $"⚠️ Firebase returned {statusCode} — backing off for 5 minutes");
                        if (!_firebaseQuotaWarningShown)
                        {
                            _firebaseQuotaWarningShown = true;
                            try
                            {
                                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                    Windows.ToastWindow.ShowToast("Cloud sync temporarily limited — retrying in 5 minutes"));
                            }
                            catch { } // Best-effort: failure is acceptable
                        }
                    }
                    else
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        Logger.LogAction("FIREBASE ERROR", $"Tunnel push failed: HTTP {statusCode} — {body}");
                    }
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
                    using var response = await _client.GetAsync(url);

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
                            string myId = SettingsManager.Current.DeviceId ?? "";
                            string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                string name = prop.Value.TryGetProperty("DeviceName", out var n) ? n.GetString() ?? "" : "";
                                string devId = prop.Value.TryGetProperty("DeviceId", out var di) ? di.GetString() ?? prop.Name : prop.Name;

                                // Guard: Always skip self (by DeviceId, node key, or machine name)
                                if (string.Equals(prop.Name, myId, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(devId, myId, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(name, myName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(prop.Name, myName, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                string type = prop.Value.TryGetProperty("DeviceType", out var dt) ? dt.GetString() ?? "" : "";
                                bool online = prop.Value.TryGetProperty("IsOnline", out var on) && on.GetBoolean();
                                string localIp = prop.Value.TryGetProperty("LocalIp", out var lip) ? lip.GetString() ?? "" : "";
                                string globalUrl = prop.Value.TryGetProperty("GlobalUrl", out var gurl) ? gurl.GetString() ?? "" : "";

                                // Real-time TTL check: treat devices with heartbeat older than 2 minutes as offline
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
        /// Purge stale and unpaired device entries from Firebase.
        /// Removes old GUID-based entries, duplicate self nodes, and modern entries that are
        /// offline or not in the paired devices list.
        /// </summary>
        public static async Task CleanupStaleDevices()
        {
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;
                string url = (await AuthUrl($"active_devices/{pairingKey}.json"));
                using var response = await _client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        using var doc = JsonDocument.Parse(json);
                        long nowMs = NetworkClock.UtcNowMs;
                        string myDeviceId = SettingsManager.Current.DeviceId ?? "";
                        string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

                        var pairedIds = new HashSet<string>(
                            DevicePairingManager.GetPairedDevices().Select(d => d.DeviceId),
                            StringComparer.OrdinalIgnoreCase);

                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            // Clean up old duplicate self entries that used machine name as key instead of DeviceId
                            if (string.Equals(prop.Name, myName, StringComparison.OrdinalIgnoreCase) && !string.Equals(prop.Name, myDeviceId, StringComparison.OrdinalIgnoreCase))
                            {
                                string deleteSelfGhostUrl = (await AuthUrl($"active_devices/{pairingKey}/{prop.Name}.json"));
                                using var _ = await _client.DeleteAsync(deleteSelfGhostUrl);
                                Logger.LogAction("FIREBASE CLEANUP", $"Removed duplicate self entry: {prop.Name}");
                                continue;
                            }

                            // Never delete current active self
                            if (string.Equals(prop.Name, myDeviceId, StringComparison.OrdinalIgnoreCase)) continue;

                            string name = prop.Value.TryGetProperty("DeviceName", out var n) ? n.GetString() ?? "" : "";
                            string devId = prop.Value.TryGetProperty("DeviceId", out var di) ? di.GetString() ?? prop.Name : prop.Name;

                            long deviceTs = 0;
                            if (prop.Value.TryGetProperty("Timestamp", out var ts))
                                deviceTs = (long)ts.GetDouble();

                            bool isOnline = prop.Value.TryGetProperty("IsOnline", out var onProp) && onProp.GetBoolean();
                            bool isPaired = pairedIds.Contains(prop.Name) || pairedIds.Contains(devId) || pairedIds.Contains(name);

                            // If device is not paired AND (offline or no heartbeat for > 3 minutes): delete it from Firebase
                            if (!isPaired && (!isOnline || (nowMs - deviceTs) > 180_000))
                            {
                                string deleteUrl = (await AuthUrl($"active_devices/{pairingKey}/{prop.Name}.json"));
                                using var delResp = await _client.DeleteAsync(deleteUrl);
                                Logger.LogAction("FIREBASE CLEANUP", $"Purged stale unpaired ghost: {prop.Name} ({name})");
                            }
                            // If device is paired but offline for more than 7 days: delete from active_devices
                            else if (isPaired && (nowMs - deviceTs) > 7 * 24 * 3600_000)
                            {
                                string deleteUrl = (await AuthUrl($"active_devices/{pairingKey}/{prop.Name}.json"));
                                using var delResp = await _client.DeleteAsync(deleteUrl);
                                Logger.LogAction("FIREBASE CLEANUP", $"Purged long-dead device from active_devices: {prop.Name}");
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
            string pairingKey = DevicePairingManager.EnsurePairingKey();
            if (string.IsNullOrEmpty(pairingKey))
            {
                Logger.LogAction("FIREBASE", "Skipped GetDeviceGroups — no pairing key");
                return result;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string url = (await AuthUrl($"device_groups/{pairingKey}.json"));
                    using var httpResponse = await _client.GetAsync(url);

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
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey))
                {
                    Logger.LogAction("FIREBASE", "Skipped SaveDeviceGroup — no pairing key");
                    return;
                }

                string url = (await AuthUrl($"device_groups/{pairingKey}/{groupId}.json"));
                // SECURITY: Include ownerUid for Firebase rule ownership validation (M-01 hardening)
                string ownerUid = "";
                try { ownerUid = await FirebaseAuthManager.GetUidAsync() ?? ""; } catch (Exception ex) { Logger.LogAction("FIREBASE", $"UID fetch for device group failed: {ex.Message}"); }
                var payload = new { name, deviceNames, ownerUid };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _client.PutAsync(url, content);
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
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey))
                {
                    Logger.LogAction("FIREBASE", "Skipped DeleteDeviceGroup — no pairing key");
                    return;
                }

                string url = (await AuthUrl($"device_groups/{pairingKey}/{groupId}.json"));
                using var response = await _client.DeleteAsync(url);
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
