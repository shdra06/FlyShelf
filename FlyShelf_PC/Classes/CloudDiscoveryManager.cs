using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AdvanceClip.ViewModels;
using System.Linq;
using System.IO;

namespace AdvanceClip.Classes
{
    public partial class FirebaseSyncManager
    {
        private static readonly HttpClient _client = new HttpClient();
        private const string FIREBASE_BASE = "https://advance-sync-default-rtdb.firebaseio.com";
        
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
        
        /// <summary>Returns the scoped clipboard path for this device's pairing key.</summary>
        private static async Task<string> GetScopedClipboardUrl()
        {
            string pairingKey = DevicePairingManager.EnsurePairingKey();
            return (await AuthUrl($"clipboard/{pairingKey}.json"));
        }
        
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
        
        // Time-windowed dedup: track fingerprint ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ last push time (10s cooldown)
        private static readonly Dictionary<string, long> _recentPushTimes = new();
        private const int DEDUP_COOLDOWN_MS = 10_000; // 10 seconds ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â same content within this window is skipped
        private const int AUTO_DELETE_TEXT_MS = 5 * 60_000; // 5 minutes
        private const int AUTO_DELETE_FILE_MS = 6 * 60 * 60_000; // 6 hours safety net

        public static async Task PushToGlobalSync(ClipboardItem item)
        {
            if (!SettingsManager.Current.EnableGlobalFirebaseSync)
                return;

            // CRITICAL: Do not sync unless device has been explicitly paired
            if (!DevicePairingManager.HasPairingKey)
            {
                Logger.LogAction("FIREBASE SYNC", "Blocked ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â no pairing key. Pair with another device first.");
                return;
            }

            // Time-windowed dedup: skip if same content was pushed within last 10 seconds
            string fingerprint = $"{item.ItemType}::{(item.RawContent ?? "").Substring(0, Math.Min(200, (item.RawContent ?? "").Length))}";
            long nowMs = NetworkClock.UtcNowMs;
            lock (_recentPushTimes)
            {
                if (_recentPushTimes.TryGetValue(fingerprint, out long lastPushTime))
                {
                    if (nowMs - lastPushTime < DEDUP_COOLDOWN_MS)
                    {
                        Logger.LogAction("FIREBASE SYNC", "Skipped rapid-fire duplicate (same content within 10s cooldown)");
                        return;
                    }
                }
                _recentPushTimes[fingerprint] = nowMs;
                
                // Clean old fingerprints (older than 60s)
                var stale = _recentPushTimes.Where(kv => nowMs - kv.Value > 60_000).Select(kv => kv.Key).ToList();
                foreach (var key in stale) _recentPushTimes.Remove(key);
            }

            // Safety: If no DeviceName is set, use the machine name so we can always filter self-echoes
            string deviceName = SettingsManager.Current.DeviceName;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                deviceName = Environment.MachineName;
            }

            try
            {
                // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â v5 PEER-ONLY: Push directly to connected peers Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
                // PeerManager sends text/files directly via LAN or Cloudflare.
                // Firebase is NEVER used for content transfer Ã¢â‚¬â€ only for device discovery & URL exchange.
                bool isTextType = item.ItemType == ClipboardItemType.Text || item.ItemType == ClipboardItemType.Url || item.ItemType == ClipboardItemType.Code;
                bool isFileEarly = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);

                if (PeerManager.Instance != null && PeerManager.Instance.AliveCount > 0)
                {
                    int delivered = 0;

                    if (isTextType || !isFileEarly)
                    {
                        // TEXT: push directly to all peers
                        string peerTitle = !string.IsNullOrEmpty(item.FileName)
                            ? item.FileName
                            : (item.RawContent?.Length > 30 ? item.RawContent.Substring(0, 30) + "..." : item.RawContent ?? "");
                        delivered = await PeerManager.Instance.PushTextToAllPeers(
                            item.RawContent ?? "", peerTitle, item.ItemType.ToString());
                    }
                    else if (isFileEarly)
                    {
                        // FILE: upload directly to all peers via multipart
                        delivered = await PeerManager.Instance.PushFileToAllPeers(
                            item.FilePath, item.FileName ?? Path.GetFileName(item.FilePath), item.ItemType.ToString());
                    }

                    if (delivered > 0)
                    {
                        Logger.LogAction("PEER SYNC", $"Delivered to {delivered} peer(s) directly via P2P");
                    }
                    else
                    {
                        Logger.LogAction("PEER SYNC", $"Ã¢Å¡Â Ã¯Â¸Â Direct P2P delivery failed Ã¢â‚¬â€ no peers accepted the {(isTextType ? "text" : "file")}");
                    }
                }
                else
                {
                    Logger.LogAction("PEER SYNC", $"Ã¢Å¡Â Ã¯Â¸Â No peers online Ã¢â‚¬â€ {(isTextType ? "text" : "file")} not delivered");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER SYNC ERROR", ex.Message);
            }
        }


