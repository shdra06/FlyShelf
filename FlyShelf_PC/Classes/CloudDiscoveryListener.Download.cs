// ═══════════════════════════════════════════════════════════════
// CloudDiscoveryListener — File Download, Integrity Verification
// FetchAndInjectCloudFile, retry/fallback logic, SHA-256 verify,
// CloudItem model. ProcessForcedSyncPayload REMOVED (Firebase
// must never relay content — P2P only).
// Split from CloudDiscoveryListener.cs for modularity
// ═══════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class CloudDiscoveryListener
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
                    ClipboardItemType incomingType = ClipboardItemType.File;
                    string typeLower = (cloudItem.Type ?? "File").ToLowerInvariant();
                    if (typeLower == "pdf") incomingType = ClipboardItemType.Pdf;
                    else if (typeLower == "archive" || typeLower == "zip") incomingType = ClipboardItemType.Archive;
                    else if (typeLower == "video" || typeLower == "mp4") incomingType = ClipboardItemType.Video;
                    else if (typeLower == "audio" || typeLower == "mp3") incomingType = ClipboardItemType.Audio;
                    else if (typeLower == "document" || typeLower == "text") incomingType = ClipboardItemType.Document;
                    else if (typeLower == "presentation") incomingType = ClipboardItemType.Presentation;
                    else if (typeLower == "image" || typeLower == "png" || typeLower == "jpg" || typeLower == "jpeg") incomingType = ClipboardItemType.Image;

                    progressClip = new ClipboardItem
                    {
                        RawContent = $"⏳ Downloading from {cloudItem.SourceDeviceName}...",
                        FileName = cloudItem.Title,
                        Extension = "DOWNLOADING",
                        ItemType = incomingType,
                        TransferProgress = 0.1,
                        TransferStatusText = $"Connecting to {cloudItem.SourceDeviceName}..."
                    };
                    _viewModel.DroppedItems.Insert(0, progressClip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                });

                // Signal "downloading" to Firebase — sender can see this device is actively receiving
                if (!string.IsNullOrEmpty(cloudItem.Id))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await CloudDiscoveryManager.MarkDownloading(cloudItem.Id); }
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
                        string senderCurrentUrl = await CloudDiscoveryManager.GetSenderCurrentUrl(cloudItem.SourceDeviceId);
                        if (string.IsNullOrEmpty(senderCurrentUrl))
                            senderCurrentUrl = await CloudDiscoveryManager.FindSenderUrlByName(cloudItem.SourceDeviceName);
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
                        string lanUrl = await CloudDiscoveryManager.FindSenderLanUrl(cloudItem.SourceDeviceName);
                        if (!string.IsNullOrEmpty(lanUrl))
                        {
                            var parts = lanUrl.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            var lanPathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"(/download\?path=.+)$");
                            if (lanPathMatch.Success)
                            {
                                foreach (var part in parts)
                                {
                                    var trimmedPart = part.Trim();
                                    if (trimmedPart.StartsWith("http"))
                                    {
                                        string lanDownloadUrl = trimmedPart.TrimEnd('/') + lanPathMatch.Groups[1].Value;
                                        urlsToTry.Add(lanDownloadUrl);
                                        Logger.LogAction("FIREBASE SSE", $"Added LAN fallback URL: {lanDownloadUrl}");
                                    }
                                }
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
                                    {
                                        progressClip.RawContent = $"🔄 Retry {attempt + 1}/{maxRetries} — {cloudItem.Title}";
                                        progressClip.TransferStatusText = $"Retry {attempt + 1}/{maxRetries} — connecting...";
                                    }
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
                            {
                                progressClip.RawContent = $"🔄 Trying alternate download source — {cloudItem.Title}";
                                progressClip.TransferStatusText = "Trying alternate source...";
                            }
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
                    byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(262144);
                    try
                    {
                        long totalRead = 0;
                        int bytesRead;
                        DateTime lastProgressUpdate = DateTime.MinValue;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, 262144)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;

                            if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 300 && progressClip != null)
                            {
                                lastProgressUpdate = DateTime.Now;
                                string readStr = totalRead > 1_073_741_824 ? $"{totalRead / 1_073_741_824.0:F1} GB" : $"{totalRead / 1_048_576.0:F1} MB";
                                int pct = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : -1;
                                string statusText = pct >= 0
                                    ? $"⬇️ {pct}% — {readStr}/{totalSizeStr} — {cloudItem.Title}"
                                    : $"⬇️ {readStr} — {cloudItem.Title}";

                                progressClip.RawContent = statusText;
                                progressClip.TransferProgress = pct >= 0 ? pct : 0.1;
                                progressClip.TransferStatusText = pct >= 0
                                    ? $"{readStr} of {totalSizeStr} ({pct}%)"
                                    : $"{readStr} downloaded";
                            }
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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
                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ {cloudItem.Title} — file corrupted during transfer");
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

                    ClipboardHelper.SafeSetFileDropList(new System.Collections.Specialized.StringCollection { filePath }, suppressEcho: true, echoDelayMs: 100);
                    FlyShelf.Windows.ToastWindow.ShowToast($"✅ {cloudItem.Title} ({sizeStr}) from {cloudItem.SourceDeviceName}");

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
                            var bmp = ViewModels.FlyShelfViewModel.LoadImageThumbnail(filePath, 300);
                            if (bmp != null)
                            {
                                clip.Icon = bmp;
                            }
                        }
                        catch (Exception imgEx)
                        {
                            Logger.LogAction("FIREBASE SSE", $"Image preview load failed: {imgEx.Message}");
                        }
                    }

                    clip.EvaluateSmartActions();
                    _viewModel.InsertWithDedup(clip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    
                    string fileFp = $"IMG::{(clip.FormattedSize ?? "")}";
                    _viewModel.MarkAsCloudSourced(fileFp);

                    if (!string.IsNullOrEmpty(cloudItem.Id))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await CloudDiscoveryManager.MarkFileDownloaded(cloudItem.Id);
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
                    FlyShelf.Windows.ToastWindow.ShowToast($"❌ Dropped: {cloudItem.Title} — source unreachable");
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
                Logger.LogAction("INTEGRITY", $"Hash verification error: {hashEx.Message}");
                return false; // Fail-closed: can't verify = treat as corrupted, retry download
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
                {
                    progressClip.RawContent = $"🔄 Re-downloading (integrity check failed) — {cloudItem.Title}";
                    progressClip.TransferStatusText = "Re-downloading (integrity retry)...";
                    progressClip.TransferProgress = 0.1;
                }
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

        // ═══════════════════════════════════════════════════════════════
        // ProcessForcedSyncPayload: REMOVED — Firebase must never relay content.
        // All content transfer is P2P-only via PeerManager (LAN/Cloudflare direct).
        // Firebase is strictly for exchanging encrypted device URLs (discovery).
        // The forced_sync SSE listener that called this has also been removed.
        // ═══════════════════════════════════════════════════════════════

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
