// ═══════════════════════════════════════════════════════════════════
// LicenseManager — Freemium tier management for FlyShelf
// Manages Pro/Free state, daily usage counters, license key validation.
// Uses NetworkClock (NTP-synced) for tamper-resistant time.
// Persists to %AppData%/FlyShelf/license.json
// ═══════════════════════════════════════════════════════════════════
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        // ═══ DAILY LIMITS (Free tier) ═══
        // All these features are 100% offline — generous limits cost us nothing
        // and build goodwill. Only power users hit these.
        public const int FREE_HISTORY_CAP = 500;
        public const int PRO_HISTORY_CAP = 2500;
        public const int FREE_PDF_MERGE_DAILY = 20;
        public const int FREE_PDF_SAVE_DAILY = 20;
        public const int FREE_DOC_CONVERT_DAILY = 10;
        public const int FREE_IMAGE_TO_PDF_DAILY = 10;
        public const int FREE_QR_SCAN_DAILY = 20;
        public const int FREE_OCR_DAILY = 30;
        public const int FREE_TABLE_EXTRACT_DAILY = 15;
        public const int FREE_PIN_LIMIT = 20;
        public const int FREE_TODO_DAILY = 10;
        public const int FREE_NOTE_DAYS = 30;

        // ═══ KEY VALIDATION SECRET (XOR-obfuscated so it's not plaintext in the binary) ═══
        // Obfuscated with XOR key — decoded at runtime only when needed
        private static readonly byte[] _secretXorKey = Encoding.UTF8.GetBytes("FS_Desktop_Key");
        private static readonly byte[] _secretData = new byte[]
        {
            0x00, 0x3F, 0x26, 0x17, 0x0D, 0x16, 0x07, 0x12, 0x30, 0x20,
            0x2D, 0x24, 0x3A, 0x4B, 0x76, 0x61, 0x69, 0x1B, 0x36, 0x16,
            0x08, 0x01, 0x1D, 0x15, 0x00, 0x18, 0x04, 0x15, 0x32
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
                EnsureLoaded();
                return _data.Tier == "pro" && !string.IsNullOrEmpty(_data.LicenseKey);
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

        /// <summary>Check if Glass UI theme can be applied.</summary>
        public static bool CanUseGlassTheme()
        {
            return IsPro;
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
        /// Validation: 1) Offline HMAC check, 2) Server-side Firebase check (async, non-blocking).
        /// </summary>
        public static bool ActivateLicense(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            key = key.Trim().ToUpperInvariant();

            // Validate format
            if (!key.StartsWith("FS-PRO-")) return false;

            // Strip prefix and dashes
            string payload = key.Replace("FS-PRO-", "").Replace("-", "");
            if (payload.Length != 16) return false;

            // Validate checksum: first 12 chars are random, last 4 are HMAC checksum
            string randomPart = payload.Substring(0, 12);
            string checksum = payload.Substring(12, 4);

            string expectedChecksum = ComputeChecksum(randomPart);
            if (!checksum.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                return false;

            // Valid key — activate locally
            string deviceId = SettingsManager.Current.DeviceId;
            // Use NTP-synced time (tamper-resistant), fallback to OS time
            string activationTime = NetworkClock.IsSynced
                ? NetworkClock.UtcNow.ToString("o")
                : DateTime.UtcNow.ToString("o");

            _data.LicenseKey = key;
            _data.Tier = "pro";
            _data.ActivatedAt = activationTime;
            _data.DeviceId = deviceId;
            Save();

            Logger.LogAction("LICENSE", $"Pro license activated: {MaskedKey}");

            // Fire-and-forget: register activation on server (non-blocking)
            _ = ValidateKeyOnServerAsync(key, deviceId);

            return true;
        }

        /// <summary>Deactivate the current license and revert to free tier.</summary>
        public static void DeactivateLicense()
        {
            _data.LicenseKey = "";
            _data.Tier = "free";
            _data.ActivatedAt = "";
            Save();
            Logger.LogAction("LICENSE", "License deactivated — reverted to Free tier");
        }

        /// <summary>
        /// Generate a valid Pro license key. 
        /// This is used by the developer/admin to create keys for distribution.
        /// In production, this would be on a server — here it's exposed for manual key generation.
        /// </summary>
        public static string GenerateProKey()
        {
            // Generate 12 random alphanumeric chars
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var sb = new StringBuilder(12);
            for (int i = 0; i < 12; i++)
                sb.Append(chars[random.Next(chars.Length)]);

            string randomPart = sb.ToString();
            string checksum = ComputeChecksum(randomPart);

            // Format: FS-PRO-XXXX-XXXX-XXXX-XXXX
            string payload = randomPart + checksum; // 16 chars total
            return $"FS-PRO-{payload.Substring(0, 4)}-{payload.Substring(4, 4)}-{payload.Substring(8, 4)}-{payload.Substring(12, 4)}";
        }

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
                        string json = File.ReadAllText(_licensePath);
                        var loaded = JsonSerializer.Deserialize<LicenseData>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (loaded != null)
                        {
                            _data = loaded;
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

        private static void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_appDataDir);
                    string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    string tmpPath = _licensePath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _licensePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("LICENSE", $"Failed to save license: {ex.Message}");
                }
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
            // Primary: NTP-synced time from time.google.com (tamper-resistant)
            // Fallback: OS local time (if offline / NTP hasn't initialized)
            DateTime correctedNow = NetworkClock.IsSynced
                ? NetworkClock.Now.DateTime
                : DateTime.Now;
            string today = correctedNow.Date.ToString("yyyy-MM-dd");

            if (_data.DailyUsage.Date != today)
            {
                _data.DailyUsage = new DailyUsageData { Date = today };
                Save();
                Logger.LogAction("LICENSE", $"Daily usage counters reset (new day: {today}, source: {(NetworkClock.IsSynced ? "NTP" : "OS")})");
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
        /// Register/validate the license key on the server.
        /// - Records the activation (key + deviceId + timestamp) in Firebase RTDB
        /// - Checks if the key has been revoked
        /// - Checks if the key is already activated on too many devices
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

                // Sanitize key for Firebase path (replace dashes)
                string safeKey = key.Replace("-", "_");

                // 1. Check if key is revoked
                string revokeUrl = $"{dbUrl}/licenses/revoked/{safeKey}.json";
                var revokeResponse = await _httpClient.GetAsync(revokeUrl);
                if (revokeResponse.IsSuccessStatusCode)
                {
                    string revokeJson = await revokeResponse.Content.ReadAsStringAsync();
                    if (revokeJson != "null" && revokeJson.Contains("true", StringComparison.OrdinalIgnoreCase))
                    {
                        // Key has been revoked — deactivate locally
                        Logger.LogAction("LICENSE_SERVER", $"⚠️ Key {key} has been REVOKED on the server!");
                        DeactivateLicense();
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            Windows.ToastWindow.ShowToast("⚠️ Your license key has been revoked. Contact support."));
                        return;
                    }
                }

                // 2. Register this activation
                string activationTime = NetworkClock.IsSynced
                    ? NetworkClock.UtcNow.ToString("o")
                    : DateTime.UtcNow.ToString("o");

                string activationUrl = $"{dbUrl}/licenses/activations/{safeKey}/{deviceId}.json";
                var activationPayload = JsonSerializer.Serialize(new
                {
                    deviceId,
                    activatedAt = activationTime,
                    appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
                });

                var content = new StringContent(activationPayload, Encoding.UTF8, "application/json");
                var putResponse = await _httpClient.PutAsync(activationUrl, content);

                if (putResponse.IsSuccessStatusCode)
                {
                    Logger.LogAction("LICENSE_SERVER", $"✅ Activation recorded on server for device {deviceId}");
                }
                else
                {
                    Logger.LogAction("LICENSE_SERVER", $"Server PUT failed: {putResponse.StatusCode}");
                }

                // 3. Check how many devices have activated this key
                string devicesUrl = $"{dbUrl}/licenses/activations/{safeKey}.json?shallow=true";
                var devicesResponse = await _httpClient.GetAsync(devicesUrl);
                if (devicesResponse.IsSuccessStatusCode)
                {
                    string devicesJson = await devicesResponse.Content.ReadAsStringAsync();
                    // shallow=true returns {"deviceId1":true,"deviceId2":true,...}
                    int deviceCount = devicesJson.Split("true", StringSplitOptions.None).Length - 1;
                    Logger.LogAction("LICENSE_SERVER", $"Key activated on {deviceCount} device(s)");

                    // Max 3 devices per key
                    if (deviceCount > 3)
                    {
                        Logger.LogAction("LICENSE_SERVER", $"⚠️ Key used on {deviceCount} devices (max 3) — flagging");
                        // Don't deactivate immediately — just log. You can enforce later.
                    }
                }
            }
            catch (Exception ex)
            {
                // Server validation is non-blocking — if it fails, offline activation stands
                Logger.LogAction("LICENSE_SERVER", $"Server validation failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>
        /// Periodic server check — call occasionally (e.g., on app startup) to verify
        /// the license hasn't been revoked since last activation.
        /// </summary>
        public static async Task RevalidateLicenseAsync()
        {
            if (!IsPro || string.IsNullOrEmpty(_data.LicenseKey)) return;

            try
            {
                string dbUrl = FirebaseSecrets.DatabaseUrl;
                if (string.IsNullOrEmpty(dbUrl)) return;

                string safeKey = _data.LicenseKey.Replace("-", "_");
                string revokeUrl = $"{dbUrl}/licenses/revoked/{safeKey}.json";

                var response = await _httpClient.GetAsync(revokeUrl);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if (json != "null" && json.Contains("true", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogAction("LICENSE_SERVER", "License revoked on server — deactivating");
                        DeactivateLicense();
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            Windows.ToastWindow.ShowToast("⚠️ Your license has been revoked. Contact support."));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("LICENSE_SERVER", $"Revalidation failed (non-fatal): {ex.Message}");
            }
        }
    }
}
