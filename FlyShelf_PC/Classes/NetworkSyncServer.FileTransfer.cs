// ---------------------------------------------------------------
// NetworkSyncServer.Advanced — File Download, Pairing & Injection
// Split from NetworkSyncServer.Advanced.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class NetworkSyncServer
    {
        private async Task ServeFileDownload(HttpListenerRequest req, HttpListenerResponse res)
        {
            string path = req.QueryString["path"];
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try { res.StatusCode = 404; res.Close(); } catch { }
                return;
            }

            // SECURITY: Path sandbox — reject files outside allowed directories
            if (!IsPathAllowed(path))
            {
                Logger.LogAction("SECURITY", $"🚫 BLOCKED path traversal attempt: {path} from {req.RemoteEndPoint}");
                try
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"403 — Access denied: path not in allowed directory\"}");
                    res.StatusCode = 403;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                    res.Close();
                }
                catch { }
                return;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                long fileSize = fileInfo.Length;
                string ext = Path.GetExtension(path).ToLower();
                string safeFileName = Path.GetFileName(path);
                string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";

                Logger.LogAction("DOWNLOAD", $"Starting: {safeFileName} ({fileSize / 1024}KB) to {remoteIp}");

                // Content-Type
                res.ContentType = ext switch
                {
                    ".png"  => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif"  => "image/gif",
                    ".webp" => "image/webp",
                    ".pdf"  => "application/pdf",
                    ".apk"  => "application/vnd.android.package-archive",
                    ".mp4"  => "video/mp4",
                    ".mkv"  => "video/x-matroska",
                    ".zip"  => "application/zip",
                    ".rar"  => "application/x-rar-compressed",
                    _ => "application/octet-stream"
                };

                bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
                res.AddHeader("Content-Disposition", isImage
                    ? $"inline; filename=\"{safeFileName}\""
                    : $"attachment; filename=\"{safeFileName}\"");
                res.AddHeader("Cache-Control", "no-store");
                res.AddHeader("Accept-Ranges", "bytes");

                res.StatusCode = 200;
                res.ContentLength64 = fileSize;
                res.SendChunked = false;

                // Fast path: small files (≤5MB) — single read + write for minimal latency
                if (fileSize <= 5 * 1024 * 1024)
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(path);
                    await res.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                    await res.OutputStream.FlushAsync();
                    Logger.LogAction("DOWNLOAD", $"Completed (fast): {safeFileName} ({fileSize / 1024}KB)");
                }
                else
                {
                    // Large files: stream with 1MB buffer for maximum throughput
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1048576, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                    try
                    {
                        int bytesRead;
                            while ((bytesRead = await fs.ReadAsync(buffer, 0, 1048576)) > 0)
                        {
                            await res.OutputStream.WriteAsync(buffer, 0, bytesRead);
                        }
                        await res.OutputStream.FlushAsync();
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }
                    Logger.LogAction("DOWNLOAD", $"Completed (stream): {safeFileName} ({fileSize / 1024}KB)");
                }
            }
            catch (HttpListenerException ex) { Logger.LogAction("DOWNLOAD", $"Client disconnected: {ex.Message}"); }
            catch (IOException ex) { Logger.LogAction("DOWNLOAD", $"Pipe broken: {ex.Message}"); }
            catch (Exception ex) { Logger.LogAction("DOWNLOAD ERROR", $"{ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        // ═══ QR Code Pairing Handler ═══
        private async Task HandlePairRequest(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                var pairData = JsonSerializer.Deserialize<JsonElement>(body);
                string pairingKey = pairData.TryGetProperty("key", out var k) ? k.GetString() : "";
                string deviceId = pairData.TryGetProperty("deviceId", out var di) ? di.GetString() : "";
                string deviceName = pairData.TryGetProperty("deviceName", out var dn) ? dn.GetString() : "Unknown";
                string deviceType = pairData.TryGetProperty("deviceType", out var dt) ? dt.GetString() : "Mobile";
                string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "unknown";

                if (string.IsNullOrEmpty(deviceId))
                    deviceId = $"{deviceName}_{remoteIp}";

                bool success = DevicePairingManager.TryPairDevice(pairingKey, deviceId, deviceName, deviceType, remoteIp);

                if (success)
                {
                    var response = new
                    {
                        status = "paired",
                        deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                        deviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName,
                        localUrl = DisplayUrl,
                        globalUrl = GlobalUrl ?? "",
                        pin = SettingsManager.Current.WebClientPinToken
                    };
                    byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                    res.StatusCode = 200;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(json, 0, json.Length);

                    // Show toast on PC
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"📱 {deviceName} paired successfully!");
                    });
                }
                else
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                    res.StatusCode = 403;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR ERROR", ex.Message);
                byte[] err = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                try { res.OutputStream.Write(err, 0, err.Length); } catch { }
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        public void InjectReceivedFile(string filePath, string sourceDevice, string transferMethod, string sourceDeviceType = "Mobile", ClipboardItem? placeholder = null)
        {
            _cachedSyncJson = null; // Invalidate sync cache
            
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (placeholder != null)
                    {
                        _viewModel.DroppedItems.Remove(placeholder);
                    }
                    var dataObj = new System.Windows.DataObject();
                    var dropList = new System.Collections.Specialized.StringCollection { filePath };
                    dataObj.SetFileDropList(dropList);
                    
                    // skipCloudSync=true - file came FROM a peer device, don't echo it back
                    // forceClipboardSync=false - we write to clipboard ourselves with echo prevention
                    _viewModel.HandleDrop(dataObj, false, skipCloudSync: true);
                    
                    // Tag the newly created item with transport + source device info
                    if (_viewModel.DroppedItems.Count > 0)
                    {
                        var newest = _viewModel.DroppedItems[0];
                        newest.SourceDeviceName = sourceDevice;
                        newest.SourceDeviceType = sourceDeviceType;
                        newest.TransferMethod = transferMethod;
                        
                        // Persist network metadata via debounced JSON save
                        _viewModel.PersistHistoryPublic();

                        // ECHO PREVENTION: Mark file as cloud-sourced so clipboard monitor
                        // doesn't re-push it to peers/Firebase when we write to clipboard
                        string fileFp = $"IMG::{newest.FormattedSize}";
                        _viewModel.MarkAsCloudSourced(fileFp);
                    }
                    
                    // Write received file to OS clipboard so user can paste it
                    try
                    {
                        MainWindow.SetWritingClipboard(true);
                        var clipList = new System.Collections.Specialized.StringCollection { filePath };
                        System.Windows.Clipboard.SetFileDropList(clipList);
                        await System.Threading.Tasks.Task.Delay(100);
                    }
                    catch { }
                    finally { MainWindow.SetWritingClipboard(false); }
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"Saved: {System.IO.Path.GetFileName(filePath)} via {transferMethod} 📥");
                    // Wake up any long-poll clients (e.g. other Android devices waiting on /api/events)
                    NotifyClipboardChanged("File", System.IO.Path.GetFileName(filePath));
                }
                catch (Exception ex)
                {
                    Logger.LogAction("FILE INJECTION ERR", ex.Message);
                }
            });
        }

        public void InjectReceivedGroup(string[] files, string sourceDevice, string transferMethod, string sourceDeviceType = "Mobile", ClipboardItem? placeholder = null)
        {
            _cachedSyncJson = null; // Invalidate sync cache
            
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (placeholder != null)
                    {
                        _viewModel.DroppedItems.Remove(placeholder);
                    }

                    var groupItem = new ClipboardItem(files);
                    groupItem.SourceDeviceName = sourceDevice;
                    groupItem.SourceDeviceType = sourceDeviceType;
                    groupItem.TransferMethod = transferMethod;

                    _viewModel.InsertWithDedup(groupItem);
                    _viewModel.PruneOldItems();
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                    // Persist network metadata via debounced JSON save
                    _viewModel.PersistHistoryPublic();

                    // Set file drop list to clipboard
                    try
                    {
                        MainWindow.SetWritingClipboard(true);
                        var clipList = new System.Collections.Specialized.StringCollection();
                        foreach (var f in files) clipList.Add(f);
                        System.Windows.Clipboard.SetFileDropList(clipList);
                        await System.Threading.Tasks.Task.Delay(100);
                    }
                    catch { }
                    finally { MainWindow.SetWritingClipboard(false); }

                    FlyShelf.Windows.ToastWindow.ShowToast($"Saved: Group of {files.Length} files via {transferMethod} 📦");
                    NotifyClipboardChanged("Group", groupItem.FileName);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("GROUP INJECTION ERR", ex.Message);
                }
            });
        }

        public void InjectReceivedText(string text, string sourceDevice, string transferMethod, string? itemType = null, string sourceDeviceType = "Mobile")
        {
            _cachedSyncJson = null; // Invalidate sync cache

            string capturedText = text;
            string capturedSource = sourceDevice;
            string capturedType = itemType;
            string capturedTransport = transferMethod;

            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
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
                            SourceDeviceType = sourceDeviceType,
                            TransferMethod = capturedTransport
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
                            FileName = capturedText.Length > 800 ? capturedText.Substring(0, 800) + "..." : capturedText,
                            Extension = capturedTransport == "WebSocket" ? "WS" : "SYNC",
                            ItemType = clipType,
                            SourceDeviceName = capturedSource,
                            SourceDeviceType = sourceDeviceType,
                            TransferMethod = capturedTransport
                        };
                    }

                    clip.EvaluateSmartActions();
                    bool wasEmpty = _viewModel.DroppedItems.Count == 0;
                    _viewModel.InsertWithDedup(clip);
                    if (wasEmpty) _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    
                    // ECHO PREVENTION: Mark this text as cloud-sourced so the clipboard monitor
                    // doesn't re-push it to Firebase when we set the Windows clipboard below.
                    string txtFp = $"TXT::{capturedText.Substring(0, Math.Min(200, capturedText.Length))}";
                    _viewModel.MarkAsCloudSourced(txtFp);
                    
                    // Suppress clipboard monitor during our write
                    try 
                    { 
                        MainWindow.SetWritingClipboard(true);
                        System.Windows.Clipboard.SetText(capturedText);
                        await System.Threading.Tasks.Task.Delay(100);
                    } 
                    catch { }
                    finally { MainWindow.SetWritingClipboard(false); }
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"Text from {capturedSource} via {capturedTransport}! 📥");
                    // Wake up any long-poll clients (e.g. other Android devices waiting on /api/events)
                    NotifyClipboardChanged(clip.ItemType.ToString(), capturedText.Length > 40 ? capturedText.Substring(0, 40) : capturedText);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("TEXT INJECTION ERR", ex.Message);
                }
            });
        }
    }
}
