using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    public class FirebaseListener
    {
        // Separate clients: SSE stream needs infinite timeout, forced sync polls use short timeout
        private static readonly HttpClient _streamClient = new HttpClient() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static readonly HttpClient _pollClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
        private const string FIREBASE_BASE = "https://advance-sync-default-rtdb.firebaseio.com";
        
        /// <summary>Wraps a Firebase REST URL with auth token.</summary>
        private static async Task<string> AuthUrl(string path)
        {
            return await FirebaseAuthManager.AuthenticateUrl($"{FIREBASE_BASE}/{path}");
        }
        
        /// <summary>Returns the scoped clipboard URL for the current pairing key (private sync room).</summary>
        private static async Task<string> GetScopedClipboardUrl()
        {
            string pairingKey = DevicePairingManager.EnsurePairingKey();
            return await AuthUrl($"clipboard/{pairingKey}.json");
        }
        private FlyShelfViewModel _viewModel;
        private long _lastProcessedTimestamp = 0;
        private CancellationTokenSource? _cts = null;
        private HashSet<string> _processedIds = new HashSet<string>();

        public FirebaseListener(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public void StartPolling()
        {
            StopPolling();

            // CRITICAL: Do not listen to Firebase unless device is paired
            if (!DevicePairingManager.HasPairingKey)
            {
                Logger.LogAction("FIREBASE LISTENER", "Blocked Ã¢â‚¬â€ no pairing key. Pair with another device to enable cloud sync.");
                return;
            }

            _cts = new CancellationTokenSource();
            // Backlog: process items from the last 5 minutes (catch-up for devices that connect late)
            _lastProcessedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
            _processedIds.Clear();

            Logger.LogAction("FIREBASE LISTENER", "Starting SSE real-time stream + forced sync poller.");

            // 1. Main clipboard feed: DISABLED — Firebase is no longer used for content transfer
            //    All content now flows via P2P (LAN/Cloudflare direct push)
            // Task.Run(() => RunSSEStream(_cts.Token));

            // 2. Forced sync: real-time SSE stream (replaces 5s polling)
            Task.Run(() => RunForcedSyncSSE(_cts.Token));

            // 3. Peer URL discovery: SSE stream on active_devices — instant reconnect when any peer comes online
            Task.Run(() => RunPeerDiscoverySSE(_cts.Token));
        }

        public void StopPolling()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                _cts = null;
            }
        }

        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
        // SSE STREAM: Firebase REST API with Accept: text/event-stream
        // Delivers new clipboard items in ~100-300ms instead of 3s polling
        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
        private async Task RunSSEStream(CancellationToken ct)
        {
            int reconnectDelay = 1000; // Start with 1s, exponential backoff on failures
            const int MAX_RECONNECT_DELAY = 30_000;

            while (!ct.IsCancellationRequested)
            {
                // Guard: exit if pairing key was cleared
                if (!DevicePairingManager.HasPairingKey)
                {
                    Logger.LogAction("FIREBASE SSE", "No pairing key Ã¢â‚¬â€ stream exiting.");
                    return;
                }

                try
                {
                    string streamUrl = await GetScopedClipboardUrl();

                    var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                    request.Headers.Add("Accept", "text/event-stream");

                    using var response = await _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.LogAction("FIREBASE SSE", $"Stream HTTP {(int)response.StatusCode} Ã¢â‚¬â€ retrying in {reconnectDelay}ms");
                        await Task.Delay(reconnectDelay, ct);
                        reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
                        continue;
                    }

                    // Connected successfully Ã¢â‚¬â€ reset backoff
                    reconnectDelay = 1000;
                    Logger.LogAction("FIREBASE SSE", "Real-time stream CONNECTED Ã¢Å“â€œ");

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    string currentEvent = "";
                    string currentData = "";

                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break; // Stream ended

                        if (line.StartsWith("event:"))
                        {
                            currentEvent = line.Substring(6).Trim();
                        }
                        else if (line.StartsWith("data:"))
                        {
                            currentData = line.Substring(5).Trim();
                        }
                        else if (string.IsNullOrEmpty(line))
                        {
                            // Empty line = end of SSE message block
                            if (!string.IsNullOrEmpty(currentData) && currentData != "null")
                            {
                                ProcessSSEEvent(currentEvent, currentData);
                            }
                            currentEvent = "";
                            currentData = "";
                        }
                    }

                    // Stream ended (server closed connection) Ã¢â‚¬â€ reconnect
                    Logger.LogAction("FIREBASE SSE", "Stream closed by server Ã¢â‚¬â€ reconnecting...");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("FIREBASE SSE", $"Stream error: {ex.Message} Ã¢â‚¬â€ retrying in {reconnectDelay}ms");
                    try { await Task.Delay(reconnectDelay, ct); } catch { break; }
                    reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
                }
            }

            Logger.LogAction("FIREBASE SSE", "Real-time stream STOPPED.");
        }

        private void ProcessSSEEvent(string eventType, string jsonData)
        {
            // Firebase SSE events:
            // "put"   Ã¢â€ â€™ { "path": "/key" or "/", "data": { ... } }
            // "patch" Ã¢â€ â€™ { "path": "/key", "data": { ... } }
            // "keep-alive" Ã¢â€ â€™ ignore

            if (eventType == "keep-alive") return;
            if (eventType != "put" && eventType != "patch") return;

            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;

                string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                if (!root.TryGetProperty("data", out var data)) return;
                
                // data could be null (deletion event)
                if (data.ValueKind == JsonValueKind.Null) return;

                if (path == "/")
                {
                    // Full payload refresh Ã¢â‚¬â€ process all items
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        ProcessFullPayload(data);
                    }
                }
                else
                {
                    // Single item update Ã¢â‚¬â€ path is "/{key}"
                    string itemKey = path.TrimStart('/');
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        ProcessSingleItem(itemKey, data);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"Parse error: {ex.Message}");
            }
        }

        private void ProcessFullPayload(JsonElement data)
        {
            var sortedItems = new List<CloudItem>();

            foreach (JsonProperty property in data.EnumerateObject())
            {
                var item = TryParseCloudItem(property.Name, property.Value);
                if (item != null) sortedItems.Add(item);
            }

            if (sortedItems.Count == 0) return;

            var newItems = sortedItems.OrderBy(x => x.Timestamp).ToList();
            _lastProcessedTimestamp = newItems.Last().Timestamp;

            foreach (var cloudItem in newItems)
            {
                _processedIds.Add(cloudItem.Id);
                InjectCloudItem(cloudItem);
            }
            
            // Memory safety: sliding window to prevent unbounded growth
            // Keep the newest 250 IDs instead of clearing all (which could re-process items)
            if (_processedIds.Count > 500)
            {
                var recentIds = new HashSet<string>(newItems.TakeLast(250).Select(ci => ci.Id));
                _processedIds.IntersectWith(recentIds);
                // Ensure all latest items are still tracked
                foreach (var ci in newItems.TakeLast(250)) _processedIds.Add(ci.Id);
            }
        }

        private void ProcessSingleItem(string key, JsonElement data)
        {
            var item = TryParseCloudItem(key, data);
            if (item != null)
            {
                _processedIds.Add(item.Id);
                if (item.Timestamp > _lastProcessedTimestamp)
                    _lastProcessedTimestamp = item.Timestamp;
                InjectCloudItem(item);
            }
        }

        private CloudItem? TryParseCloudItem(string key, JsonElement data)
        {
            if (!data.TryGetProperty("Timestamp", out JsonElement tsElement)) return null;

            long timestamp = 0;
            if (tsElement.ValueKind == JsonValueKind.Number)
                timestamp = tsElement.GetInt64();

            // Skip already processed
            if (_processedIds.Contains(key)) return null;

            // Skip items older than session start
            if (timestamp <= _lastProcessedTimestamp) return null;

            // Self-echo prevention Ã¢â‚¬â€ use SourceDeviceId (precise) with DeviceName fallback
            string sourceDeviceId = data.TryGetProperty("SourceDeviceId", out var srcId) ? srcId.GetString() ?? "" : "";
            string sourceDevice = data.TryGetProperty("SourceDeviceName", out var srcName) ? srcName.GetString() ?? "" : "";
            string sourceType = data.TryGetProperty("SourceDeviceType", out var srcType) ? srcType.GetString() ?? "" : "";
            string myDeviceId = SettingsManager.Current.DeviceId ?? "";
            string myDeviceName = SettingsManager.Current.DeviceName ?? "";

            // Primary: filter by DeviceId (guaranteed unique)
            if (!string.IsNullOrEmpty(sourceDeviceId) && !string.IsNullOrEmpty(myDeviceId) &&
                sourceDeviceId == myDeviceId)
            {
                _processedIds.Add(key);
                return null;
            }
            // Fallback: filter by DeviceName + type (for old payloads without SourceDeviceId)
            if (string.IsNullOrEmpty(sourceDeviceId) &&
                !string.IsNullOrEmpty(myDeviceName) &&
                string.Equals(sourceDevice, myDeviceName, StringComparison.OrdinalIgnoreCase) &&
                sourceType == "PC")
            {
                _processedIds.Add(key);
                return null;
            }

            string rawContent = data.TryGetProperty("Raw", out var t3) ? t3.GetString() : "";
            string itemType = data.TryGetProperty("Type", out var t) ? t.GetString() : "Text";
            string title = data.TryGetProperty("Title", out var t2) ? t2.GetString() : "Cloud Payload";
            string downloadUrl = data.TryGetProperty("DownloadUrl", out var t6) ? t6.GetString() : "";
            string senderUrl = data.TryGetProperty("SenderUrl", out var t7) ? t7.GetString() : "";

            // AES-256-GCM decryption: if Encrypted flag is set, decrypt sensitive fields
            bool isEncrypted = data.TryGetProperty("Encrypted", out var encProp) && encProp.ValueKind == JsonValueKind.True;
            if (isEncrypted)
            {
                try
                {
                    rawContent = SyncCrypto.Decrypt(rawContent) ?? rawContent;
                    title = SyncCrypto.Decrypt(title) ?? title;
                    if (!string.IsNullOrEmpty(downloadUrl))
                        downloadUrl = SyncCrypto.Decrypt(downloadUrl) ?? downloadUrl;
                }
                catch (Exception cryptoEx)
                {
                    Logger.LogAction("SYNC_CRYPTO", $"Decryption failed for item {key}: {cryptoEx.Message}");
                    // Fall through with encrypted values Ã¢â‚¬â€ they'll appear as garbage but won't crash
                }
            }

            // Skip empty text items Ã¢â‚¬â€ never allow blank cards
            bool isFileType = itemType == "Image" || itemType == "ImageLink" || itemType == "Pdf" ||
                              itemType == "Archive" || itemType == "Video" || itemType == "Document" ||
                              itemType == "File" || itemType == "Presentation" || itemType == "Audio";
            if (!isFileType && string.IsNullOrWhiteSpace(rawContent)) return null;

            return new CloudItem
            {
                Id = key,
                Timestamp = timestamp,
                Type = itemType,
                Title = title,
                Raw = rawContent,
                DownloadUrl = downloadUrl,
                SenderUrl = senderUrl,
                FileHash = data.TryGetProperty("FileHash", out var fh) ? fh.GetString() ?? "" : "",
                SourceDeviceName = sourceDevice,
                SourceDeviceId = sourceDeviceId
            };
        }

        private void InjectCloudItem(CloudItem cloudItem)
        {
            Logger.LogAction("FIREBASE SSE", $"Ã¢Å¡Â¡ INSTANT from '{cloudItem.SourceDeviceName}': {cloudItem.Type}");

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Strict duplicate enforcement Ã¢â‚¬â€ catches P2P + Firebase race condition
                // Check by exact Raw match
                var existsLocally = _viewModel.DroppedItems.Any(i => i.RawContent == cloudItem.Raw && !string.IsNullOrWhiteSpace(cloudItem.Raw));
                
                // Also check by title/filename match for images (P2P may set Raw differently)
                if (cloudItem.Type == "ImageLink" && _viewModel.DroppedItems.Any(i => i.FileName == cloudItem.Title))
                    existsLocally = true;
                
                // P2P text dedup: if text arrived via P2P first, it's already in the shelf
                // Check by content prefix match (first 100 chars) to handle minor formatting differences
                if (!existsLocally && cloudItem.Type == "Text" && !string.IsNullOrWhiteSpace(cloudItem.Raw))
                {
                    string prefix = cloudItem.Raw.Length > 100 ? cloudItem.Raw.Substring(0, 100) : cloudItem.Raw;
                    existsLocally = _viewModel.DroppedItems.Any(i => 
                        i.RawContent != null && i.RawContent.StartsWith(prefix) && i.RawContent.Length == cloudItem.Raw.Length);
                }

                if (existsLocally)
                {
                    Logger.LogAction("FIREBASE SSE", "Skipped duplicate Ã¢â‚¬â€ already exists locally (P2P or local copy).");
                    return;
                }

                bool isFilePayload = cloudItem.Type == "ImageLink" || cloudItem.Type == "Image" || cloudItem.Type == "Pdf" ||
                                    cloudItem.Type == "Archive" || cloudItem.Type == "Video" || cloudItem.Type == "Document" ||
                                    cloudItem.Type == "Presentation" || cloudItem.Type == "Audio" || cloudItem.Type == "File";

                // Resolve download URL: try every possible combination to get a valid HTTP URL
                string resolvedUrl = cloudItem.Raw ?? "";

                // Step 1: If Raw is already a full HTTP URL, use it
                if (!resolvedUrl.StartsWith("http"))
                {
                    // Step 2: DownloadUrl might be a full URL
                    if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && cloudItem.DownloadUrl.StartsWith("http"))
                        resolvedUrl = cloudItem.DownloadUrl;
                    // Step 3: DownloadUrl is relative but SenderUrl is absolute
                    else if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && !string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.StartsWith("http"))
                        resolvedUrl = cloudItem.SenderUrl.TrimEnd('/') + (cloudItem.DownloadUrl.StartsWith("/") ? cloudItem.DownloadUrl : "/" + cloudItem.DownloadUrl);
                    // Step 4: Raw is a relative path like /download?path=..., combine with SenderUrl
                    else if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.StartsWith("http") && resolvedUrl.StartsWith("/"))
                        resolvedUrl = cloudItem.SenderUrl.TrimEnd('/') + resolvedUrl;
                    // Step 5: Raw contains a file path like C:\... Ã¢â‚¬â€ try to build URL from SenderUrl
                    else if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.StartsWith("http") && isFilePayload)
                        resolvedUrl = cloudItem.SenderUrl.TrimEnd('/') + "/download?path=" + Uri.EscapeDataString(resolvedUrl);
                }
                cloudItem.Raw = resolvedUrl;

                if (isFilePayload && resolvedUrl.StartsWith("http"))
                {
                    _ = FetchAndInjectCloudFile(cloudItem);
                }
                else
                {
                    // Skip blank text items Ã¢â‚¬â€ never create empty cards
                    if (string.IsNullOrWhiteSpace(cloudItem.Raw))
                    {
                        Logger.LogAction("FIREBASE SSE", "Skipped empty/whitespace-only text item from cloud.");
                        return;
                    }

                    // Detect transfer method: Cloudflare tunnel vs Firebase cloud
                    bool isCloudflare = (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com")) ||
                                        (!string.IsNullOrEmpty(cloudItem.Raw) && cloudItem.Raw.Contains(".trycloudflare.com"));
                    var clip = new ClipboardItem
                    {
                        RawContent = cloudItem.Raw,
                        FileName = cloudItem.Title,
                        Extension = cloudItem.Type == "Url" ? "LINK" : "CLOUD",
                        ItemType = cloudItem.Type == "Url" ? ClipboardItemType.Url : ClipboardItemType.Text,
                        SourceDeviceName = cloudItem.SourceDeviceName ?? "Remote",
                        SourceDeviceType = "Mobile",
                        TransferMethod = isCloudflare ? "Cloudflare" : "Cloud"
                    };
                    clip.EvaluateSmartActions();
                    _viewModel.DroppedItems.Insert(0, clip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    
                    // Mark as cloud-sourced so HandleDrop doesn't re-push to Firebase
                    string txtFp = $"TXT::{(cloudItem.Raw ?? "").Substring(0, Math.Min(200, (cloudItem.Raw ?? "").Length))}";
                    _viewModel.MarkAsCloudSourced(txtFp);

                    // Auto-copy text to system clipboard for instant paste
                    // CRITICAL: Must guard with _isWritingClipboard to prevent echo loop
                    // (clipboard change Ã¢â€ â€™ recapture Ã¢â€ â€™ push back to Firebase Ã¢â€ â€™ infinite loop)
                    _ = Task.Run(async () =>
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                MainWindow.SetWritingClipboard(true);
                                if (!string.IsNullOrEmpty(cloudItem.Raw))
                                    System.Windows.Clipboard.SetText(cloudItem.Raw);
                                // Delay clearing Ã¢â‚¬â€ WM_CLIPBOARDUPDATE is dispatched async by Windows
                                await System.Threading.Tasks.Task.Delay(500);
                            }
                            catch { }
                            finally { MainWindow.SetWritingClipboard(false); }
                        });
                    });

                    AdvanceClip.Windows.ToastWindow.ShowToast($"Ã¢Å¡Â¡ {cloudItem.SourceDeviceName}: {(cloudItem.Raw?.Length > 40 ? cloudItem.Raw.Substring(0, 40) + "..." : cloudItem.Raw)}");

                    // Receipt confirmation: mark this item as received by this device
                    if (!string.IsNullOrEmpty(cloudItem.Id))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await FirebaseSyncManager.MarkFileDownloaded(cloudItem.Id); }
                            catch { }
                        });
                    }
                }
            });
        }

        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
        // FORCED SYNC SSE: Real-time stream for items force-sent to this device
        // Replaces the old 5s polling loop Ã¢â‚¬â€ delivery is now ~100-300ms
        // Ã¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢ÂÃ¢â€¢Â
        private async Task RunForcedSyncSSE(CancellationToken ct)
        {
            int reconnectDelay = 1000;
            const int MAX_RECONNECT_DELAY = 30_000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string deviceId = SettingsManager.Current.DeviceId;
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    string forcedUrl = (await AuthUrl($"forced_sync/{deviceId}.json"));
                    var request = new HttpRequestMessage(HttpMethod.Get, forcedUrl);
                    request.Headers.Add("Accept", "text/event-stream");

                    using var response = await _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.LogAction("FORCED SYNC SSE", $"HTTP {(int)response.StatusCode} Ã¢â‚¬â€ retrying in {reconnectDelay}ms");
                        await Task.Delay(reconnectDelay, ct);
                        reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
                        continue;
                    }

                    reconnectDelay = 1000;
                    Logger.LogAction("FORCED SYNC SSE", "Real-time stream CONNECTED Ã¢Å“â€œ");

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    string currentEvent = "";
                    string currentData = "";

                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break;

                        if (line.StartsWith("event:"))
                            currentEvent = line.Substring(6).Trim();
                        else if (line.StartsWith("data:"))
                            currentData = line.Substring(5).Trim();
                        else if (string.IsNullOrEmpty(line))
                        {
                            if (!string.IsNullOrEmpty(currentData) && currentData != "null" && currentEvent == "put")
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(currentData);
                                    var root = doc.RootElement;
                                    string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                                    if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                                    {
                                        currentEvent = ""; currentData = ""; continue;
                                    }

                                    // Re-serialize the data to JSON for ProcessForcedSyncPayload
                                    if (path == "/")
                                    {
                                        // Full payload: data is the entire forced_sync/{deviceId} node
                                        if (data.ValueKind == JsonValueKind.Object)
                                            ProcessForcedSyncPayload(data.GetRawText(), deviceId);
                                    }
                                    else
                                    {
                                        // Single item: path is /{key}, data is the item
                                        string key = path.TrimStart('/');
                                        if (data.ValueKind == JsonValueKind.Object)
                                        {
                                            string wrappedJson = "{" + $"\"{key}\":{data.GetRawText()}" + "}";
                                            ProcessForcedSyncPayload(wrappedJson, deviceId);
                                        }
                                    }
                                }
                                catch (Exception parseEx)
                                {
                                    Logger.LogAction("FORCED SYNC SSE", $"Parse error: {parseEx.Message}");
                                }
                            }
                            currentEvent = "";
                            currentData = "";
                        }
                    }

                    Logger.LogAction("FORCED SYNC SSE", "Stream closed Ã¢â‚¬â€ reconnecting...");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("FORCED SYNC SSE", $"Stream error: {ex.Message} Ã¢â‚¬â€ retrying in {reconnectDelay}ms");
                    try { await Task.Delay(reconnectDelay, ct); } catch { break; }
                    reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
                }
            }

            Logger.LogAction("FORCED SYNC SSE", "Stream STOPPED.");
        }

        // ═══════════════════════════════════════════════════════════════
        // PEER DISCOVERY SSE: Watch active_devices for URL changes
        // When any device posts a new URL, all paired devices instantly
        // pick it up, update PeerManager, and the URL auto-deletes.
        // ═══════════════════════════════════════════════════════════════

        private async Task RunPeerDiscoverySSE(CancellationToken ct)
        {
            const int INITIAL_RECONNECT = 2000;
            const int MAX_RECONNECT = 30000;
            int reconnectDelay = INITIAL_RECONNECT;
            string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string pairingKey = DevicePairingManager.EnsurePairingKey();
                    if (string.IsNullOrEmpty(pairingKey))
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    string url = await AuthUrl($"active_devices/{pairingKey}.json");
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Accept", "text/event-stream");

                    var response = await _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.LogAction("PEER SSE", $"HTTP {(int)response.StatusCode} — retrying in {reconnectDelay}ms");
                        await Task.Delay(reconnectDelay, ct);
                        reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT);
                        continue;
                    }

                    Logger.LogAction("PEER SSE", "Watching active_devices for URL changes ✓");
                    reconnectDelay = INITIAL_RECONNECT;

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    string currentEvent = "";
                    string currentData = "";

                    while (!reader.EndOfStream && !ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break;

                        if (line.StartsWith("event:")) currentEvent = line.Substring(6).Trim();
                        else if (line.StartsWith("data:")) currentData = line.Substring(5).Trim();
                        else if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(currentData))
                        {
                            // Process URL change event — only put/patch with valid JSON data
                            if ((currentEvent == "put" || currentEvent == "patch") && 
                                !string.IsNullOrWhiteSpace(currentData) && currentData != "null")
                            {
                                _ = Task.Run(() => ProcessPeerUrlChange(currentData, myDeviceId, ct));
                            }
                            currentEvent = "";
                            currentData = "";
                        }
                    }

                    Logger.LogAction("PEER SSE", "Stream closed — reconnecting...");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("PEER SSE", $"Error: {ex.Message} — retrying in {reconnectDelay}ms");
                    try { await Task.Delay(reconnectDelay, ct); } catch { break; }
                    reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT);
                }
            }

            Logger.LogAction("PEER SSE", "Discovery stream STOPPED.");
        }

        private async Task ProcessPeerUrlChange(string jsonData, string myDeviceId, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonData) || jsonData == "null")
                    return;
                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;

                // Firebase SSE "put" has { "path": "/...", "data": {...} }
                if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                    return;

                // Could be a single device update (path: "/DeviceId") or full snapshot (path: "/")
                string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";

                var peerManager = PeerManager.Instance;
                if (peerManager == null) return;

                bool anyNewPeer = false;

                if (path == "/")
                {
                    // Full snapshot — scan all devices
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in data.EnumerateObject())
                        {
                            if (ProcessSingleDeviceUrl(prop.Name, prop.Value, myDeviceId, peerManager))
                                anyNewPeer = true;
                        }
                    }
                }
                else
                {
                    // Single device update — path is like "/DeviceId" or "/DeviceId/GlobalUrl"
                    string deviceKey = path.TrimStart('/').Split('/')[0];
                    if (!string.IsNullOrEmpty(deviceKey) && deviceKey != myDeviceId)
                    {
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            if (ProcessSingleDeviceUrl(deviceKey, data, myDeviceId, peerManager))
                                anyNewPeer = true;
                        }
                    }
                }

                // If we detected new/updated peer URLs, trigger handshake immediately
                if (anyNewPeer)
                {
                    Logger.LogAction("PEER SSE", "New peer URL detected — handshaking now...");
                    // Small delay to let the other device's server be ready
                    await Task.Delay(1000, ct);
                    await peerManager.ForceResync();

                    // Auto-delete: wait 5 seconds then clean the URL from Firebase
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000, ct);
                        await CleanupPeerUrlFromFirebase(myDeviceId, ct);
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER SSE", $"ProcessPeerUrlChange error: {ex.Message}");
            }
        }

        private bool ProcessSingleDeviceUrl(string deviceKey, JsonElement data, string myDeviceId, PeerManager peerManager)
        {
            if (deviceKey == myDeviceId) return false;

            string globalUrl = data.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
            string localUrl = data.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
            string deviceName = data.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? deviceKey : deviceKey;

            if (string.IsNullOrEmpty(globalUrl) && string.IsNullOrEmpty(localUrl))
                return false;

            Logger.LogAction("PEER SSE", $"⚡ URL arrived for {deviceName}: CF={globalUrl} LAN={localUrl}");
            return true;
        }

        private async Task CleanupPeerUrlFromFirebase(string myDeviceId, CancellationToken ct)
        {
            try
            {
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (string.IsNullOrEmpty(pairingKey)) return;

                // Only delete OUR OWN URL (each device cleans up after itself)
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string gUrl = await AuthUrl($"active_devices/{pairingKey}/{myDeviceId}/GlobalUrl.json");
                await client.DeleteAsync(gUrl);
                string lUrl = await AuthUrl($"active_devices/{pairingKey}/{myDeviceId}/LocalIp.json");
                await client.DeleteAsync(lUrl);

                Logger.LogAction("PEER SSE", $"🧹 Auto-cleaned our URLs from Firebase (5s TTL)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER SSE", $"Cleanup error: {ex.Message}");
            }
        }

        private async Task FetchAndInjectCloudFile(CloudItem cloudItem)
        {
            ClipboardItem? progressClip = null;
            string filePath = "";
            try
            {
                string senderName = string.IsNullOrWhiteSpace(cloudItem.SourceDeviceName) ? "CloudSync" : cloudItem.SourceDeviceName.Replace(" ", "_");
                string extractPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", senderName);
                Directory.CreateDirectory(extractPath);

                string fallbackExt = cloudItem.Type == "Pdf" ? ".pdf" : cloudItem.Type == "Archive" ? ".zip" : cloudItem.Type == "Video" ? ".mp4" : cloudItem.Type == "Audio" ? ".mp3" : cloudItem.Type == "Document" ? ".docx" : cloudItem.Type == "Presentation" ? ".pptx" : ".jpg";
                string safeTitle = (cloudItem.Title ?? "file").Replace("/", "_").Replace("\\", "_");
                filePath = Path.Combine(extractPath, safeTitle);
                if (!Path.HasExtension(safeTitle)) filePath += fallbackExt;

                int counter = 1;
                string basePath = filePath;
                while (File.Exists(filePath))
                {
                    filePath = Path.Combine(extractPath, $"{Path.GetFileNameWithoutExtension(basePath)}_{counter++}{Path.GetExtension(basePath)}");
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    progressClip = new ClipboardItem
                    {
                        RawContent = $"Ã¢ÂÂ³ Downloading from {cloudItem.SourceDeviceName}...",
                        FileName = cloudItem.Title,
                        Extension = "DOWNLOADING",
                        ItemType = ClipboardItemType.Text
                    };
                    _viewModel.DroppedItems.Insert(0, progressClip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                });

                // Signal "downloading" to Firebase Ã¢â‚¬â€ sender can see this device is actively receiving
                if (!string.IsNullOrEmpty(cloudItem.Id))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await FirebaseSyncManager.MarkDownloading(cloudItem.Id); }
                        catch { }
                    });
                }

                // AUTHENTICATION: /download requires pairing key or PIN
                // Enhanced download with fallback: try primary URL, then alternative URLs
                HttpResponseMessage response = null;
                int maxRetries = 2;
                int[] retryDelays = { 500, 1500 }; // Fast retries — DNS errors skip instantly

                using var downloadClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
                // Add authentication headers so the sender's /download endpoint accepts the request
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(pairingKey))
                    downloadClient.DefaultRequestHeaders.Add("X-Pairing-Key", pairingKey);
                downloadClient.DefaultRequestHeaders.Add("X-FlyShelf-Client", "DesktopSync");
                
                // Build fallback URL list: primary first, then alternatives
                var urlsToTry = new List<string> { cloudItem.Raw };
                
                // If primary is Cloudflare, add DownloadUrl and SenderUrl-based alternatives
                if (cloudItem.Raw.Contains(".trycloudflare.com"))
                {
                    // Try to get the sender's CURRENT tunnel URL from Firebase
                    // The entry's URL may be stale (sender restarted tunnel)
                    try
                    {
                        string senderCurrentUrl = await FirebaseSyncManager.GetSenderCurrentUrl(cloudItem.SourceDeviceId);
                        // Fallback: ID-based lookup often fails because SourceDeviceId (e.g. "LAPTOP-JMHPDLG7")
                        // doesn't match the active_devices key (e.g. "PC_LAPTOP-JMHPDLG7_SONAL").
                        // Try name-based scan as fallback.
                        if (string.IsNullOrEmpty(senderCurrentUrl))
                            senderCurrentUrl = await FirebaseSyncManager.FindSenderUrlByName(cloudItem.SourceDeviceName);
                        if (!string.IsNullOrEmpty(senderCurrentUrl) && senderCurrentUrl.Contains(".trycloudflare.com"))
                        {
                            // Extract the /download?path=... from the original URL and rebuild with current tunnel
                            var pathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"(/download\?path=.+)$");
                            if (pathMatch.Success)
                            {
                                string freshUrl = senderCurrentUrl.TrimEnd('/') + pathMatch.Groups[1].Value;
                                if (freshUrl != cloudItem.Raw)
                                {
                                    urlsToTry.Insert(0, freshUrl); // Try fresh URL FIRST
                                    Logger.LogAction("FIREBASE SSE", $"Using sender's current tunnel URL: {senderCurrentUrl}");
                                }
                            }
                        }
                    }
                    catch { /* Best effort Ã¢â‚¬â€ fall back to original URL */ }

                    // DownloadUrl might be a Firebase Storage URL (firebasestorage.googleapis.com)
                    if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && cloudItem.DownloadUrl.StartsWith("http") && cloudItem.DownloadUrl != cloudItem.Raw)
                        urlsToTry.Add(cloudItem.DownloadUrl);
                    
                    // SenderUrl might be a different Cloudflare URL (tunnel restarted)
                    if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com") && !cloudItem.Raw.Contains(cloudItem.SenderUrl))
                    {
                        // Rebuild download URL with the sender's current tunnel URL
                        var pathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"/download\?path=(.+)$");
                        if (pathMatch.Success)
                            urlsToTry.Add($"{cloudItem.SenderUrl.TrimEnd('/')}/download?path={pathMatch.Groups[1].Value}");
                    }

                    // LAST RESORT: Try sender's LAN URL — works if both PCs are on the same network
                    // even when all Cloudflare tunnel URLs are dead (DNS errors)
                    try
                    {
                        string lanUrl = await FirebaseSyncManager.FindSenderLanUrl(cloudItem.SourceDeviceName);
                        if (!string.IsNullOrEmpty(lanUrl))
                        {
                            var lanPathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"(/download\?path=.+)$");
                            if (lanPathMatch.Success)
                            {
                                string lanDownloadUrl = lanUrl.TrimEnd('/') + lanPathMatch.Groups[1].Value;
                                urlsToTry.Add(lanDownloadUrl);
                                Logger.LogAction("FIREBASE SSE", $"Added LAN fallback URL: {lanDownloadUrl}");
                            }
                        }
                    }
                    catch { /* Best effort — LAN fallback is optional */ }
                }

                // De-duplicate URLs (same URL can appear via multiple resolution paths)
                urlsToTry = urlsToTry.Distinct().ToList();

                string successUrl = null;
                foreach (var tryUrl in urlsToTry)
                {
                    bool succeeded = false;
                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            if (attempt > 0)
                            {
                                Logger.LogAction("FIREBASE SSE", $"Download retry {attempt + 1}/{maxRetries} after {retryDelays[attempt - 1]}ms...");
                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (progressClip != null)
                                        progressClip.RawContent = $"Ã°Å¸â€â€ž Retry {attempt + 1}/{maxRetries} Ã¢â‚¬â€ {cloudItem.Title}";
                                });
                                await Task.Delay(retryDelays[attempt - 1]);
                            }

                            var request = new HttpRequestMessage(HttpMethod.Get, tryUrl);
                            response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                            if (response.IsSuccessStatusCode)
                            {
                                Logger.LogAction("FIREBASE SSE", $"Download connected on attempt {attempt + 1}: {tryUrl}");
                                successUrl = tryUrl;
                                succeeded = true;
                                break;
                            }

                            Logger.LogAction("FIREBASE SSE", $"Download attempt {attempt + 1} failed: HTTP {(int)response.StatusCode} from {tryUrl}");
                        }
                        catch (Exception retryEx)
                        {
                            string errMsg = retryEx.Message;
                            Logger.LogAction("FIREBASE SSE", $"Download attempt {attempt + 1} error: {errMsg}");

                            // DNS failure = "No such host is known" — this URL is permanently dead,
                            // don't waste time retrying. Jump to the next URL immediately.
                            bool isDnsFailure = errMsg.Contains("No such host") || errMsg.Contains("name or address could not be resolved");
                            // Connection refused = server is down, also not retryable on same URL
                            bool isConnectionRefused = errMsg.Contains("actively refused") || errMsg.Contains("Connection refused");

                            if (isDnsFailure || isConnectionRefused)
                            {
                                Logger.LogAction("FIREBASE SSE", $"Non-retryable error — skipping to next URL");
                                break; // Break inner retry loop, move to next URL
                            }
                        }
                    }
                    
                    if (succeeded) break;
                    
                    // Log that we're trying the next fallback URL
                    if (urlsToTry.IndexOf(tryUrl) < urlsToTry.Count - 1)
                    {
                        string nextUrl = urlsToTry[urlsToTry.IndexOf(tryUrl) + 1];
                        Logger.LogAction("FIREBASE SSE", $"Primary URL failed Ã¢â‚¬â€ trying fallback: {nextUrl}");
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (progressClip != null)
                                progressClip.RawContent = $"Ã°Å¸â€â€ž Trying alternate download source Ã¢â‚¬â€ {cloudItem.Title}";
                        });
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    int code = response != null ? (int)response.StatusCode : 0;
                    string tried = string.Join(", ", urlsToTry.Select(u => u.Length > 60 ? u.Substring(0, 60) + "..." : u));
                    throw new Exception($"File Download Error: HTTP {code} after {maxRetries} attempts from {tried}");
                }

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                string totalSizeStr = totalBytes > 0
                    ? (totalBytes > 1_073_741_824 ? $"{totalBytes / 1_073_741_824.0:F1}GB" : $"{totalBytes / 1_048_576.0:F1}MB")
                    : "unknown";

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
                {
                    byte[] buffer = new byte[262144]; // 256KB buffer for better throughput
                    long totalRead = 0;
                    int bytesRead;
                    DateTime lastProgressUpdate = DateTime.MinValue;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 400 && progressClip != null)
                        {
                            lastProgressUpdate = DateTime.Now;
                            string readStr = totalRead > 1_073_741_824 ? $"{totalRead / 1_073_741_824.0:F1}GB" : $"{totalRead / 1_048_576.0:F1}MB";
                            int pct = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : -1;
                            string statusText = pct >= 0
                                ? $"Ã¢Â¬â€¡Ã¯Â¸Â {pct}% Ã¢â‚¬â€ {readStr}/{totalSizeStr} Ã¢â‚¬â€ {cloudItem.Title}"
                                : $"Ã¢Â¬â€¡Ã¯Â¸Â {readStr} Ã¢â‚¬â€ {cloudItem.Title}";

                            // Non-blocking Ã¢â‚¬â€ don't stall the download waiting for UI
                            progressClip.RawContent = statusText;
                            progressClip.FileName = $"{cloudItem.Title} ({pct}%)";
                        }
                    }
                }

                // SHA-256 integrity verification Ã¢â‚¬â€ verify downloaded file matches source hash
                bool integrityOk = true;
                if (!string.IsNullOrEmpty(cloudItem.FileHash))
                {
                    try
                    {
                        using var verifyStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576);
                        var localHash = System.Security.Cryptography.SHA256.HashData(verifyStream);
                        string localHashHex = BitConverter.ToString(localHash).Replace("-", "").ToLowerInvariant();
                        if (localHashHex != cloudItem.FileHash)
                        {
                            Logger.LogAction("INTEGRITY", $"Ã¢ÂÅ’ SHA-256 MISMATCH for {cloudItem.Title}: expected {cloudItem.FileHash.Substring(0, 16)}..., got {localHashHex.Substring(0, 16)}...");
                            integrityOk = false;
                            // Delete corrupted file
                            try { File.Delete(filePath); } catch { }
                        }
                        else
                        {
                            Logger.LogAction("INTEGRITY", $"Ã¢Å“â€¦ SHA-256 verified: {cloudItem.Title} ({cloudItem.FileHash.Substring(0, 16)}...)");
                        }
                    }
                    catch (Exception hashEx)
                    {
                        Logger.LogAction("INTEGRITY", $"Hash verification failed: {hashEx.Message}");
                    }
                }
                // Also check HTTP header hash if available
                else if (response.Headers.TryGetValues("X-Content-SHA256", out var hashValues))
                {
                    string serverHash = hashValues.FirstOrDefault() ?? "";
                    if (!string.IsNullOrEmpty(serverHash))
                    {
                        try
                        {
                            using var verifyStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576);
                            var localHash = System.Security.Cryptography.SHA256.HashData(verifyStream);
                            string localHashHex = BitConverter.ToString(localHash).Replace("-", "").ToLowerInvariant();
                            if (localHashHex != serverHash)
                            {
                                Logger.LogAction("INTEGRITY", $"Ã¢ÂÅ’ SHA-256 MISMATCH (HTTP header) for {cloudItem.Title}");
                                integrityOk = false;
                                try { File.Delete(filePath); } catch { }
                            }
                            else
                            {
                                Logger.LogAction("INTEGRITY", $"Ã¢Å“â€¦ SHA-256 verified (HTTP header): {cloudItem.Title}");
                            }
                        }
                        catch { }
                    }
                }

                // If integrity check failed, retry download ONCE
                if (!integrityOk)
                {
                    Logger.LogAction("INTEGRITY", $"Ã°Å¸â€â€ž Retrying download due to corruption: {cloudItem.Title}");
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (progressClip != null)
                            progressClip.RawContent = $"Ã°Å¸â€â€ž Re-downloading (integrity check failed) Ã¢â‚¬â€ {cloudItem.Title}";
                    });

                    try
                    {
                        using var retryClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
                        string retryPairingKey = DevicePairingManager.EnsurePairingKey();
                        if (!string.IsNullOrEmpty(retryPairingKey))
                            retryClient.DefaultRequestHeaders.Add("X-Pairing-Key", retryPairingKey);
                        retryClient.DefaultRequestHeaders.Add("X-FlyShelf-Client", "DesktopSync");
                        var retryResponse = await retryClient.GetAsync(successUrl);
                        if (retryResponse.IsSuccessStatusCode)
                        {
                            using var retryContent = await retryResponse.Content.ReadAsStreamAsync();
                            using var retryFile = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 262144);
                            await retryContent.CopyToAsync(retryFile);

                            // Verify retry
                            if (!string.IsNullOrEmpty(cloudItem.FileHash))
                            {
                                using var rv = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                var rh = System.Security.Cryptography.SHA256.HashData(rv);
                                string rhHex = BitConverter.ToString(rh).Replace("-", "").ToLowerInvariant();
                                integrityOk = rhHex == cloudItem.FileHash;
                                if (!integrityOk)
                                {
                                    Logger.LogAction("INTEGRITY", $"Ã¢ÂÅ’ RETRY ALSO FAILED Ã¢â‚¬â€ file may be corrupted at source: {cloudItem.Title}");
                                }
                                else
                                {
                                    Logger.LogAction("INTEGRITY", $"Ã¢Å“â€¦ Retry succeeded Ã¢â‚¬â€ file verified: {cloudItem.Title}");
                                }
                            }
                            else integrityOk = true; // No hash to verify against
                        }
                    }
                    catch (Exception retryEx)
                    {
                        Logger.LogAction("INTEGRITY", $"Retry download failed: {retryEx.Message}");
                    }
                }

                // If integrity verification failed even after retry, abort Ã¢â‚¬â€ don't inject corrupted file
                if (!integrityOk)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (progressClip != null)
                            _viewModel.DroppedItems.Remove(progressClip);
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Ã¢ÂÅ’ {cloudItem.Title} Ã¢â‚¬â€ file corrupted during transfer");
                    });
                    return;
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (progressClip != null)
                    {
                        _viewModel.DroppedItems.Remove(progressClip);
                    }

                    var fileInfo = new FileInfo(filePath);
                    string sizeStr = fileInfo.Length > 1_073_741_824
                        ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB"
                        : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                    try { MainWindow.SetWritingClipboard(true); System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath }); await System.Threading.Tasks.Task.Delay(500); } catch { } finally { MainWindow.SetWritingClipboard(false); }
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Ã¢Å“â€¦ {cloudItem.Title} ({sizeStr}) from {cloudItem.SourceDeviceName}");

                    var clip = new ClipboardItem(filePath);
                    clip.SourceDeviceName = cloudItem.SourceDeviceName ?? "Remote";
                    clip.SourceDeviceType = "Mobile";
                    bool isCfDownload = (!string.IsNullOrEmpty(cloudItem.Raw) && cloudItem.Raw.Contains(".trycloudflare.com")) ||
                                        (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com"));
                    clip.TransferMethod = isCfDownload ? "Cloudflare" : "Cloud";

                    if (clip.ItemType == ClipboardItemType.Image && clip.Icon == null)
                    {
                        try
                        {
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(filePath);
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 400;
                            bmp.EndInit();
                            bmp.Freeze();
                            clip.Icon = bmp;
                        }
                        catch (Exception imgEx)
                        {
                            Logger.LogAction("FIREBASE SSE", $"Image preview load failed: {imgEx.Message}");
                        }
                    }

                    clip.EvaluateSmartActions();
                    _viewModel.DroppedItems.Insert(0, clip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    
                    // Mark as cloud-sourced so clipboard echo doesn't re-push to Firebase
                    string fileFp = $"IMG::{(clip.FormattedSize ?? "")}";
                    _viewModel.MarkAsCloudSourced(fileFp);

                    // Track download completion via downloadedBy model
                    // This marks us as having downloaded, then checks if all targets are done
                    if (!string.IsNullOrEmpty(cloudItem.Id))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await FirebaseSyncManager.MarkFileDownloaded(cloudItem.Id);
                                Logger.LogAction("SYNC_TRACK", $"Marked download complete: {cloudItem.Title} [{cloudItem.Id}]");
                            }
                            catch (Exception delEx)
                            {
                                Logger.LogAction("SYNC_TRACK", $"MarkFileDownloaded failed: {delEx.Message}");
                            }
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"File Download Error: {ex.Message} | URL: {cloudItem.Raw}");
                
                // Drop the failed entry completely Ã¢â‚¬â€ don't bloat UI or Firebase with un-downloadable ghosts
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (progressClip != null)
                        _viewModel.DroppedItems.Remove(progressClip);
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Ã¢ÂÅ’ Dropped: {cloudItem.Title} Ã¢â‚¬â€ source unreachable");
                });
                
                // Clean up partial file on disk
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                
                // DON'T delete the Firebase entry Ã¢â‚¬â€ other devices may still need it.
                // The 24h TTL safety net will handle cleanup for truly orphaned entries.
                Logger.LogAction("FIREBASE SSE", $"Download failed but keeping Firebase entry for other devices: {cloudItem.Title} [{cloudItem.Id}]");
            }
        }

        private class CloudItem
        {
            public string Id { get; set; }
            public long Timestamp { get; set; }
            public string Type { get; set; }
            public string Title { get; set; }
            public string Raw { get; set; }
            public string DownloadUrl { get; set; }
            public string SenderUrl { get; set; }
            public string FileHash { get; set; }
            public string SourceDeviceName { get; set; }
            public string SourceDeviceId { get; set; }
        }

        private void ProcessForcedSyncPayload(string json, string deviceId)
        {
            _ = Task.Run(async () =>
            {
                try { await ProcessForcedSyncPayloadCore(json, deviceId); }
                catch (Exception ex) { Logger.LogAction("FIREBASE", $"ProcessForcedSyncPayload crash: {ex.Message}"); }
            });
        }

        private async Task ProcessForcedSyncPayloadCore(string json, string deviceId)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var keysToDelete = new List<string>();

                    foreach (JsonProperty prop in root.EnumerateObject())
                    {
                        var data = prop.Value;
                        string type = data.TryGetProperty("Type", out var t) ? t.GetString() ?? "Text" : "Text";
                        string title = data.TryGetProperty("Title", out var t2) ? t2.GetString() ?? "" : "";
                        string raw = data.TryGetProperty("Raw", out var t3) ? t3.GetString() ?? "" : "";
                        string source = data.TryGetProperty("ForcedBy", out var t4) ? t4.GetString() ?? "" :
                                       (data.TryGetProperty("SourceDeviceName", out var t5) ? t5.GetString() ?? "" : "");
                        string sourceDeviceType = data.TryGetProperty("SourceDeviceType", out var t5b) ? t5b.GetString() ?? "Unknown" : "Unknown";
                        string downloadUrl = data.TryGetProperty("DownloadUrl", out var t6) ? t6.GetString() ?? "" : "";
                        string senderUrl = data.TryGetProperty("SenderUrl", out var t7) ? t7.GetString() ?? "" : "";

                        Logger.LogAction("FORCED SYNC", $"Received from '{source}': {type} - {title}");

                        // Resolve relative URLs using SenderUrl
                        string resolvedUrl = raw;
                        if (!resolvedUrl.StartsWith("http") && !string.IsNullOrEmpty(downloadUrl))
                        {
                            if (downloadUrl.StartsWith("http"))
                                resolvedUrl = downloadUrl;
                            else if (!string.IsNullOrEmpty(senderUrl) && senderUrl.StartsWith("http"))
                                resolvedUrl = senderUrl + downloadUrl;
                        }
                        if (!resolvedUrl.StartsWith("http") && !string.IsNullOrEmpty(senderUrl) && senderUrl.StartsWith("http") && resolvedUrl.StartsWith("/"))
                            resolvedUrl = senderUrl + resolvedUrl;

                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            bool isFilePayload = type == "Image" || type == "ImageLink" || type == "Pdf" || type == "Archive" || type == "Video" || type == "Document" || type == "File";

                            if (isFilePayload && resolvedUrl.StartsWith("http"))
                            {
                                var cloudItem = new CloudItem { Id = prop.Name, Type = type, Title = title, Raw = resolvedUrl, DownloadUrl = downloadUrl, SenderUrl = senderUrl, SourceDeviceName = source };
                                _ = FetchAndInjectCloudFile(cloudItem);
                            }
                            else
                            {
                                // Skip blank items Ã¢â‚¬â€ never allow empty cards
                                if (string.IsNullOrWhiteSpace(raw)) return;

                                var clip = new ClipboardItem
                                {
                                    RawContent = raw,
                                    FileName = title,
                                    Extension = "FORCED",
                                    ItemType = type == "Url" ? ClipboardItemType.Url : ClipboardItemType.Text,
                                    SourceDeviceName = source,
                                    SourceDeviceType = sourceDeviceType,
                                    TransferMethod = "ForceSend"
                                };
                                clip.EvaluateSmartActions();
                                _viewModel.DroppedItems.Insert(0, clip);
                                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                            }

                            AdvanceClip.Windows.ToastWindow.ShowToast($"Ã¢Å¡Â¡ Force Sync from {source}");
                        });

                        keysToDelete.Add(prop.Name);
                    }

                    foreach (var key in keysToDelete)
                    {
                        string deleteUrl = (await AuthUrl($"forced_sync/{deviceId}/{key}.json"));
                        try { await _pollClient.DeleteAsync(deleteUrl); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FORCED SYNC", "Parse Error: " + ex.Message);
            }
        }
    }
}


