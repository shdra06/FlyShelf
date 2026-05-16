// ---------------------------------------------------------------
// NetworkSyncServer � HTTP Request Handlers
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
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    public partial class NetworkSyncServer
    {
        private void ServeHtml(HttpListenerResponse res)
        {
            try
            {
                string path = Path.Combine(AdvanceClip.Classes.RuntimeHost.ExecutionDir, "Resources", "WebClient", "index.html");
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

        // ═══ RESPONSE CACHE: Avoid re-serializing on rapid polls ═══
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
                System.Windows.Application.Current.Dispatcher.Invoke(() => { currentCount = _viewModel.DroppedItems.Count; });

                // Use cached response if still fresh and item count unchanged
                if (_cachedSyncJson != null && (now - _cachedSyncTimestamp) < SYNC_CACHE_TTL_MS && currentCount == _cachedItemCount)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = _cachedSyncJson.Length;
                    try { res.OutputStream.Write(_cachedSyncJson, 0, _cachedSyncJson.Length); } catch { }
                    res.Close();
                    return;
                }

                // Rebuild cache
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    string deviceId = SettingsManager.Current.DeviceId ?? "PC";
                    var payload = _viewModel.DroppedItems
                        .Where(x => x.Extension != "MOBILE") // Don't echo Mobile items back
                        .Take(15).Select(x => new
                    {
                        id = x.GetHashCode().ToString() + "_" + x.DateCopied.Ticks.ToString(),
                        EventId = $"{deviceId}_{((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds()}_{x.GetHashCode():X4}",
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
                    })
                    // Sort by freshness — bumped items get DateCopied = Now, so they appear first
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();

                    // Filter out encrypted/Base64 blobs that echo back from mobile
                    payload.RemoveAll(x => {
                        var raw = x.Raw ?? x.Title ?? "";
                        return raw.Length > 30 && !raw.Contains(' ') && System.Text.RegularExpressions.Regex.IsMatch(raw, @"^[A-Za-z0-9+/=\r\n]+$");
                    });

                    string json = JsonSerializer.Serialize(payload);
                    _cachedSyncJson = Encoding.UTF8.GetBytes(json);
                    _cachedSyncTimestamp = now;
                    _cachedItemCount = currentCount;
                });

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = _cachedSyncJson!.Length;
                try { res.OutputStream.Write(_cachedSyncJson, 0, _cachedSyncJson.Length); } catch { }
                res.Close();
            }
            catch { try { res.StatusCode = 500; } catch { } try { res.Close(); } catch { } }
        }

        private async Task HandleTextUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // SPEED: Read body first, then respond 200 IMMEDIATELY so the sender isn't blocked
            string text;
            string sourceDevice;
            string itemType = null;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync();
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
                }
                catch
                {
                    // Not valid JSON — treat entire body as plain text (legacy sender)
                }
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
                // Determine item type from payload or text content
                ClipboardItemType clipType;
                if (!string.IsNullOrEmpty(capturedType) && Enum.TryParse<ClipboardItemType>(capturedType, true, out var parsed))
                    clipType = parsed;
                else
                    clipType = capturedText.StartsWith("http") ? ClipboardItemType.Url : ClipboardItemType.Text;

                var clip = new ClipboardItem
                {
                    RawContent = capturedText,
                    FileName = capturedText.Length > 40 ? capturedText.Substring(0, 40) + "..." : capturedText,
                    Extension = capturedTransport.label,
                    ItemType = clipType,
                    SourceDeviceName = capturedSource,
                    SourceDeviceType = capturedSource.Contains("PC") || capturedSource.Contains("LAPTOP") || capturedSource.Contains("DESKTOP") ? "PC" : "Mobile",
                    TransferMethod = capturedTransport.transport
                };
                clip.EvaluateSmartActions();
                _viewModel.DroppedItems.Insert(0, clip);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                
                // ECHO PREVENTION: Mark this text as cloud-sourced so the clipboard monitor
                // doesn't re-push it to Firebase when we set the Windows clipboard below.
                string txtFp = $"TXT::{capturedText.Substring(0, Math.Min(200, capturedText.Length))}";
                _viewModel.MarkAsCloudSourced(txtFp);
                
                // Suppress clipboard monitor during our write
                try 
                { 
                    MainWindow.SetWritingClipboard(true);
                    System.Windows.Clipboard.SetText(capturedText);
                    await System.Threading.Tasks.Task.Delay(500);
                } 
                catch { }
                finally { MainWindow.SetWritingClipboard(false); }
                
                AdvanceClip.Windows.ToastWindow.ShowToast($"Text from {capturedSource} via {capturedTransport.transport}! 📱");
                // Wake up any long-poll clients (e.g. other Android devices waiting on /api/events)
                NotifyClipboardChanged(clipType.ToString(), capturedText.Length > 40 ? capturedText.Substring(0, 40) : capturedText);
            });
        }

        private async Task HandleFileUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try 
            {
                string sourceDevice = req.Headers["X-Source-Device"];
                if (string.IsNullOrEmpty(sourceDevice)) sourceDevice = req.QueryString["sourceDevice"];
                if (!string.IsNullOrEmpty(sourceDevice))
                {
                    try { sourceDevice = Uri.UnescapeDataString(sourceDevice); } catch { }
                }
                else
                {
                    sourceDevice = "Mobile";
                }
                var fileTransport = DetectTransport(req);

                string dateString = DateTime.Now.ToString("dd-MM-yyyy");
                string uploadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Clipboard", sourceDevice, dateString);
                Directory.CreateDirectory(uploadDir);

                string encodedName = req.Headers["X-File-Name"] ?? req.QueryString["name"];
                string mappedType = req.Headers["X-File-Type"] ?? req.QueryString["type"] ?? "Document";
                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Receiving {rawName} from {sourceDevice}... 📥");
                });

                int counter = 1;
                string finalPath = Path.Combine(uploadDir, rawName);
                while(File.Exists(finalPath))
                {
                    finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                // Parse multipart/form-data or raw body
                string contentType = req.ContentType ?? "";
                if (contentType.Contains("multipart/form-data") && contentType.Contains("boundary="))
                {
                    // Extract boundary string
                    string boundary = contentType.Substring(contentType.IndexOf("boundary=") + "boundary=".Length).Trim();
                    if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                        boundary = boundary.Substring(1, boundary.Length - 2);
                    
                    byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
                    
                    // Read entire body into memory
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    byte[] body = ms.ToArray();
                    
                    // Find the file content: skip past the first boundary + headers (ends with \r\n\r\n)
                    int headerEnd = -1;
                    for (int i = 0; i < body.Length - 3; i++)
                    {
                        if (body[i] == 0x0D && body[i + 1] == 0x0A && body[i + 2] == 0x0D && body[i + 3] == 0x0A)
                        {
                            // Found \r\n\r\n — content starts after this
                            headerEnd = i + 4;
                            break;
                        }
                    }
                    
                    // ── Extract filename from multipart Content-Disposition if X-File-Name was missing ──
                    if (rawName == "uploaded_file.dat" && headerEnd > 0)
                    {
                        string partHeaders = Encoding.UTF8.GetString(body, 0, headerEnd);
                        // Look for: filename="actual_name.png"  or filename*=UTF-8''encoded_name
                        var fnMatch = System.Text.RegularExpressions.Regex.Match(partHeaders,
                            @"filename=""?([^""\r\n]+)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (fnMatch.Success)
                        {
                            string extracted = fnMatch.Groups[1].Value.Trim();
                            try { extracted = Uri.UnescapeDataString(extracted); } catch { }
                            if (!string.IsNullOrWhiteSpace(extracted) && extracted != "file")
                            {
                                rawName = extracted;
                                // Recalculate path with correct filename
                                counter = 1;
                                finalPath = Path.Combine(uploadDir, rawName);
                                while (File.Exists(finalPath))
                                {
                                    finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                                }
                            }
                        }
                    }

                    if (headerEnd > 0)
                    {
                        // Find trailing boundary
                        int contentEnd = body.Length;
                        byte[] endMarker = Encoding.UTF8.GetBytes("\r\n--" + boundary);
                        for (int i = headerEnd; i < body.Length - endMarker.Length; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < endMarker.Length; j++)
                            {
                                if (body[i + j] != endMarker[j]) { match = false; break; }
                            }
                            if (match) { contentEnd = i; break; }
                        }
                        
                        using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        fs.Write(body, headerEnd, contentEnd - headerEnd);
                    }
                    else
                    {
                        // Fallback: save raw
                        using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        fs.Write(body, 0, body.Length);
                    }
                }
                else
                {
                    // Raw binary body — save directly
                    using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await req.InputStream.CopyToAsync(fs);
                }

                string originalDateStr = req.Headers["X-Original-Date"];
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    try
                    {
                        var originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                        File.SetCreationTime(finalPath, originalDate);
                        File.SetLastWriteTime(finalPath, originalDate);
                    } catch { }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var dataObj = new System.Windows.DataObject();
                    var dropList = new System.Collections.Specialized.StringCollection { finalPath };
                    dataObj.SetFileDropList(dropList);
                    // skipFirebaseSync=true — file came FROM a peer device, don't echo it back
                    // forceClipboardSync=false — we write to clipboard ourselves with echo prevention
                    _viewModel.HandleDrop(dataObj, false, skipFirebaseSync: true);
                    
                    // Tag the newly created item with transport + source device info
                    if (_viewModel.DroppedItems.Count > 0)
                    {
                        var newest = _viewModel.DroppedItems[0];
                        newest.SourceDeviceName = sourceDevice;
                        newest.SourceDeviceType = sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile";
                        newest.TransferMethod = fileTransport.transport;
                        
                        // ECHO PREVENTION: Mark file as cloud-sourced so clipboard monitor
                        // doesn't re-push it to peers/Firebase when we write to clipboard
                        string fileFp = $"IMG::{newest.FormattedSize}";
                        _viewModel.MarkAsCloudSourced(fileFp);
                    }
                    
                    // Write received file to OS clipboard so user can paste it
                    try
                    {
                        MainWindow.SetWritingClipboard(true);
                        var clipList = new System.Collections.Specialized.StringCollection { finalPath };
                        System.Windows.Clipboard.SetFileDropList(clipList);
                        await System.Threading.Tasks.Task.Delay(500);
                    }
                    catch { }
                    finally { MainWindow.SetWritingClipboard(false); }
                    
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Saved: {Path.GetFileName(finalPath)} via {fileTransport.transport} ✅");
                    // Wake up any long-poll clients (e.g. other Android devices waiting on /api/events)
                    NotifyClipboardChanged("File", rawName);
                });

                res.StatusCode = 200;
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("SERVER ERR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private DateTime _lastArchiveToastTime = DateTime.MinValue;
        // Track files per batch for auto-clipboard (copy to clipboard if ≤2 files in batch)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _batchFiles = new();

        private async Task HandleArchiveUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string batchName = req.Headers["X-Batch-Name"];
                if (!string.IsNullOrEmpty(batchName))
                {
                    try { batchName = Uri.UnescapeDataString(batchName); } catch { }
                }
                
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "FlyShelf_Mobile_Transfer";
                string archiveSource = req.Headers["X-Source-Device"] ?? req.QueryString["sourceDevice"] ?? "Mobile";
                try { archiveSource = Uri.UnescapeDataString(archiveSource); } catch { }
                var archiveTransport = DetectTransport(req);

                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Synced", batchName);
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
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                }

                if ((DateTime.Now - _lastArchiveToastTime).TotalSeconds > 2)
                {
                    _lastArchiveToastTime = DateTime.Now;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Extracting batch data... 📦");
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
                    await req.InputStream.CopyToAsync(fs);
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
                
                // Auto-copy to Windows clipboard if ≤2 files in this batch
                if (batchList.Count <= 2)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var fileList = new System.Collections.Specialized.StringCollection();
                            lock (batchList) { foreach (var f in batchList) fileList.Add(f); }
                            System.Windows.Clipboard.SetFileDropList(fileList);
                            AdvanceClip.Windows.ToastWindow.ShowToast($"📋 {rawName} copied to clipboard");
                            
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
                            _viewModel.DroppedItems.Insert(0, clip);
                            _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                        }
                        catch { }
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
            try
            {
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string senderDevice = req.Headers["X-Source-Device"] ?? "Android";
                string originalDateStr = req.Headers["X-Original-Date"];

                string rawName = "relayed_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }

                // Save to Downloads/Synced/Relay_{sender}/
                string relayDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    "Downloads", "FlyShelf", "Relay", senderDevice.Replace(" ", "_"));
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

                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    var fbRes = await _httpClient.PostAsync(
                        await FirebaseAuthManager.AuthenticateUrl("https://advance-sync-default-rtdb.firebaseio.com/clipboard.json"), content);

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
                                    try { await _httpClient.DeleteAsync(await FirebaseAuthManager.AuthenticateUrl($"https://advance-sync-default-rtdb.firebaseio.com/clipboard/{entryKey}.json")); } catch { }
                                });
                            }
                        }
                        catch { }
                    }
                }

                string sizeStr = new FileInfo(finalPath).Length > 1_073_741_824 
                    ? $"{new FileInfo(finalPath).Length / 1_073_741_824.0:F1} GB" 
                    : $"{new FileInfo(finalPath).Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"📡 Relayed {rawName} ({sizeStr}) from {senderDevice}");
                });

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"status\":\"ok\",\"downloadUrl\":\"{downloadUrl}\",\"size\":\"{sizeStr}\"}}");
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
        private async Task ProcessRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            try
            {
                string path = req.Url.LocalPath.ToLower();
                string remoteAddr = req.RemoteEndPoint?.ToString() ?? "unknown";
                Logger.LogAction("HTTP", $"[{remoteAddr}] {req.HttpMethod} {path}");
                
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Original-Date, X-FlyShelf-Client, X-Pairing-Key");
                res.AddHeader("Access-Control-Expose-Headers", "X-Global-Url");
                if (!string.IsNullOrEmpty(GlobalUrl)) res.AddHeader("X-Global-Url", GlobalUrl);

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 200;
                    res.Close();
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    ServeHtml(res);
                }
                else if (path == "/ping")
                {
                    // Unauthenticated ping for LAN reachability detection
                    byte[] pong = Encoding.UTF8.GetBytes("pong");
                    res.StatusCode = 200;
                    res.ContentType = "text/plain";
                    res.OutputStream.Write(pong, 0, pong.Length);
                    res.Close();
                }
                else if (path == "/api/health" && req.HttpMethod == "GET")
                {
                    // Health check is PUBLIC — needed for Cloudflare tunnel self-verification
                    res.StatusCode = 200;
                    res.Close();
                }
                else if (path == "/ws/peer" && req.IsWebSocketRequest)
                {
                    // WebSocket peer liveness — persistent connection for instant death detection
                    string wsPairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                    string expectedKey = DevicePairingManager.EnsurePairingKey();
                    if (string.IsNullOrEmpty(wsPairingKey) || wsPairingKey != expectedKey)
                    {
                        res.StatusCode = 403;
                        res.Close();
                        return;
                    }
                    string peerDeviceId = req.Headers["X-Device-Id"] ?? req.QueryString["deviceId"] ?? "unknown";
                    Logger.LogAction("WS", $"✅ Peer WebSocket accepted from {peerDeviceId}");
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    _ = Task.Run(() => HandlePeerWebSocket(wsContext.WebSocket, peerDeviceId));
                }
                else if (path == "/download" && req.HttpMethod == "GET")
                {
                    // SECURITY: /download requires authentication (pairing key or PIN)
                    string dlPairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"] ?? "";
                    string dlPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"] ?? "";
                    bool dlAuthed = DevicePairingManager.IsDevicePaired(dlPairingKey) ||
                                   (!string.IsNullOrEmpty(dlPin) && dlPin == SettingsManager.Current.WebClientPinToken);

                    if (!dlAuthed)
                    {
                        Logger.LogAction("SECURITY", $"⛔ Rejected unauthenticated /download from {req.RemoteEndPoint}");
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 — Download requires authentication\"}");
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                    }
                    else
                    {
                        await ServeFileDownload(req, res);
                    }
                }
                else if (path == "/api/pair" && req.HttpMethod == "POST")
                {
                    // QR Code pairing — validates pairing key and registers device
                    await HandlePairRequest(req, res);
                }
                else if (path == "/api/discover" && req.HttpMethod == "GET")
                {
                    // Paired device discovery — returns current connection URLs
                    string pairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"];
                    if (DevicePairingManager.IsDevicePaired(pairingKey))
                    {
                        string deviceId = req.Headers["X-Device-Id"] ?? "";
                        string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(deviceId))
                            DevicePairingManager.TouchDevice(deviceId, remoteIp);

                        var info = new
                        {
                            status = "ok",
                            localUrl = DisplayUrl,
                            globalUrl = GlobalUrl ?? "",
                            pin = SettingsManager.Current.WebClientPinToken,
                            deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName
                        };
                        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info));
                        res.StatusCode = 200;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(json, 0, json.Length);
                        res.Close();
                    }
                    else
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                        res.StatusCode = 403;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                    }
                }
                else
                {
                    // HARD SECURE AUTHENTICATION BARRIER
                    string providedPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"];
                    string pairingKey = req.Headers["X-Pairing-Key"] ?? req.QueryString["key"];
                    
                    bool isNativeMobileCompanion = req.Headers["User-Agent"]?.Contains("FlyShelfMobile_Native") == true || req.Headers["X-FlyShelf-Client"] == "MobileCompanion" || req.Headers["X-FlyShelf-Client"] == "DesktopSync";
                    bool isPairedDevice = DevicePairingManager.IsDevicePaired(pairingKey);
                    
                    if (!isNativeMobileCompanion && !isPairedDevice && (string.IsNullOrEmpty(providedPin) || providedPin != SettingsManager.Current.WebClientPinToken))
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 Unauthorized - Invalid PIN\"}");
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                        return;
                    }

                    if (path == "/api/health" && req.HttpMethod == "GET")
                    {
                        res.StatusCode = 200;
                        res.Close();
                    }
                    else if (path == "/api/sync" && req.HttpMethod == "GET")
                    {
                        // Track this device as directly connected (Phase 3)
                        string deviceId = req.Headers["X-Pairing-Key"] ?? req.Headers["X-Device-Id"] ?? req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                        _directDeviceLastSeen[deviceId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        FirebaseSyncManager.DirectlyConnectedDeviceCount = GetDirectlyConnectedDeviceCount();
                        ServeClipboardData(res);
                    }
                    else if (path == "/api/events" && req.HttpMethod == "GET")
                    {
                        // Long-poll endpoint — blocks until clipboard changes or 30s timeout
                        // React Native can't use SSE/ReadableStream, so we use long-polling instead
                        var tcs = new TaskCompletionSource<string>();
                        lock (_longPollLock) { _longPollWaiters.Add(tcs); }
                        try
                        {
                            var timeoutTask = Task.Delay(30000);
                            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                            
                            if (completedTask == tcs.Task)
                            {
                                // Clipboard changed! Return the event data
                                string payload = await tcs.Task;
                                byte[] data = Encoding.UTF8.GetBytes(payload);
                                res.StatusCode = 200;
                                res.ContentType = "application/json";
                                res.ContentLength64 = data.Length;
                                res.OutputStream.Write(data, 0, data.Length);
                            }
                            else
                            {
                                // Timeout — return 204 No Content (no new events)
                                res.StatusCode = 204;
                            }
                        }
                        catch { res.StatusCode = 500; }
                        finally
                        {
                            lock (_longPollLock) { _longPollWaiters.Remove(tcs); }
                            try { res.Close(); } catch { }
                        }
                    }
                    else if (path == "/api/sync_text" && req.HttpMethod == "POST")
                    {
                        await HandleTextUpload(req, res);
                    }
                    else if (path == "/api/sync_file" && req.HttpMethod == "POST")
                    {
                        await HandleFileUpload(req, res);
                    }
                    else if (path == "/api/archive_upload" && req.HttpMethod == "POST")
                    {
                        await HandleArchiveUpload(req, res);
                    }
                    else if (path == "/api/upload_chunk" && req.HttpMethod == "POST")
                    {
                        await HandleChunkUpload(req, res);
                    }
                    else if (path == "/api/upload_finalize" && req.HttpMethod == "POST")
                    {
                        await HandleChunkFinalize(req, res);
                    }
                    else if (path == "/api/relay_upload" && req.HttpMethod == "POST")
                    {
                        await HandleRelayUpload(req, res);
                    }
                    else if (path == "/api/convert_to_pdf" && req.HttpMethod == "POST")
                    {
                        await HandleConvertToPdf(req, res);
                    }
                    else if (path == "/logs" && req.HttpMethod == "GET")
                    {
                        // Serve the live log dashboard page
                        ServeLogDashboard(res);
                    }
                    else if (path == "/api/logs/stream" && req.HttpMethod == "GET")
                    {
                        // SSE live log stream — stays open, pushes new log lines in real-time
                        await ServeLogStream(req, res);
                    }
                    else if (path == "/api/logs" && req.HttpMethod == "GET")
                    {
                        // Return combined PC + mobile logs as JSON
                        ServeLogsJson(req, res);
                    }
                    else if (path == "/api/logs" && req.HttpMethod == "POST")
                    {
                        // Accept logs from paired devices
                        await HandleRemoteLogPost(req, res);
                    }
                    else
                    {
                        res.StatusCode = 404;
                        res.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SERVER REQUEST FAULT", ex.Message);
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }

        /// <summary>
        /// Holds a WebSocket connection with a peer for instant liveness detection.
        /// Sends ping every 30s, receives pong. If the peer dies or tunnel drops,
        /// the WebSocket closes instantly — no 50s heartbeat delay.
        /// </summary>
        private async Task HandlePeerWebSocket(WebSocket ws, string peerDeviceId)
        {
            var buf = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    // Wait for incoming messages (peer sends pings, we just read them)
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.LogAction("WS", $"Peer {peerDeviceId} closed WebSocket gracefully");
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    }
                    // If we receive a text "ping", reply "pong"
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                        if (msg == "ping")
                        {
                            byte[] pong = Encoding.UTF8.GetBytes("pong");
                            await ws.SendAsync(new ArraySegment<byte>(pong), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                }
            }
            catch (WebSocketException)
            {
                Logger.LogAction("WS", $"Peer {peerDeviceId} WebSocket dropped (connection lost)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("WS", $"Peer {peerDeviceId} WebSocket error: {ex.Message}");
            }
            finally
            {
                ws.Dispose();
            }
        }

    }
}


