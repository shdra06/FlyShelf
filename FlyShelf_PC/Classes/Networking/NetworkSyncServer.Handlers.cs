// ---------------------------------------------------------------
// NetworkSyncServer ï¿½ HTTP Request Handlers
// ServeHtml, ClipboardData, TextUpload, FileUpload,
// ArchiveUpload, RelayUpload
// Split from NetworkSyncServer.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Globalization;
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
        private async Task ServeHtml(HttpListenerResponse res)
        {
            try
            {
                string path = Path.Combine(FlyShelf.Classes.RuntimeHost.ExecutionDir, "Resources", "WebClient", "index.html");
                Logger.LogAction("HTML", $"Serving from: {path} (exists: {File.Exists(path)})");
                if (File.Exists(path))
                {
                    byte[] buffer = await File.ReadAllBytesAsync(path);
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
            catch (Exception ex) { Logger.LogAction("HTML ERROR", ex.Message); try { res.StatusCode = 500; } catch { } /* Best-effort: failure is acceptable */ }
            finally { try { res.Close(); } catch { } /* Best-effort: failure is acceptable */ }
        }

        // ═ ═ ═ RESPONSE CACHE: Avoid re-serializing on rapid polls ═ ═ ═
        // Sealed record class for atomic reference swap — ensures json/timestamp/count are always consistent
        private sealed record SyncCacheEntry(byte[] Json, long Timestamp, int ItemCount);
        private volatile SyncCacheEntry? _syncCache = null;
        private const int SYNC_CACHE_TTL_MS = 500; // Cache for 500ms — fast invalidation for real-time sync

        // [FIX H-05]: Changed from async void to async Task — async void crashes the process on exception
        private async Task ServeClipboardData(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                // DEV DEBUG: Log every sync request
                string reqDevice = req.Headers["X-Source-Device"] ?? req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                string reqIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
                long sinceMs = 0;
                if (!string.IsNullOrEmpty(req.QueryString["since"]))
                {
                    long.TryParse(req.QueryString["since"], CultureInfo.InvariantCulture, out sinceMs);
                }
                Logger.LogAction("SYNC-GET", $"device={reqDevice} ip={reqIp} since={sinceMs} limit={req.QueryString["limit"] ?? "default"}");

                // CLOCK-SKEW GUARD: If client since timestamp is far in the future compared to PC clock
                // (e.g. PC clock jumped backwards due to NTP or timezone adjustment), reset sinceMs to prevent starvation
                long currentPcEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (sinceMs > currentPcEpoch + 60_000)
                {
                    sinceMs = 0; // PC clock jumped backward or client clock is ahead — fallback to fresh top items
                }

                int limit = 3; // Default: Only first 3 entries on initial pairing
                if (!string.IsNullOrEmpty(req.QueryString["limit"]) && int.TryParse(req.QueryString["limit"], CultureInfo.InvariantCulture, out int customLimit) && customLimit > 0)
                {
                    limit = Math.Min(customLimit, 25);
                }
                else if (sinceMs > 0)
                {
                    // Delta sync: fetch up to 15 items newer than since timestamp
                    limit = 15;
                }

                // FIRST-CONNECT GUARD: If sinceMs is 0 (initial/reconnect), restrict to last 24h
                // to prevent ancient clipboard history from flooding the mobile device.
                if (sinceMs == 0)
                {
                    sinceMs = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
                    limit = Math.Min(limit, 5); // Cap initial sync to 5 items max
                    Logger.LogAction("SYNC-GET", $"Initial connect from {reqDevice} — restricting to last 24h, limit={limit}");
                }

                // PERF: Capture item count + references in a SINGLE Dispatcher.Invoke call.
                List<(string? rawContent, string? fileName, string? filePath, string? extension,
                      ClipboardItemType itemType, DateTime dateCopied, bool isPassword)>? snapshot = null;
                string deviceId = "";
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) { res.Close(); return; }
                await dispatcher.InvokeAsync(() =>
                {
                    deviceId = SettingsManager.Current.DeviceId ?? "PC";
                    var query = _viewModel.DroppedItems
                        .Where(x => x.Extension != "MOBILE" && x.Extension != "DOWNLOADING" && !x.IsPassword);

                    if (sinceMs > 0)
                    {
                        query = query.Where(x => ((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds() > sinceMs);
                    }

                    // BUG FIX #2: Sort oldest-first before Take(). DroppedItems is newest-first,
                    // so without this, Take(15) grabs the 15 newest and permanently skips
                    // any items in the middle when there are more than 15 new items.
                    snapshot = query
                        .OrderBy(x => x.DateCopied)
                        .Take(limit)
                        .Select(x => (x.RawContent, x.FileName, x.FilePath, x.Extension, x.ItemType, x.DateCopied, x.IsPassword))
                        .ToList();
                }, System.Windows.Threading.DispatcherPriority.Normal);

                // Build payload + serialize on background thread
                object payloadList = Array.Empty<object>();
                if (snapshot != null && snapshot.Count > 0)
                {
                    var items = snapshot.Select(x => {
                        string contentKey = x.rawContent ?? x.fileName ?? x.filePath ?? "";
                        int stableHash = contentKey.GetHashCode(StringComparison.Ordinal);
                        string devName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                        return new
                        {
                            id = stableHash.ToString("X8", CultureInfo.InvariantCulture) + "_" + x.dateCopied.Ticks.ToString(CultureInfo.InvariantCulture),
                            EventId = $"{deviceId}_{((DateTimeOffset)x.dateCopied).ToUnixTimeMilliseconds()}_{stableHash:X8}",
                            Title = string.IsNullOrEmpty(x.fileName) ? (x.rawContent?.Length > 40 ? string.Concat(x.rawContent.AsSpan(0, 40), "...") : x.rawContent) : x.fileName,
                            Type = x.itemType.ToString(),
                            PreviewUrl = (x.itemType == ClipboardItemType.Image || x.itemType == ClipboardItemType.QRCode) ? (!string.IsNullOrEmpty(x.filePath) ? $"/download?path={Uri.EscapeDataString(x.filePath)}" : (x.rawContent ?? "")) : "",
                            DownloadUrl = !string.IsNullOrEmpty(x.filePath) ? $"/download?path={Uri.EscapeDataString(x.filePath)}" : (x.rawContent ?? ""),
                            Raw = x.rawContent ?? x.fileName ?? "",
                            FileName = x.fileName ?? "",
                            Time = x.dateCopied.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                            Timestamp = ((DateTimeOffset)x.dateCopied).ToUnixTimeMilliseconds(),
                            SourceDeviceName = x.extension == "MOBILE" ? "Mobile" : devName,
                            SourceDeviceType = x.extension == "MOBILE" ? "Mobile" : "PC"
                        };
                    })
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();

                    items.RemoveAll(x => {
                        var raw = x.Raw ?? x.Title ?? "";
                        return raw.Length > 30 && !raw.Contains(' ') && _rxBase64.IsMatch(raw);
                    });

                    payloadList = items;
                }

                string json = JsonSerializer.Serialize(payloadList);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = jsonBytes.Length;
                // Send PC identity in response headers so Android can update device status
                res.AddHeader("X-PC-DeviceName", SettingsManager.Current.DeviceName ?? Environment.MachineName);
                res.AddHeader("X-PC-DeviceId", SettingsManager.Current.DeviceId ?? "");
                res.AddHeader("X-PC-LAN-Active", (!string.IsNullOrEmpty(CloudDiscoveryManager.CachedLocalUrl)).ToString());
                res.AddHeader("X-PC-Cloud-Active", (!string.IsNullOrEmpty(CloudDiscoveryManager.CachedGlobalUrl)).ToString());
                await res.OutputStream.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                await res.OutputStream.FlushAsync();
                // DEV DEBUG: Log items served
                Logger.LogAction("SYNC-RESP", $"device={reqDevice} items={snapshot?.Count ?? 0} bytes={jsonBytes.Length} cloud={!string.IsNullOrEmpty(CloudDiscoveryManager.CachedGlobalUrl)}");
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
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { } // Best-effort: failure is acceptable
                res.Close();
                return;
            }

            // SPEED: Read body first, then respond 200 IMMEDIATELY so the sender isn't blocked
            string text;
            string sourceDevice;
            string itemType = null!;
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
            if (text.TrimStart().StartsWith('{'))
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
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { } // Best-effort: failure is acceptable
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
            _syncCache = null;

            // Process asynchronously on UI thread (fire-and-forget)
            string capturedText = text;
            string capturedSource = sourceDevice;
            string capturedType = itemType;
            var capturedTransport = DetectTransport(req);
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                // Detect if capturedText is a path or file:// URI
                string possiblePath = capturedText;
                if (possiblePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        possiblePath = new Uri(possiblePath).LocalPath;
                    }
                    catch { } // Best-effort: failure is acceptable
                }

                bool isPath = false;
                try
                {
                    if (_rxWinPath.IsMatch(possiblePath) || possiblePath.StartsWith("\\\\", StringComparison.Ordinal))
                    {
                        // PT-2 FIX: Validate the resolved path before trusting it.
                        // Reject: non-rooted paths, paths inside system directories, non-existent files.
                        string fullPath = Path.GetFullPath(possiblePath);
                        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                        bool isSensitive = fullPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)
                                        || fullPath.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase)
                                        || fullPath.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase);

                        if (!isSensitive && Path.IsPathRooted(fullPath) && File.Exists(fullPath))
                        {
                            possiblePath = fullPath; // Use normalized, validated path
                            isPath = true;
                        }
                        else if (isSensitive)
                        {
                            Logger.LogAction("SECURITY", $"Rejected file:// path in sensitive directory from {capturedSource}: {fullPath}");
                        }
                    }
                }
                catch { } // Best-effort: failure is acceptable

                ClipboardItem clip;
                if (isPath)
                {
                    // Construct as physical file (using our new offline fallback constructor)
                    clip = new ClipboardItem(possiblePath)
                    {
                        SourceDeviceName = capturedSource,
                        SourceDeviceType = capturedSource.Contains("PC", StringComparison.Ordinal) || capturedSource.Contains("LAPTOP", StringComparison.Ordinal) || capturedSource.Contains("DESKTOP", StringComparison.Ordinal) ? "PC" : "Mobile",
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
                        catch { } // Best-effort: failure is acceptable
                    });
                }
                else
                {
                    // Determine item type from payload or text content
                    ClipboardItemType clipType;
                    if (!string.IsNullOrEmpty(capturedType) && Enum.TryParse<ClipboardItemType>(capturedType, true, out var parsed))
                        clipType = parsed;
                    else
                        clipType = capturedText.StartsWith("http", StringComparison.Ordinal) ? ClipboardItemType.Url : ClipboardItemType.Text;

                    clip = new ClipboardItem
                    {
                        RawContent = capturedText,
                        FileName = capturedText.Length > 40 ? string.Concat(capturedText.AsSpan(0, 40), "...") : capturedText,
                        Extension = capturedTransport.label,
                        ItemType = clipType,
                        SourceDeviceName = capturedSource,
                        SourceDeviceType = capturedSource.Contains("PC", StringComparison.Ordinal) || capturedSource.Contains("LAPTOP", StringComparison.Ordinal) || capturedSource.Contains("DESKTOP", StringComparison.Ordinal) ? "PC" : "Mobile",
                        TransferMethod = capturedTransport.transport
                    };
                }

                clip.EvaluateSmartActions();
                bool wasEmpty = _viewModel.DroppedItems.Count == 0;
                _viewModel.InsertWithDedup(clip);
                if (wasEmpty) _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                // PERF: throttled — network sync is non-critical
                _viewModel.SchedulePersistHistoryPublic();
                
                // ECHO PREVENTION: Mark this text as cloud-sourced so the clipboard monitor
                // doesn't re-push it to Firebase when we set the Windows clipboard below.
                string normalizedContent = FlyShelfViewModel.NormalizeTextForFingerprint(capturedText);
                string txtFp = $"TXT::{normalizedContent.Substring(0, Math.Min(200, normalizedContent.Length))}";
                _viewModel.MarkAsCloudSourced(txtFp);
                
                // Suppress clipboard monitor during our write
                ClipboardHelper.SafeSetText(capturedText, suppressEcho: true, echoDelayMs: 500);
                
                FlyShelf.Windows.ToastWindow.ShowToast($"Text from {capturedSource} via {capturedTransport.transport}!");
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
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { } // Best-effort: failure is acceptable
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
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { } // Best-effort: failure is acceptable
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
                    try { sourceDevice = Path.GetFileName(Uri.UnescapeDataString(sourceDevice)); } catch { } // Best-effort: failure is acceptable
                }
                if (string.IsNullOrWhiteSpace(sourceDevice))
                {
                    sourceDevice = "Mobile";
                }

                string dateString = DateTime.Now.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
                string uploadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Clipboard", sourceDevice, dateString);
                Directory.CreateDirectory(uploadDir);

                string encodedName = req.Headers["X-File-Name"] ?? req.QueryString["name"];
                string mappedType = req.Headers["X-File-Type"] ?? req.Headers["X-Item-Type"] ?? req.QueryString["type"] ?? "Document";
                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { } // Best-effort: failure is acceptable
                }
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "uploaded_file.dat";

                long totalBytes = req.ContentLength64;
                if (totalBytes > LicenseManager.FREE_SYNC_SIZE_LIMIT && !LicenseManager.IsPro)
                {
                    res.StatusCode = 413; // Payload Too Large
                    try { await WriteJsonResponse(res, false, "File size exceeds 50 GB limit for Free tier."); } catch { } // Best-effort: failure is acceptable
                    res.Close();
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Incoming transfer rejected: file exceeds 50 GB Free tier limit.");
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
                            sourceDevice.Contains("PC", StringComparison.Ordinal) || sourceDevice.Contains("LAPTOP", StringComparison.Ordinal) || sourceDevice.Contains("DESKTOP", StringComparison.Ordinal) ? "PC" : "Mobile"
                        );
                    });
                }
                else
                {
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Receiving {rawName} from {sourceDevice}...");
                    });
                }

                // Copy stream to temporary file on disk with progress
                tempFile = Path.Combine(Path.GetTempPath(), $"FS_Upload_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).Substring(0, 8)}.tmp");
                using (var tempFs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 1_048_576, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1_048_576]; // 1MB buffer (upgraded from 64KB for high-throughput LAN)
                    long totalRead = 0;
                    int read;
                    var lastProgressUpdate = DateTime.MinValue;
                    using var uploadCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                    while ((read = await req.InputStream.ReadAsync(buffer, 0, buffer.Length, uploadCts.Token)) > 0)
                    {
                        uploadCts.CancelAfter(TimeSpan.FromSeconds(60)); // Reset timeout on each successful chunk
                        await tempFs.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (totalRead > LicenseManager.FREE_SYNC_SIZE_LIMIT && !LicenseManager.IsPro)
                        {
                            throw new InvalidDataException("File size exceeds 50 GB limit for Free tier.");
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
                            
                            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                placeholder.TransferProgress = progress;
                                placeholder.TransferStatusText = $"Transferring... {progress:F0}% ({speedText})";
                            });
                        }
                    }
                }

                DateTime? applyDate = null;
                string originalDateStr = req.Headers["X-Original-Date"];
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, CultureInfo.InvariantCulture, out long epochMs))
                {
                    try
                    {
                        applyDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                    } catch { } // Best-effort: failure is acceptable
                }

                string? finalPath = null;
                string contentType = req.ContentType ?? "";
                if (contentType.Contains("multipart/form-data", StringComparison.Ordinal) && contentType.Contains("boundary=", StringComparison.Ordinal))
                {
                    string boundary = contentType.Substring(contentType.IndexOf("boundary=", StringComparison.Ordinal) + "boundary=".Length).Trim();
                    if (boundary.StartsWith('"') && boundary.EndsWith('"'))
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
                        } catch { } // Best-effort: failure is acceptable
                    }
                }

                if (string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
                {
                    throw new FileNotFoundException("Failed to save or parse uploaded file.");
                }

                // Handle group ZIP reconstruction
                if (mappedType == "Group")
                {
                    string extractDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Extracted", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).Substring(0, 8));
                    Directory.CreateDirectory(extractDir);
                    SafeExtractZip(finalPath, extractDir);

                    string[] extractedPaths = Directory.GetFileSystemEntries(extractDir);
                    InjectReceivedGroup(
                        extractedPaths, 
                        sourceDevice, 
                        fileTransport.transport, 
                        sourceDevice.Contains("PC", StringComparison.Ordinal) || sourceDevice.Contains("LAPTOP", StringComparison.Ordinal) || sourceDevice.Contains("DESKTOP", StringComparison.Ordinal) ? "PC" : "Mobile", 
                        placeholder
                    );
                }
                else
                {
                    InjectReceivedFile(
                        finalPath, 
                        sourceDevice, 
                        fileTransport.transport, 
                        sourceDevice.Contains("PC", StringComparison.Ordinal) || sourceDevice.Contains("LAPTOP", StringComparison.Ordinal) || sourceDevice.Contains("DESKTOP", StringComparison.Ordinal) ? "PC" : "Mobile", 
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
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.DroppedItems.Remove(placeholder));
                }
                res.StatusCode = 500;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { } // Best-effort: failure is acceptable
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
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { } // Best-effort: failure is acceptable
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
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { } // Best-effort: failure is acceptable
                res.Close();
                return;
            }

            long totalBytes = req.ContentLength64;
            if (totalBytes > LicenseManager.FREE_SYNC_SIZE_LIMIT && !LicenseManager.IsPro)
            {
                res.StatusCode = 413;
                try { await WriteJsonResponse(res, false, "File size exceeds 50 GB limit for Free tier."); } catch { } // Best-effort: failure is acceptable
                res.Close();
                _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Incoming archive rejected: exceeds 50 GB Free tier limit.");
                });
                return;
            }

            try
            {
                string batchName = req.Headers["X-Batch-Name"];
                if (!string.IsNullOrEmpty(batchName))
                {
                    try { batchName = Path.GetFileName(Uri.UnescapeDataString(batchName)); } catch { } // Best-effort: failure is acceptable
                }
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "FlyShelf_Mobile_Transfer";
                
                string archiveSource = req.Headers["X-Source-Device"] ?? req.QueryString["sourceDevice"] ?? "Mobile";
                if (!string.IsNullOrEmpty(archiveSource))
                {
                    try { archiveSource = Path.GetFileName(Uri.UnescapeDataString(archiveSource)); } catch { } // Best-effort: failure is acceptable
                }
                if (string.IsNullOrWhiteSpace(archiveSource)) archiveSource = "Mobile";

                var archiveTransport = DetectTransport(req);

                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                string originalDateStr = req.Headers["X-Original-Date"];
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, CultureInfo.InvariantCulture, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }

                string encodedName = req.Headers["X-File-Name"];
                string rawName = "uploaded_media.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { } // Best-effort: failure is acceptable
                }
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "uploaded_media.dat";

                if ((DateTime.Now - _lastArchiveToastTime).TotalSeconds > 2)
                {
                    _lastArchiveToastTime = DateTime.Now;
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Extracting batch data...");
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
                                try { await WriteJsonResponse(res, false, "Archive exceeds 500 MB size limit."); } catch { } // Best-effort: failure is acceptable
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
                    } catch { } // Best-effort: failure is acceptable
                }

                res.StatusCode = 200;

                // Track file in batch for auto-clipboard
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }
                
                // Auto-copy to Windows clipboard if â‰¤2 files in this batch
                if (batchList.Count <= 2)
                {
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var fileList = new System.Collections.Specialized.StringCollection();
                            lock (batchList) { foreach (var f in batchList) fileList.Add(f); }
                            ClipboardHelper.SafeSetFileDropList(fileList);
                            FlyShelf.Windows.ToastWindow.ShowToast($"{rawName} copied to clipboard");
                            
                            // Insert proper file entry into FlyShelf (clickable → opens in default app)
                            var clip = new ClipboardItem
                            {
                                RawContent = finalPath,
                                FileName = rawName,
                                FilePath = finalPath,
                                Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(CultureInfo.InvariantCulture),
                                ItemType = ClipboardItemType.File,
                                SourceDeviceName = archiveSource,
                                SourceDeviceType = archiveSource.Contains("PC", StringComparison.Ordinal) || archiveSource.Contains("LAPTOP", StringComparison.Ordinal) || archiveSource.Contains("DESKTOP", StringComparison.Ordinal) ? "PC" : "Mobile",
                                TransferMethod = archiveTransport.transport
                            };
                            clip.EvaluateSmartActions();
                            bool wasEmpty = _viewModel.DroppedItems.Count == 0;
                            _viewModel.InsertWithDedup(clip);
                            if (wasEmpty) _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                            // PERF: throttled — network sync is non-critical
                            _viewModel.SchedulePersistHistoryPublic();
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
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { } // Best-effort: failure is acceptable
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
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { } // Best-effort: failure is acceptable
                res.Close();
                return;
            }

            long totalBytes = req.ContentLength64;
            if (totalBytes > LicenseManager.FREE_SYNC_SIZE_LIMIT && !LicenseManager.IsPro)
            {
                res.StatusCode = 413;
                try { await WriteJsonResponse(res, false, "File size exceeds 50 GB limit for Free tier."); } catch { } // Best-effort: failure is acceptable
                res.Close();
                _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Incoming relay rejected: exceeds 50 GB Free tier limit.");
                });
                return;
            }

            try
            {
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string senderDevice = req.Headers["X-Source-Device"] ?? "Android";
                if (!string.IsNullOrEmpty(senderDevice))
                {
                    try { senderDevice = Path.GetFileName(Uri.UnescapeDataString(senderDevice)); } catch { } // Best-effort: failure is acceptable
                }
                if (string.IsNullOrWhiteSpace(senderDevice)) senderDevice = "Android";
                
                string originalDateStr = req.Headers["X-Original-Date"];

                string rawName = "relayed_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { } // Best-effort: failure is acceptable
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

                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, CultureInfo.InvariantCulture, out long epochMs))
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                    try { File.SetCreationTime(finalPath, dt); File.SetLastWriteTime(finalPath, dt); } catch { } // Best-effort: failure is acceptable
                }

                // Build Cloudflare download URL
                string globalUrl = _cfDaemon.GlobalUrl;
                string downloadUrl = "";
                if (!string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com", StringComparison.Ordinal))
                {
                    downloadUrl = $"{globalUrl}/download?path={Uri.EscapeDataString(finalPath)}";
                }

                // Push to Firebase so all devices see it
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    var fileInfo = new FileInfo(finalPath);
                    string ext = Path.GetExtension(rawName).ToLower(CultureInfo.InvariantCulture);
                    string fileType = ext switch
                    {
                        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "Video",
                        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "Audio",
                        ".pdf" => "Pdf",
                        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
                        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "Image",
                        ".ppt" or ".pptx" => "Presentation",
                        ".apk" or ".aab" or ".xapk" or ".apks" => "File",
                        _ => "File"
                    };
                }

                string sizeStr = new FileInfo(finalPath).Length > 1_073_741_824 
                    ? string.Create(CultureInfo.InvariantCulture, $"{new FileInfo(finalPath).Length / 1_073_741_824.0:F1} GB") 
                    : string.Create(CultureInfo.InvariantCulture, $"{new FileInfo(finalPath).Length / 1_048_576.0:F1} MB");

                _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Relayed {rawName} ({sizeStr}) from {senderDevice}");
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

        // ═══════════════════════════════════════════════════════════
        // NETWORK DASHBOARD API
        // ═══════════════════════════════════════════════════════════

        private static readonly DateTime _serverStartTime = DateTime.UtcNow;

        private void ServeNetworkDashboard(HttpListenerResponse res)
        {
            try
            {
                // Build peers array
                var peersArray = new List<object>();
                var peerMgr = PeerManager.Instance;
                if (peerMgr != null)
                {
                    foreach (var kvp in peerMgr.ConnectedPeers)
                    {
                        var p = kvp.Value;
                        peersArray.Add(new
                        {
                            deviceId = p.DeviceId,
                            deviceName = p.DeviceName,
                            transport = p.Transport,
                            isAlive = p.IsAlive,
                            lastSeen = p.LastSeen.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                            transferPort = p.TransferPort
                        });
                    }
                }

                // Cloudflare status
                string cfStatus = "Inactive";
                try
                {
                    string cfUrl = _cfDaemon?.GlobalUrl ?? "";
                    if (!string.IsNullOrEmpty(cfUrl) && cfUrl.Contains("trycloudflare.com", StringComparison.Ordinal))
                        cfStatus = "Active";
                }
                catch { /* Best-effort */ }

                // Total transfers
                int totalTransfers = TransferHistory.Instance?.Entries?.Count ?? 0;

                // Uptime
                double uptimeMinutes = (DateTime.UtcNow - _serverStartTime).TotalMinutes;

                var dashboard = new
                {
                    peers = peersArray,
                    serverStatus = "Online",
                    cloudflareStatus = cfStatus,
                    totalTransfers = totalTransfers,
                    uptimeMinutes = (int)uptimeMinutes
                };

                string json = JsonSerializer.Serialize(dashboard);
                byte[] data = Encoding.UTF8.GetBytes(json);

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = data.Length;
                try { res.OutputStream.Write(data, 0, data.Length); } catch { } // Best-effort
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("DASHBOARD", $"ServeNetworkDashboard failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SPEED TEST RECEIVER — accepts payload from LanSpeedTest clients
        // ═══════════════════════════════════════════════════════════

        private async Task HandleSpeedTest(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                // Consume the payload (discard) — the client is measuring upload throughput
                byte[] buffer = new byte[65536];
                long totalRead = 0;
                int read;
                while ((read = await req.InputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    totalRead += read;
                    if (totalRead > 5_000_000) break; // Safety cap at 5MB
                }

                byte[] ok = Encoding.UTF8.GetBytes($"{{\"received\":{totalRead}}}");
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.ContentLength64 = ok.Length;
                res.OutputStream.Write(ok, 0, ok.Length);
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("SPEEDTEST", $"HandleSpeedTest failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES & TODOS SYNC
        // ═══════════════════════════════════════════════════════════


        private byte[]? _cachedNotesJson = null;
        private long _cachedNotesTimestamp = 0;
        private string? _cachedNotesQuery = null; // Cache key: query string
        private const int NOTES_CACHE_TTL_MS = 2000;

        private void ServeNotesData(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string queryKey = req.Url?.Query ?? "";
                long now = Environment.TickCount64;
                var cached = _cachedNotesJson;
                if (cached != null && (now - _cachedNotesTimestamp) < NOTES_CACHE_TTL_MS && _cachedNotesQuery == queryKey)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = cached.Length;
                    try { res.OutputStream.Write(cached, 0, cached.Length); } catch { } // Best-effort: failure is acceptable
                    res.Close();
                    return;
                }

                // Lazy-load notes if not yet loaded
                if (!NoteManager.Days.Any())
                {
                    try { NoteManager.Load(); } catch { } // Best-effort: failure is acceptable
                }

                // Parse date-range query params: ?days=N or ?date=YYYY-MM-DDT00:00:00
                var queryParams = req.QueryString;
                string? daysParam = queryParams["days"];
                string? dateParam = queryParams["date"];

                string json;
                if (!string.IsNullOrEmpty(dateParam) && DateTime.TryParse(dateParam, out var targetDate))
                {
                    // Single date fetch — return only the matching day
                    json = NoteManager.GetSyncPayloadFiltered(d => d.Date.Date == targetDate.Date);
                }
                else if (int.TryParse(daysParam, out int n) && n > 0)
                {
                    // Date range fetch — return last N days
                    var cutoff = DateTime.Today.AddDays(-n);
                    json = NoteManager.GetSyncPayloadFiltered(d => d.Date.Date >= cutoff);
                }
                else
                {
                    // No filter — return all (backwards compatible)
                    json = NoteManager.GetSyncPayload();
                }

                byte[] data = Encoding.UTF8.GetBytes(json);
                _cachedNotesJson = data;
                _cachedNotesTimestamp = now;
                _cachedNotesQuery = queryKey;

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = data.Length;
                try { res.OutputStream.Write(data, 0, data.Length); } catch { } // Best-effort: failure is acceptable
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES_SERVE", $"ServeNotesData failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { } // Best-effort: failure is acceptable
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        private async Task HandleNotesUpdate(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string json = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(json))
                {
                    res.StatusCode = 400;
                    res.Close();
                    return;
                }

                // Lazy-load notes if not yet loaded
                if (!NoteManager.Days.Any())
                {
                    try { NoteManager.Load(); } catch { } // Best-effort: failure is acceptable
                }

                string deviceName = req.Headers["X-Device-Name"] ?? "Unknown";
                NoteManager.MergeFromMobile(json, deviceName);

                // Invalidate cache
                _cachedNotesJson = null;

                byte[] ok = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.ContentLength64 = ok.Length;
                res.OutputStream.Write(ok, 0, ok.Length);
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES_SYNC", $"HandleNotesUpdate failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { } // Best-effort: failure is acceptable
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        private byte[]? _cachedTodosJson = null;
        private long _cachedTodosTimestamp = 0;
        private string? _cachedTodosQuery = null; // Cache key: query string
        private const int TODOS_CACHE_TTL_MS = 2000;

        private void ServeTodosData(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string queryKey = req.Url?.Query ?? "";
                long now = Environment.TickCount64;
                var cached = _cachedTodosJson;
                if (cached != null && (now - _cachedTodosTimestamp) < TODOS_CACHE_TTL_MS && _cachedTodosQuery == queryKey)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = cached.Length;
                    try { res.OutputStream.Write(cached, 0, cached.Length); } catch { } // Best-effort: failure is acceptable
                    res.Close();
                    return;
                }

                // Lazy-load todos if not yet loaded
                if (!TodoManager.Days.Any())
                {
                    try { TodoManager.Load(); } catch { } // Best-effort: failure is acceptable
                }

                // Parse date-range query params: ?days=N or ?date=YYYY-MM-DDT00:00:00
                var queryParams = req.QueryString;
                string? daysParam = queryParams["days"];
                string? dateParam = queryParams["date"];

                string json;
                if (!string.IsNullOrEmpty(dateParam) && DateTime.TryParse(dateParam, out var targetDate))
                {
                    // Single date fetch
                    json = TodoManager.GetSyncPayloadFiltered(d => d.Date.Date == targetDate.Date);
                }
                else if (int.TryParse(daysParam, out int n) && n > 0)
                {
                    // Date range fetch — return last N days
                    var cutoff = DateTime.Today.AddDays(-n);
                    json = TodoManager.GetSyncPayloadFiltered(d => d.Date.Date >= cutoff);
                }
                else
                {
                    // No filter — return all (backwards compatible)
                    json = TodoManager.GetSyncPayload();
                }

                byte[] data = Encoding.UTF8.GetBytes(json);
                _cachedTodosJson = data;
                _cachedTodosTimestamp = now;
                _cachedTodosQuery = queryKey;

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = data.Length;
                try { res.OutputStream.Write(data, 0, data.Length); } catch { } // Best-effort: failure is acceptable
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("TODOS_SERVE", $"ServeTodosData failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { } // Best-effort: failure is acceptable
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        private async Task HandleTodosUpdate(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string json = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(json))
                {
                    res.StatusCode = 400;
                    res.Close();
                    return;
                }

                // Lazy-load todos if not yet loaded
                if (!TodoManager.Days.Any())
                {
                    try { TodoManager.Load(); } catch { } // Best-effort: failure is acceptable
                }

                string deviceName = req.Headers["X-Device-Name"] ?? "Unknown";
                TodoManager.MergeFromMobile(json, deviceName);

                // Invalidate cache
                _cachedTodosJson = null;

                byte[] ok = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.ContentLength64 = ok.Length;
                res.OutputStream.Write(ok, 0, ok.Length);
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("TODOS_SYNC", $"HandleTodosUpdate failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { } // Best-effort: failure is acceptable
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        // ═══ SHORTCUTS SYNC ═══
        private void ServeShortcutsData(HttpListenerResponse res)
        {
            try
            {
                // Lazy-load shortcuts if not yet loaded
                if (!ShortcutManager.Shortcuts.Any())
                {
                    try { ShortcutManager.Load(); } catch { }
                }

                // Serialize only the fields mobile needs: Trigger, Label, Expansion
                var payload = ShortcutManager.Shortcuts.Select(s => new
                {
                    s.Trigger,
                    s.Label,
                    s.Expansion
                });
                string json = System.Text.Json.JsonSerializer.Serialize(payload);
                byte[] data = Encoding.UTF8.GetBytes(json);

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = data.Length;
                try { res.OutputStream.Write(data, 0, data.Length); } catch { }
                res.Close();
            }
            catch (Exception ex)
            {
                Logger.LogAction("SHORTCUTS_SERVE", $"ServeShortcutsData failed: {ex.Message}");
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }
    }
}