        /// <summary>
        /// Purge Firebase clipboard entries whose DownloadUrl contains a dead Cloudflare URL.
        /// Called when tunnel restarts and gets a new subdomain ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â old URLs become permanently unreachable.
        /// </summary>
        public static async Task PurgeStaleFileEntries(string deadUrl)
        {
            if (string.IsNullOrEmpty(deadUrl) || !deadUrl.Contains("trycloudflare.com")) return;
            
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;
                string myDeviceId = SettingsManager.Current.DeviceId ?? "";

                string url = (await AuthUrl($"clipboard/{pairingKey}.json"));
                var response = await _client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return;

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                int purged = 0;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    try
                    {
                        var entry = prop.Value;
                        // SAFETY: Only purge entries from THIS device ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â don't nuke other devices' entries
                        string sourceId = entry.TryGetProperty("SourceDeviceId", out var sid) ? sid.GetString() ?? "" : "";
                        string sourceName = entry.TryGetProperty("SourceDeviceName", out var sn) ? sn.GetString() ?? "" : "";
                        bool isMyEntry = (!string.IsNullOrEmpty(sourceId) && sourceId == myDeviceId) ||
                                         (string.IsNullOrEmpty(sourceId) && string.Equals(sourceName, SettingsManager.Current.DeviceName, StringComparison.OrdinalIgnoreCase));
                        if (!isMyEntry) continue;

                        // Check if Raw or DownloadUrl contains the dead Cloudflare URL
                        string raw = entry.TryGetProperty("Raw", out var r) ? r.GetString() ?? "" : "";
                        string dlUrl = entry.TryGetProperty("DownloadUrl", out var d) ? d.GetString() ?? "" : "";
                        
                        if (raw.Contains(deadUrl) || dlUrl.Contains(deadUrl))
                        {
                            string title = entry.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                            await DeleteFirebaseEntry(pairingKey, prop.Name);
                            purged++;
                            Logger.LogAction("PURGE", $"Deleted MY stale file entry: {title} (dead URL: {deadUrl.Substring(0, Math.Min(40, deadUrl.Length))}...)");
                        }
                    }
                    catch { }
                }

