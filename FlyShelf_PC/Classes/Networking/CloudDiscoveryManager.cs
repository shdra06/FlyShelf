using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlyShelf.ViewModels;
using System.Linq;
using System.IO;
using System.Collections.Generic;

namespace FlyShelf.Classes
{
    public partial class CloudDiscoveryManager
    {
        // AUDIT Task 5: Use shared pool instance instead of per-class HttpClient (prevents socket exhaustion)
        private static HttpClient _client => HttpClientPool.Default;
        private static string FIREBASE_BASE => FirebaseAuthManager.FirebaseDatabaseUrl;
        
        /// <summary>
        /// Wraps a Firebase REST URL with the auth token. ALL Firebase calls must use this.
        /// </summary>
        private static async Task<string> AuthUrl(string path)
        {
            string url = $"{FIREBASE_BASE}/{path}";
            return await FirebaseAuthManager.AuthenticateUrl(url);
        }

        /// <summary>
        /// Public version of AuthUrl for use by PeerManager and other classes.
        /// </summary>
        public static async Task<string> AuthUrlPublic(string path)
        {
            return await AuthUrl(path);
        }
        
        // ZERO-TRUST: GetScopedClipboardUrl removed — Firebase stores zero clipboard data.
        
        // Public Cloudflare URL for constructing file download links
        public static string CachedGlobalUrl { get; set; } = "";
        // Whether the Cloudflare tunnel has been verified working (HTTP 200 on self-ping)
        public static bool CachedTunnelVerified { get; set; } = false;
        // Local LAN server URL as fallback when Cloudflare is off
        public static string CachedLocalUrl { get; set; } = "";
        // Track the last URL pushed to Firebase so we only write on change (not every 60s)
        private static string _lastPushedTunnelUrl = "";
        // Count of paired devices recently reached via direct connection (LAN/Cloudflare)
        // When this equals total paired devices, Firebase clipboard push can be skipped.
        public static int DirectlyConnectedDeviceCount { get; set; } = 0;
        
        // Time-windowed dedup: track fingerprint → last push time (10s cooldown)
        private static readonly Dictionary<string, long> _recentPushTimes = new();
        // Track whether last push of this content actually succeeded (prevents dedup from swallowing retries)
        private static readonly Dictionary<string, bool> _recentPushSuccess = new();
        private const int DEDUP_COOLDOWN_MS = 10_000; // 10 seconds — same content within this window is skipped
        private const int AUTO_DELETE_TEXT_MS = 5 * 60_000; // 5 minutes
        private const int AUTO_DELETE_FILE_MS = 6 * 60 * 60_000; // 6 hours safety net

