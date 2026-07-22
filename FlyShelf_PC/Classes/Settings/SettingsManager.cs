using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Classes
{
    public class AdvanceSettings : ObservableObject
    {
        private bool _keepItemOnDragOut = true;
        public bool KeepItemOnDragOut { get => _keepItemOnDragOut; set => SetProperty(ref _keepItemOnDragOut, value); }
        
        private int _mediumFormWidth = 360;
        public int MediumFormWidth { get => _mediumFormWidth; set => SetProperty(ref _mediumFormWidth, value); }

        private int _mediumFormHeight = 380;
        public int MediumFormHeight { get => _mediumFormHeight; set => SetProperty(ref _mediumFormHeight, value); }

        private int _miniFormWidth = 260;
        public int MiniFormWidth { get => _miniFormWidth; set => SetProperty(ref _miniFormWidth, value); }

        private int _miniFormHeight = 260;
        public int MiniFormHeight { get => _miniFormHeight; set => SetProperty(ref _miniFormHeight, value); }

        private double _quickLookWidth = 0;
        public double QuickLookWidth { get => _quickLookWidth; set => SetProperty(ref _quickLookWidth, value); }

        private double _quickLookHeight = 0;
        public double QuickLookHeight { get => _quickLookHeight; set => SetProperty(ref _quickLookHeight, value); }

        private int _widgetTaskbarAlignment = 0;
        public int WidgetTaskbarAlignment { get => _widgetTaskbarAlignment; set => SetProperty(ref _widgetTaskbarAlignment, value); } // -1=Auto, 0=Far Left, 1=After Start, 2=Before Tray, 3=Custom Slider
        
        // Tier 1 Settings
        private bool _enableLocalNetworkSync = false;
        public bool EnableLocalNetworkSync { get => _enableLocalNetworkSync; set => SetProperty(ref _enableLocalNetworkSync, value); }
        
        private bool _enableLocalLAN = true;
        public bool EnableLocalLAN { get => _enableLocalLAN; set => SetProperty(ref _enableLocalLAN, value); }
        
        private bool _enableGlobalCloudflare = false;
        public bool EnableGlobalCloudflare { get => _enableGlobalCloudflare; set => SetProperty(ref _enableGlobalCloudflare, value); }
        
        private bool _enableCloudDiscovery = true;
        public bool EnableCloudDiscovery { get => _enableCloudDiscovery; set => SetProperty(ref _enableCloudDiscovery, value); }

        // Granular sync direction controls
        private bool _enableIncomingSync = true;
        /// <summary>When false, incoming clipboard items from paired devices are silently discarded.</summary>
        public bool EnableIncomingSync { get => _enableIncomingSync; set => SetProperty(ref _enableIncomingSync, value); }

        private bool _enableOutgoingSync = true;
        /// <summary>When false, local clipboard items are NOT pushed to paired devices.</summary>
        public bool EnableOutgoingSync { get => _enableOutgoingSync; set => SetProperty(ref _enableOutgoingSync, value); }
        
        private string _webClientPinToken = "";
        /// <summary>Stored encrypted via DPAPI. Getter returns plaintext, setter accepts plaintext.</summary>
        [JsonIgnore]
        public string WebClientPinToken { get => _webClientPinToken; set => SetProperty(ref _webClientPinToken, value); }

        /// <summary>Serialized to JSON — holds the DPAPI-encrypted blob of WebClientPinToken.</summary>
        public string WebClientPinTokenEncrypted
        {
            get => string.IsNullOrEmpty(_webClientPinToken) ? "" : SecureStorage.Encrypt(_webClientPinToken);
            set => _webClientPinToken = string.IsNullOrEmpty(value) ? "" : SecureStorage.Decrypt(value);
        }

        /// <summary>
        /// Backwards compatibility: Accepts old plaintext WebClientPinToken from config.json
        /// and migrates it to DPAPI-encrypted storage on next save.
        /// </summary>
        [JsonPropertyName("WebClientPinToken")]
        public string WebClientPinTokenLegacy
        {
            get => null; // Never serialize this — only WebClientPinTokenEncrypted is written
            set
            {
                // Only accept legacy plaintext if encrypted field wasn't already loaded
                if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(_webClientPinToken))
                {
                    _webClientPinToken = value;
                }
            }
        }
        
        private int _savedLocalPort = 0;
        public int SavedLocalPort { get => _savedLocalPort; set => SetProperty(ref _savedLocalPort, value); }

        private System.Collections.ObjectModel.ObservableCollection<string> _customSnifferPaths = new System.Collections.ObjectModel.ObservableCollection<string>();
        public System.Collections.ObjectModel.ObservableCollection<string> CustomSnifferPaths { get => _customSnifferPaths; set => SetProperty(ref _customSnifferPaths, value); }

        private string _customArchiveExtractionPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Extracted");
        public string CustomArchiveExtractionPath { get => _customArchiveExtractionPath; set => SetProperty(ref _customArchiveExtractionPath, value); }
        
        private string _deviceName = System.Environment.MachineName;
        public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }
        
        private string _deviceId = "";
        /// <summary>
        /// [SECURITY FIX v2.3.0]: Hardware-bound device ID derived from Windows SID + machine name.
        /// Prevents device-limit bypass by deleting config.json (same machine → same ID).
        /// Existing devices keep their persisted DeviceId from config.json.
        /// </summary>
        public string DeviceId
        {
            get
            {
                if (string.IsNullOrEmpty(_deviceId))
                    _deviceId = GenerateHardwareDeviceId();
                return _deviceId;
            }
            set => SetProperty(ref _deviceId, value);
        }

        /// <summary>
        /// Derives a deterministic device ID from hardware/OS identifiers.
        /// Uses: Windows user SID + machine name + Windows product ID.
        /// Returns a stable "PC_" + 12-char hex hash that survives config deletion.
        /// </summary>
        private static string GenerateHardwareDeviceId()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                // Windows user SID (unique per user account, survives reinstalls)
                sb.Append(System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value ?? "");
                sb.Append('|');
                // Machine name (changes if hostname changes, but that's acceptable)
                sb.Append(Environment.MachineName);
                sb.Append('|');
                // Windows Product ID (unique per Windows install)
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    sb.Append(key?.GetValue("ProductId")?.ToString() ?? "");
                }
                catch { /* Registry access may fail in sandboxed environments */ }

                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
                // Take first 12 hex chars for a compact but unique ID
                string hexHash = BitConverter.ToString(hash).Replace("-", "", StringComparison.Ordinal).Substring(0, 12);
                return $"PC_{hexHash}";
            }
            catch
            {
                // Ultimate fallback — random GUID (should never reach here)
                return $"PC_{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).Substring(0, 12)}";
            }
        }

        private bool _enableQuickPasteHotkeys = true;
        public bool EnableQuickPasteHotkeys { get => _enableQuickPasteHotkeys; set => SetProperty(ref _enableQuickPasteHotkeys, value); }

        // ═══ Summon Hotkey Customization ═══
        private uint _hotkeyModifier = 0x0001; // MOD_ALT
        public uint HotkeyModifier { get => _hotkeyModifier; set => SetProperty(ref _hotkeyModifier, value); }

        private uint _hotkeyKey = 0x43; // VK_C
        public uint HotkeyKey { get => _hotkeyKey; set => SetProperty(ref _hotkeyKey, value); }

        [JsonIgnore]
        public string HotkeyDisplayString
        {
            get
            {
                var parts = new List<string>();
                if ((_hotkeyModifier & 0x0002) != 0) parts.Add("Ctrl");
                if ((_hotkeyModifier & 0x0001) != 0) parts.Add("Alt");
                if ((_hotkeyModifier & 0x0004) != 0) parts.Add("Shift");
                if ((_hotkeyModifier & 0x0008) != 0) parts.Add("Win");
                parts.Add(GetKeyName(_hotkeyKey));
                return string.Join(" + ", parts);
            }
        }

        public static string GetKeyName(uint vk)
        {
            // Common VK codes to display names
            return vk switch
            {
                >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0-9
                >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A-Z
                >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",      // F1-F24
                0x20 => "Space",
                0x0D => "Enter",
                0x1B => "Esc",
                0x09 => "Tab",
                0x2E => "Delete",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PgUp",
                0x22 => "PgDn",
                0x2D => "Insert",
                0xBF => "/",
                0xBE => ".",
                0xBC => ",",
                0xBB => "=",
                0xBD => "-",
                0xBA => ";",
                0xDE => "'",
                0xC0 => "`",
                0xDB => "[",
                0xDD => "]",
                0xDC => "\\",
                _ => $"Key(0x{vk:X2})"
            };
        }

        // Theme & Appearance
        private string _clipboardWallpaperPath = "";
        public string ClipboardWallpaperPath { get => _clipboardWallpaperPath; set => SetProperty(ref _clipboardWallpaperPath, value); }

        /// <summary>
        /// Tracks a user-chosen custom wallpaper that takes priority over desktop/theme wallpapers.
        /// Set when user picks a wallpaper via "Choose Wallpaper", cleared when they click "Remove Wallpaper".
        /// This persists across mode switches (mica/desktop/glass/theme).
        /// </summary>
        private string _manualWallpaperPath = "";
        public string ManualWallpaperPath { get => _manualWallpaperPath; set => SetProperty(ref _manualWallpaperPath, value); }

        private bool _enableBlurBehind = true;
        public bool EnableBlurBehind { get => _enableBlurBehind; set => SetProperty(ref _enableBlurBehind, value); }

        private int _colorScheme = 0; // 0=Dark, 1=Light
        public int ColorScheme { get => _colorScheme; set => SetProperty(ref _colorScheme, value); }

        // QR Pairing
        private string _pairingKey = "";
        /// <summary>Decrypted pairing key (in-memory only, never serialized directly).</summary>
        [JsonIgnore]
        public string PairingKey { get => _pairingKey; set => SetProperty(ref _pairingKey, value); }

        /// <summary>Serialized to JSON — holds the DPAPI-encrypted blob of PairingKey.</summary>
        public string PairingKeyEncrypted
        {
            get => string.IsNullOrEmpty(_pairingKey) ? "" : SecureStorage.Encrypt(_pairingKey);
            set => _pairingKey = string.IsNullOrEmpty(value) ? "" : SecureStorage.Decrypt(value);
        }

        /// <summary>
        /// Backwards compatibility: Accepts old plaintext PairingKey from config.json
        /// and migrates it to DPAPI-encrypted storage on next save.
        /// </summary>
        [JsonPropertyName("PairingKey")]
        public string PairingKeyLegacy
        {
            get => null; // Never serialize this — only PairingKeyEncrypted is written
            set
            {
                // Only accept legacy plaintext if encrypted field wasn't already loaded
                if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(_pairingKey))
                {
                    _pairingKey = value;
                }
            }
        }

        // Shake to Open
        private bool _enableShakeToOpen = true;
        public bool EnableShakeToOpen { get => _enableShakeToOpen; set => SetProperty(ref _enableShakeToOpen, value); }

        // Taskbar Widget
        private bool _enableTaskbarWidget = true;
        public bool EnableTaskbarWidget { get => _enableTaskbarWidget; set => SetProperty(ref _enableTaskbarWidget, value); }

        // Desktop Mascot
        private bool _enableDesktopMascot = false;
        public bool EnableDesktopMascot { get => _enableDesktopMascot; set => SetProperty(ref _enableDesktopMascot, value); }

        // Manual horizontal offset (physical pixels) — lets users nudge the widget left/right on problematic taskbars
        private int _widgetHorizontalOffset = 0;
        public int WidgetHorizontalOffset { get => _widgetHorizontalOffset; set => SetProperty(ref _widgetHorizontalOffset, value); }

        private int _version = 2;
        public int Version { get => _version; set => SetProperty(ref _version, value); }

        // Auto-Cleanup: 7=7 days, 14=14 days, 30=30 days, 0=Never
        private int _clipboardRetentionDays = 7;
        public int ClipboardRetentionDays { get => _clipboardRetentionDays; set => SetProperty(ref _clipboardRetentionDays, value); }

        // ═══ Mascot Theme System ═══
        private string _activeThemeName = "";
        public string ActiveThemeName { get => _activeThemeName; set => SetProperty(ref _activeThemeName, value); }

        /// <summary>
        /// Controls clipboard background mode: "mica" (system blur), "desktop" (Windows wallpaper), or "theme" (custom theme).
        /// </summary>
        private string _themeDisplayMode = "mica";
        public string ThemeDisplayMode { get => _themeDisplayMode; set => SetProperty(ref _themeDisplayMode, value); }

        // ═══ Color Theme System (Midnight/Ocean/Sunset/Emerald/Lavender/Light) ═══
        private string _colorThemeName = "Default";
        public string ColorThemeName { get => _colorThemeName; set => SetProperty(ref _colorThemeName, value); }

        private bool _themeAnimationsEnabled = true;
        public bool ThemeAnimationsEnabled { get => _themeAnimationsEnabled; set => SetProperty(ref _themeAnimationsEnabled, value); }

        private bool _enableSummonAnimations = true;
        public bool EnableSummonAnimations { get => _enableSummonAnimations; set => SetProperty(ref _enableSummonAnimations, value); }

        // Auto-Start on Windows Boot
        private bool _autoStartEnabled = true;
        public bool AutoStartEnabled { get => _autoStartEnabled; set => SetProperty(ref _autoStartEnabled, value); }

        // Aero Clipboard UI — alternate visual shell
        private bool _useAlternateClipboardUI = false;
        public bool UseAlternateClipboardUI { get => _useAlternateClipboardUI; set => SetProperty(ref _useAlternateClipboardUI, value); }

        // Toast Notifications — allow users to disable in-app toasts
        private bool _enableNotifications = true;
        public bool EnableNotifications { get => _enableNotifications; set => SetProperty(ref _enableNotifications, value); }

        // Show Source App Label — displays "Copied from [app]" on clipboard cards
        private bool _showSourceAppLabel = true;
        public bool ShowSourceAppLabel { get => _showSourceAppLabel; set => SetProperty(ref _showSourceAppLabel, value); }

        // First-time onboarding — tracks whether the user has completed the startup tutorial
        private bool _hasCompletedOnboarding = false;
        public bool HasCompletedOnboarding { get => _hasCompletedOnboarding; set => SetProperty(ref _hasCompletedOnboarding, value); }

        // ═══ AI Provider Settings ═══

        /// <summary>AI provider: "auto", "gemini", "openai", "claude", "windows", "offline"</summary>
        private string _aiProvider = "auto";
        public string AiProvider { get => _aiProvider; set => SetProperty(ref _aiProvider, value); }

        /// <summary>Decrypted API key (in-memory only, never serialized directly).</summary>
        private string _aiApiKey = "";
        [JsonIgnore]
        public string AiApiKey { get => _aiApiKey; set => SetProperty(ref _aiApiKey, value); }

        /// <summary>Serialized to JSON — holds the DPAPI-encrypted blob of AiApiKey.</summary>
        public string AiApiKeyEncrypted
        {
            get => string.IsNullOrEmpty(_aiApiKey) ? "" : SecureStorage.Encrypt(_aiApiKey);
            set => _aiApiKey = string.IsNullOrEmpty(value) ? "" : SecureStorage.Decrypt(value);
        }

        /// <summary>Optional model name override (e.g. "gpt-4o", "gemini-1.5-pro").</summary>
        private string _aiModelOverride = "";
        public string AiModelOverride { get => _aiModelOverride; set => SetProperty(ref _aiModelOverride, value); }

        /// <summary>Master AI toggle — when false, all AI features are disabled.</summary>
        private bool _aiEnabled = true;
        public bool AiEnabled { get => _aiEnabled; set => SetProperty(ref _aiEnabled, value); }

        /// <summary>Default max response tokens for AI generation (256–4096).</summary>
        private int _aiMaxTokens = 1024;
        public int AiMaxTokens { get => _aiMaxTokens; set => SetProperty(ref _aiMaxTokens, value); }

        /// <summary>Preferred language for AI translations (empty = auto-detect).</summary>
        private string _aiPreferredLanguage = "";
        public string AiPreferredLanguage { get => _aiPreferredLanguage; set => SetProperty(ref _aiPreferredLanguage, value); }

        /// <summary>Default AI method for OCR/Table: "auto" (API if key exists, else popup), "api" (always API), "local" (always local).</summary>
        private string _defaultAiMethod = "auto";
        public string DefaultAiMethod { get => _defaultAiMethod; set => SetProperty(ref _defaultAiMethod, value); }

        /// <summary>
        /// Reflection-based property copying to keep the static Current reference stable.
        /// </summary>
        public void CopyFrom(AdvanceSettings source)
        {
            if (source == null) return;
            foreach (var prop in typeof(AdvanceSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.CanWrite && prop.CanRead)
                {
                    if (prop.Name == nameof(CustomSnifferPaths))
                    {
                        var sourceList = prop.GetValue(source) as System.Collections.ObjectModel.ObservableCollection<string>;
                        var destList = prop.GetValue(this) as System.Collections.ObjectModel.ObservableCollection<string>;
                        if (sourceList != null && destList != null)
                        {
                            destList.Clear();
                            foreach (var item in sourceList)
                            {
                                destList.Add(item);
                            }
                        }
                    }
                    else
                    {
                        prop.SetValue(this, prop.GetValue(source));
                    }
                }
            }
        }
    }

    public static class SettingsManager
    {
        public static AdvanceSettings Current { get; private set; } = new AdvanceSettings();

        private static string GetConfigPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "FlyShelf");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }

        private static bool _handlersRegistered = false;

        public static void Load()
        {
            string path = GetConfigPath();
            // SM-2 FIX: Clean up any stale .tmp file left from a crash mid-write on the previous run
            try { File.Delete(path + ".tmp"); } catch { } // Best-effort: failure is acceptable
            try
            {
                if (File.Exists(path))
                {
                    var json = RunWithRetry(() => File.ReadAllText(path));
                    
                    // Legacy migration: Rename old settings keys in raw json if needed
                    if (json.Contains("\"EnableGlobalFirebaseSync\""))
                    {
                        Logger.LogAction("SETTINGS_MIGRATION", "Migrating legacy 'EnableGlobalFirebaseSync' setting.");
                        json = json.Replace("\"EnableGlobalFirebaseSync\"", "\"EnableCloudDiscovery\"");
                    }

                    // Check version via JsonDocument
                    int version = 0;
                    try
                    {
                        using (var doc = JsonDocument.Parse(json))
                        {
                            if (doc.RootElement.TryGetProperty("Version", out var versionProp))
                            {
                                version = versionProp.GetInt32();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("SETTINGS_LOAD_WARN", $"Settings JSON is corrupt: {ex.Message}");
                        // Backup corrupt file
                        try 
                        { 
                            string corruptBackup = path + ".corrupt_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                            RunWithRetry(() => File.Copy(path, corruptBackup, true));
                            Logger.LogAction("SETTINGS_LOAD_WARN", $"Backed up corrupt settings to {corruptBackup}");
                        } 
                        catch { } // Best-effort: failure is acceptable
                        // [FIX M-28]: Prevent fall-through to Deserialize with corrupt data
                        json = null;
                    }

                    var settings = JsonSerializer.Deserialize<AdvanceSettings>(json);
                    if (settings != null)
                    {
                        Current.CopyFrom(settings);
                        if (version < 1)
                        {
                            Logger.LogAction("SETTINGS_MIGRATION", $"Upgrading config version from {version} to 1.");
                            Current.Version = 1;
                            Save(); // Persist the migrated settings with version 1
                        }
                        // ═══ v1→v2 Migration: Ensure clean Mica Blur default ═══
                        // Existing installs from v1 get the classic Mica Blur (grey) look.
                        // Users can switch to "FlyShelf" (desktop wallpaper) mode from the theme combo.
                        if (version < 2)
                        {
                            Logger.LogAction("SETTINGS_MIGRATION", $"Upgrading config version from {version} to 2 — Mica Blur default.");
                            Current.ActiveThemeName = "";
                            Current.ThemeDisplayMode = "mica";
                            Current.Version = 2;
                            // Write synchronously to guarantee persistence before DebouncedSave can race
                            try
                            {
                                var migJson = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                                string tmpPath = path + ".tmp";
                                RunWithRetry(() => File.WriteAllText(tmpPath, migJson));
                                RunWithRetry(() => File.Move(tmpPath, path, true));
                            }
                            catch { } // Best-effort: failure is acceptable
                        }
                    }
                }

                // Generate a random PIN for first-time users (replaces insecure static '55555' default)
                if (string.IsNullOrEmpty(Current.WebClientPinToken))
                {
                    // [SECURITY FIX v2.1.0]: Use cryptographic RNG with alphanumeric charset (M-03)
                    const string pinChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // No ambiguous chars (0/O, 1/I/L)
                    var pinBytes = new byte[8];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(pinBytes);
                    // [FIX M-39]: Minor modulo bias (256 % 31) — acceptable for non-cryptographic PIN generation
                    Current.WebClientPinToken = new string(pinBytes.Select(b => pinChars[b % pinChars.Length]).ToArray());
                    Logger.LogAction("SETTINGS", "Generated random WebClient PIN for new install.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SETTINGS_LOAD_ERROR", $"Failed to load settings: {ex.Message}");
            }
            
            if (!_handlersRegistered)
            {
                _handlersRegistered = true;
                // PM-12: Use named method handler instead of anonymous lambda
                // so it can be unsubscribed if needed, and avoids closure overhead.
                Current.PropertyChanged += OnSettingsPropertyChanged;
                if (Current.CustomSnifferPaths != null)
                {
                    Current.CustomSnifferPaths.CollectionChanged += OnSnifferPathsCollectionChanged;
                }
            }
        }

        // PM-12: Named handlers for PropertyChanged / CollectionChanged
        // PM-11: Call Save() directly — Save() already has its own debounce timer.
        // The outer DebouncedSave() timer was redundant and created a double-debounce.
        private static void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Save();
        }

        private static void OnSnifferPathsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Save();
        }

        public static void ResetToDefaults()
        {
            Current.CopyFrom(new AdvanceSettings());
            Save();
        }

        private static readonly object _saveLock = new();
        private static System.Threading.Timer _saveDebouncerTimer;

        public static void Save()
        {
            try
            {
                // Debounce: reset the 500ms timer on each call. Only the last call
                // within 500ms actually triggers the write. Prevents rapid saves
                // during slider drags, checkbox toggles, etc.
                // [FIX H-09]: Atomic timer swap — prevents race where old timer fires between Dispose and reassignment
                var newTimer = new System.Threading.Timer(_ =>
                {
                    string path = GetConfigPath();
                    lock (_saveLock)
                    {
                        try
                        {
                            // Serialize INSIDE the lock so we snapshot Current at write-time,
                            // not at call-time. This prevents stale data if a property changes
                            // between the Save() call and the background thread executing.
                            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                            string tempPath = path + ".tmp";
                            RunWithRetry(() => File.WriteAllText(tempPath, json));
                            RunWithRetry(() => File.Move(tempPath, path, true));
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("SETTINGS_SAVE", $"Failed to write config: {ex.Message}");
                        }
                    }
                }, null, 500, System.Threading.Timeout.Infinite);
                var old = System.Threading.Interlocked.Exchange(ref _saveDebouncerTimer, newTimer);
                old?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogAction("SETTINGS_SAVE", $"Failed to dispatch save task: {ex.Message}");
            }
        }

        /// <summary>
        /// Performs a full uninstall: removes auto-start registry entry,
        /// deletes the entire %AppData%\FlyShelf\ directory, and terminates the application.
        /// </summary>
        public static void PerformFullUninstall()
        {
            // 1. Disable auto-start (uses StartupTask API for MSIX, and Registry Run key for unpackaged)
            try
            {
                StartupHelper.SetRunAtStartupAsync(false).Wait(2000);
                Logger.LogAction("UNINSTALL", "Successfully disabled auto-start using unified startup API.");
            }
            catch (Exception ex)
            {
                Logger.LogAction("UNINSTALL", $"Failed to disable auto-start: {ex.Message}");
            }

            // 2. Delete the entire %AppData%\FlyShelf\ directory
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf");
            try
            {
                if (Directory.Exists(appDataDir))
                {
                    Directory.Delete(appDataDir, recursive: true);
                    Logger.LogAction("UNINSTALL", $"Deleted app data directory: {appDataDir}");
                }
            }
            catch (Exception ex)
            {
                // Some files may be locked; attempt individual cleanup
                Logger.LogAction("UNINSTALL", $"Failed to delete app data directory: {ex.Message}");
                try
                {
                    foreach (var dir in Directory.GetDirectories(appDataDir))
                    {
                        try { Directory.Delete(dir, true); } catch { } // Best-effort: failure is acceptable
                    }
                    foreach (var file in Directory.GetFiles(appDataDir))
                    {
                        try { File.Delete(file); } catch { } // Best-effort: failure is acceptable
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }

            // 3. Delete sandbox temp directory if it exists
            try
            {
                string sandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox");
                if (Directory.Exists(sandboxDir))
                    Directory.Delete(sandboxDir, recursive: true);
            }
            catch { }

            // 4. Delete temp update directory if it exists
            try
            {
                string tempUpdateDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlyShelf_Update");
                if (System.IO.Directory.Exists(tempUpdateDir))
                    System.IO.Directory.Delete(tempUpdateDir, true);
            }
            catch { }

            // 5. Clean stale zip files in temp
            try
            {
                foreach (var zipFile in System.IO.Directory.GetFiles(System.IO.Path.GetTempPath(), "FlyShelf_*.zip"))
                {
                    try { System.IO.File.Delete(zipFile); } catch { }
                }
            }
            catch { }

            // 6. Shut down the application
            try
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    System.Windows.Application.Current?.Shutdown();
                });
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        // [FIX STABLE-1]: Consolidated into FileRetryHelper
        private static T RunWithRetry<T>(Func<T> action, int retries = 3, int delayMs = 100)
            => FileRetryHelper.RunWithRetry(action, retries, delayMs);

        private static void RunWithRetry(Action action, int retries = 3, int delayMs = 100)
            => FileRetryHelper.RunWithRetry(action, retries, delayMs);
    }
}

