// ---------------------------------------------------------------
// NetworkSyncServer.Advanced — File Download, Pairing & Injection
// Split from NetworkSyncServer.Advanced.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public partial class NetworkSyncServer
    {
        // ═══ Pair-endpoint rate limiter ═══
        // Tracks failed pairing attempts per remote IP.
        // Key: IP string. Value: (failCount, windowStartTicks)
        private static readonly ConcurrentDictionary<string, (int count, long windowStart)> _pairFailsByIp = new();
        private const int PAIR_MAX_FAILS_PER_IP   = 5;          // max failures from one IP per window
        private const int PAIR_MAX_FAILS_GLOBAL    = 20;         // total failures across all IPs before global lockout
        private static int _pairGlobalFailCount    = 0;
        private const long PAIR_RATE_WINDOW_TICKS  = 60L * 10_000_000; // 60-second window

        // ═══ HTTP Transfer tracking (for Android REST-based file transfers) ═══
        private static readonly ConcurrentDictionary<Guid, HttpTransferInfo> _pendingHttpTransfers = new();
        private const int TRANSFER_STALE_MINUTES = 30; // Auto-cleanup abandoned transfers after 30 min

        private class HttpTransferInfo
        {
            public Guid TransferId { get; set; }
            public string FileName { get; set; } = "";
            public long FileSize { get; set; }
            public string FilePath { get; set; } = "";
            public string DeviceId { get; set; } = "";
            public string DeviceName { get; set; } = "";
            public long ResumeFrom { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        /// <summary>Purge HTTP transfers older than TRANSFER_STALE_MINUTES to prevent memory leaks.</summary>
        private static void CleanupStaleHttpTransfers()
        {
            var stale = _pendingHttpTransfers.Where(kv =>
                (DateTime.UtcNow - kv.Value.CreatedAt).TotalMinutes > TRANSFER_STALE_MINUTES).ToList();
            foreach (var kv in stale)
                _pendingHttpTransfers.TryRemove(kv.Key, out _);
        }

        private async Task ServeFileDownload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // PC-C1 FIX: Authenticate request via pairing key (header or query string fallback)
            var pairingKey = req.Headers["X-Pairing-Key"];
            if (string.IsNullOrEmpty(pairingKey))
                pairingKey = req.QueryString["key"];
            if (string.IsNullOrEmpty(pairingKey) || !DevicePairingManager.IsDevicePaired(pairingKey))
            {
                Logger.LogAction("SECURITY", $"🚫 BLOCKED unauthenticated download request from {req.RemoteEndPoint}");
                try { res.StatusCode = 403; res.Close(); } catch { } // Best-effort: failure is acceptable
                return;
            }

            string path = req.QueryString["path"];
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try { res.StatusCode = 404; res.Close(); } catch { } // Best-effort: failure is acceptable
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
                catch { } // Best-effort: failure is acceptable
                return;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                long fileSize = fileInfo.Length;
                string ext = Path.GetExtension(path).ToLower(CultureInfo.InvariantCulture);
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
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        // ═══ QR Code Pairing Handler ═══
        private async Task HandlePairRequest(HttpListenerRequest req, HttpListenerResponse res)
        {
            string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            try
            {
                // ═══ RATE LIMIT CHECK ═══
                long nowTicks = DateTime.UtcNow.Ticks;

                // Global lockout: too many failures from any IP combined
                if (System.Threading.Volatile.Read(ref _pairGlobalFailCount) >= PAIR_MAX_FAILS_GLOBAL)
                {
                    byte[] tooMany = Encoding.UTF8.GetBytes("{\"error\":\"Too many pairing attempts. Try again later.\"}");
                    res.StatusCode = 429;
                    res.ContentType = "application/json";
                    try { res.OutputStream.Write(tooMany, 0, tooMany.Length); } catch { } // Best-effort: failure is acceptable
                    Logger.LogAction("SECURITY", $"⛔ /api/pair global rate-limit hit from {remoteIp}");
                    return;
                }

                // Per-IP lockout
                if (_pairFailsByIp.TryGetValue(remoteIp, out var ipState))
                {
                    // Reset window if 60s has elapsed
                    if (nowTicks - ipState.windowStart > PAIR_RATE_WINDOW_TICKS)
                        _pairFailsByIp.TryRemove(remoteIp, out _);
                    else if (ipState.count >= PAIR_MAX_FAILS_PER_IP)
                    {
                        byte[] blocked = Encoding.UTF8.GetBytes("{\"error\":\"Too many pairing attempts from this device. Try again in 60 seconds.\"}");
                        res.StatusCode = 429;
                        res.ContentType = "application/json";
                        try { res.OutputStream.Write(blocked, 0, blocked.Length); } catch { } // Best-effort: failure is acceptable
                        Logger.LogAction("SECURITY", $"⛔ /api/pair rate-limited IP: {remoteIp} ({ipState.count} fails)");
                        return;
                    }
                }

                if (req.ContentLength64 > 65_536) // 64KB limit
                {
                    res.StatusCode = 413;
                    await WriteJsonResponse(res, false, "Request body too large");
                    return;
                }

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

                if (string.IsNullOrEmpty(deviceId))
                    deviceId = $"{deviceName}_{remoteIp}";

                bool success = DevicePairingManager.TryPairDevice(pairingKey, deviceId, deviceName, deviceType, remoteIp);

                if (success)
                {
                    // Clear any recorded failures from this IP on success
                    _pairFailsByIp.TryRemove(remoteIp, out _);

                    var response = new
                    {
                        status = "paired",
                        deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                        deviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName,
                        localUrl = DisplayUrl,
                        globalUrl = GlobalUrl ?? "",
                        pin = SettingsManager.Current.WebClientPinToken,
                        isPro = LicenseManager.IsPro,
                        licenseKey = LicenseManager.IsPro ? LicenseManager.MaskedKey : ""
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
                    // Record the failure for rate limiting
                    _pairFailsByIp.AddOrUpdate(
                        remoteIp,
                        _ => (1, nowTicks),
                        (_, old) => (old.windowStart + PAIR_RATE_WINDOW_TICKS < nowTicks)
                            ? (1, nowTicks)                   // window expired — reset
                            : (old.count + 1, old.windowStart) // still in window — increment
                    );
                    System.Threading.Interlocked.Increment(ref _pairGlobalFailCount);
                    // Auto-reset global counter after 5 minutes to recover from transient attack
                    _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(5))
                        .ContinueWith(_ => System.Threading.Interlocked.Decrement(ref _pairGlobalFailCount));

                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                    res.StatusCode = 403;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                    Logger.LogAction("SECURITY", $"⚠️ Failed pair attempt from {remoteIp} (key length={pairingKey?.Length ?? 0})");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR ERROR", ex.Message);
                byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Internal server error\"}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                try { res.OutputStream.Write(err, 0, err.Length); } catch { } // Best-effort: failure is acceptable
            }
            finally
            {
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
            }
        }

        // ═══ Peer Announce Handler — Instant P2P Reverse Discovery ═══
        /// <summary>
        /// Called when a remote peer POSTs to /api/peer_announce after discovering us.
        /// The peer sends its own URLs so we can connect back instantly (no Firebase round-trip).
        /// Validates the pairing key, registers the peer in PeerManager, and returns our own URLs.
        /// </summary>
        private async Task HandlePeerAnnounce(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                if (req.ContentLength64 > 65_536) // 64KB limit
                {
                    res.StatusCode = 413;
                    await WriteJsonResponse(res, false, "Request body too large");
                    return;
                }

                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

                var data = JsonSerializer.Deserialize<JsonElement>(body);
                string pairingKey = data.TryGetProperty("pairingKey", out var pk) ? pk.GetString() : "";
                string deviceId = data.TryGetProperty("deviceId", out var di) ? di.GetString() : "";
                string deviceName = data.TryGetProperty("deviceName", out var dn) ? dn.GetString() : "";
                string lanUrl = data.TryGetProperty("lanUrl", out var lu) ? lu.GetString() : "";
                string cloudflareUrl = data.TryGetProperty("cloudflareUrl", out var cu) ? cu.GetString() : "";

                // Validate pairing key
                if (string.IsNullOrEmpty(pairingKey) || !DevicePairingManager.IsDevicePaired(pairingKey))
                {
                    Logger.LogAction("PEER_ANNOUNCE", $"⛔ Rejected announce from {deviceName} — invalid pairing key");
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid pairing key\"}");
                    res.StatusCode = 403;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                    res.Close();
                    return;
                }

                // PM-1 FIX: Also verify the device ID is in the paired devices list
                if (!string.IsNullOrEmpty(deviceId))
                {
                    var pairedDevices = DevicePairingManager.GetPairedDevices();
                    bool deviceKnown = pairedDevices.Any(d => d.DeviceId == deviceId);
                    if (!deviceKnown)
                    {
                        Logger.LogAction("PEER_ANNOUNCE", $"⛔ Rejected announce from {deviceName} — device ID {deviceId} not in paired devices list");
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Device not recognized\"}");
                        res.StatusCode = 403;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                        return;
                    }
                }

                Logger.LogAction("PEER_ANNOUNCE", $"📢 Received announce from {deviceName} (LAN={lanUrl} CF={cloudflareUrl})");

                // Handle the announce in PeerManager (creates/updates peer, handshakes back if needed)
                if (PeerManager.Instance != null)
                {
                    _ = Task.Run(() => PeerManager.Instance.HandlePeerAnnounce(deviceId, deviceName, lanUrl, cloudflareUrl));
                }

                // Return our own URLs so the announcer gets our latest info
                var response = new
                {
                    deviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName,
                    deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                    lanUrl = CloudDiscoveryManager.CachedLocalUrl ?? "",
                    cloudflareUrl = CloudDiscoveryManager.CachedGlobalUrl ?? ""
                };
                byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.OutputStream.Write(json, 0, json.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("PEER_ANNOUNCE ERROR", ex.Message);
                byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Internal server error\"}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                try { res.OutputStream.Write(err, 0, err.Length); } catch { } // Best-effort: failure is acceptable
            }
            finally
            {
                try { res.Close(); } catch { } // Best-effort: failure is acceptable
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
                    _viewModel.HandleDrop(dataObj, false, skipCloudSync: true, sourceDevice, sourceDeviceType, transferMethod);
                    
                    // Write received file to OS clipboard so user can paste it
                    ClipboardHelper.SafeSetFileDropList(new System.Collections.Specialized.StringCollection { filePath }, suppressEcho: true, echoDelayMs: 100);
                    
                    string sizeStr = "";
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            sizeStr = $" ({FlyShelf.Classes.FormatHelper.FormatSize(new FileInfo(filePath).Length)})";
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                    string friendlyType = FlyShelf.Classes.FormatHelper.GetFileTypeFriendly(filePath);
                    FlyShelf.Windows.ToastWindow.ShowToast($"{friendlyType} received{sizeStr} via {transferMethod} 📥");
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
                    var clipList = new System.Collections.Specialized.StringCollection();
                    foreach (var f in files) clipList.Add(f);
                    ClipboardHelper.SafeSetFileDropList(clipList, suppressEcho: true, echoDelayMs: 100);

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
                        catch { } // Best-effort: failure is acceptable
                    }

                    bool isPath = false;
                    try
                    {
                        if (_rxWinPath.IsMatch(possiblePath) || possiblePath.StartsWith("\\\\", StringComparison.Ordinal))
                        {
                            isPath = true;
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
                            FileName = capturedText.Length > 5000 ? string.Concat(capturedText.AsSpan(0, 5000), "...") : capturedText,
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
                    
                    // Persist history so the synced text survives app restarts
                    _viewModel.PersistHistoryPublic();
                    
                    // ECHO PREVENTION: Mark this text as cloud-sourced so the clipboard monitor
                    // doesn't re-push it to Firebase when we set the Windows clipboard below.
                    string txtFp = $"TXT::{capturedText.Substring(0, Math.Min(200, capturedText.Length))}";
                    _viewModel.MarkAsCloudSourced(txtFp);
                    
                    // Suppress clipboard monitor during our write
                    ClipboardHelper.SafeSetText(capturedText, suppressEcho: true, echoDelayMs: 100);
                    
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

        // ═══ Android REST Transfer Endpoints ═══

        private async Task HandleNearbyQuery(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                var info = new
                {
                    type = "FlyShelf_Nearby_v1",
                    deviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName,
                    deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                    deviceType = "PC",
                    httpPort = CurrentPort,
                    transferPort = LanTransferEngine.TRANSFER_PORT,
                    version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                    peerCount = PeerManager.Instance?.AliveCount ?? 0,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info));
                res.StatusCode = 200;
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(data, 0, data.Length);
            }
            catch { res.StatusCode = 500; }
            finally { try { res.Close(); } catch { } /* Best-effort: failure is acceptable */ }
        }

        private async Task HandleTransferOffer(HttpListenerRequest req, HttpListenerResponse res)
        {
            // PC-C2 FIX: Authenticate request via pairing key (header or query string fallback)
            var pairingKey = req.Headers["X-Pairing-Key"];
            if (string.IsNullOrEmpty(pairingKey))
                pairingKey = req.QueryString["key"];
            if (string.IsNullOrEmpty(pairingKey) || !DevicePairingManager.IsDevicePaired(pairingKey))
            {
                Logger.LogAction("SECURITY", $"🚫 BLOCKED unauthenticated transfer offer from {req.RemoteEndPoint}");
                try { res.StatusCode = 403; res.Close(); } catch { } // Best-effort: failure is acceptable
                return;
            }

            try
            {
                // Cleanup abandoned transfers older than 30 minutes
                CleanupStaleHttpTransfers();
                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = await reader.ReadToEndAsync();

                var offer = JsonSerializer.Deserialize<JsonElement>(body);
                string fileName = offer.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "unnamed" : "unnamed";
                // SECURITY: Sanitize filename — strip directory separators and path traversal
                fileName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == "..")
                    fileName = $"transfer_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                long fileSize = offer.TryGetProperty("fileSize", out var fs) ? fs.GetInt64() : 0;
                string deviceId = offer.TryGetProperty("deviceId", out var di) ? di.GetString() ?? "" : "";
                string deviceName = offer.TryGetProperty("deviceName", out var dn) ? dn.GetString() ?? "Mobile" : "Mobile";
                // SECURITY: Sanitize deviceName used in directory path
                deviceName = string.Join("_", deviceName.Split(Path.GetInvalidFileNameChars()));

                var transferId = Guid.NewGuid();
                string receivePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "FlyShelf", "SyncedFiles", "Received", deviceName);
                Directory.CreateDirectory(receivePath);
                string filePath = Path.Combine(receivePath, fileName);

                // Check for existing partial file (resume support)
                long resumeFrom = 0;
                if (File.Exists(filePath))
                {
                    var existingFile = new FileInfo(filePath);
                    if (existingFile.Length < fileSize)
                        resumeFrom = existingFile.Length;
                }

                // Store pending transfer info for the upload endpoint
                _pendingHttpTransfers[transferId] = new HttpTransferInfo
                {
                    TransferId = transferId,
                    FileName = fileName,
                    FileSize = fileSize,
                    FilePath = filePath,
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    ResumeFrom = resumeFrom,
                    CreatedAt = DateTime.UtcNow
                };

                Logger.LogAction("TRANSFER", $"📥 HTTP transfer offer from {deviceName}: {fileName} ({fileSize / 1024}KB), resume from {resumeFrom}");

                var response = new
                {
                    transferId = transferId.ToString(),
                    accepted = true,
                    resumeFrom = resumeFrom
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                res.StatusCode = 200;
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSFER", $"Transfer offer error: {ex.Message}");
                byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Transfer offer failed\"}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                try { await res.OutputStream.WriteAsync(err, 0, err.Length); } catch { } // Best-effort: failure is acceptable
            }
            finally { try { res.Close(); } catch { } /* Best-effort: failure is acceptable */ }
        }

        private async Task HandleTransferUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // PC-C2 FIX: Authenticate request via pairing key (header or query string fallback)
            var pairingKey = req.Headers["X-Pairing-Key"];
            if (string.IsNullOrEmpty(pairingKey))
                pairingKey = req.QueryString["key"];
            if (string.IsNullOrEmpty(pairingKey) || !DevicePairingManager.IsDevicePaired(pairingKey))
            {
                Logger.LogAction("SECURITY", $"🚫 BLOCKED unauthenticated transfer upload from {req.RemoteEndPoint}");
                try { res.StatusCode = 403; res.Close(); } catch { } // Best-effort: failure is acceptable
                return;
            }

            try
            {
                string transferIdStr = req.QueryString["id"] ?? req.Headers["X-Transfer-Id"] ?? "";
                if (!Guid.TryParse(transferIdStr, out var transferId) || !_pendingHttpTransfers.TryGetValue(transferId, out var info))
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid or expired transfer ID\"}");
                    res.StatusCode = 404;
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(err, 0, err.Length);
                    res.Close();
                    return;
                }

                // Determine write position from Content-Range or resumeFrom
                long writePosition = info.ResumeFrom;
                string rangeHeader = req.Headers["Content-Range"];
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    // Format: "bytes START-END/TOTAL"
                    var match = Regex.Match(rangeHeader, @"bytes (\d+)-(\d+)/(\d+)");
                    if (match.Success)
                        writePosition = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                }

                // SECURITY: Validate write position is within bounds
                if (writePosition < 0 || writePosition > info.FileSize)
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Invalid write position\"}");
                    res.StatusCode = 400;
                    res.ContentType = "application/json";
                    await res.OutputStream.WriteAsync(err, 0, err.Length);
                    res.Close();
                    return;
                }

                Logger.LogAction("TRANSFER", $"📥 HTTP upload: {info.FileName} from pos {writePosition}");

                // Stream write with 1MB buffer
                using var fs = new FileStream(info.FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1048576, FileOptions.Asynchronous);
                fs.Seek(writePosition, SeekOrigin.Begin);

                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                long totalWritten = writePosition;
                try
                {
                    int bytesRead;
                    while ((bytesRead = await req.InputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, bytesRead);
                        totalWritten += bytesRead;
                        // Guard: stop if we've received more data than declared file size
                        if (totalWritten >= info.FileSize) break;
                    }
                    await fs.FlushAsync();
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }

                bool isComplete = totalWritten >= info.FileSize;

                if (isComplete)
                {
                    _pendingHttpTransfers.TryRemove(transferId, out _);
                    // Inject into clipboard
                    InjectReceivedFile(info.FilePath, info.DeviceName, "HTTP", "Mobile");
                    Logger.LogAction("TRANSFER", $"✅ HTTP transfer complete: {info.FileName} ({totalWritten / 1024}KB) from {info.DeviceName}");
                }

                var response = new
                {
                    status = isComplete ? "completed" : "partial",
                    bytesReceived = totalWritten,
                    fileSize = info.FileSize,
                    progress = info.FileSize > 0 ? Math.Round((double)totalWritten / info.FileSize * 100, 1) : 100.0
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                res.StatusCode = 200;
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSFER", $"Transfer upload error: {ex.Message}");
                byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Transfer upload failed\"}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                try { await res.OutputStream.WriteAsync(err, 0, err.Length); } catch { } // Best-effort: failure is acceptable
            }
            finally { try { res.Close(); } catch { } /* Best-effort: failure is acceptable */ }
        }

        private Task HandleTransferStatus(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string transferIdStr = req.QueryString["id"] ?? req.Headers["X-Transfer-Id"] ?? "";
                if (!Guid.TryParse(transferIdStr, out var transferId) || !_pendingHttpTransfers.TryGetValue(transferId, out var info))
                {
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"Transfer not found\"}");
                    res.StatusCode = 404;
                    res.ContentType = "application/json";
                    res.OutputStream.Write(err, 0, err.Length);
                    res.Close();
                    return Task.CompletedTask;
                }

                long currentSize = File.Exists(info.FilePath) ? new FileInfo(info.FilePath).Length : 0;
                var response = new
                {
                    transferId = transferId.ToString(),
                    fileName = info.FileName,
                    fileSize = info.FileSize,
                    bytesReceived = currentSize,
                    progress = info.FileSize > 0 ? Math.Round((double)currentSize / info.FileSize * 100, 1) : 0.0,
                    status = currentSize >= info.FileSize ? "completed" : "receiving"
                };
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.OutputStream.Write(data, 0, data.Length);
            }
            catch { res.StatusCode = 500; }
            finally { try { res.Close(); } catch { } /* Best-effort: failure is acceptable */ }
            return Task.CompletedTask;
        }
    }
}