        /// <summary>
        /// Registers room presence under members/{pairingKey}/{uid} = true.
        /// Authenticates the path and makes a PUT request to secure membership in the room.
        /// </summary>
        public static async Task<bool> RegisterRoomMembershipAsync(string pairingKey)
        {
            if (string.IsNullOrEmpty(pairingKey)) return false;
            try
            {
                string uid = await FirebaseAuthManager.GetUidAsync();
                if (string.IsNullOrEmpty(uid))
                {
                    Logger.LogAction("ROOM_MEMBER", "[STEP 2/6: ROOM MEMBERSHIP ERROR] Cannot register room membership: UID is empty.");
                    return false;
                }

                Logger.LogAction("ROOM_MEMBER", $"[STEP 2/6: ROOM MEMBERSHIP] Registering members/{pairingKey[..Math.Min(8, pairingKey.Length)]}.../{uid[..Math.Min(8, uid.Length)]}...");
                AppLogger.Log("CLOUD_DISCOVERY", $"Registering room membership for {pairingKey[..Math.Min(8, pairingKey.Length)]}...");
                string url = await AuthUrl($"members/{pairingKey}/{uid}.json");
                var content = new StringContent("true", Encoding.UTF8, "application/json");
                using var response = await _client.PutAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    Logger.LogAction("ROOM_MEMBER", $"[STEP 2/6: ROOM MEMBERSHIP] ✅ Registered room membership successfully for key: {pairingKey[..Math.Min(8, pairingKey.Length)]}...");
                    AppLogger.Log("CLOUD_DISCOVERY", "Room membership registered successfully.");
                    return true;
                }
                else
                {
                    string body = await response.Content.ReadAsStringAsync();
                    Logger.LogAction("ROOM_MEMBER", $"[STEP 2/6: ROOM MEMBERSHIP ERROR] ❌ Room membership registration failed: HTTP {(int)response.StatusCode} - {body}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("ROOM_MEMBER", $"[STEP 2/6: ROOM MEMBERSHIP ERROR] ❌ Room membership error: {ex.Message}");
                return false;
            }
        }

        public static async Task PushToCloudHub(ClipboardItem item)
        {
            // SECURITY: Password items must NEVER be synced to any device
            if (item.IsPassword)
            {
                Logger.LogAction("FIREBASE SYNC", "Blocked password item from cloud sync — password items are never synced");
                return;
            }

            if (!SettingsManager.Current.EnableCloudDiscovery)
            {
                Logger.LogAction("PEER SYNC", "PushToCloudHub skipped — EnableCloudDiscovery is OFF");
                return;
            }

            // CRITICAL: Do not sync unless device has been explicitly paired
            if (!DevicePairingManager.HasPairingKey)
            {
                Logger.LogAction("FIREBASE SYNC", "Blocked ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â  no pairing key. Pair with another device first.");
                return;
            }

            // Time-windowed dedup: skip if same content was pushed SUCCESSFULLY within last 10 seconds
            string contentKey = !string.IsNullOrEmpty(item.FilePath) ? item.FilePath : (item.RawContent ?? "");
            string fingerprint = $"{item.ItemType}::{contentKey.AsSpan(0, Math.Min(200, contentKey.Length))}";
            long nowMs = NetworkClock.UtcNowMs;
            lock (_recentPushTimes)
            {
                if (_recentPushTimes.TryGetValue(fingerprint, out long lastPushTime))
                {
                    // Only dedup if the previous push of this content was SUCCESSFUL
                    bool lastSucceeded = _recentPushSuccess.TryGetValue(fingerprint, out bool s) && s;
                    if (nowMs - lastPushTime < DEDUP_COOLDOWN_MS && lastSucceeded)
                    {
                        Logger.LogAction("FIREBASE SYNC", "Skipped rapid-fire duplicate (same content within 10s cooldown)");
                        return;
                    }
                }
                _recentPushTimes[fingerprint] = nowMs;
                
                // Clean old fingerprints (older than 60s)
                var stale = _recentPushTimes.Where(kv => nowMs - kv.Value > 60_000).Select(kv => kv.Key).ToList();
                foreach (var key in stale) { _recentPushTimes.Remove(key); _recentPushSuccess.Remove(key); }
            }

            // Safety: If no DeviceName is set, use the machine name so we can always filter self-echoes
            string deviceName = SettingsManager.Current.DeviceName;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                deviceName = Environment.MachineName;
            }

            try
            {
                AppLogger.Log("CLOUD_SYNC", $"Syncing {item.ItemType} item across network...");
                // ═━═━═━ v5 PEER-ONLY: Push directly to connected peers ═━═━═━
                // PeerManager sends text/files directly via LAN or Cloudflare.
                // Mobile devices pull via /api/sync and receive real-time notifications via WebSocket/long-poll.
                bool isTextType = item.ItemType == ClipboardItemType.Text || item.ItemType == ClipboardItemType.Url || item.ItemType == ClipboardItemType.Code;
                bool isFileEarly = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);

                // Instantly notify any connected mobile clients via NetworkSyncServer
                NetworkSyncServer.Instance?.NotifyClipboardChanged(
                    item.ItemType.ToString(), 
                    item.FileName ?? (item.RawContent != null ? item.RawContent[..Math.Min(40, item.RawContent.Length)] : ""));

                int delivered = 0;
                if (PeerManager.Instance != null && PeerManager.Instance.AliveCount > 0)
                {
                    if (isTextType || !isFileEarly)
                    {
                        // TEXT: push directly to all PC peers
                        string peerTitle = !string.IsNullOrEmpty(item.FileName)
                            ? item.FileName
                            : (item.RawContent?.Length > 30 ? string.Concat(item.RawContent.AsSpan(0, 30), "...") : item.RawContent ?? "");
                        delivered = await PeerManager.Instance.PushTextToAllPeers(
                            item.RawContent ?? "", peerTitle, item.ItemType.ToString("G"));
                    }
                    else if (isFileEarly)
                    {
                        // FILE: upload directly to all PC peers via multipart
                        delivered = await PeerManager.Instance.PushFileToAllPeers(
                            item.FilePath, item.FileName ?? Path.GetFileName(item.FilePath), item.ItemType.ToString("G"));
                    }
                }

                int mobileCount = (NetworkSyncServer.Instance?.GetDirectlyConnectedDeviceCount() ?? 0) + NetworkSyncServer.ActivePeerWebSocketCount;
                lock (_recentPushTimes) { _recentPushSuccess[fingerprint] = true; }

                if (delivered > 0 || mobileCount > 0)
                {
                    Logger.LogAction("PEER SYNC", $"Staged/Delivered: {delivered} PC peer(s), {mobileCount} mobile companion(s)");
                    AppLogger.Log("CLOUD_SYNC", $"Delivered {item.ItemType} to {delivered} peer(s), {mobileCount} companion(s)");
                }
                else
                {
                    Logger.LogAction("PEER SYNC", "Staged for pull-based sync (no active peers connected at this moment)");
                    AppLogger.Log("CLOUD_SYNC", $"Staged {item.ItemType} for pull-based sync (no active peers connected)");
                }
            }
            catch (Exception ex)
            {
                lock (_recentPushTimes) { _recentPushSuccess[fingerprint] = false; }
                Logger.LogAction("PEER SYNC ERROR", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// ZERO-TRUST POLICY: Firebase is strictly signaling-only.
        /// No clipboard entries, files, or text are ever stored in, read from, or deleted from Firebase.
        /// </summary>
        public static Task PurgeStaleFileEntries(string deadUrl) => Task.CompletedTask;
        public static Task DeleteFirebaseEntry(string pairingKey, string entryKey) => Task.CompletedTask;
        public static Task MarkDownloading(string entryId) => Task.CompletedTask;
        public static Task MarkFileDownloaded(string entryId) => Task.CompletedTask;

        /// <summary>
        /// Look up a sender device's current Cloudflare tunnel URL from Firebase.
        /// Used when downloading files: the original entry's URL may be stale if the sender restarted its tunnel.
        /// </summary>
        public static async Task<string> GetSenderCurrentUrl(string senderDeviceId)
        {
            if (string.IsNullOrEmpty(senderDeviceId)) return "";
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return "";

                string url = (await AuthUrl($"active_devices/{pairingKey}/{senderDeviceId}.json"));
                using var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return "";

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return "";

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("GlobalUrl", out var gurl))
                    return gurl.GetString() ?? "";
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"GetSenderCurrentUrl failed: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// Fallback: Find a sender's current GlobalUrl by scanning active devices by DeviceName.
        /// Used when GetSenderCurrentUrl fails because the Firebase entry's SourceDeviceId
        /// doesn't match the active_devices key format (name vs full ID).
        /// </summary>
        public static async Task<string> FindSenderUrlByName(string senderDeviceName)
        {
            if (string.IsNullOrEmpty(senderDeviceName)) return "";
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return "";

                string url = (await AuthUrl($"active_devices/{pairingKey}.json"));
                using var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return "";

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return "";

                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("DeviceName", out var nameEl))
                    {
                        string name = nameEl.GetString() ?? "";
                        if (name.Equals(senderDeviceName, StringComparison.OrdinalIgnoreCase) ||
                            prop.Name.Contains(senderDeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (prop.Value.TryGetProperty("GlobalUrl", out var gurl))
                            {
                                string globalUrl = gurl.GetString() ?? "";
                                if (!string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com", StringComparison.Ordinal))
                                {
                                    Logger.LogAction("FIREBASE SSE", $"Found sender URL by name '{senderDeviceName}': {globalUrl}");
                                    return globalUrl;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"FindSenderUrlByName failed: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// Get a sender's LAN URL by scanning active devices by name.
        /// Used as last-resort fallback when all Cloudflare URLs are dead (DNS errors).
        /// The file can still be downloaded over LAN if both PCs are on the same network.
        /// </summary>
        public static async Task<string> FindSenderLanUrl(string senderDeviceName)
        {
            if (string.IsNullOrEmpty(senderDeviceName)) return "";
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return "";

                string url = (await AuthUrl($"active_devices/{pairingKey}.json"));
                using var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return "";

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return "";

                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("DeviceName", out var nameEl))
                    {
                        string name = nameEl.GetString() ?? "";
                        if (name.Equals(senderDeviceName, StringComparison.OrdinalIgnoreCase) ||
                            prop.Name.Contains(senderDeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Return the LocalIp (HTTP URL like http://192.168.1.x:8999)
                            if (prop.Value.TryGetProperty("LocalIp", out var lip))
                            {
                                string lanUrl = lip.GetString() ?? "";
                                if (!string.IsNullOrEmpty(lanUrl) && lanUrl.StartsWith("http", StringComparison.Ordinal))
                                {
                                    Logger.LogAction("FIREBASE SSE", $"Found sender LAN URL by name '{senderDeviceName}': {lanUrl}");
                                    return lanUrl;
                                }
                            }
                            // Also check the Url field (which might be the LAN URL)
                            if (prop.Value.TryGetProperty("Url", out var urlProp))
                            {
                                string directUrl = urlProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(directUrl) && directUrl.StartsWith("http", StringComparison.Ordinal) && !directUrl.Contains("trycloudflare", StringComparison.Ordinal))
                                {
                                    Logger.LogAction("FIREBASE SSE", $"Found sender direct URL by name '{senderDeviceName}': {directUrl}");
                                    return directUrl;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"FindSenderLanUrl failed: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// Sends a lightweight high-priority silent wake signal to the paired device ecosystem.
        /// Android devices listening or checking for wake signals will immediately trigger background fetch for the floating ball.
        /// </summary>
        public static async Task SendSilentWakePing(string pairingKey, string itemType)
        {
            if (string.IsNullOrEmpty(pairingKey)) return;
            try
            {
                string url = await AuthUrl($"active_devices/{pairingKey}/wakeSignal.json");
                var payload = new
                {
                    type = itemType,
                    sender = SettingsManager.Current.DeviceName ?? "PC",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _client.PutAsync(url, content);
                if (resp.IsSuccessStatusCode)
                {
                    Logger.LogAction("WAKE", $"Sent silent wake signal for '{itemType}' to active_devices/{pairingKey}/wakeSignal");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("WAKE_ERR", $"SendSilentWakePing failed: {ex.Message}");
            }
        }
    }
}
