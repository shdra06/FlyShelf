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
    public partial class FirebaseListener
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

                    while (!ct.IsCancellationRequested)
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

                // Detect urlRequest on sub-path: /DeviceId/urlRequest
                if (path.Contains("/urlRequest") && data.ValueKind == JsonValueKind.Object)
                {
                    string requestingDevice = path.TrimStart('/').Split('/')[0];
                    if (!string.IsNullOrEmpty(requestingDevice) && requestingDevice != myDeviceId)
                    {
                        _ = Task.Run(() => peerManager.HandlePeerUrlRequest(requestingDevice));
                    }
                    return;
                }

                if (path == "/")
                {
                    // Full snapshot — scan all devices
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in data.EnumerateObject())
                        {
                            if (prop.Name == myDeviceId) continue;

                            // Check if any peer has a urlRequest for us
                            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("urlRequest", out _))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlRequest(prop.Name));
                            }

                            string globalUrl = prop.Value.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                            string localUrl = prop.Value.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                            string deviceName = prop.Value.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? prop.Name : prop.Name;

                            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlUpdate(prop.Name, deviceName, localUrl, globalUrl));
                            }
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
                            // Check if this is a urlRequest
                            if (data.TryGetProperty("urlRequest", out _))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlRequest(deviceKey));
                            }

                            string globalUrl = data.TryGetProperty("GlobalUrl", out var gu) ? gu.GetString() ?? "" : "";
                            string localUrl = data.TryGetProperty("LocalIp", out var li) ? li.GetString() ?? "" : "";
                            string deviceName = data.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? deviceKey : deviceKey;

                            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
                            {
                                _ = Task.Run(() => peerManager.HandlePeerUrlUpdate(deviceKey, deviceName, localUrl, globalUrl));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER SSE", $"ProcessPeerUrlChange error: {ex.Message}");
            }
        }

    }
}
