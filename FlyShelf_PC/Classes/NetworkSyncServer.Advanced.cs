// ---------------------------------------------------------------
// NetworkSyncServer � Advanced Operations
// ChunkUpload, ConvertToPdf, MultipartParsing, FileDownload,
// QR Pairing, Remote Logging, Log Dashboard
// Split from NetworkSyncServer.cs for modularity
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
        // ─── Chunked Upload System (bypasses Cloudflare 100MB limit) ───
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _chunkSessions = new();

        private async Task HandleChunkUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string sessionId = req.Headers["X-Upload-Session"] ?? "";
                string chunkIndexStr = req.Headers["X-Chunk-Index"] ?? "0";
                
                if (string.IsNullOrEmpty(sessionId))
                {
                    res.StatusCode = 400;
                    res.Close();
                    return;
                }

                string chunkDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Chunks", sessionId);
                Directory.CreateDirectory(chunkDir);
                _chunkSessions[sessionId] = chunkDir;

                string chunkPath = Path.Combine(chunkDir, $"chunk_{chunkIndexStr.PadLeft(6, '0')}");
                using (var fs = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CHUNK UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private async Task HandleChunkFinalize(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string sessionId = req.Headers["X-Upload-Session"] ?? "";
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string batchName = req.Headers["X-Batch-Name"] ?? "";
                string originalDateStr = req.Headers["X-Original-Date"];
                string totalChunksStr = req.Headers["X-Total-Chunks"] ?? "0";

                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                if (!string.IsNullOrEmpty(batchName))
                    try { batchName = Uri.UnescapeDataString(batchName); } catch { }
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "FlyShelf_Chunked_Transfer";
                string sourceDevice = req.Headers["X-Source-Device"] ?? "Remote";
                try { sourceDevice = Uri.UnescapeDataString(sourceDevice); } catch { }
                var chunkTransport = DetectTransport(req);

                if (!_chunkSessions.TryGetValue(sessionId, out string chunkDir) || !Directory.Exists(chunkDir))
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                // Merge all chunks in order
                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                int counter = 1;
                string finalPath = Path.Combine(archiveDir, rawName);
                while (File.Exists(finalPath))
                {
                    finalPath = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                var chunkFiles = Directory.GetFiles(chunkDir, "chunk_*").OrderBy(f => f).ToArray();

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Assembling {rawName} ({chunkFiles.Length} chunks)... 📦");
                });

                using (var outputFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    foreach (var chunkFile in chunkFiles)
                    {
                        using (var chunkFs = new FileStream(chunkFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                        {
                            await chunkFs.CopyToAsync(outputFs);
                        }
                    }
                }

                // Set original timestamps
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }
                if (originalDate.HasValue)
                {
                    try { File.SetCreationTime(finalPath, originalDate.Value); File.SetLastWriteTime(finalPath, originalDate.Value); } catch { }
                }

                // Cleanup temp chunks
                try { Directory.Delete(chunkDir, true); } catch { }
                _chunkSessions.TryRemove(sessionId, out _);

                var fileInfo = new FileInfo(finalPath);
                string sizeStr = fileInfo.Length > 1_073_741_824 ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB" : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"✅ {rawName} ({sizeStr}) received!");
                    // Auto-copy to clipboard + insert into FlyShelf
                    try
                    {
                        var fileList = new System.Collections.Specialized.StringCollection { finalPath };
                        System.Windows.Clipboard.SetFileDropList(fileList);
                        
                        var clip = new ClipboardItem
                        {
                            RawContent = finalPath,
                            FileName = rawName,
                            FilePath = finalPath,
                            Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(),
                            ItemType = ClipboardItemType.File,
                            SourceDeviceName = sourceDevice,
                            SourceDeviceType = sourceDevice.Contains("PC") || sourceDevice.Contains("LAPTOP") || sourceDevice.Contains("DESKTOP") ? "PC" : "Mobile",
                            TransferMethod = chunkTransport.transport
                        };
                        clip.EvaluateSmartActions();
                        _viewModel.DroppedItems.Insert(0, clip);
                        _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    }
                    catch { }
                });

                // Also track in batch for consistency 
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes($"{{\"status\":\"ok\",\"size\":\"{sizeStr}\"}}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CHUNK FINALIZE ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private async Task HandleConvertToPdf(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string fileName = req.QueryString["name"] ?? $"document_{DateTime.Now.Ticks}.docx";
                string convertDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Conversions");
                Directory.CreateDirectory(convertDir);

                string inputPath = Path.Combine(convertDir, fileName);
                using (var fs = new FileStream(inputPath, FileMode.Create, FileAccess.Write))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                string pdfName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
                string pdfPath = Path.Combine(convertDir, pdfName);

                // Try LibreOffice conversion first (most reliable cross-platform)
                bool converted = false;
                string[] libreOfficePaths = new[] {
                    @"C:\Program Files\LibreOffice\program\soffice.exe",
                    @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
                };

                string sofficePath = libreOfficePaths.FirstOrDefault(p => File.Exists(p));
                if (sofficePath != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = sofficePath,
                        Arguments = $"--headless --convert-to pdf --outdir \"{convertDir}\" \"{inputPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            await proc.WaitForExitAsync();
                            converted = proc.ExitCode == 0 && File.Exists(pdfPath);
                        }
                    }
                }

                // Fallback: Try Microsoft Word COM automation
                if (!converted)
                {
                    try
                    {
                        Type wordType = Type.GetTypeFromProgID("Word.Application");
                        if (wordType != null)
                        {
                            dynamic word = Activator.CreateInstance(wordType);
                            word.Visible = false;
                            dynamic doc = word.Documents.Open(inputPath);
                            doc.SaveAs2(pdfPath, 17); // 17 = wdFormatPDF
                            doc.Close(false);
                            word.Quit();
                            converted = File.Exists(pdfPath);
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(word);
                        }
                    }
                    catch { }
                }

                if (converted && File.Exists(pdfPath))
                {
                    // Also add the PDF to the clipboard shelf
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        var dropList = new System.Collections.Specialized.StringCollection { pdfPath };
                        dataObj.SetFileDropList(dropList);
                        _viewModel.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast($"Converted: {pdfName} ✅");
                    });

                    string downloadUrl = $"/download?path={Uri.EscapeDataString(pdfPath)}";
                    string json = JsonSerializer.Serialize(new { success = true, downloadUrl, fileName = pdfName });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 200;
                    try { res.OutputStream.Write(buffer, 0, buffer.Length); } catch { }
                }
                else
                {
                    string json = JsonSerializer.Serialize(new { success = false, error = "No converter found. Install LibreOffice or Microsoft Word." });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 500;
                    try { res.OutputStream.Write(buffer, 0, buffer.Length); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("CONVERT PDF ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }
        private async Task<string?> ProcessStreamingMultipartFile(string tempFilePath, string boundary, string destinationDir, DateTime? applyDate = null)
        {
            try
            {
                using (var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    int bufferSize = Math.Min(1024 * 1024, (int)fs.Length);
                    byte[] headBuffer = new byte[bufferSize];
                    int readLen = await fs.ReadAsync(headBuffer, 0, bufferSize);
                    
                    ReadOnlySpan<byte> headSpan = new ReadOnlySpan<byte>(headBuffer, 0, readLen);
                    
                    byte[] filenameSeq = Encoding.ASCII.GetBytes("filename=\"");
                    int filenameIdx = headSpan.IndexOf(filenameSeq);

                    if (filenameIdx != -1)
                    {
                        byte[] headerEndSeq = Encoding.ASCII.GetBytes("\r\n\r\n");
                        int headerEndRel = headSpan.Slice(filenameIdx).IndexOf(headerEndSeq);

                        if (headerEndRel != -1)
                        {
                            long physicalDataStart = filenameIdx + headerEndRel + 4;
                            
                            string headerStr = Encoding.UTF8.GetString(headBuffer, 0, (int)physicalDataStart);
                            int nameIndexStart = headerStr.IndexOf("filename=\"") + 10;
                            int nameEnd = headerStr.IndexOf("\"", nameIndexStart);
                            string fileName = headerStr.Substring(nameIndexStart, nameEnd - nameIndexStart);
                            if (string.IsNullOrWhiteSpace(fileName)) fileName = "uploaded_file.dat";
                            fileName = Path.GetFileName(fileName);
                            
                            int counter = 1;
                            string finalPath = Path.Combine(destinationDir, fileName);
                            while(File.Exists(finalPath))
                            {
                                finalPath = Path.Combine(destinationDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{counter++}{Path.GetExtension(fileName)}");
                            }

                            fs.Seek(0, SeekOrigin.End);
                            long totalLen = fs.Length;
                            int tailSearchSize = Math.Min(8192, (int)totalLen);
                            fs.Seek(totalLen - tailSearchSize, SeekOrigin.Begin);
                            
                            byte[] tailBuffer = new byte[tailSearchSize];
                            int tailReadLen = await fs.ReadAsync(tailBuffer, 0, tailSearchSize);
                            
                            ReadOnlySpan<byte> tailSpan = new ReadOnlySpan<byte>(tailBuffer, 0, tailReadLen);
                            byte[] footerSeq = Encoding.ASCII.GetBytes("\r\n--" + boundary);
                            int footerIdxRel = tailSpan.LastIndexOf(footerSeq);
                            
                            long physicalDataEnd = totalLen;
                            if (footerIdxRel != -1)
                            {
                                physicalDataEnd = (totalLen - tailSearchSize) + footerIdxRel;
                            }

                            fs.Seek(physicalDataStart, SeekOrigin.Begin);
                            long bytesRemaining = physicalDataEnd - physicalDataStart;

                            using (var outFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] transferBuf = new byte[81920];
                                while (bytesRemaining > 0)
                                {
                                    int toRead = (int)Math.Min(transferBuf.Length, bytesRemaining);
                                    int r = await fs.ReadAsync(transferBuf, 0, toRead);
                                    if (r == 0) break;
                                    await outFs.WriteAsync(transferBuf, 0, r);
                                    bytesRemaining -= r;
                                }
                            }

                            if (applyDate.HasValue)
                            {
                                try
                                {
                                    File.SetCreationTime(finalPath, applyDate.Value);
                                    File.SetLastWriteTime(finalPath, applyDate.Value);
                                } catch { }
                            }

                            return finalPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("FILE PARSER", ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            }
            return null;
        }
#pragma warning restore CA2022

        // Helper: detect if a remote IP is on the same LAN (private range)
        private static bool IsLanAddress(string remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp)) return false;
            // 127.x, 10.x, 192.168.x, 172.16-31.x = local/LAN
            if (remoteIp.StartsWith("127.") || remoteIp.StartsWith("10.") || remoteIp.StartsWith("192.168.")) return true;
            if (remoteIp.StartsWith("172."))
            {
                if (int.TryParse(remoteIp.Split('.').ElementAtOrDefault(1), out int b) && b >= 16 && b <= 31) return true;
            }
            return false;
        }

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
                res.AddHeader("Access-Control-Allow-Origin", "*");
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
                        
                        // Persist these updated network metadata fields to SQLite database
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            Classes.ClipboardHistoryManager.UpdateItemNetworkFields(newest);
                        });

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
                        await System.Threading.Tasks.Task.Delay(500);
                    }
                    catch { }
                    finally { MainWindow.SetWritingClipboard(false); }
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"Saved: {System.IO.Path.GetFileName(filePath)} via {transferMethod} ðŸ“¥");
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

                    _viewModel.DroppedItems.Insert(0, groupItem);
                    _viewModel.PruneOldItems();
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                    // Persist network fields to database
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        Classes.ClipboardHistoryManager.UpdateItemNetworkFields(groupItem);
                    });

                    // Set file drop list to clipboard
                    try
                    {
                        MainWindow.SetWritingClipboard(true);
                        var clipList = new System.Collections.Specialized.StringCollection();
                        foreach (var f in files) clipList.Add(f);
                        System.Windows.Clipboard.SetFileDropList(clipList);
                        await System.Threading.Tasks.Task.Delay(500);
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
                        if (System.Text.RegularExpressions.Regex.IsMatch(possiblePath, @"^[a-zA-Z]:[\\/]") || possiblePath.StartsWith("\\\\"))
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
                            FileName = capturedText.Length > 40 ? capturedText.Substring(0, 40) + "..." : capturedText,
                            Extension = capturedTransport == "WebSocket" ? "WS" : "SYNC",
                            ItemType = clipType,
                            SourceDeviceName = capturedSource,
                            SourceDeviceType = sourceDeviceType,
                            TransferMethod = capturedTransport
                        };
                    }

                    clip.EvaluateSmartActions();
                    bool wasEmpty = _viewModel.DroppedItems.Count == 0;
                    _viewModel.DroppedItems.Insert(0, clip);
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
                        await System.Threading.Tasks.Task.Delay(500);
                    } 
                    catch { }
                    finally { MainWindow.SetWritingClipboard(false); }
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"Text from {capturedSource} via {capturedTransport}! ðŸ“¥");
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
