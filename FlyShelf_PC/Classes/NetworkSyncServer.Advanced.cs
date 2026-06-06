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
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

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
                
                if (string.IsNullOrEmpty(sessionId) || !System.Text.RegularExpressions.Regex.IsMatch(sessionId, "^[a-zA-Z0-9_-]+$"))
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
            string sessionId = req.Headers["X-Upload-Session"] ?? "";
            if (string.IsNullOrEmpty(sessionId) || !System.Text.RegularExpressions.Regex.IsMatch(sessionId, "^[a-zA-Z0-9_-]+$"))
            {
                res.StatusCode = 400;
                res.Close();
                return;
            }

            // ── Loopback/Echo Prevention Gate ──
            string sourceDeviceId = req.Headers["X-Source-DeviceId"] ?? "";
            if (!string.IsNullOrEmpty(sourceDeviceId) && sourceDeviceId == SettingsManager.Current.DeviceId)
            {
                Logger.LogAction("SYNC_GATE", "Ignored loopback chunk finalization from self");
                if (_chunkSessions.TryRemove(sessionId, out string gateChunkDir))
                {
                    try { if (Directory.Exists(gateChunkDir)) Directory.Delete(gateChunkDir, true); } catch { }
                }
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "loopback_ignored"); } catch { }
                res.Close();
                return;
            }

            // ── Incoming Sync Gate ──
            if (!SettingsManager.Current.EnableIncomingSync)
            {
                // Clean up chunk temp directory so it doesn't accumulate
                if (_chunkSessions.TryRemove(sessionId, out string gateChunkDir))
                {
                    try { if (Directory.Exists(gateChunkDir)) Directory.Delete(gateChunkDir, true); } catch { }
                }
                res.StatusCode = 200;
                try { await WriteJsonResponse(res, true, "sync_paused"); } catch { }
                res.Close();
                return;
            }

            try
            {
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string batchName = req.Headers["X-Batch-Name"] ?? "";
                string originalDateStr = req.Headers["X-Original-Date"];
                string totalChunksStr = req.Headers["X-Total-Chunks"] ?? "0";

                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Path.GetFileName(Uri.UnescapeDataString(encodedName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(rawName)) rawName = "uploaded_file.dat";

                if (!string.IsNullOrEmpty(batchName))
                {
                    try { batchName = Path.GetFileName(Uri.UnescapeDataString(batchName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "FlyShelf_Chunked_Transfer";

                string sourceDevice = req.Headers["X-Source-Device"] ?? "Remote";
                if (!string.IsNullOrEmpty(sourceDevice))
                {
                    try { sourceDevice = Path.GetFileName(Uri.UnescapeDataString(sourceDevice)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(sourceDevice)) sourceDevice = "Remote";

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

                // Validate chunk count integrity to prevent writing corrupted/truncated files on clipboard
                if (int.TryParse(totalChunksStr, out int expectedChunks) && expectedChunks > 0 && chunkFiles.Length != expectedChunks)
                {
                    Logger.LogAction("CHUNK FINALIZE ERROR", $"Chunk count mismatch for session {sessionId}. Expected: {expectedChunks}, Found: {chunkFiles.Length}");
                    res.StatusCode = 400;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Chunk count mismatch. Transfer may be incomplete.\"}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    return;
                }

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

                var fileInfo = new FileInfo(finalPath);
                if (fileInfo.Length > 50L * 1024 * 1024 && !LicenseManager.IsPro)
                {
                    try { File.Delete(finalPath); } catch { }
                    try { Directory.Delete(chunkDir, true); } catch { }
                    _chunkSessions.TryRemove(sessionId, out _);

                    res.StatusCode = 413;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"File transfer limited to 50 MB on Free tier.\"}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);

                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ File assembly rejected: exceeds 50 MB Free tier limit.");
                    });
                    return;
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

                string sizeStr = fileInfo.Length > 1_073_741_824 ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB" : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    FlyShelf.Windows.ToastWindow.ShowToast($"✅ {rawName} ({sizeStr}) received!");
                    // Auto-copy to clipboard + insert into FlyShelf
                    try
                    {
                        var fileList = new System.Collections.Specialized.StringCollection { finalPath };
                        ClipboardHelper.SafeSetFileDropList(fileList);
                        
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
                        _viewModel.InsertWithDedup(clip);
                        _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                        
                        // Persist history so the synced assembled chunk file survives app restarts
                        _viewModel.PersistHistoryPublic();
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
            if (!LicenseManager.CanConvertDoc())
            {
                res.StatusCode = 402; // Payment Required
                byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Daily document conversion limit reached on Free tier.\"}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                res.Close();
                return;
            }

            try
            {
                string fileName = req.QueryString["name"] ?? "";
                if (!string.IsNullOrEmpty(fileName))
                {
                    try { fileName = Path.GetFileName(Uri.UnescapeDataString(fileName)); } catch { }
                }
                if (string.IsNullOrWhiteSpace(fileName)) fileName = $"document_{DateTime.Now.Ticks}.docx";

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

                    // Record successful doc conversion
                    LicenseManager.RecordDocConversion();

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

        private async Task HandleMergePdfs(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (!LicenseManager.CanMergePdf())
            {
                res.StatusCode = 402; // Payment Required
                byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Daily PDF merge limit reached on Free tier.\"}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                res.Close();
                return;
            }

            try
            {
                // [SECURITY FIX v2.1.0]: Reject oversized text uploads (DoS prevention)
                long contentLength = req.ContentLength64;
                if (contentLength > 10_485_760) // 10MB max
                {
                    res.StatusCode = 413;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Request body too large (10MB max)\"}");
                    res.ContentType = "application/json";
                    res.OutputStream.Write(errBytes, 0, errBytes.Length);
                    res.Close();
                    return;
                }
                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("urls", out var urlsProp) || urlsProp.ValueKind != JsonValueKind.Array)
                {
                    res.StatusCode = 400;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Invalid request: 'urls' array is required.\"}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    res.Close();
                    return;
                }

                var urls = new List<string>();
                foreach (var element in urlsProp.EnumerateArray())
                {
                    string urlStr = element.GetString() ?? "";
                    if (!string.IsNullOrEmpty(urlStr))
                    {
                        urls.Add(urlStr);
                    }
                }

                if (urls.Count < 2)
                {
                    res.StatusCode = 400;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"At least two URLs are required to merge.\"}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    res.Close();
                    return;
                }

                string mergeTempDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Merges");
                Directory.CreateDirectory(mergeTempDir);

                var localFiles = new List<string>();
                var tempFilesCreated = new List<string>();

                foreach (var url in urls)
                {
                    string localPath = "";

                    // Optimization: Bypass download if URL points to our own download server and path exists
                    if (url.Contains("/download?path="))
                    {
                        try
                        {
                            int qIdx = url.IndexOf("?path=");
                            if (qIdx != -1)
                            {
                                string pathParam = url.Substring(qIdx + 6);
                                int ampIdx = pathParam.IndexOf('&');
                                if (ampIdx != -1)
                                {
                                    pathParam = pathParam.Substring(0, ampIdx);
                                }
                                string decodedPath = Uri.UnescapeDataString(pathParam);
                                if (File.Exists(decodedPath) && IsPathAllowed(decodedPath))
                                {
                                    localPath = decodedPath;
                                }
                            }
                        }
                        catch {}
                    }

                    // Download if not resolved locally
                    if (string.IsNullOrEmpty(localPath))
                    {
                        // [SECURITY FIX v2.1.0]: SSRF protection — validate URL before download
                        if (!IsUrlSafeForDownload(url))
                        {
                            Logger.LogAction("MERGE PDF SECURITY", $"Blocked SSRF attempt: {url}");
                            continue;
                        }
                        try
                        {
                            // Strip query parameters for extension detection
                            string cleanUrl = url;
                            int qMarkIdx = url.IndexOf('?');
                            if (qMarkIdx != -1) cleanUrl = url.Substring(0, qMarkIdx);
                            string ext = Path.GetExtension(cleanUrl).ToLower();
                            if (string.IsNullOrEmpty(ext)) ext = ".pdf"; // default fallback

                            string tempFile = Path.Combine(mergeTempDir, Guid.NewGuid().ToString() + ext);
                            using (var downloadRes = await _httpClient.GetAsync(url))
                            {
                                downloadRes.EnsureSuccessStatusCode();
                                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await downloadRes.Content.CopyToAsync(fs);
                                }
                            }
                            localPath = tempFile;
                            tempFilesCreated.Add(tempFile);
                        }
                        catch (Exception dlEx)
                        {
                            Logger.LogAction("MERGE PDF DOWNLOAD ERR", $"Failed to download URL '{url}': {dlEx.Message}");
                        }
                    }

                    if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                    {
                        // Convert image to PDF if necessary
                        string ext = Path.GetExtension(localPath).ToLower();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            try
                            {
                                string pdfPath = ConversionUtils.ConvertImageToPdf(localPath);
                                if (File.Exists(pdfPath))
                                {
                                    localPath = pdfPath;
                                    tempFilesCreated.Add(pdfPath); // also clean up the generated PDF later
                                }
                            }
                            catch (Exception imgEx)
                            {
                                Logger.LogAction("MERGE PDF IMG CONVERT ERR", $"Failed to convert image '{localPath}': {imgEx.Message}");
                            }
                        }
                        else if (ext == ".doc" || ext == ".docx")
                        {
                            try
                            {
                                string pdfPath = await ConversionUtils.ConvertDocToPdfAsync(localPath);
                                if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
                                {
                                    localPath = pdfPath;
                                    tempFilesCreated.Add(pdfPath); // also clean up the generated PDF later
                                }
                            }
                            catch (Exception docEx)
                            {
                                Logger.LogAction("MERGE PDF DOC CONVERT ERR", $"Failed to convert doc '{localPath}': {docEx.Message}");
                            }
                        }

                        // Ensure it's a PDF now
                        if (Path.GetExtension(localPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            localFiles.Add(localPath);
                        }
                    }
                }

                if (localFiles.Count < 2)
                {
                    res.StatusCode = 400;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Could not resolve at least two valid PDF files to merge.\"}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    res.Close();
                    return;
                }

                // Merge the files
                string mergeDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "FlyShelf", "Merged");
                Directory.CreateDirectory(mergeDir);
                string baseName = $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                string outputPath = Path.Combine(mergeDir, baseName);

                bool mergeSuccess = await Task.Run(() =>
                {
                    try
                    {
                        using (PdfDocument outputDocument = new PdfDocument())
                        {
                            foreach (var filePath in localFiles)
                            {
                                using (PdfDocument inputDocument = PdfReader.Open(filePath, PdfDocumentOpenMode.Import))
                                {
                                    for (int idx = 0; idx < inputDocument.PageCount; idx++)
                                    {
                                        PdfPage page = inputDocument.Pages[idx];
                                        outputDocument.AddPage(page);
                                    }
                                }
                            }
                            outputDocument.Save(outputPath);
                        }
                        return true;
                    }
                    catch (Exception mergeEx)
                    {
                        Logger.LogAction("PDF MERGE ERR", $"Merge process failed: {mergeEx.Message}");
                        return false;
                    }
                });

                // Clean up temporary downloaded / converted files
                foreach (var tempFile in tempFilesCreated)
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch {}
                }

                if (mergeSuccess && File.Exists(outputPath))
                {
                    LicenseManager.RecordPdfMerge();

                    // Register to local clipboard shelf on dispatcher
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        var dropList = new System.Collections.Specialized.StringCollection { outputPath };
                        dataObj.SetFileDropList(dropList);
                        _viewModel.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast($"Merged PDF: {baseName} ✅");
                    });

                    string pairingKey = DevicePairingManager.EnsurePairingKey();
                    string downloadUrl = $"/download?path={Uri.EscapeDataString(outputPath)}";
                    if (!string.IsNullOrEmpty(pairingKey))
                    {
                        downloadUrl += $"&key={Uri.EscapeDataString(pairingKey)}";
                    }

                    string json = JsonSerializer.Serialize(new { success = true, downloadUrl, fileName = baseName });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                else
                {
                    res.StatusCode = 500;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Failed to save merged PDF document.\"}");
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("MERGE PDF ROUTE ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

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

        // ═══ File Download, Pairing & Injection moved to NetworkSyncServer.FileTransfer.cs ═══

        /// <summary>
        /// [SECURITY v2.1.0] Validates a URL is safe for server-side download.
        /// Blocks private/loopback IPs, non-HTTP schemes, and cloud metadata endpoints.
        /// </summary>
        private static bool IsUrlSafeForDownload(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                // Only allow HTTP/HTTPS
                if (uri.Scheme != "http" && uri.Scheme != "https") return false;
                // Block cloud metadata endpoints
                if (uri.Host == "169.254.169.254" || uri.Host == "metadata.google.internal") return false;
                // Block loopback and private IPs
                if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
                {
                    if (System.Net.IPAddress.IsLoopback(ip)) return false;
                    byte[] bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        // 10.x.x.x, 172.16-31.x.x, 192.168.x.x
                        if (bytes[0] == 10) return false;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
                        if (bytes[0] == 192 && bytes[1] == 168) return false;
                        if (bytes[0] == 127) return false;
                    }
                }
                // Block localhost variants
                if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
