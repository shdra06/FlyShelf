// ═══════════════════════════════════════════════════════════════
// FirebaseListener — File Download, Integrity & Forced Sync
// FetchAndInjectCloudFile, retry/fallback logic, SHA-256 verify,
// ProcessForcedSyncPayload, CloudItem model
// Split from FirebaseListener.cs for modularity
// ═══════════════════════════════════════════════════════════════
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
                        RawContent = $"⏳ Downloading from {cloudItem.SourceDeviceName}...",
                        FileName = cloudItem.Title,
                        Extension = "DOWNLOADING",
                        ItemType = ClipboardItemType.Text
                    };
                    _viewModel.DroppedItems.Insert(0, progressClip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                });

                // Signal "downloading" to Firebase — sender can see this device is actively receiving
                if (!string.IsNullOrEmpty(cloudItem.Id))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await FirebaseSyncManager.MarkDownloading(cloudItem.Id); }
                        catch { }
                    });
                }

                // AUTHENTICATION: /download requires pairing key or PIN
                HttpResponseMessage response = null;
                int maxRetries = 2;
                int[] retryDelays = { 500, 1500 };

                using var downloadClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(pairingKey))
                    downloadClient.DefaultRequestHeaders.Add("X-Pairing-Key", pairingKey);
                downloadClient.DefaultRequestHeaders.Add("X-FlyShelf-Client", "DesktopSync");
                
                // Build fallback URL list: primary first, then alternatives
                var urlsToTry = new List<string> { cloudItem.Raw };
                
                // If primary is Cloudflare, add DownloadUrl and SenderUrl-based alternatives
                if (cloudItem.Raw.Contains(".trycloudflare.com"))
                {
                    try
                    {
                        string senderCurrentUrl = await FirebaseSyncManager.GetSenderCurrentUrl(cloudItem.SourceDeviceId);
                        if (string.IsNullOrEmpty(senderCurrentUrl))
                            senderCurrentUrl = await FirebaseSyncManager.FindSenderUrlByName(cloudItem.SourceDeviceName);
                        if (!string.IsNullOrEmpty(senderCurrentUrl) && senderCurrentUrl.Contains(".trycloudflare.com"))
                        {
                            var pathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"(/download\?path=.+)$");
                            if (pathMatch.Success)
                            {
                                string freshUrl = senderCurrentUrl.TrimEnd('/') + pathMatch.Groups[1].Value;
                                if (freshUrl != cloudItem.Raw)
                                {
                                    urlsToTry.Insert(0, freshUrl);
                                    Logger.LogAction("FIREBASE SSE", $"Using sender's current tunnel URL: {senderCurrentUrl}");
                                }
                            }
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && cloudItem.DownloadUrl.StartsWith("http") && cloudItem.DownloadUrl != cloudItem.Raw)
                        urlsToTry.Add(cloudItem.DownloadUrl);
                    
                    if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com") && !cloudItem.Raw.Contains(cloudItem.SenderUrl))
                    {
                        var pathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"/download\?path=(.+)$");
                        if (pathMatch.Success)
                            urlsToTry.Add($"{cloudItem.SenderUrl.TrimEnd('/')}/download?path={pathMatch.Groups[1].Value}");
                    }

                    // LAST RESORT: Try sender's LAN URL
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
                    catch { }
                }

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
                                        progressClip.RawContent = $"🔄 Retry {attempt + 1}/{maxRetries} — {cloudItem.Title}";
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

                            bool isDnsFailure = errMsg.Contains("No such host") || errMsg.Contains("name or address could not be resolved");
                            bool isConnectionRefused = errMsg.Contains("actively refused") || errMsg.Contains("Connection refused");

                            if (isDnsFailure || isConnectionRefused)
                            {
                                Logger.LogAction("FIREBASE SSE", $"Non-retryable error — skipping to next URL");
                                break;
                            }
                        }
                    }
                    
                    if (succeeded) break;
                    
                    if (urlsToTry.IndexOf(tryUrl) < urlsToTry.Count - 1)
                    {
                        string nextUrl = urlsToTry[urlsToTry.IndexOf(tryUrl) + 1];
                        Logger.LogAction("FIREBASE SSE", $"Primary URL failed — trying fallback: {nextUrl}");
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (progressClip != null)
                                progressClip.RawContent = $"🔄 Trying alternate download source — {cloudItem.Title}";
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
                    byte[] buffer = new byte[262144];
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
                                ? $"⬇️ {pct}% — {readStr}/{totalSizeStr} — {cloudItem.Title}"
                                : $"⬇️ {readStr} — {cloudItem.Title}";

                            progressClip.RawContent = statusText;
                            progressClip.FileName = $"{cloudItem.Title} ({pct}%)";
                        }
                    }
                }

                // SHA-256 integrity verification
                bool integrityOk = true;
                if (!string.IsNullOrEmpty(cloudItem.FileHash))
                {
                    integrityOk = VerifyFileHash(filePath, cloudItem.FileHash, cloudItem.Title);
                    if (!integrityOk) try { File.Delete(filePath); } catch { }
                }
                else if (response.Headers.TryGetValues("X-Content-SHA256", out var hashValues))
                {
                    string serverHash = hashValues.FirstOrDefault() ?? "";
                    if (!string.IsNullOrEmpty(serverHash))
                    {
                        integrityOk = VerifyFileHash(filePath, serverHash, cloudItem.Title);
                        if (!integrityOk) try { File.Delete(filePath); } catch { }
                    }
                }

                // If integrity check failed, retry download ONCE
                if (!integrityOk)
                {
                    integrityOk = await RetryDownloadWithVerification(successUrl, filePath, cloudItem, progressClip);
                }

                // If integrity verification failed even after retry, abort
                if (!integrityOk)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (progressClip != null)
                            _viewModel.DroppedItems.Remove(progressClip);
                        AdvanceClip.Windows.ToastWindow.ShowToast($"❌ {cloudItem.Title} — file corrupted during transfer");
                    });
                    return;
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (progressClip != null)
                        _viewModel.DroppedItems.Remove(progressClip);

                    var fileInfo = new FileInfo(filePath);
                    string sizeStr = fileInfo.Length > 1_073_741_824
                        ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB"
                        : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                    try { MainWindow.SetWritingClipboard(true); System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath }); await System.Threading.Tasks.Task.Delay(500); } catch { } finally { MainWindow.SetWritingClipboard(false); }
                    AdvanceClip.Windows.ToastWindow.ShowToast($"✅ {cloudItem.Title} ({sizeStr}) from {cloudItem.SourceDeviceName}");

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
                    
                    string fileFp = $"IMG::{(clip.FormattedSize ?? "")}";
                    _viewModel.MarkAsCloudSourced(fileFp);

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
                
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (progressClip != null)
                        _viewModel.DroppedItems.Remove(progressClip);
                    AdvanceClip.Windows.ToastWindow.ShowToast($"❌ Dropped: {cloudItem.Title} — source unreachable");
                });
                
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                
                Logger.LogAction("FIREBASE SSE", $"Download failed but keeping Firebase entry for other devices: {cloudItem.Title} [{cloudItem.Id}]");
            }
        }

        /// <summary>
        /// Verify a downloaded file against an expected SHA-256 hash.
        /// </summary>
        private bool VerifyFileHash(string filePath, string expectedHash, string title)
        {
            try
            {
                using var verifyStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576);
                var localHash = System.Security.Cryptography.SHA256.HashData(verifyStream);
                string localHashHex = BitConverter.ToString(localHash).Replace("-", "").ToLowerInvariant();
                if (localHashHex != expectedHash)
                {
                    Logger.LogAction("INTEGRITY", $"❌ SHA-256 MISMATCH for {title}: expected {expectedHash.Substring(0, 16)}..., got {localHashHex.Substring(0, 16)}...");
                    return false;
                }
                Logger.LogAction("INTEGRITY", $"✅ SHA-256 verified: {title} ({expectedHash.Substring(0, 16)}...)");
                return true;
            }
            catch (Exception hashEx)
            {
                Logger.LogAction("INTEGRITY", $"Hash verification failed: {hashEx.Message}");
                return true; // Can't verify = assume OK
            }
        }

        /// <summary>
        /// Retry a failed download once and re-verify integrity.
        /// </summary>
        private async Task<bool> RetryDownloadWithVerification(string url, string filePath, CloudItem cloudItem, ClipboardItem progressClip)
        {
            Logger.LogAction("INTEGRITY", $"🔄 Retrying download due to corruption: {cloudItem.Title}");
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (progressClip != null)
                    progressClip.RawContent = $"🔄 Re-downloading (integrity check failed) — {cloudItem.Title}";
            });

            try
            {
                using var retryClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
                string retryPairingKey = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(retryPairingKey))
                    retryClient.DefaultRequestHeaders.Add("X-Pairing-Key", retryPairingKey);
                retryClient.DefaultRequestHeaders.Add("X-FlyShelf-Client", "DesktopSync");
                var retryResponse = await retryClient.GetAsync(url);
                if (retryResponse.IsSuccessStatusCode)
                {
                    using var retryContent = await retryResponse.Content.ReadAsStreamAsync();
                    using var retryFile = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 262144);
                    await retryContent.CopyToAsync(retryFile);

                    if (!string.IsNullOrEmpty(cloudItem.FileHash))
                    {
                        return VerifyFileHash(filePath, cloudItem.FileHash, cloudItem.Title);
                    }
                    return true; // No hash to verify against
                }
            }
            catch (Exception retryEx)
            {
                Logger.LogAction("INTEGRITY", $"Retry download failed: {retryEx.Message}");
            }
            return false;
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
                                var ci = new CloudItem { Id = prop.Name, Type = type, Title = title, Raw = resolvedUrl, DownloadUrl = downloadUrl, SenderUrl = senderUrl, SourceDeviceName = source };
                                _ = FetchAndInjectCloudFile(ci);
                            }
                            else
                            {
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

                            AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ Force Sync from {source}");
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
    }
}
