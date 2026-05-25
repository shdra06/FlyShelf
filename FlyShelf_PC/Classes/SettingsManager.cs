using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Classes
{
    public class AdvanceSettings : ObservableObject
    {
        private bool _keepItemOnDragOut = true;
        public bool KeepItemOnDragOut { get => _keepItemOnDragOut; set => SetProperty(ref _keepItemOnDragOut, value); }
        
        private string _geminiApiKey = "";
        /// <summary>Stored encrypted via DPAPI. Getter returns plaintext, setter accepts plaintext.</summary>
        [JsonIgnore]
        public string GeminiApiKey
        {
            get => _geminiApiKey;
            set => SetProperty(ref _geminiApiKey, value);
        }
        
        /// <summary>Serialized to JSON — holds the DPAPI-encrypted blob of GeminiApiKey.</summary>
        public string GeminiApiKeyEncrypted
        {
            get => string.IsNullOrEmpty(_geminiApiKey) ? "" : SecureStorage.Encrypt(_geminiApiKey);
            set => _geminiApiKey = string.IsNullOrEmpty(value) ? "" : SecureStorage.Decrypt(value);
        }

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
        public int WidgetTaskbarAlignment { get => _widgetTaskbarAlignment; set => SetProperty(ref _widgetTaskbarAlignment, value); } // 0=Left, 1=Center, 2=Right
        
        // Tier 1 Settings
        private bool _enableLocalNetworkSync = false;
        public bool EnableLocalNetworkSync { get => _enableLocalNetworkSync; set => SetProperty(ref _enableLocalNetworkSync, value); }
        
        private bool _enableLocalLAN = true;
        public bool EnableLocalLAN { get => _enableLocalLAN; set => SetProperty(ref _enableLocalLAN, value); }
        
        private bool _enableGlobalCloudflare = false;
        public bool EnableGlobalCloudflare { get => _enableGlobalCloudflare; set => SetProperty(ref _enableGlobalCloudflare, value); }
        
        private bool _enableCloudDiscovery = true;
        public bool EnableCloudDiscovery { get => _enableCloudDiscovery; set => SetProperty(ref _enableCloudDiscovery, value); }
        
        private string _webClientPinToken = "";
        public string WebClientPinToken { get => _webClientPinToken; set => SetProperty(ref _webClientPinToken, value); }
        
        private int _savedLocalPort = 0;
        public int SavedLocalPort { get => _savedLocalPort; set => SetProperty(ref _savedLocalPort, value); }

        private System.Collections.ObjectModel.ObservableCollection<string> _customSnifferPaths = new System.Collections.ObjectModel.ObservableCollection<string>();
        public System.Collections.ObjectModel.ObservableCollection<string> CustomSnifferPaths { get => _customSnifferPaths; set => SetProperty(ref _customSnifferPaths, value); }

        private string _customArchiveExtractionPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "SyncedFiles", "Extracted");
        public string CustomArchiveExtractionPath { get => _customArchiveExtractionPath; set => SetProperty(ref _customArchiveExtractionPath, value); }
        
        private string _deviceName = System.Environment.MachineName;
        public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }
        
        private string _deviceId = $"PC_{Guid.NewGuid().ToString("N").Substring(0, 12)}";
        public string DeviceId { get => _deviceId; set => SetProperty(ref _deviceId, value); }

        private bool _enableQuickPasteHotkeys = true;
        public bool EnableQuickPasteHotkeys { get => _enableQuickPasteHotkeys; set => SetProperty(ref _enableQuickPasteHotkeys, value); }

        // Theme & Appearance
        private string _clipboardWallpaperPath = "";
        public string ClipboardWallpaperPath { get => _clipboardWallpaperPath; set => SetProperty(ref _clipboardWallpaperPath, value); }

        private bool _enableBlurBehind = true;
        public bool EnableBlurBehind { get => _enableBlurBehind; set => SetProperty(ref _enableBlurBehind, value); }

        private int _colorScheme = 0; // 0=Dark, 1=Light
        public int ColorScheme { get => _colorScheme; set => SetProperty(ref _colorScheme, value); }

        // QR Pairing
        private string _pairingKey = "";
        public string PairingKey { get => _pairingKey; set => SetProperty(ref _pairingKey, value); }

        // Shake to Open
        private bool _enableShakeToOpen = true;
        public bool EnableShakeToOpen { get => _enableShakeToOpen; set => SetProperty(ref _enableShakeToOpen, value); }

        // Taskbar Widget
        private bool _enableTaskbarWidget = true;
        public bool EnableTaskbarWidget { get => _enableTaskbarWidget; set => SetProperty(ref _enableTaskbarWidget, value); }

        private int _version = 1;
        public int Version { get => _version; set => SetProperty(ref _version, value); }

        // Auto-Cleanup: 7=7 days, 14=14 days, 30=30 days, 0=Never
        private int _clipboardRetentionDays = 7;
        public int ClipboardRetentionDays { get => _clipboardRetentionDays; set => SetProperty(ref _clipboardRetentionDays, value); }

        // ═══ Mascot Theme System ═══
        private string _activeThemeName = "FlyShelf Default";
        public string ActiveThemeName { get => _activeThemeName; set => SetProperty(ref _activeThemeName, value); }

        /// <summary>
        /// Controls clipboard background mode: "mica" (system blur), "desktop" (Windows wallpaper), or "theme" (custom theme).
        /// </summary>
        private string _themeDisplayMode = "theme";
        public string ThemeDisplayMode { get => _themeDisplayMode; set => SetProperty(ref _themeDisplayMode, value); }

        private bool _themeAnimationsEnabled = true;
        public bool ThemeAnimationsEnabled { get => _themeAnimationsEnabled; set => SetProperty(ref _themeAnimationsEnabled, value); }

        private bool _enableSummonAnimations = true;
        public bool EnableSummonAnimations { get => _enableSummonAnimations; set => SetProperty(ref _enableSummonAnimations, value); }

        // Auto-Start on Windows Boot
        private bool _autoStartEnabled = true;
        public bool AutoStartEnabled { get => _autoStartEnabled; set => SetProperty(ref _autoStartEnabled, value); }
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

        private static System.Threading.Timer? _saveDebounce;

        public static void Load()
        {
            string path = GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    
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
                            string corruptBackup = path + ".corrupt_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            File.Copy(path, corruptBackup, true);
                            Logger.LogAction("SETTINGS_LOAD_WARN", $"Backed up corrupt settings to {corruptBackup}");
                        } 
                        catch { }
                    }

                    var settings = JsonSerializer.Deserialize<AdvanceSettings>(json);
                    if (settings != null)
                    {
                        Current = settings;
                        if (version < 1)
                        {
                            Logger.LogAction("SETTINGS_MIGRATION", $"Upgrading config version from {version} to 1.");
                            Current.Version = 1;
                            Save(); // Persist the migrated settings with version 1
                        }
                    }
                }

                // Generate a random PIN for first-time users (replaces insecure static '55555' default)
                if (string.IsNullOrEmpty(Current.WebClientPinToken))
                {
                    Current.WebClientPinToken = Random.Shared.Next(10000, 99999).ToString();
                    Logger.LogAction("SETTINGS", "Generated random WebClient PIN for new install.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SETTINGS_LOAD_ERROR", $"Failed to load settings: {ex.Message}");
            }
            
            Current.PropertyChanged += (s, e) => DebouncedSave();
            if (Current.CustomSnifferPaths != null)
            {
                Current.CustomSnifferPaths.CollectionChanged += (s, e) => DebouncedSave();
            }
        }

        /// <summary>
        /// Coalesces rapid property changes (e.g. window resize) into a single disk write.
        /// </summary>
        private static void DebouncedSave()
        {
            _saveDebounce?.Dispose();
            _saveDebounce = new System.Threading.Timer(_ => Save(), null, 500, System.Threading.Timeout.Infinite);
        }

        public static void ResetToDefaults()
        {
            Current = new AdvanceSettings();
            Current.PropertyChanged += (s, e) => DebouncedSave();
            if (Current.CustomSnifferPaths != null)
            {
                Current.CustomSnifferPaths.CollectionChanged += (s, e) => DebouncedSave();
            }
            Save();
        }

        private static readonly object _saveLock = new();

        public static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                string path = GetConfigPath();
                System.Threading.Tasks.Task.Run(() =>
                {
                    lock (_saveLock)
                    {
                        try
                        {
                            string tempPath = path + ".tmp";
                            File.WriteAllText(tempPath, json);
                            File.Move(tempPath, path, true);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("SETTINGS_SAVE", $"Failed to write config: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("SETTINGS_SAVE", $"Failed to serialize config: {ex.Message}");
            }
        }

        /// <summary>
        /// Performs a full uninstall: removes auto-start registry entry,
        /// deletes the entire %AppData%\FlyShelf\ directory, and terminates the application.
        /// </summary>
        public static void PerformFullUninstall()
        {
            // 1. Remove auto-start registry entry
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    key?.DeleteValue("FlyShelf", throwOnMissingValue: false);
                }
                Logger.LogAction("UNINSTALL", "Removed auto-start registry entry.");
            }
            catch (Exception ex)
            {
                Logger.LogAction("UNINSTALL", $"Failed to remove registry entry: {ex.Message}");
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
                        try { Directory.Delete(dir, true); } catch { }
                    }
                    foreach (var file in Directory.GetFiles(appDataDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                catch { }
            }

            // 3. Delete sandbox temp directory if it exists
            try
            {
                string sandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox");
                if (Directory.Exists(sandboxDir))
                    Directory.Delete(sandboxDir, recursive: true);
            }
            catch { }

            // 4. Shut down the application
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });
            }
            catch
            {
                Environment.Exit(0);
            }
        }
    }
}

