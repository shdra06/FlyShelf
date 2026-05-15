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
    public class FirebaseSyncManager
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
                    return; // URL hasn't changed ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â skip Firebase write
                }

                var payload = new
                {
                    DeviceId = SettingsManager.Current.DeviceId,
                    DeviceName = SettingsManager.Current.DeviceName,
                    DeviceType = "PC",
                    Url = localIp.Contains("http") ? localIp : url,
                    LocalIp = localIp,
                    GlobalUrl = url.Contains("trycloudflare.com") ? url : "",
                    TlsUrl = NetworkSyncServer.Instance?.TlsUrl ?? "",
                    TlsThumbprint = NetworkSyncServer.Instance?.TlsThumbprint ?? "",
                    IsOnline = isOnline,
                    Timestamp = NetworkClock.UtcNowMs
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Use PUT to register or update our specific Device node (scoped to pairing key)
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) { Logger.LogAction("FIREBASE SYNC", "Skipped device registration ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â no pairing key"); return; }
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
        /// Force-send clipboard items to specific target devices via Firebase forced_sync node.
        /// Files of ANY size are supported ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â uses Cloudflare download URLs (no upload needed).
        /// </summary>
        public static async Task<int> ForceSendToDevices(List<ClipboardItem> items, List<string> targetDeviceIds)
        {
            int sent = 0;
            string deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

            foreach (var targetId in targetDeviceIds)
            {
                foreach (var item in items)
                {
                    try
                    {
                        bool isFile = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
                        string downloadUrl = "";
                        string raw = item.RawContent ?? "";

                        if (isFile)
                        {
                            // Use Cloudflare URL (preferred ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â no size limit, instant)
                            if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com"))
                            {
                                downloadUrl = $"{CachedGlobalUrl}/download?path={Uri.EscapeDataString(item.FilePath)}";
                                raw = downloadUrl;
                                long fileSize = new FileInfo(item.FilePath).Length;
                                Logger.LogAction("FORCED SYNC", $"File '{item.FileName}' ({fileSize / (1024*1024)}MB) ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ Cloudflare URL");
                            }
                            else
                            {
                                // Wait for Cloudflare tunnel
                                Logger.LogAction("FORCED SYNC", $"No Cloudflare yet ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â waiting up to 20s...");
                                for (int i = 0; i < 40; i++)
                                {
                                    await Task.Delay(500);
                                    if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com")) break;
                                }

                                if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com"))
                                {
                                    downloadUrl = $"{CachedGlobalUrl}/download?path={Uri.EscapeDataString(item.FilePath)}";
                                    raw = downloadUrl;
                                    Logger.LogAction("FORCED SYNC", $"File '{item.FileName}' ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ Cloudflare URL (delayed)");
                                }
                                else
                                {
                                    // No Cloudflare tunnel available — cannot send file remotely
                                    Logger.LogAction("FORCED SYNC", $"⚠️ Cannot send file '{item.FileName}' remotely — no Cloudflare tunnel");
                                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                        AdvanceClip.Windows.ToastWindow.ShowToast($"⚠️ {item.FileName} — can't share remotely (no tunnel)");
                                    });
                                    continue;
                                }
                            }
                        }

                        var payload = new
                        {
                            Title = string.IsNullOrEmpty(item.FileName) ? (raw.Length > 30 ? raw.Substring(0, 30) + "..." : raw) : item.FileName,
                            Type = item.ItemType.ToString(),
                            Raw = raw,
                            DownloadUrl = downloadUrl,
                            FileName = item.FileName ?? "",
                            FileSize = isFile ? new FileInfo(item.FilePath).Length : 0,
                            SenderUrl = !string.IsNullOrEmpty(CachedGlobalUrl) ? CachedGlobalUrl : CachedLocalUrl ?? "",
                            ForcedBy = deviceName,
                            ForcedAt = NetworkClock.UtcNowMs,
                            SourceDeviceName = deviceName,
                            SourceDeviceType = "PC",
                            Timestamp = NetworkClock.UtcNowMs
                        };

                        string json = JsonSerializer.Serialize(payload);
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        string url2 = (await AuthUrl($"forced_sync/{targetId}.json"));
                        var response = await _client.PostAsync(url2, content);
                        if (response.IsSuccessStatusCode) sent++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("FORCED SYNC", $"Send error: {ex.Message}");
                    }
                }
            }
            return sent;
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
                                    long deviceTs = ts.GetInt64();
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
                                    deviceTs = ts.GetInt64();

                                if (deviceTs > 0 && (nowMs - deviceTs) < STALE_THRESHOLD_MS)
                                {
                                    // Old-format device is still active ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â keep it
                                    Logger.LogAction("FIREBASE CLEANUP", $"Keeping active old-format device: {prop.Name}");
                                    continue;
                                }

                                // Stale GUID-based entry ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â safe to remove
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

        // ÃƒÂ¢Ã¢â‚¬Â¢Ã‚ÂÃƒÂ¢Ã¢â‚¬Â¢Ã‚ÂÃƒÂ¢Ã¢â‚¬Â¢Ã‚Â Device Groups CRUD ÃƒÂ¢Ã¢â‚¬Â¢Ã‚ÂÃƒÂ¢Ã¢â‚¬Â¢Ã‚ÂÃƒÂ¢Ã¢â‚¬Â¢Ã‚Â

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
                var payload = new { name, deviceNames };
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