                if (purged > 0)
                    Logger.LogAction("PURGE", $"ÃƒÂ¢Ã…â€œÃ¢â‚¬Â¦ Purged {purged} of MY stale file entries with dead Cloudflare URL");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PURGE", $"Failed to purge stale entries: {ex.Message}");
            }
        }

        /// <summary>
        /// Delete a specific clipboard entry from Firebase by its key.
        /// </summary>
        public static async Task DeleteFirebaseEntry(string pairingKey, string entryKey)
        {
            try
            {
                string deleteUrl = (await AuthUrl($"clipboard/{pairingKey}/{entryKey}.json"));
                await _client.DeleteAsync(deleteUrl);
            }
            catch { }
        }

        /// <summary>
        /// Signal that this device has STARTED downloading a file.
        /// Sender can see this status to know the item was received.
        /// </summary>
        public static async Task MarkDownloading(string entryId)
        {
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;
                string myDeviceId = SettingsManager.Current.DeviceId ?? "PC";

                string statusUrl = (await AuthUrl($"clipboard/{pairingKey}/{entryId}/downloadStatus/{myDeviceId}.json"));
                string statusJson = $"{{\"status\":\"downloading\",\"startedAt\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                await _client.PutAsync(statusUrl, new StringContent(statusJson, Encoding.UTF8, "application/json"));
                Logger.LogAction("SYNC_STATUS", $"Signaled DOWNLOADING: {entryId}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("SYNC_STATUS", $"MarkDownloading failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Mark a file entry as downloaded by this device. When all target devices
        /// have downloaded, the entry is automatically deleted from Firebase.
        /// Offline devices are skipped (not blocking deletion).
        /// </summary>
        public static async Task MarkFileDownloaded(string entryId)
        {
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;
                string myDeviceId = SettingsManager.Current.DeviceId ?? "PC";

                // Step 1: Mark this device as having downloaded the file
                string markUrl = (await AuthUrl($"clipboard/{pairingKey}/{entryId}/downloadedBy/{myDeviceId}.json"));
                var markContent = new StringContent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), Encoding.UTF8, "application/json");
                await _client.PutAsync(markUrl, markContent);

                // Step 1b: Signal "downloaded" status so sender knows this device is done
                try
                {
                    string statusUrl = (await AuthUrl($"clipboard/{pairingKey}/{entryId}/downloadStatus/{myDeviceId}.json"));
                    string statusJson = $"{{\"status\":\"downloaded\",\"completedAt\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                    await _client.PutAsync(statusUrl, new StringContent(statusJson, Encoding.UTF8, "application/json"));
                    Logger.LogAction("SYNC_STATUS", $"Signaled DOWNLOADED: {entryId}");
                }
                catch { }

                // Step 2: Read the full entry to check if all targets have downloaded
                string entryUrl = (await AuthUrl($"clipboard/{pairingKey}/{entryId}.json"));
                var response = await _client.GetAsync(entryUrl);
                if (!response.IsSuccessStatusCode) return;

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null") return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Get targetDevices array
                var targetDevices = new List<string>();
                if (root.TryGetProperty("targetDevices", out var targets) && targets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in targets.EnumerateArray())
                    {
                        string devId = t.GetString() ?? "";
                        if (!string.IsNullOrEmpty(devId)) targetDevices.Add(devId);
                    }
                }

                // Get downloadedBy object
                var downloaded = new HashSet<string>();
                if (root.TryGetProperty("downloadedBy", out var dlBy) && dlBy.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in dlBy.EnumerateObject())
                        downloaded.Add(prop.Name);
                }

                // Step 3: Check active status of remaining targets
                // Mark devices as auto-complete if they've been offline for >1 hour
                // (gives them time to come online, but doesn't block cleanup forever)
                var remaining = targetDevices.Where(d => !downloaded.Contains(d)).ToList();
                if (remaining.Count > 0)
                {
                    try
                    {
                        string devicesUrl = (await AuthUrl($"active_devices/{pairingKey}.json"));
                        var devResponse = await _client.GetAsync(devicesUrl);
                        if (devResponse.IsSuccessStatusCode)
                        {
                            string devJson = await devResponse.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(devJson) && devJson != "null")
                            {
                                using var devDoc = JsonDocument.Parse(devJson);
                                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                const long OFFLINE_GRACE_MS = 60 * 60_000; // 1 hour grace period
                                foreach (var prop in devDoc.RootElement.EnumerateObject())
                                {
                                    var dev = prop.Value;
                                    string devId = dev.TryGetProperty("DeviceId", out var di) ? di.GetString() ?? prop.Name : prop.Name;
                                    if (!remaining.Contains(devId)) continue;

                                    bool isOnline = dev.TryGetProperty("IsOnline", out var io) && io.GetBoolean();
                                    long ts = dev.TryGetProperty("Timestamp", out var tsv) ? tsv.GetInt64() : 0;
                                    long offlineFor = now - ts;

                                    // Only auto-complete if offline for more than 1 hour
                                    if (!isOnline || offlineFor > OFFLINE_GRACE_MS)
                                    {
                                        string offlineUrl = (await AuthUrl($"clipboard/{pairingKey}/{entryId}/downloadedBy/{devId}.json"));
                                        await _client.PutAsync(offlineUrl, new StringContent("-1", Encoding.UTF8, "application/json"));
                                        downloaded.Add(devId);
                                        Logger.LogAction("SYNC_TRACK", $"Auto-completed offline device ({offlineFor / 60_000}min offline): {devId}");
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Step 4: If all target devices have downloaded (or are offline), delete entry
                if (targetDevices.Count > 0 && targetDevices.All(d => downloaded.Contains(d)))
                {
                    await DeleteFirebaseEntry(pairingKey, entryId);
                    Logger.LogAction("SYNC_CLEANUP", $"All {targetDevices.Count} devices done ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â entry deleted: {entryId}");
                }
                else if (targetDevices.Count == 0)
                {
                    // No targetDevices ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â delete immediately. New items wipe old ones anyway.
                    await DeleteFirebaseEntry(pairingKey, entryId);
                    Logger.LogAction("SYNC_CLEANUP", $"No targetDevices ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â entry deleted: {entryId}");
                }
                else
                {
                    int done = downloaded.Count;
                    int total = targetDevices.Count;
                    Logger.LogAction("SYNC_TRACK", $"Downloaded by {done}/{total} devices ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â waiting for {total - done} more");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SYNC_TRACK", $"MarkFileDownloaded error: {ex.Message}");
            }
        }

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
                var response = await _client.GetAsync(url);
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
                var response = await _client.GetAsync(url);
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
                                if (!string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com"))
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
                var response = await _client.GetAsync(url);
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
                                if (!string.IsNullOrEmpty(lanUrl) && lanUrl.StartsWith("http"))
                                {
                                    Logger.LogAction("FIREBASE SSE", $"Found sender LAN URL by name '{senderDeviceName}': {lanUrl}");
                                    return lanUrl;
                                }
                            }
                            // Also check the Url field (which might be the LAN URL)
                            if (prop.Value.TryGetProperty("Url", out var urlProp))
                            {
                                string directUrl = urlProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(directUrl) && directUrl.StartsWith("http") && !directUrl.Contains("trycloudflare"))
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
    }
}
