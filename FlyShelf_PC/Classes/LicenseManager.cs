// ═══════════════════════════════════════════════════════════════════
// LicenseManager — Freemium tier management for FlyShelf v2.0.0
// Security-hardened: HMAC re-validation on load, anti-tamper,
// assembly integrity checks, enforced device limits.
// Uses NetworkClock (NTP-synced) for tamper-resistant time.
// Persists to %AppData%/FlyShelf/license.json
// ═══════════════════════════════════════════════════════════════════
using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    // ═══════════════════════════════════════════════════════════════
    // DATA MODEL
    // ═══════════════════════════════════════════════════════════════

    public class LicenseData
    {
        public string LicenseKey { get; set; } = "";
        public string Tier { get; set; } = "free"; // "free" or "pro"
        public string ActivatedAt { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public DailyUsageData DailyUsage { get; set; } = new();
        // ═══ Server-side JWT validation (v2.1.0) ═══
        public string ActivationToken { get; set; } = ""; // JWT from /api/activate
        public string LastValidated { get; set; } = "";   // ISO timestamp of last successful server validation
    }

    public class DailyUsageData
    {
        public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
        public int PdfMerges { get; set; }
        public int PdfSaves { get; set; }
        public int DocConversions { get; set; }
        public int ImageToPdf { get; set; }
        public int QrScans { get; set; }
        public int OcrExtractions { get; set; }
        public int TableExtractions { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // LICENSE MANAGER SINGLETON
    // ═══════════════════════════════════════════════════════════════

    public static class LicenseManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
        private static readonly string _licensePath = Path.Combine(_appDataDir, "license.json");
        private static readonly object _lock = new();

        private static LicenseData _data = new();
        private static bool _loaded = false;

        // ═══ ANTI-TAMPER: Runtime integrity sentinel (v2.4.0) ═══
        // Delegated to AntiTamperService — see AntiTamperService.cs
        private static int _tierSentinel = 0;

        /// <summary>Computes tier sentinel via AntiTamperService.</summary>
        private static int ComputeTierSentinel(string tier, string key)
        {
            return AntiTamperService.ComputeTierSentinel(tier, key, _appDataDir, GetKeySecret());
        }

        /// <summary>Updates the sentinel to match current _data state.</summary>
        private static void UpdateTierSentinel()
        {
            _tierSentinel = ComputeTierSentinel(_data.Tier, _data.LicenseKey);
        }

        // ═══ Server-side validation endpoint (v2.1.0) ═══
        private const string VERCEL_API_BASE = "https://fly-shelf.vercel.app/api";
        private const int REVALIDATION_INTERVAL_DAYS = 7;
        private const int OFFLINE_GRACE_PERIOD_DAYS = 14;

        // ═══ DAILY LIMITS (Free tier) ═══
        // All these features are 100% offline — generous limits cost us nothing
        // and build goodwill. Only power users hit these.
        public const int FREE_HISTORY_CAP = 500;
        public const int PRO_HISTORY_CAP = 2500;
        public const int FREE_PDF_MERGE_DAILY = 10;
        public const int FREE_PDF_SAVE_DAILY = 10;
        public const int FREE_DOC_CONVERT_DAILY = 10;
        public const int FREE_IMAGE_TO_PDF_DAILY = 10;
        public const int FREE_QR_SCAN_DAILY = 2;
        public const int FREE_OCR_DAILY = 15;
        public const int FREE_TABLE_EXTRACT_DAILY = 5;
        public const int FREE_PIN_LIMIT = 20;
        public const int FREE_TODO_DAILY = 10;
        public const int FREE_NOTE_DAYS = 60;
        public const int FREE_NOTE_IMAGES_PER_CARD = 1;
        public const int PRO_NOTE_IMAGES_PER_CARD = 5;

        // ═══ KEY VALIDATION SECRET (XOR-obfuscated so it's not plaintext in the binary) ═══
        // Obfuscated with XOR key — decoded at runtime only when needed
        private static readonly byte[] _secretXorKey = Encoding.UTF8.GetBytes("FS_Desktop_Key");
        private static readonly byte[] _secretData = new byte[]
        {
            0x00, 0x00, 0x00, 0x14, 0x17, 0x1C, 0x34, 0x3F, 0x17, 0x49,
            0x32, 0x7F, 0x37, 0x4E, 0x30, 0x02, 0x6D, 0x2A, 0x20, 0x4B,
            0x1C, 0x38, 0x1F, 0x2F, 0x6D, 0x7B, 0x57, 0x4F
        };
        private static string GetKeySecret()
        {
            var result = new byte[_secretData.Length];
            for (int i = 0; i < _secretData.Length; i++)
                result[i] = (byte)(_secretData[i] ^ _secretXorKey[i % _secretXorKey.Length]);
            return Encoding.UTF8.GetString(result);
        }

        // ═══════════════════════════════════════════════════════════════
        // PROPERTIES
        // ═══════════════════════════════════════════════════════════════

        /// <summary>True if user has an active Pro license.</summary>
        public static bool IsPro
        {
            get
            {
                // ═══ ANTI-DEBUG CHECK (v2.4.0) — delegated to AntiTamperService ═══
                if (AntiTamperService.CheckDebuggerPeriodic()) return false;

                EnsureLoaded();

                // Basic tier check
                if (_data.Tier != "pro" || string.IsNullOrEmpty(_data.LicenseKey))
                    return false;

                // ═══ TIER SENTINEL CHECK (v2.4.0) ═══
                // Verify the in-memory Tier wasn't patched by Cheat Engine / memory editor.
                // The sentinel is computed from Tier + LicenseKey + a salt and must match.
                if (_tierSentinel != ComputeTierSentinel(_data.Tier, _data.LicenseKey))
                {
                    Logger.LogAction("SECURITY", "⚠️ Tier sentinel mismatch — possible memory tampering detected");
                    _data.Tier = "free";
                    _data.LicenseKey = "";
                    _tierSentinel = 0;
                    try { Save(); } catch (Exception ex) { Logger.LogAction("LICENSE", $"Failed to save after sentinel reset: {ex.Message}"); }
                    return false;
                }

                return true;
            }
        }

        /// <summary>Current tier display name.</summary>
        public static string TierName => IsPro ? "Pro" : "Free";

        /// <summary>Current license key (masked for display).</summary>
        public static string MaskedKey
        {
            get
            {
                if (string.IsNullOrEmpty(_data.LicenseKey)) return "";
                if (_data.LicenseKey.Length < 8) return "****";
                return _data.LicenseKey.Substring(0, 7) + "..." + _data.LicenseKey.Substring(_data.LicenseKey.Length - 4);
            }
        }

        /// <summary>Full license key (for activation dialog).</summary>
        public static string FullKey => _data.LicenseKey ?? "";

        /// <summary>When the license was activated (ISO 8601 string).</summary>
        public static string ActivatedAt
        {
            get
            {
                EnsureLoaded();
                if (string.IsNullOrEmpty(_data.ActivatedAt)) return "";
                // Parse ISO date and format as a readable date
                if (DateTimeOffset.TryParse(_data.ActivatedAt, out var dt))
                    return dt.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt");
                return _data.ActivatedAt;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // DAILY USAGE — GETTERS
        // ═══════════════════════════════════════════════════════════════

        public static int PdfMergesToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.PdfMerges; } }
        public static int PdfSavesToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.PdfSaves; } }
        public static int DocConversionsToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.DocConversions; } }
        public static int ImageToPdfToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.ImageToPdf; } }
        public static int QrScansToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.QrScans; } }
        public static int OcrExtractionsToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.OcrExtractions; } }
        public static int TableExtractionsToday { get { EnsureLoaded(); EnsureTodayReset(); return _data.DailyUsage.TableExtractions; } }

        // ═══════════════════════════════════════════════════════════════
        // FEATURE GATES — Check if action is allowed
        // ═══════════════════════════════════════════════════════════════

        public static bool CanMergePdf()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.PdfMerges < FREE_PDF_MERGE_DAILY;
        }

        public static bool CanSavePdf()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.PdfSaves < FREE_PDF_SAVE_DAILY;
        }

        public static bool CanConvertDoc()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.DocConversions < FREE_DOC_CONVERT_DAILY;
        }

        public static bool CanConvertImageToPdf()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.ImageToPdf < FREE_IMAGE_TO_PDF_DAILY;
        }

        public static bool CanScanQr()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.QrScans < FREE_QR_SCAN_DAILY;
        }

        public static bool CanExtractOcr()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.OcrExtractions < FREE_OCR_DAILY;
        }

        public static bool CanExtractTable()
        {
            if (IsPro) return true;
            EnsureTodayReset();
            return _data.DailyUsage.TableExtractions < FREE_TABLE_EXTRACT_DAILY;
        }

        /// <summary>Check if a theme can be used. Free users can only use "FlyShelf Default".</summary>
        public static bool CanUseTheme(string? themeName)
        {
            if (IsPro) return true;
            if (string.IsNullOrEmpty(themeName)) return true; // disabling themes is always allowed
            return themeName.Equals("FlyShelf Default", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Check if custom wallpaper can be set.</summary>
        public static bool CanSetCustomWallpaper()
        {
            return IsPro;
        }

        /// <summary>Check if Glass UI (Acrylic Blur) theme can be applied. Free for all users.</summary>
        public static bool CanUseGlassTheme()
        {
            return true;
        }

        /// <summary>Check if Cloudflare tunnel can be enabled.</summary>
        public static bool CanUseCloudflare()
        {
            return IsPro;
        }

        /// <summary>Check if custom sniffer paths can be added.</summary>
        public static bool CanAddCustomSnifferPaths()
        {
            return IsPro;
        }

        /// <summary>Returns the history cap for the current tier.</summary>
        public static int GetHistoryCap()
        {
            return IsPro ? PRO_HISTORY_CAP : FREE_HISTORY_CAP;
        }

        /// <summary>Returns the max pin count for the current tier.</summary>
        public static int GetPinLimit()
        {
            return IsPro ? int.MaxValue : FREE_PIN_LIMIT;
        }

        /// <summary>Returns the max to-do items per day for the current tier.</summary>
        public static int GetTodoDailyLimit()
        {
            return IsPro ? int.MaxValue : FREE_TODO_DAILY;
        }

        /// <summary>Returns the max note history days for the current tier.</summary>
        public static int GetNoteHistoryDays()
        {
            return IsPro ? int.MaxValue : FREE_NOTE_DAYS;
        }

        /// <summary>Returns retention day options for current tier.</summary>
        public static int[] GetRetentionOptions()
        {
            return IsPro ? new[] { 7, 14, 30, 0 } : new[] { 7 };
        }

        // ═══════════════════════════════════════════════════════════════
        // USAGE RECORDING — Call after successful action
        // ═══════════════════════════════════════════════════════════════

        public static void RecordPdfMerge()
        {
            EnsureTodayReset();
            _data.DailyUsage.PdfMerges++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"📄 PDF Merge: {GetRemaining("pdf_merge")} remaining today"));
            }
        }

        public static void RecordPdfSave()
        {
            EnsureTodayReset();
            _data.DailyUsage.PdfSaves++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"📄 PDF Page Save: {GetRemaining("pdf_save")} remaining today"));
            }
        }

        public static void RecordDocConversion()
        {
            EnsureTodayReset();
            _data.DailyUsage.DocConversions++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"♻️ Doc Conversion: {GetRemaining("doc_convert")} remaining today"));
            }
        }

        public static void RecordImageToPdf()
        {
            EnsureTodayReset();
            _data.DailyUsage.ImageToPdf++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"🖼️ Image → PDF: {GetRemaining("image_to_pdf")} remaining today"));
            }
        }

        public static void RecordQrScan()
        {
            EnsureTodayReset();
            _data.DailyUsage.QrScans++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"📷 QR Code Scan: {GetRemaining("qr_scan")} remaining today"));
            }
        }

        public static void RecordOcrExtraction()
        {
            EnsureTodayReset();
            _data.DailyUsage.OcrExtractions++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"🔍 OCR Text: {GetRemaining("ocr")} remaining today"));
            }
        }

        public static void RecordTableExtraction()
        {
            EnsureTodayReset();
            _data.DailyUsage.TableExtractions++;
            Save();
            if (!IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast($"📊 Table Extraction: {GetRemaining("table_extract")} remaining today"));
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // USAGE SUMMARY — For UI display
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Returns a formatted usage string like "3/10".</summary>
        public static string GetUsageDisplay(string feature)
        {
            if (IsPro) return "∞";
            return feature switch
            {
                "pdf_merge" => $"{PdfMergesToday}/{FREE_PDF_MERGE_DAILY}",
                "pdf_save" => $"{PdfSavesToday}/{FREE_PDF_SAVE_DAILY}",
                "doc_convert" => $"{DocConversionsToday}/{FREE_DOC_CONVERT_DAILY}",
                "image_to_pdf" => $"{ImageToPdfToday}/{FREE_IMAGE_TO_PDF_DAILY}",
                "qr_scan" => $"{QrScansToday}/{FREE_QR_SCAN_DAILY}",
                "ocr" => $"{OcrExtractionsToday}/{FREE_OCR_DAILY}",
                "table_extract" => $"{TableExtractionsToday}/{FREE_TABLE_EXTRACT_DAILY}",
                _ => "—"
            };
        }

        /// <summary>Returns remaining count for a feature.</summary>
        public static int GetRemaining(string feature)
        {
            if (IsPro) return int.MaxValue;
            return feature switch
            {
                "pdf_merge" => Math.Max(0, FREE_PDF_MERGE_DAILY - PdfMergesToday),
                "pdf_save" => Math.Max(0, FREE_PDF_SAVE_DAILY - PdfSavesToday),
                "doc_convert" => Math.Max(0, FREE_DOC_CONVERT_DAILY - DocConversionsToday),
                "image_to_pdf" => Math.Max(0, FREE_IMAGE_TO_PDF_DAILY - ImageToPdfToday),
                "qr_scan" => Math.Max(0, FREE_QR_SCAN_DAILY - QrScansToday),
                "ocr" => Math.Max(0, FREE_OCR_DAILY - OcrExtractionsToday),
                "table_extract" => Math.Max(0, FREE_TABLE_EXTRACT_DAILY - TableExtractionsToday),
                _ => 0
            };
        }

        // ═══════════════════════════════════════════════════════════════
        // LICENSE KEY ACTIVATION
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate and activate a license key. Returns true if activation succeeds.
        /// Key format: FS-PRO-XXXX-XXXX-XXXX-XXXX (alphanumeric, 16 chars payload)
        /// The last 4 chars are an HMAC checksum of the first 12 chars.
        /// v2.1.0: Calls server-side /api/activate for JWT token.
        /// v2.2.1: Made async to prevent UI thread deadlock.
        /// v2.3.0: Server activation is MANDATORY — no offline fallback.
        ///         This ensures only legitimately purchased keys can activate.
        /// </summary>
        public static async Task<bool> ActivateLicenseAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            key = key.Trim().ToUpperInvariant();

            // Validate format
            if (!key.StartsWith("FS-PRO-")) return false;

            // Strip prefix and dashes
            string payload = key.Replace("FS-PRO-", "").Replace("-", "");
            if (payload.Length != 16) return false;

            // Fast client-side pre-check (prevents sending obviously fake keys to server)
            string randomPart = payload.Substring(0, 12);
            string checksum = payload.Substring(12, 4);
            string expectedChecksum = ComputeChecksum(randomPart);
            if (!checksum.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                return false;

            // Valid format — attempt server-side activation
            string deviceId = SettingsManager.Current?.DeviceId ?? "";
            string activationTime = NetworkClock.IsSynced
                ? NetworkClock.UtcNow.ToString("o")
                : DateTime.UtcNow.ToString("o");

            // [SECURITY FIX v2.3.0]: Server activation is MANDATORY.
            // No offline fallback — prevents forged-key activation without purchase verification.
            string serverToken = null;
            string serverError = null;
            try
            {
                var result = await ActivateOnServerAsync(key, deviceId).ConfigureAwait(false);
                serverToken = result.token;
                serverError = result.error;
            }
            catch (Exception ex)
            {
                Logger.LogAction("LICENSE", $"Server activation failed: {ex.Message}");
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast("⚠️ Could not reach activation server. Please check your internet connection and try again."));
                return false; // v2.3.0: FAIL — do NOT activate offline
            }

            // If server explicitly rejected the key, fail activation
            if (!string.IsNullOrEmpty(serverError))
            {
                Logger.LogAction("LICENSE", $"Server rejected activation: {serverError}");
                string userMsg = serverError switch
                {
                    "key_not_found" => "⚠️ This license key was not found. Please check your key and try again.",
                    "revoked" => "⚠️ This license key has been revoked. Contact support.",
                    "device_limit" => "⚠️ This key has reached the maximum device limit (3 devices).",
                    "invalid_key" => "⚠️ Invalid license key format.",
                    _ => $"⚠️ Activation failed: {serverError}"
                };
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast(userMsg));
                return false;
            }

            // v2.3.0: Server must return a JWT — no tokenless activation
            if (string.IsNullOrEmpty(serverToken))
            {
                Logger.LogAction("LICENSE", "Server returned no token and no error — activation failed");
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    Windows.ToastWindow.ShowToast("⚠️ Activation server returned an unexpected response. Please try again."));
                return false;
            }

            // Activate locally with verified JWT
            _data.LicenseKey = key;
            _data.Tier = "pro";
            _data.ActivatedAt = activationTime;
            _data.DeviceId = deviceId;
            _data.ActivationToken = serverToken;
            _data.LastValidated = activationTime;
            UpdateTierSentinel(); // v2.4.0: Set sentinel so IsPro integrity check passes
            Logger.LogAction("LICENSE", $"Pro license activated with server JWT: {MaskedKey}");
            Save();

            // Push updated licensing properties to active_devices
            if (NetworkSyncServer.Instance != null && NetworkSyncServer.Instance.ServerUrl != "Not Running" && NetworkSyncServer.Instance.ServerUrl != "Offline")
            {
                _ = CloudDiscoveryManager.PushTunnelUrl(
                    NetworkSyncServer.Instance.GlobalUrl ?? NetworkSyncServer.Instance.ServerUrl ?? "offline",
                    true,
                    NetworkSyncServer.Instance.ServerUrl ?? "",
                    forceWrite: true
                );
            }

            return true;
        }

        /// <summary>
        /// Synchronous wrapper — DEPRECATED. Use ActivateLicenseAsync instead.
        /// Uses Task.Run to avoid SynchronizationContext deadlock on UI thread.
        /// </summary>
        [System.Obsolete("Use ActivateLicenseAsync instead to avoid UI thread blocking.")]
        public static bool ActivateLicense(string key)
        {
            return Task.Run(() => ActivateLicenseAsync(key)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Call the Vercel /api/activate endpoint to get a signed JWT.
        /// Returns (token, null) on success, (null, errorCode) on server rejection,
        /// or (null, null) on network failure.
        /// </summary>
        private static async Task<(string token, string error)> ActivateOnServerAsync(string key, string deviceId)
        {
            try
            {
                var requestBody = JsonSerializer.Serialize(new { key, deviceId });
                var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{VERCEL_API_BASE}/activate", content);

                string responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (response.IsSuccessStatusCode && root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    string jwt = root.GetProperty("token").GetString() ?? "";
                    return (jwt, null);
                }

                // Server explicitly rejected
                string errCode = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "unknown" : "unknown";
                return (null, errCode);
            }
            catch (TaskCanceledException)
            {
                // Timeout — treat as network failure
                return (null, null);
            }
            catch (HttpRequestException)
            {
                // Network failure — allow offline activation
                return (null, null);
            }
        }

        /// <summary>Deactivate the current license and revert to free tier.</summary>
        public static void DeactivateLicense()
        {
            _data.LicenseKey = "";
            _data.Tier = "free";
            _data.ActivatedAt = "";
            _data.ActivationToken = "";
            _data.LastValidated = "";
            UpdateTierSentinel(); // v2.4.0: Reset sentinel for free tier
            Save();
            Logger.LogAction("LICENSE", "License deactivated — reverted to Free tier");

            // Push updated licensing properties to active_devices so companion apps are updated in real-time
            if (NetworkSyncServer.Instance != null && NetworkSyncServer.Instance.ServerUrl != "Not Running" && NetworkSyncServer.Instance.ServerUrl != "Offline")
            {
                _ = CloudDiscoveryManager.PushTunnelUrl(
                    NetworkSyncServer.Instance.GlobalUrl ?? NetworkSyncServer.Instance.ServerUrl ?? "offline",
                    true,
                    NetworkSyncServer.Instance.ServerUrl ?? "",
                    forceWrite: true
                );
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // NOTE: GenerateProKey() has been REMOVED from the desktop app
        // as of v2.0.0 security hardening. Key generation now happens
        // exclusively on the Vercel backend (api/verifyPayment.js).
        // This prevents decompilers from extracting a ready-made keygen.
        // ═══════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════
        // INTERNAL — Persistence
        // ═══════════════════════════════════════════════════════════════

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            Load();
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_licensePath))
                    {
                        string raw = File.ReadAllText(_licensePath);
                        // [SECURITY FIX v2.1.0]: Decrypt DPAPI-encrypted license data (M-02)
                        // Falls back to plaintext for backward compatibility with pre-v2.1.0 license files
                        string json = SecureStorage.Decrypt(raw);
                        var loaded = JsonSerializer.Deserialize<LicenseData>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (loaded != null)
                        {
                            _data = loaded;

                            // ═══ SECURITY v2.0.0: Re-validate HMAC checksum on load ═══
                            // Prevents license.json tampering (editing Tier to "pro" by hand)
                            if (_data.Tier == "pro" && !string.IsNullOrEmpty(_data.LicenseKey))
                            {
                                if (!ValidateKeyChecksum(_data.LicenseKey))
                                {
                                    Logger.LogAction("LICENSE", "⚠️ HMAC checksum failed on load — license.json may be tampered. Reverting to Free.");
                                    _data.LicenseKey = "";
                                    _data.Tier = "free";
                                    _data.ActivatedAt = "";
                                    // Save the clean state back
                                    try { SaveInternal(); } catch (Exception ex) { Logger.LogAction("LICENSE", $"Failed to save after HMAC reset: {ex.Message}"); }
                                }
                            }

                            // ═══ SECURITY v2.4.0: Compute tier sentinel after load ═══
                            UpdateTierSentinel();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("LICENSE", $"Failed to load license: {ex.Message}");
                    _data = new LicenseData();
                }
                _loaded = true;
            }
        }

        /// <summary>
        /// Validates that a key's HMAC checksum is correct without activating it.
        /// Used on load to detect tampering of license.json.
        /// </summary>
        private static bool ValidateKeyChecksum(string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return false;
                key = key.Trim().ToUpperInvariant();
                if (!key.StartsWith("FS-PRO-")) return false;
                string payload = key.Replace("FS-PRO-", "").Replace("-", "");
                if (payload.Length != 16) return false;
                string randomPart = payload.Substring(0, 12);
                string checksum = payload.Substring(12, 4);
                string expected = ComputeChecksum(randomPart);
                return checksum.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void Save()
        {
            lock (_lock)
            {
                SaveInternal();
            }
        }

        private static void SaveInternal()
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);
                string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                // [SECURITY FIX v2.1.0]: DPAPI-encrypt license data at rest (M-02)
                string encrypted = SecureStorage.Encrypt(json);
                string tmpPath = _licensePath + ".tmp";
                File.WriteAllText(tmpPath, encrypted);
                File.Move(tmpPath, _licensePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("LICENSE", $"Failed to save license: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset daily counters if the date has changed (midnight rollover).
        /// Uses NetworkClock (NTP-synced from time.google.com) as primary time source
        /// so users cannot cheat by changing their system clock.
        /// Falls back to OS time if NTP hasn't synced yet.
        /// </summary>
        private static void EnsureTodayReset()
        {
            lock (_lock)
            {
                // [SECURITY FIX v2.4.0]: Use trusted time (NTP > persisted anchor + monotonic clock > OS)
                // Prevents daily limit reset bypass via system clock manipulation
                var (trustedNow, _) = NetworkClock.GetTrustedUtcNow();
                DateTime correctedNow = trustedNow.ToLocalTime().DateTime;
                string today = correctedNow.Date.ToString("yyyy-MM-dd");

                if (_data.DailyUsage.Date != today)
                {
                    _data.DailyUsage = new DailyUsageData { Date = today };
                    Save();
                    Logger.LogAction("LICENSE", $"Daily usage counters reset (new day: {today}, trusted: {NetworkClock.IsTimeTrusted})");
                }
            }
        }

        /// <summary>Compute a 4-char HMAC checksum for key validation.</summary>
        private static string ComputeChecksum(string input)
        {
            string secret = GetKeySecret();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
            // Take first 4 chars of hex digest, uppercase
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 4).ToUpperInvariant();
        }

        // ═══════════════════════════════════════════════════════════════
        // SERVER-SIDE KEY VALIDATION (Firebase RTDB)
        // ═══════════════════════════════════════════════════════════════

        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>
        /// Legacy Firebase validation — used for pre-v2.1.0 users without JWT.
        /// Now uses authenticated Firebase calls (security audit v2.1.0).
        /// Also attempts JWT migration: if this user has no JWT, tries /api/activate.
        /// Non-blocking — runs in background. If server is unreachable, offline activation stands.
        /// </summary>
        private static async Task ValidateKeyOnServerAsync(string key, string deviceId)
        {
            try
            {
                string dbUrl = FirebaseSecrets.DatabaseUrl;
                if (string.IsNullOrEmpty(dbUrl))
                {
                    Logger.LogAction("LICENSE_SERVER", "No Firebase URL configured — skipping server validation");
                    return;
                }

                // Get Firebase auth token for authenticated REST calls (security audit v2.1.0)
                string firebaseAuth = null;
                try { firebaseAuth = await FirebaseAuthManager.GetIdTokenAsync(); } catch (Exception ex) { Logger.LogAction("LICENSE", $"Firebase auth token fetch failed: {ex.Message}"); }
                // Helper to append ?auth= or &auth= to Firebase REST URLs
                string AuthUrl(string url)
                {
                    if (string.IsNullOrEmpty(firebaseAuth)) return url;
                    return url.Contains("?") ? $"{url}&auth={firebaseAuth}" : $"{url}?auth={firebaseAuth}";
                }

                string safeKey = key.Replace("-", "_");

                // 1. Check if key is revoked
                string revokeUrl = AuthUrl($"{dbUrl}/licenses/revoked/{safeKey}.json");
                var revokeResponse = await _httpClient.GetAsync(revokeUrl);
                if (revokeResponse.IsSuccessStatusCode)
                {
                    string revokeJson = await revokeResponse.Content.ReadAsStringAsync();
                    if (revokeJson != "null" && revokeJson.Contains("true", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogAction("LICENSE_SERVER", $"⚠️ Key {key} has been REVOKED on the server!");
                        DeactivateLicense();
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            Windows.ToastWindow.ShowToast("⚠️ Your license key has been revoked. Contact support."));
                        return;
                    }
                }

                // 2. Register this activation (authenticated)
                string activationTime = NetworkClock.IsSynced
                    ? NetworkClock.UtcNow.ToString("o")
                    : DateTime.UtcNow.ToString("o");

                string activationUrl = AuthUrl($"{dbUrl}/licenses/activations/{safeKey}/{deviceId}.json");
                // Get Firebase UID for rule compliance: .validate requires uid === auth.uid
                string firebaseUid = "";
                try { firebaseUid = await FirebaseAuthManager.GetUidAsync() ?? ""; } catch (Exception ex) { Logger.LogAction("LICENSE", $"Firebase UID fetch failed: {ex.Message}"); }
                var activationPayload = JsonSerializer.Serialize(new
                {
                    deviceId,
                    activatedAt = activationTime,
                    uid = firebaseUid,
                    appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown"
                });

                var content = new StringContent(activationPayload, Encoding.UTF8, "application/json");
                var putResponse = await _httpClient.PutAsync(activationUrl, content);

                if (putResponse.IsSuccessStatusCode)
                    Logger.LogAction("LICENSE_SERVER", $"✅ Activation recorded on server for device {deviceId}");
                else
                    Logger.LogAction("LICENSE_SERVER", $"Server PUT failed: {putResponse.StatusCode}");

                // 3. Check device limit (authenticated)
                string devicesUrl = AuthUrl($"{dbUrl}/licenses/activations/{safeKey}.json?shallow=true");
                var devicesResponse = await _httpClient.GetAsync(devicesUrl);
                if (devicesResponse.IsSuccessStatusCode)
                {
                    string devicesJson = await devicesResponse.Content.ReadAsStringAsync();
                    int deviceCount = devicesJson.Split("true", StringSplitOptions.None).Length - 1;
                    Logger.LogAction("LICENSE_SERVER", $"Key activated on {deviceCount} device(s)");

                    if (deviceCount > 3)
                    {
                        Logger.LogAction("LICENSE_SERVER", $"⚠️ Key used on {deviceCount} devices (max 3) — DEACTIVATING");
                        DeactivateLicense();
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            Windows.ToastWindow.ShowToast("⚠️ License limit exceeded (max 3 devices). Please contact support."));
                        return;
                    }
                }

                // 4. JWT Migration: If this user has no JWT yet, try to get one from /api/activate
                if (string.IsNullOrEmpty(_data.ActivationToken))
                {
                    Logger.LogAction("LICENSE_SERVER", "No JWT found — attempting silent migration via /api/activate");
                    try
                    {
                        var (token, error) = await ActivateOnServerAsync(key, deviceId);
                        if (!string.IsNullOrEmpty(token))
                        {
                            _data.ActivationToken = token;
                            _data.LastValidated = NetworkClock.GetTrustedUtcNow().time.ToString("o");
                            Save();
                            Logger.LogAction("LICENSE_SERVER", "✅ Silent JWT migration successful");
                        }
                        else if (!string.IsNullOrEmpty(error) && error != "unknown")
                        {
                            Logger.LogAction("LICENSE_SERVER", $"JWT migration rejected: {error}");
                        }
                    }
                    catch (Exception migEx)
                    {
                        Logger.LogAction("LICENSE_SERVER", $"JWT migration failed (non-fatal): {migEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("LICENSE_SERVER", $"Server validation failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Periodic server check — called on app startup.
        /// v2.1.0: Uses JWT-based revalidation via /api/revalidate.
        /// Falls back to legacy Firebase check for pre-JWT users.
        /// Implements 14-day offline grace period.
        /// </summary>
        public static async Task RevalidateLicenseAsync()
        {
            if (!IsPro || string.IsNullOrEmpty(_data.LicenseKey)) return;

            string deviceId = _data.DeviceId;
            if (string.IsNullOrEmpty(deviceId))
                deviceId = SettingsManager.Current.DeviceId;

            // ═══ JWT-based revalidation (v2.1.0) ═══
            if (!string.IsNullOrEmpty(_data.ActivationToken))
            {
                // Check if revalidation is needed (every 7 days)
                if (DateTimeOffset.TryParse(_data.LastValidated, out var lastValidated))
                {
                    // [SECURITY FIX v2.4.0]: Use trusted time (NTP > persisted anchor + monotonic clock > OS)
                    // Prevents clock-rollback bypass when NTP is blocked
                    var (now, isTrusted) = NetworkClock.GetTrustedUtcNow();
                    double daysSinceValidation = (now - lastValidated).TotalDays;
                    if (daysSinceValidation < REVALIDATION_INTERVAL_DAYS)
                    {
                        Logger.LogAction("LICENSE_SERVER", $"JWT still fresh ({daysSinceValidation:F1}d since last validation, trusted: {isTrusted}) — skipping revalidation");
                        return;
                    }
                }

                // Attempt JWT revalidation via Vercel
                try
                {
                    var requestBody = JsonSerializer.Serialize(new { token = _data.ActivationToken, deviceId });
                    var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync($"{VERCEL_API_BASE}/revalidate", content);
                    string responseJson = await response.Content.ReadAsStringAsync();

                    using var doc = JsonDocument.Parse(responseJson);
                    var root = doc.RootElement;

                    if (response.IsSuccessStatusCode && root.TryGetProperty("valid", out var valid) && valid.GetBoolean())
                    {
                        // Success — update token and timestamp
                        string newToken = root.GetProperty("token").GetString() ?? _data.ActivationToken;
                        _data.ActivationToken = newToken;
                        _data.LastValidated = NetworkClock.GetTrustedUtcNow().time.ToString("o");
                        Save();
                        Logger.LogAction("LICENSE_SERVER", "✅ JWT revalidation successful — token refreshed");
                        return;
                    }

                    // Server explicitly rejected (revoked, device_limit, etc.)
                    if (root.TryGetProperty("error", out var errProp))
                    {
                        string error = errProp.GetString() ?? "";
                        if (error == "revoked" || error == "device_limit")
                        {
                            Logger.LogAction("LICENSE_SERVER", $"Server rejected revalidation: {error} — deactivating");
                            DeactivateLicense();
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                Windows.ToastWindow.ShowToast(error == "revoked"
                                    ? "⚠️ Your license has been revoked. Contact support."
                                    : "⚠️ License limit exceeded (max 3 devices). Contact support."));
                            return;
                        }
                        if (error == "invalid_token")
                        {
                            // Token corrupted — try to re-activate with the key
                            Logger.LogAction("LICENSE_SERVER", "Invalid JWT — attempting re-activation");
                            _data.ActivationToken = "";
                            var (newToken, activateError) = await ActivateOnServerAsync(_data.LicenseKey, deviceId);
                            if (!string.IsNullOrEmpty(newToken))
                            {
                                _data.ActivationToken = newToken;
                                _data.LastValidated = NetworkClock.GetTrustedUtcNow().time.ToString("o");
                                Save();
                                Logger.LogAction("LICENSE_SERVER", "✅ Re-activation successful after invalid JWT");
                            }
                            else if (!string.IsNullOrEmpty(activateError))
                            {
                                Logger.LogAction("LICENSE_SERVER", $"Re-activation rejected: {activateError} — deactivating");
                                DeactivateLicense();
                            }
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    // Network failure — apply grace period with trusted time
                    if (DateTimeOffset.TryParse(_data.LastValidated, out var lastCheck))
                    {
                        // [SECURITY FIX v2.4.0]: Use trusted time (NTP > monotonic anchor > OS)
                        var (now, isTrusted) = NetworkClock.GetTrustedUtcNow();
                        double daysOffline = (now - lastCheck).TotalDays;
                        if (daysOffline >= OFFLINE_GRACE_PERIOD_DAYS)
                        {
                            // [SECURITY FIX v2.3.0]: DEACTIVATE after grace period — not just warn
                            Logger.LogAction("LICENSE_SERVER", $"Offline for {daysOffline:F0}d (grace: {OFFLINE_GRACE_PERIOD_DAYS}d, trusted: {isTrusted}) — DEACTIVATING");
                            DeactivateLicense();
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                Windows.ToastWindow.ShowToast("⚠️ License expired — please connect to internet and re-activate your license key."));
                        }
                        else
                        {
                            Logger.LogAction("LICENSE_SERVER", $"Revalidation failed (offline {daysOffline:F0}d, grace {OFFLINE_GRACE_PERIOD_DAYS}d) — continuing");
                        }
                    }
                    else
                    {
                        Logger.LogAction("LICENSE_SERVER", "Revalidation failed (no LastValidated) — continuing with grace");
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("LICENSE_SERVER", $"JWT revalidation error (non-fatal): {ex.Message}");
                    return;
                }
            }

            // ═══ Legacy fallback: Firebase-only check (pre-JWT users) ═══
            // Also triggers JWT migration via ValidateKeyOnServerAsync
            try
            {
                await ValidateKeyOnServerAsync(_data.LicenseKey, deviceId);
            }
            catch (Exception ex)
            {
                Logger.LogAction("LICENSE_SERVER", $"Legacy revalidation failed (non-fatal): {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ANTI-TAMPER: Runtime Assembly Integrity Check (v2.0.0)
        // Delegated to AntiTamperService.VerifyAssemblyIntegrity()
        // ═══════════════════════════════════════════════════════════════

        public static void VerifyAssemblyIntegrity()
        {
            AntiTamperService.VerifyAssemblyIntegrity(
                _appDataDir,
                GetKeySecret(),
                _data.Tier == "pro",
                !string.IsNullOrEmpty(_data.LicenseKey),
                onBinaryChanged: () =>
                {
                    // v2.4.0 SECURITY FIX: Force immediate server re-activation.
                    // Clear JWT so next revalidation requires full re-activation.
                    _data.ActivationToken = "";
                    _data.LastValidated = "";
                    try { SaveInternal(); } catch (Exception ex) { Logger.LogAction("INTEGRITY", $"Failed to save after JWT clear: {ex.Message}"); }
                    Logger.LogAction("INTEGRITY", "Pro JWT cleared — server re-activation required");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(5000);
                            await RevalidateLicenseAsync();

                            if (_data.Tier == "pro" && !string.IsNullOrEmpty(_data.ActivationToken))
                            {
                                string currentHash = AntiTamperService.ComputeCurrentAssemblyHash();
                                if (currentHash != null)
                                {
                                    string hashFile = Path.Combine(_appDataDir, ".assembly_hash");
                                    Directory.CreateDirectory(_appDataDir);
                                    File.WriteAllText(hashFile, AntiTamperService.SignAssemblyHash(currentHash, GetKeySecret()));
                                    Logger.LogAction("INTEGRITY", "✅ Post-update revalidation successful — hash updated");
                                }
                            }
                            else
                            {
                                Logger.LogAction("INTEGRITY", "⚠️ Post-update revalidation FAILED — license deactivated");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("INTEGRITY", $"Post-update revalidation network error: {ex.Message}");
                        }
                    });
                }
            );
        }
    }
}
