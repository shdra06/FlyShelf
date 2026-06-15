// ---------------------------------------------------------------
// NetworkSyncServer ï¿½ HTTP Request Handlers
// ServeHtml, ClipboardData, TextUpload, FileUpload,
// ArchiveUpload, RelayUpload
// Split from NetworkSyncServer.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class NetworkSyncServer
    {
        private void ServeHtml(HttpListenerResponse res)
        {
            try
            {
                string path = Path.Combine(FlyShelf.Classes.RuntimeHost.ExecutionDir, "Resources", "WebClient", "index.html");
                Logger.LogAction("HTML", $"Serving from: {path} (exists: {File.Exists(path)})");
                if (File.Exists(path))
                {
                    byte[] buffer = File.ReadAllBytes(path);
                    res.ContentType = "text/html; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.OutputStream.Write(buffer, 0, buffer.Length);
                    Logger.LogAction("HTML", $"Served {buffer.Length} bytes OK");
                }
                else
                {
                    byte[] err = Encoding.UTF8.GetBytes("UI payload not found.");
                    res.StatusCode = 404;
                    res.OutputStream.Write(err, 0, err.Length);
                }
            }
            catch (Exception ex) { Logger.LogAction("HTML ERROR", ex.Message); try { res.StatusCode = 500; } catch { } }
            finally { try { res.Close(); } catch { } }
        }

        // ═ ═ ═ RESPONSE CACHE: Avoid re-serializing on rapid polls ═ ═ ═
        private byte[]? _cachedSyncJson = null;
        private long _cachedSyncTimestamp = 0;
        private int _cachedItemCount = 0;
        private const int SYNC_CACHE_TTL_MS = 500; // Cache for 500ms — fast invalidation for real-time sync

        private void ServeClipboardData(HttpListenerResponse res)
        {
            try
            {
                long now = Environment.TickCount64;
                int currentCount = 0;
                System.Windows.Application.Current.Dispatcher.Invoke(() => { currentCount = _viewModel.DroppedItems.Count; }, System.Windows.Threading.DispatcherPriority.Normal, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(2));

                // Use cached response if still fresh and item count unchanged
                var cached = _cachedSyncJson; // Capture reference to avoid TOCTOU race
                if (cached != null && (now - _cachedSyncTimestamp) < SYNC_CACHE_TTL_MS && currentCount == _cachedItemCount)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = cached.Length;
                    try { res.OutputStream.Write(cached, 0, cached.Length); } catch { }
                    res.Close();
                    return;
                }

                // Rebuild cache
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    string deviceId = SettingsManager.Current.DeviceId ?? "PC";
                    var payload = _viewModel.DroppedItems
                        .Where(x => x.Extension != "MOBILE") // Don't echo Mobile items back
                        .Where(x => !x.IsPassword) // SECURITY: Never expose password items to any device
                        .Take(15).Select(x => {
                        string contentKey = x.RawContent ?? x.FileName ?? x.FilePath ?? "";
                        int stableHash = contentKey.GetHashCode(StringComparison.Ordinal);
                        return new
                    {
                        id = stableHash.ToString("X8") + "_" + x.DateCopied.Ticks.ToString(),
                        EventId = $"{deviceId}_{((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds()}_{stableHash:X8}",
                        Title = string.IsNullOrEmpty(x.FileName) ? (x.RawContent?.Length > 20 ? x.RawContent.Substring(0, 20) + "..." : x.RawContent) : x.FileName,
                        Type = x.ItemType.ToString(),
                        PreviewUrl = (x.ItemType == ClipboardItemType.Image || x.ItemType == ClipboardItemType.QRCode) ? (!string.IsNullOrEmpty(x.FilePath) ? $"/download?path={Uri.EscapeDataString(x.FilePath)}" : (x.RawContent ?? "")) : "",
                        DownloadUrl = !string.IsNullOrEmpty(x.FilePath) ? $"/download?path={Uri.EscapeDataString(x.FilePath)}" : (x.RawContent ?? ""),
                        Raw = x.RawContent ?? x.FileName ?? "",
                        FileName = x.FileName ?? "",
                        Time = x.DateCopied.ToString("HH:mm:ss"),
                        Timestamp = ((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds(),
                        SourceDeviceName = x.Extension == "MOBILE" ? "Mobile" : (SettingsManager.Current.DeviceName ?? Environment.MachineName),
                        SourceDeviceType = x.Extension == "MOBILE" ? "Mobile" : "PC"
                    };
                    })
                    // Sort by freshness — bumped items get DateCopied = Now, so they appear first
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();

                    payload.RemoveAll(x => {
                        var raw = x.Raw ?? x.Title ?? "";
                        return raw.Length > 30 && !raw.Contains(' ') && _rxBase64.IsMatch(raw);
                    });

                    string json = JsonSerializer.Serialize(payload);
                    _cachedSyncJson = Encoding.UTF8.GetBytes(json);
                    _cachedSyncTimestamp = now;
                    _cachedItemCount = currentCount;
                }, System.Windows.Threading.DispatcherPriority.Normal, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(2));

                var freshCache = _cachedSyncJson!; // Capture reference after Dispatcher.Invoke
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = freshCache.Length;
                try { res.OutputStream.Write(freshCache, 0, freshCache.Length); } catch { }
                res.Close();
            }
            catch (Exception ex) { Logger.LogAction("SYNC_SERVE", $"ServeClipboardData failed: {ex.Message}"); try { res.StatusCode = 500; } catch { } try { res.Close(); } catch { } }
        }

        private async Task HandleTextUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── Incoming Sync Gate ──
            if (!SettingsManager.Current.EnableIncomingSync)
            {
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { }
                res.Close();
                return;
            }

            // SPEED: Read body first, then respond 200 IMMEDIATELY so the sender isn't blocked
            string text;
            string sourceDevice;
            string itemType = null;
            string sourceDeviceId = req.Headers["X-Source-DeviceId"] ?? "";
            // [SECURITY FIX v2.1.0]: Reject oversized text uploads (DoS prevention)
            long contentLength = req.ContentLength64;
            const long MAX_TEXT_BYTES = 10_485_760; // 10MB max
            if (contentLength > MAX_TEXT_BYTES)
            {
                res.StatusCode = 413;
                byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Request body too large (10MB max)\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(errBytes, 0, errBytes.Length);
                res.Close();
                return;
            }
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                // Guard against chunked encoding (ContentLength64 == -1) by reading with a limit
                if (contentLength < 0)
                {
                    // Unknown length (chunked) — read with a byte count limit
                    var sb = new StringBuilder();
                    char[] charBuf = new char[8192];
                    int totalChars = 0;
                    int charsRead;
                    while ((charsRead = await reader.ReadAsync(charBuf, 0, charBuf.Length)) > 0)
                    {
                        totalChars += charsRead;
                        if (totalChars > MAX_TEXT_BYTES) // ~10M chars ≈ 10-40MB UTF-8
                        {
                            res.StatusCode = 413;
                            byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Request body too large (10MB max)\"}");
                            res.ContentType = "application/json";
                            res.OutputStream.Write(errBytes, 0, errBytes.Length);
                            res.Close();
                            return;
                        }
                        sb.Append(charBuf, 0, charsRead);
                    }
                    text = sb.ToString();
                }
                else
                {
                    text = await reader.ReadToEndAsync();
                }
                sourceDevice = req.Headers["X-Source-Device"] ?? "Mobile";
            }

            // v5 PeerManager sends JSON: {"type":"Url","title":"...","data":"actual text","sourceDeviceId":"..."}
            // Parse it to extract the actual content. Fall back to raw body for plain text senders.
            if (text.TrimStart().StartsWith("{"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("data", out var dataProp))
                    {
                        text = dataProp.GetString() ?? text;
                    }
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        itemType = typeProp.GetString();
                    }
                    if (root.TryGetProperty("sourceDeviceName", out var srcProp))
                    {
                        sourceDevice = srcProp.GetString() ?? sourceDevice;
                    }
                    if (root.TryGetProperty("sourceDeviceId", out var sdidProp))
                    {
                        sourceDeviceId = sdidProp.GetString() ?? sourceDeviceId;
                    }
                }
                catch
                {
                    // Not valid JSON — treat entire body as plain text (legacy sender)
                }
            }

            // Loopback check
            if (!string.IsNullOrEmpty(sourceDeviceId) && sourceDeviceId == SettingsManager.Current.DeviceId)
            {
                Logger.LogAction("SYNC_GATE", "Ignored loopback sync_text from self");
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { }
                res.Close();
                return;
            }

            // SECURITY: Block text from recently-unpaired devices (auth bypass prevention)
            if (DevicePairingManager.IsDeviceBlocked(sourceDeviceId))
            {
                Logger.LogAction("SYNC", $"Rejected text from blocked device: {sourceDeviceId}");
                res.StatusCode = 403;
                byte[] blockErr = Encoding.UTF8.GetBytes("{\"error\":\"Device blocked\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(blockErr, 0, blockErr.Length);
                res.Close();
                return;
            }

            // Respond instantly — don't make Android wait for UI processing
            res.StatusCode = 200;
            res.Close();

            // Invalidate sync cache so next poll picks up the new item
            _cachedSyncJson = null;

            // Process asynchronously on UI thread (fire-and-forget)
            string capturedText = text;
            string capturedSource = sourceDevice;
            string capturedType = itemType;
            var capturedTransport = DetectTransport(req);
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                // Detect if capturedText is a path or file:// URI
                string possiblePath = capturedText;
                if (possiblePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        possiblePath = new Uri(possiblePath).LocalPath;
                    }
                    catch { }
                }

                bool isPath = false;
                try
                {
                    if (_rxWinPath.IsMatch(possiblePath) || possiblePath.StartsWith("\\\\"))
                    {
                        isPath = true;
                    }
                }
                catch { }

                ClipboardItem clip;
                if (isPath)
                {
                    // Construct as physical file (using our new offline fallback constructor)
                    clip = new ClipboardItem(possiblePath)
                    {
                        SourceDeviceName = capturedSource,
                        SourceDeviceType = capturedSource.Contains("PC") || capturedSource.Contains("LAPTOP") || capturedSource.Contains("DESKTOP") ? "PC" : "Mobile",
                        TransferMethod = capturedTransport.transport
                    };
                    // Load its shell icon in the background thread via _viewModel.GetIcon
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var icon = _viewModel.GetIcon(possiblePath);
                            if (icon != null)
                            {
                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => clip.Icon = icon);
                            }
                        }
                        catch { }
                    });
                }
                else
                {
                    // Determine item type from payload or text content
                    ClipboardItemType clipType;
                    if (!string.IsNullOrEmpty(capturedType) && Enum.TryParse<ClipboardItemType>(capturedType, true, out var parsed))
                        clipType = parsed;
                    else
                        clipType = capturedText.StartsWith("http") ? ClipboardItemType.Url : ClipboardItemType.Text;

                    clip = new ClipboardItem
                    {
                        RawContent = capturedText,
                        FileName = capturedText.Length > 40 ? capturedText.Substring(0, 40) + "..." : capturedText,
                        Extension = capturedTransport.label,
                        ItemType = clipType,
                        SourceDeviceName = capturedSource,
                        SourceDeviceType = capturedSource.Contains("PC") || capturedSource.Contains("LAPTOP") || capturedSource.Contains("DESKTOP") ? "PC" : "Mobile",
                        TransferMethod = capturedTransport.transport
                    };
                }

                clip.EvaluateSmartActions();
                bool wasEmpty = _viewModel.DroppedItems.Count == 0;
                _viewModel.InsertWithDedup(clip);
                if (wasEmpty) _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                // Persist history so the synced text survives app restarts
                _viewModel.PersistHistoryPublic();
                
                // ECHO PREVENTION: Mark this text as cloud-sourced so the clipboard monitor
                // doesn't re-push it to Firebase when we set the Windows clipboard below.
                string normalizedContent = FlyShelfViewModel.NormalizeTextForFingerprint(capturedText);
                string txtFp = $"TXT::{normalizedContent.Substring(0, Math.Min(200, normalizedContent.Length))}";
                _viewModel.MarkAsCloudSourced(txtFp);
                
                // Suppress clipboard monitor during our write
                ClipboardHelper.SafeSetText(capturedText, suppressEcho: true, echoDelayMs: 500);
                
                FlyShelf.Windows.ToastWindow.ShowToast($"Text from {capturedSource} via {capturedTransport.transport}! 📱");
                // Wake up any long-poll clients (e.g. other Android devices waiting on /api/events)
                NotifyClipboardChanged(clip.ItemType.ToString(), capturedText.Length > 40 ? capturedText.Substring(0, 40) : capturedText);
            });
        }

        private async Task HandleFileUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── Loopback/Echo Prevention Gate ──
            string sourceDeviceId = req.Headers["X-Source-DeviceId"] ?? req.QueryString["sourceDeviceId"] ?? "";
            if (!string.IsNullOrEmpty(sourceDeviceId) && sourceDeviceId == SettingsManager.Current.DeviceId)
            {
                Logger.LogAction("SYNC_GATE", "Ignored loopback sync_file from self");
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { }
                res.Close();
                return;
            }

            // SECURITY: Block files from recently-unpaired devices (auth bypass prevention)
            if (DevicePairingManager.IsDeviceBlocked(sourceDeviceId))
            {
                Logger.LogAction("SYNC", $"Rejected file from blocked device: {sourceDeviceId}");
                res.StatusCode = 403;
                byte[] blockErr = Encoding.UTF8.GetBytes("{\"error\":\"Device blocked\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(blockErr, 0, blockErr.Length);
                res.Close();
                return;
            }

            // ── Incoming Sync Gate ──
            if (!SettingsManager.Current.EnableIncomingSync)
            {
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { }
                res.Close();
                return;
            }

            string tempFile = "";
            ClipboardItem? placeholder = null;
            string sourceDevice = "Mobile";
            var fileTransport = DetectTransport(req);

            try 
            {
                sourceDevice = req.Headers["X-Source-Device"];
                if (string.IsNullOrEmpty(sourceDevice)) sourceDevice = req.QueryString["sourceDevice"];
                if (!string.IsNullOrEmpty(sourceDevice))
                {
                    try { sourceDevice = Path.GetFileName(Uri.UnescapeDataString(sourceDevice)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(sourceDevice))
                {
                    sourceDevice = "Mobile";
                }

                string dateString = DateTime.Now.ToString("dd-MM-yyyy");
                string uploadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Clipboard", sourceDevice, dateString);
                Directory.CreateDirectory(uploadDir);

                string encodedName = req.Headers["X-File-Name"] ?? req.QueryString["name"];
                string mappedType = req.Headers["X-File-Type"] ?? req.Headers["X-Item-Type"] ?? req.QueryString["type"] ?? "Document";
                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "uploaded_file.dat";

                long totalBytes = req.ContentLength64;
                if (totalBytes > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                {
                    res.StatusCode = 413; // Payload Too Large
                    try { await WriteJsonResponse(res, false, "File size exceeds 50 MB limit for Free tier."); } catch { }
                    res.Close();
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Incoming transfer rejected: file exceeds 50 MB Free tier limit.");
                    });
                    return;
                }

                bool isLargeFile = totalBytes >= 10 * 1024 * 1024;

                if (isLargeFile)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        placeholder = _viewModel.CreateTransferPlaceholder(
                            rawName, 
                            totalBytes, 
                            sourceDevice, 
                            fileTransport.transport, 
                            sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile"
                        );
                    });
                }
                else
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Receiving {rawName} from {sourceDevice}... 📥");
                    });
                }

                // Copy stream to temporary file on disk with progress
                tempFile = Path.Combine(Path.GetTempPath(), $"FS_Upload_{Guid.NewGuid().ToString().Substring(0, 8)}.tmp");
                using (var tempFs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    long totalRead = 0;
                    int read;
                    var lastProgressUpdate = DateTime.MinValue;
                    using var uploadCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                    while ((read = await req.InputStream.ReadAsync(buffer, 0, buffer.Length, uploadCts.Token)) > 0)
                    {
                        uploadCts.CancelAfter(TimeSpan.FromSeconds(60)); // Reset timeout on each successful chunk
                        await tempFs.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (totalRead > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                        {
                            throw new InvalidDataException("File size exceeds 50 MB limit for Free tier.");
                        }

                        if (isLargeFile && placeholder != null && (DateTime.Now - lastProgressUpdate).TotalMilliseconds >= 300)
                        {
                            lastProgressUpdate = DateTime.Now;
                            double progress = totalBytes > 0 ? ((double)totalRead / totalBytes * 100) : 50;
                            if (progress < 1) progress = 1;
                            if (progress > 99) progress = 99;
                            string speedText = totalBytes > 0 
                                ? $"{FlyShelfViewModel.FormatBytesStatic(totalRead)} of {FlyShelfViewModel.FormatBytesStatic(totalBytes)}" 
                                : $"{FlyShelfViewModel.FormatBytesStatic(totalRead)}";
                            
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                placeholder.TransferProgress = progress;
                                placeholder.TransferStatusText = $"Transferring... {progress:F0}% ({speedText})";
                            });
                        }
                    }
                }

                DateTime? applyDate = null;
                string originalDateStr = req.Headers["X-Original-Date"];
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    try
                    {
                        applyDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                    } catch { }
                }

                string? finalPath = null;
                string contentType = req.ContentType ?? "";
                if (contentType.Contains("multipart/form-data") && contentType.Contains("boundary="))
                {
                    string boundary = contentType.Substring(contentType.IndexOf("boundary=") + "boundary=".Length).Trim();
                    if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                        boundary = boundary.Substring(1, boundary.Length - 2);

                    finalPath = await ProcessStreamingMultipartFile(tempFile, boundary, uploadDir, applyDate);
                }
                else
                {
                    // Raw binary
                    int counter = 1;
                    finalPath = Path.Combine(uploadDir, rawName);
                    while (File.Exists(finalPath))
                    {
                        finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                    }
                    File.Move(tempFile, finalPath, true);

                    if (applyDate.HasValue)
                    {
                        try
                        {
                            File.SetCreationTime(finalPath, applyDate.Value);
                            File.SetLastWriteTime(finalPath, applyDate.Value);
                        } catch { }
                    }
                }

                if (string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
                {
                    throw new FileNotFoundException("Failed to save or parse uploaded file.");
                }

                // Handle group ZIP reconstruction
                if (mappedType == "Group")
                {
                    string extractDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Extracted", $"{Guid.NewGuid().ToString().Substring(0, 8)}");
                    Directory.CreateDirectory(extractDir);
                    SafeExtractZip(finalPath, extractDir);

                    string[] extractedPaths = Directory.GetFileSystemEntries(extractDir);
                    InjectReceivedGroup(
                        extractedPaths, 
                        sourceDevice, 
                        fileTransport.transport, 
                        sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile", 
                        placeholder
                    );
                }
                else
                {
                    InjectReceivedFile(
                        finalPath, 
                        sourceDevice, 
                        fileTransport.transport, 
                        sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile", 
                        placeholder
                    );
                }

                res.StatusCode = 200;
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("SERVER ERR", ex.Message);
                if (placeholder != null)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.DroppedItems.Remove(placeholder));
                }
                res.StatusCode = 500;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
                res.Close();
            }
        }


        private DateTime _lastArchiveToastTime = DateTime.MinValue;
        // Track files per batch for auto-clipboard (copy to clipboard if â‰¤2 files in batch)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _batchFiles = new();

        private async Task HandleArchiveUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── Loopback/Echo Prevention Gate ──
            string sourceDeviceId = req.Headers["X-Source-DeviceId"] ?? req.QueryString["sourceDeviceId"] ?? "";
            if (!string.IsNullOrEmpty(sourceDeviceId) && sourceDeviceId == SettingsManager.Current.DeviceId)
            {
                Logger.LogAction("SYNC_GATE", "Ignored loopback archive_upload from self");
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { }
                res.Close();
                return;
            }

            // SECURITY: Block archives from recently-unpaired devices (auth bypass prevention)
            if (DevicePairingManager.IsDeviceBlocked(sourceDeviceId))
            {
                Logger.LogAction("SYNC", $"Rejected archive from blocked device: {sourceDeviceId}");
                res.StatusCode = 403;
                byte[] blockErr = Encoding.UTF8.GetBytes("{\"error\":\"Device blocked\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(blockErr, 0, blockErr.Length);
                res.Close();
                return;
            }

            // ── Incoming Sync Gate ──
            if (!SettingsManager.Current.EnableIncomingSync)
            {
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { }
                res.Close();
                return;
            }

            long totalBytes = req.ContentLength64;
            if (totalBytes > 50L * 1024 * 1024 && !LicenseManager.IsPro)
            {
                res.StatusCode = 413;
                try { await WriteJsonResponse(res, false, "File size exceeds 50 MB limit for Free tier."); } catch { }
                res.Close();
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Incoming archive rejected: exceeds 50 MB Free tier limit.");
                });
                return;
            }

            try
            {
                string batchName = req.Headers["X-Batch-Name"];
                if (!string.IsNullOrEmpty(batchName))
                {
                    try { batchName = Path.GetFileName(Uri.UnescapeDataString(batchName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "FlyShelf_Mobile_Transfer";
                
                string archiveSource = req.Headers["X-Source-Device"] ?? req.QueryString["sourceDevice"] ?? "Mobile";
                if (!string.IsNullOrEmpty(archiveSource))
                {
                    try { archiveSource = Path.GetFileName(Uri.UnescapeDataString(archiveSource)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(archiveSource)) archiveSource = "Mobile";

                var archiveTransport = DetectTransport(req);

                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                string originalDateStr = req.Headers["X-Original-Date"];
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }

                string encodedName = req.Headers["X-File-Name"];
                string rawName = "uploaded_media.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "uploaded_media.dat";

                if ((DateTime.Now - _lastArchiveToastTime).TotalSeconds > 2)
                {
                    _lastArchiveToastTime = DateTime.Now;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Extracting batch data... 📦");
                    });
                }

                int counter = 1;
                string finalPath = Path.Combine(archiveDir, rawName);
                while(File.Exists(finalPath))
                {
                    finalPath = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (totalBytes < 0)
                    {
                        // Chunked encoding — manual copy with size limit and per-chunk timeout
                        long archiveTotalRead = 0;
                        const long maxArchiveSize = 500L * 1024 * 1024; // 500MB
                        byte[] copyBuf = new byte[65536];
                        int copyRead;
                        using var archiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        while ((copyRead = await req.InputStream.ReadAsync(copyBuf, 0, copyBuf.Length, archiveCts.Token)) > 0)
                        {
                            archiveCts.CancelAfter(TimeSpan.FromSeconds(60)); // Reset timeout per chunk
                            archiveTotalRead += copyRead;
                            if (archiveTotalRead > maxArchiveSize)
                            {
                                res.StatusCode = 413;
                                try { await WriteJsonResponse(res, false, "Archive exceeds 500 MB size limit."); } catch { }
                                return;
                            }
                            await fs.WriteAsync(copyBuf, 0, copyRead);
                        }
                    }
                    else
                    {
                        // Known content length — use CopyToAsync with 5-minute timeout
                        using var archiveCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                        await req.InputStream.CopyToAsync(fs, 81920, archiveCts.Token);
                    }
                }

                if (originalDate.HasValue)
                {
                    try
                    {
                        File.SetCreationTime(finalPath, originalDate.Value);
                        File.SetLastWriteTime(finalPath, originalDate.Value);
                    } catch { }
                }

                res.StatusCode = 200;

                // Track file in batch for auto-clipboard
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }
                
                // Auto-copy to Windows clipboard if â‰¤2 files in this batch
                if (batchList.Count <= 2)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var fileList = new System.Collections.Specialized.StringCollection();
                            lock (batchList) { foreach (var f in batchList) fileList.Add(f); }
                            ClipboardHelper.SafeSetFileDropList(fileList);
                            FlyShelf.Windows.ToastWindow.ShowToast($"📋 {rawName} copied to clipboard");
                            
                            // Insert proper file entry into FlyShelf (clickable → opens in default app)
                            var clip = new ClipboardItem
                            {
                                RawContent = finalPath,
                                FileName = rawName,
                                FilePath = finalPath,
                                Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(),
                                ItemType = ClipboardItemType.File,
                                SourceDeviceName = archiveSource,
                                SourceDeviceType = archiveSource.Contains("PC") || archiveSource.Contains("LAPTOP") || archiveSource.Contains("DESKTOP") ? "PC" : "Mobile",
                                TransferMethod = archiveTransport.transport
                            };
                            clip.EvaluateSmartActions();
                            bool wasEmpty = _viewModel.DroppedItems.Count == 0;
                            _viewModel.InsertWithDedup(clip);
                            if (wasEmpty) _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                            // Persist history so the synced archive item survives app restarts
                            _viewModel.PersistHistoryPublic();
                        }
                        catch (Exception ex) { Logger.LogAction("ARCHIVE", $"Clipboard set failed: {ex.Message}"); }
                    });
                }
                
                // Clean up old batches after 5 minutes
                _ = Task.Run(async () => { await Task.Delay(300_000); _batchFiles.TryRemove(batchName, out _); });
            }
            catch (Exception ex)
            {
                Logger.LogAction("ARCHIVE UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        // ─── Relay Upload: Android uploads file → PC saves + pushes Cloudflare URL to Firebase ───
        private async Task HandleRelayUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // ── Loopback/Echo Prevention Gate ──
            string sourceDeviceId = req.Headers["X-Source-DeviceId"] ?? "";
            if (!string.IsNullOrEmpty(sourceDeviceId) && sourceDeviceId == SettingsManager.Current.DeviceId)
            {
                Logger.LogAction("SYNC_GATE", "Ignored loopback relay_upload from self");
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { }
                res.Close();
                return;
            }

            // SECURITY: Block relay uploads from recently-unpaired devices (auth bypass prevention)
            if (DevicePairingManager.IsDeviceBlocked(sourceDeviceId))
            {
                Logger.LogAction("SYNC", $"Rejected relay from blocked device: {sourceDeviceId}");
                res.StatusCode = 403;
                byte[] blockErr = Encoding.UTF8.GetBytes("{\"error\":\"Device blocked\"}");
                res.ContentType = "application/json";
                res.OutputStream.Write(blockErr, 0, blockErr.Length);
                res.Close();
                return;
            }

            // ── Incoming Sync Gate ──
            if (!SettingsManager.Current.EnableIncomingSync)
            {
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { }
                res.Close();
                return;
            }

            long totalBytes = req.ContentLength64;
            if (totalBytes > 50L * 1024 * 1024 && !LicenseManager.IsPro)
            {
                res.StatusCode = 413;
                try { await WriteJsonResponse(res, false, "File size exceeds 50 MB limit for Free tier."); } catch { }
                res.Close();
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Incoming relay rejected: exceeds 50 MB Free tier limit.");
                });
                return;
            }

            try
            {
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string senderDevice = req.Headers["X-Source-Device"] ?? "Android";
                if (!string.IsNullOrEmpty(senderDevice))
                {
                    try { senderDevice = Path.GetFileName(Uri.UnescapeDataString(senderDevice)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(senderDevice)) senderDevice = "Android";
                
                string originalDateStr = req.Headers["X-Original-Date"];

                string rawName = "relayed_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "relayed_file.dat";

                // Save to AppData/FlyShelf/SyncedFiles/Relay_{sender}/
                string relayDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                    "FlyShelf", "SyncedFiles", "Relay", senderDevice.Replace(" ", "_"));
                Directory.CreateDirectory(relayDir);

                int counter = 1;
                string finalPath = Path.Combine(relayDir, rawName);
                while (File.Exists(finalPath))
                {
                    finalPath = Path.Combine(relayDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                    try { File.SetCreationTime(finalPath, dt); File.SetLastWriteTime(finalPath, dt); } catch { }
                }

                // Build Cloudflare download URL
                string globalUrl = _cfDaemon.GlobalUrl;
                string downloadUrl = "";
                if (!string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com"))
                {
                    downloadUrl = $"{globalUrl}/download?path={Uri.EscapeDataString(finalPath)}";
                }

                // Push to Firebase so all devices see it
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    var fileInfo = new FileInfo(finalPath);
                    string ext = Path.GetExtension(rawName).ToLower();
                    string fileType = ext switch
                    {
                        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "Video",
                        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "Audio",
                        ".pdf" => "Pdf",
                        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
                        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "ImageLink",
                        ".doc" or ".docx" or ".txt" or ".rtf" => "Document",
                        ".ppt" or ".pptx" => "Presentation",
                        ".apk" => "Archive",
                        _ => "File"
                    };

                    string deviceName = SettingsManager.Current?.DeviceName ?? Environment.MachineName;
                    var payload = new
                    {
                        Title = rawName,
                        Type = fileType,
                        Raw = downloadUrl,
                        PreviewUrl = downloadUrl,
                        DownloadUrl = downloadUrl,
                        FileName = rawName,
                        FileSize = fileInfo.Length,
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SourceDeviceName = senderDevice,
                        SourceDeviceType = "Mobile",
                        RelayedVia = deviceName
                    };

                    string pairingKey = DevicePairingManager.EnsurePairingKey();
                    if (!string.IsNullOrEmpty(pairingKey))
                    {
                        string json = System.Text.Json.JsonSerializer.Serialize(payload);
                        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        var fbRes = await _httpClient.PostAsync(
                            await FirebaseAuthManager.AuthenticateUrl($"{FirebaseAuthManager.FirebaseDatabaseUrl}/clipboard/{pairingKey}.json"), content);

                        if (fbRes.IsSuccessStatusCode)
                        {
                            string fbBody = await fbRes.Content.ReadAsStringAsync();
                            try
                            {
                                var fbObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(fbBody);
                                if (fbObj != null && fbObj.TryGetValue("name", out string? entryKey) && !string.IsNullOrEmpty(entryKey))
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        await Task.Delay(24 * 60 * 60_000);
                                        try { await _httpClient.DeleteAsync(await FirebaseAuthManager.AuthenticateUrl($"{FirebaseAuthManager.FirebaseDatabaseUrl}/clipboard/{pairingKey}/{entryKey}.json")); } catch { }
                                    });
                                }
                            }
                            catch (Exception ex) { Logger.LogAction("RELAY", $"Firebase response parse failed: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        Logger.LogAction("RELAY", "Cannot push relay to Firebase: pairingKey is empty.");
                    }
                }

                string sizeStr = new FileInfo(finalPath).Length > 1_073_741_824 
                    ? $"{new FileInfo(finalPath).Length / 1_073_741_824.0:F1} GB" 
                    : $"{new FileInfo(finalPath).Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"📡 Relayed {rawName} ({sizeStr}) from {senderDevice}");
                });

                res.StatusCode = 200;
                var relayResponsePayload = new { status = "ok", downloadUrl = downloadUrl, size = sizeStr };
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(relayResponsePayload));
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("RELAY UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }
    }
}
