// ═══════════════════════════════════════════════════════════════════
// ThemeManager — Singleton manager for the mascot theme system.
// Handles loading, importing, switching, and hot-reloading of themes.
// Themes live in %AppData%/FlyShelf/Themes/ — no recompilation needed.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FlyShelf.Classes
{
    public class ThemeManager
    {
        // ═══ Singleton ═══
        private static ThemeManager? _instance;
        public static ThemeManager Instance => _instance ??= new ThemeManager();

        // ═══ State ═══
        private readonly string _themesDir;
        private FileSystemWatcher? _watcher;
        private ThemePackage? _activeTheme;

        /// <summary>All discovered themes on disk.</summary>
        public ObservableCollection<ThemePackage> AvailableThemes { get; } = new();

        /// <summary>Currently active theme (null if disabled).</summary>
        public ThemePackage? ActiveTheme => _activeTheme;

        /// <summary>Fires when the active theme changes.</summary>
        public event Action<ThemePackage?>? ActiveThemeChanged;

        /// <summary>Fires when a new theme is discovered or removed.</summary>
        public event Action? ThemeListChanged;

        private ThemeManager()
        {
            _themesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlyShelf", "Themes");
            Directory.CreateDirectory(_themesDir);
        }

        /// <summary>
        /// Initialize the theme engine: scan for themes, load the active one, start watcher.
        /// Call once during app startup (after settings are loaded).
        /// </summary>
        public void Initialize()
        {
            try
            {
                Logger.LogAction("THEME", $"Initializing theme engine. Themes dir: {_themesDir}");

                // Create default theme if it does not exist
                string defaultPath = Path.Combine(_themesDir, "flyshelf-default");
                if (!Directory.Exists(defaultPath) || !Directory.GetDirectories(_themesDir).Any())
                {
                    CreateDefaultTheme();
                }

                // Load active theme synchronously from settings first (highly optimized: only parses one directory/manifest)
                string activeName = SettingsManager.Current.ActiveThemeName ?? "";
                if (string.IsNullOrEmpty(activeName))
                {
                    activeName = "FlyShelf Default";
                }

                try
                {
                    foreach (var dir in Directory.GetDirectories(_themesDir))
                    {
                        string manifestPath = Path.Combine(dir, "manifest.json");
                        if (File.Exists(manifestPath))
                        {
                            string json = File.ReadAllText(manifestPath);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("name", out var nameProp) && 
                                activeName.Equals(nameProp.GetString(), StringComparison.OrdinalIgnoreCase))
                            {
                                var theme = ThemePackage.LoadFromDirectory(dir);
                                if (theme.IsValid)
                                {
                                    _activeTheme = theme;
                                    AvailableThemes.Add(theme);
                                    Logger.LogAction("THEME", $"Active theme restored synchronously: '{_activeTheme.Name}'");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("THEME", $"Error restoring active theme synchronously: {ex.Message}");
                }

                // Safe Fallback: if active theme is null or failed to load, load FlyShelf Default
                if (_activeTheme == null)
                {
                    try
                    {
                        if (!Directory.Exists(defaultPath))
                        {
                            CreateDefaultTheme();
                        }
                        var theme = ThemePackage.LoadFromDirectory(defaultPath);
                        if (theme.IsValid)
                        {
                            _activeTheme = theme;
                            if (!AvailableThemes.Any(t => t.Name == theme.Name))
                            {
                                AvailableThemes.Add(theme);
                            }
                            SettingsManager.Current.ActiveThemeName = theme.Name;
                            Logger.LogAction("THEME", "Gracefully fell back to active theme 'FlyShelf Default'");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("THEME", $"Fatal error loading default theme fallback: {ex.Message}");
                    }
                }

                // Kick off the background theme scanning asynchronously
                RefreshThemeList();

                // Start filesystem watcher for hot-reload
                StartWatcher();

                Logger.LogAction("THEME", $"Theme engine ready. {AvailableThemes.Count} theme(s) found, active: '{_activeTheme?.Name ?? "none"}'");
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME_FATAL", $"Fatal unhandled crash in ThemeManager: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan the themes directory and rebuild the available themes list on a background thread.
        /// </summary>
        public void RefreshThemeList()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var loadedThemes = new List<ThemePackage>();
                try
                {
                    if (Directory.Exists(_themesDir))
                    {
                        foreach (var dir in Directory.GetDirectories(_themesDir))
                        {
                            var theme = ThemePackage.LoadFromDirectory(dir);
                            if (theme.IsValid)
                            {
                                loadedThemes.Add(theme);
                            }
                            else
                            {
                                Logger.LogAction("THEME", $"Skipped invalid theme: {Path.GetFileName(dir)} — {theme.LoadError}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("THEME", $"Error scanning themes on background thread: {ex.Message}");
                }

                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    AvailableThemes.Clear();
                    foreach (var theme in loadedThemes)
                    {
                        AvailableThemes.Add(theme);
                    }

                    // Keep the active theme reference synced to the new package instance in the collection
                    string activeName = SettingsManager.Current.ActiveThemeName ?? "";
                    if (!string.IsNullOrEmpty(activeName))
                    {
                        var theme = AvailableThemes.FirstOrDefault(t => 
                            t.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
                        if (theme != null && theme.IsValid)
                        {
                            _activeTheme = theme;
                        }
                    }

                    ThemeListChanged?.Invoke();
                });
            });
        }

        /// <summary>
        /// Set the active theme by name. Pass null/empty to disable themes.
        /// </summary>
        public void SetActiveTheme(string? themeName)
        {
            if (string.IsNullOrEmpty(themeName))
            {
                _activeTheme = null;
                SettingsManager.Current.ActiveThemeName = "";
                Logger.LogAction("THEME", "Theme disabled");
                ActiveThemeChanged?.Invoke(null);
                return;
            }

            if (!LicenseManager.CanUseTheme(themeName))
            {
                UpgradePrompt.ShowThemeLimit();
                return;
            }

            var theme = AvailableThemes.FirstOrDefault(t =>
                t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase));

            if (theme == null || !theme.IsValid)
            {
                Logger.LogAction("THEME", $"Theme '{themeName}' not found or invalid");
                return;
            }

            _activeTheme = theme;
            SettingsManager.Current.ActiveThemeName = theme.Name;
            Logger.LogAction("THEME", $"Switched to theme: '{theme.Name}' by {theme.Author}");
            ActiveThemeChanged?.Invoke(theme);
        }

        /// <summary>
        /// Import a .flyshelf-theme file (zip) into the themes directory.
        /// Returns the imported theme name, or null on failure.
        /// </summary>
        public string? ImportTheme(string zipPath)
        {
            if (!LicenseManager.IsPro)
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    UpgradePrompt.ShowThemeLimit());
                return null;
            }

            if (!File.Exists(zipPath))
            {
                Logger.LogAction("THEME", $"Import failed: file not found: {zipPath}");
                return null;
            }

            try
            {
                // Peek inside the zip to find manifest.json and determine theme name
                string themeName = null;
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var manifestEntry = archive.Entries.FirstOrDefault(e =>
                        e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));

                    if (manifestEntry != null)
                    {
                        using var stream = manifestEntry.Open();
                        using var reader = new StreamReader(stream);
                        string json = reader.ReadToEnd();
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        {
                            themeName = nameProp.GetString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(themeName))
                {
                    themeName = Path.GetFileNameWithoutExtension(zipPath)
                        .Replace(".flyshelf-theme", "")
                        .Replace(" ", "_");
                }

                // Sanitize folder name
                string safeName = string.Join("_", themeName.Split(Path.GetInvalidFileNameChars()));
                string targetDir = Path.Combine(_themesDir, safeName);

                // If theme already exists, create a unique name
                int counter = 1;
                while (Directory.Exists(targetDir))
                {
                    targetDir = Path.Combine(_themesDir, $"{safeName}_{counter++}");
                }

                // Extract — handle both flat zips and zips with a root folder
                Directory.CreateDirectory(targetDir);
                ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);

                // Check if the zip had a single root folder (common pattern)
                var subDirs = Directory.GetDirectories(targetDir);
                if (subDirs.Length == 1 && !File.Exists(Path.Combine(targetDir, "manifest.json")))
                {
                    // Move contents up one level
                    string innerDir = subDirs[0];
                    foreach (var file in Directory.GetFiles(innerDir))
                    {
                        File.Move(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
                    }
                    foreach (var dir in Directory.GetDirectories(innerDir))
                    {
                        string destDir = Path.Combine(targetDir, Path.GetFileName(dir));
                        if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
                        Directory.Move(dir, destDir);
                    }
                    Directory.Delete(innerDir, true);
                }

                // Validate the extracted theme
                var theme = ThemePackage.LoadFromDirectory(targetDir);
                if (theme.IsValid)
                {
                    RefreshThemeList();
                    Logger.LogAction("THEME", $"Imported theme: '{theme.Name}' ({theme.Animations.Count} animations)");
                    return theme.Name;
                }
                else
                {
                    // Clean up invalid import
                    Directory.Delete(targetDir, true);
                    Logger.LogAction("THEME", $"Import failed — invalid theme: {theme.LoadError}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Import error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete a theme from disk.
        /// </summary>
        public bool DeleteTheme(string themeName)
        {
            var theme = AvailableThemes.FirstOrDefault(t =>
                t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase));

            if (theme == null || string.IsNullOrEmpty(theme.ThemePath)) return false;

            try
            {
                // If this is the active theme, deactivate first
                if (_activeTheme?.Name == theme.Name)
                {
                    SetActiveTheme(null);
                }

                Directory.Delete(theme.ThemePath, true);
                AvailableThemes.Remove(theme);
                ThemeListChanged?.Invoke();
                Logger.LogAction("THEME", $"Deleted theme: '{themeName}'");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Delete theme error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the resolved file path for a specific animation trigger.
        /// Returns null if no active theme or animation not defined.
        /// </summary>
        public string? GetAnimationPath(string triggerName)
        {
            var anim = _activeTheme?.GetAnimation(triggerName);
            return anim?.ResolvedFilePath;
        }

        /// <summary>
        /// Get the animation config for a trigger.
        /// </summary>
        public ThemeAnimation? GetAnimation(string triggerName)
        {
            return _activeTheme?.GetAnimation(triggerName);
        }

        /// <summary>
        /// Get placement for a named placement in the active theme.
        /// </summary>
        public ThemePlacement GetPlacement(string placementName)
        {
            return _activeTheme?.GetPlacement(placementName) ?? new ThemePlacement();
        }

        /// <summary>
        /// Opens the themes folder in Explorer for the user.
        /// </summary>
        public void OpenThemesFolder()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", _themesDir);
            }
            catch { }
        }

        /// <summary>
        /// Returns a snapshot list of all installed themes (for UI binding).
        /// </summary>
        public List<ThemePackage> GetInstalledThemes()
        {
            return AvailableThemes.ToList();
        }

        /// <summary>
        /// Returns the themes directory path.
        /// </summary>
        public string ThemesDirectory => _themesDir;

        /// <summary>
        /// Gets the resolved wallpaper file path from the active theme, or null if none.
        /// </summary>
        public string? GetWallpaperPath()
        {
            if (_activeTheme != null && !string.IsNullOrEmpty(_activeTheme.WallpaperPath))
                return _activeTheme.WallpaperPath;
            return null;
        }

        // ═══════════════════════════════════════════════════════════════
        // GLASS UI THEME — Dynamic ResourceDictionary injection
        // Separate from mascot sprite themes; controls button/card styles.
        // ═══════════════════════════════════════════════════════════════

        private const string GlassThemeSource = "pack://application:,,,/Resources/Styles/GlassTheme.xaml";

        /// <summary>
        /// Returns true if the Glass UI theme ResourceDictionary is currently loaded.
        /// </summary>
        public bool IsGlassThemeActive
        {
            get
            {
                var app = System.Windows.Application.Current;
                if (app == null) return false;
                foreach (var d in app.Resources.MergedDictionaries)
                {
                    if (d.Source != null && d.Source.OriginalString == GlassThemeSource)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Merges the GlassTheme.xaml ResourceDictionary into Application.Resources.
        /// Safe to call multiple times — skips if already loaded.
        /// </summary>
        public void ApplyGlassTheme()
        {
            if (!LicenseManager.CanUseGlassTheme())
            {
                UpgradePrompt.ShowThemeLimit();
                return;
            }

            try
            {
                if (IsGlassThemeActive) return;
                var dict = new System.Windows.ResourceDictionary
                {
                    Source = new Uri(GlassThemeSource, UriKind.Absolute)
                };
                System.Windows.Application.Current?.Resources.MergedDictionaries.Add(dict);
                Logger.LogAction("THEME", "Glass UI theme applied");
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Failed to apply Glass theme: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the GlassTheme.xaml ResourceDictionary from Application.Resources.
        /// Safe to call even if not loaded.
        /// </summary>
        public void RemoveGlassTheme()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;
                System.Windows.ResourceDictionary? toRemove = null;
                foreach (var d in app.Resources.MergedDictionaries)
                {
                    if (d.Source != null && d.Source.OriginalString == GlassThemeSource)
                    {
                        toRemove = d;
                        break;
                    }
                }
                if (toRemove != null)
                {
                    app.Resources.MergedDictionaries.Remove(toRemove);
                    Logger.LogAction("THEME", "Glass UI theme removed");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Failed to remove Glass theme: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // COLOR THEME SYSTEM — Dynamic ResourceDictionary swap for
        // accent/surface/text colors (Midnight/Ocean/Sunset/Emerald/Lavender/Light)
        // ═══════════════════════════════════════════════════════════════

        private const string ColorThemePrefix = "pack://application:,,,/Resources/Themes/Theme.";
        private static readonly string[] ValidColorThemes = { "Midnight", "Ocean", "Sunset", "Emerald", "Lavender", "Light" };
        private System.Windows.ResourceDictionary? _activeColorThemeDict;

        /// <summary>
        /// Returns the name of the currently loaded color theme.
        /// </summary>
        public string ActiveColorTheme => SettingsManager.Current.ColorThemeName ?? "Default";

        /// <summary>
        /// Returns the list of available color theme names.
        /// </summary>
        public static IReadOnlyList<string> AvailableColorThemes => ValidColorThemes;

        /// <summary>
        /// Switch to a named color theme. Swaps the theme ResourceDictionary at runtime.
        /// </summary>
        public void ApplyColorTheme(string themeName)
        {
            try
            {
                // Validate theme name
                if (string.IsNullOrEmpty(themeName) || !Array.Exists(ValidColorThemes, t => t.Equals(themeName, StringComparison.OrdinalIgnoreCase)))
                {
                    // "Default" means remove the theme overlay — don't fall back to Midnight
                    if (!string.IsNullOrEmpty(themeName) && themeName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        RemoveColorTheme();
                        return;
                    }
                    Logger.LogAction("COLOR_THEME", $"Invalid color theme: '{themeName}', falling back to Default");
                    RemoveColorTheme();
                    return;
                }

                // Normalize casing
                themeName = Array.Find(ValidColorThemes, t => t.Equals(themeName, StringComparison.OrdinalIgnoreCase)) ?? "Default";
                if (themeName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveColorTheme();
                    return;
                }

                var app = System.Windows.Application.Current;
                if (app == null) return;

                // Remove the existing color theme dictionary
                RemoveColorThemeDict(app);

                // Build the new theme source URI
                string themeSource = $"{ColorThemePrefix}{themeName}.xaml";

                // Load and add the new theme dictionary
                var dict = new System.Windows.ResourceDictionary
                {
                    Source = new Uri(themeSource, UriKind.Absolute)
                };
                app.Resources.MergedDictionaries.Add(dict);
                _activeColorThemeDict = dict;

                // Persist the choice
                SettingsManager.Current.ColorThemeName = themeName;

                // Auto-apply matching wallpaper for dark themes
                // Light and Default use desktop wallpaper (handled by clearing path)
                ApplyColorThemeWallpaper(themeName);

                Logger.LogAction("COLOR_THEME", $"Applied color theme: '{themeName}'");
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Failed to apply color theme '{themeName}': {ex.Message}");
            }
        }

        // ═══ Color Theme Wallpaper ═══

        /// <summary>
        /// Map of color theme names to their embedded wallpaper resource names.
        /// Light and Default have no wallpaper (use desktop).
        /// </summary>
        private static readonly Dictionary<string, string> ThemeWallpaperMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Midnight", "Resources/Wallpapers/Theme_Midnight.png" },
            { "Ocean",    "Resources/Wallpapers/Theme_Ocean.png" },
            { "Sunset",   "Resources/Wallpapers/Theme_Sunset.png" },
            { "Emerald",  "Resources/Wallpapers/Theme_Emerald.png" },
            { "Lavender", "Resources/Wallpapers/Theme_Lavender.png" },
        };

        /// <summary>
        /// Applies the matching wallpaper for a color theme. For themes with embedded wallpapers
        /// (Midnight, Ocean, Sunset, Emerald, Lavender), extracts to AppData and sets the path.
        /// For Light/Default, clears the wallpaper.
        /// </summary>
        private void ApplyColorThemeWallpaper(string themeName)
        {
            try
            {
                if (!ThemeWallpaperMap.TryGetValue(themeName, out string resourcePath))
                {
                    // Light or Default — clear wallpaper (use desktop or no wallpaper)
                    // Don't clear if user manually set a custom wallpaper that's not a theme wallpaper
                    string currentWp = SettingsManager.Current.ClipboardWallpaperPath ?? "";
                    if (currentWp.Contains("ColorThemeWallpapers", StringComparison.OrdinalIgnoreCase))
                    {
                        SettingsManager.Current.ClipboardWallpaperPath = "";
                    }
                    return;
                }

                // Extract the embedded wallpaper to AppData if not already present
                string wallpaperPath = ExtractColorThemeWallpaper(themeName, resourcePath);
                if (!string.IsNullOrEmpty(wallpaperPath))
                {
                    SettingsManager.Current.ClipboardWallpaperPath = wallpaperPath;
                    Logger.LogAction("COLOR_THEME", $"Applied wallpaper for '{themeName}': {wallpaperPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Wallpaper apply failed for '{themeName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts an embedded wallpaper resource to %AppData%/FlyShelf/ColorThemeWallpapers/.
        /// Returns the filesystem path. Skips extraction if file already exists.
        /// </summary>
        private string ExtractColorThemeWallpaper(string themeName, string resourcePath)
        {
            try
            {
                string wallpaperDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FlyShelf", "ColorThemeWallpapers");
                Directory.CreateDirectory(wallpaperDir);

                string destPath = Path.Combine(wallpaperDir, $"Theme_{themeName}.png");

                // Skip if already extracted
                if (File.Exists(destPath))
                    return destPath;

                // Load from embedded resource via pack URI
                string packUri = $"pack://application:,,,/{resourcePath.Replace('\\', '/')}";
                var streamInfo = System.Windows.Application.GetResourceStream(new Uri(packUri, UriKind.Absolute));
                if (streamInfo?.Stream == null)
                {
                    Logger.LogAction("COLOR_THEME", $"Wallpaper resource not found: {packUri}");
                    return "";
                }

                using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    streamInfo.Stream.CopyTo(fs);
                }
                streamInfo.Stream.Dispose();

                Logger.LogAction("COLOR_THEME", $"Extracted wallpaper: {destPath}");
                return destPath;
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Extract wallpaper failed: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Removes the current color theme ResourceDictionary from the app's merged dictionaries.
        /// </summary>
        private void RemoveColorThemeDict(System.Windows.Application app)
        {
            try
            {
                System.Windows.ResourceDictionary? toRemove = null;
                foreach (var d in app.Resources.MergedDictionaries)
                {
                    if (d.Source != null && d.Source.OriginalString.StartsWith(ColorThemePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove = d;
                        break;
                    }
                }
                if (toRemove != null)
                {
                    app.Resources.MergedDictionaries.Remove(toRemove);
                }
                _activeColorThemeDict = null;
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Error removing color theme dict: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore the saved color theme on startup. Call after Initialize().
        /// </summary>
        public void RestoreColorTheme()
        {
            string savedTheme = SettingsManager.Current.ColorThemeName ?? "Default";
            if (savedTheme.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                RemoveColorTheme();
                return;
            }
            ApplyColorTheme(savedTheme);
        }

        /// <summary>
        /// Removes the current color theme dictionary, restoring the app to palette defaults
        /// with the user's desktop wallpaper applied.
        /// </summary>
        public void RemoveColorTheme()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;
                RemoveColorThemeDict(app);
                SettingsManager.Current.ColorThemeName = "Default";

                // Switch to desktop wallpaper mode — the original FlyShelf look
                SettingsManager.Current.ThemeDisplayMode = "desktop";
                SetActiveTheme(null); // Clear mascot

                Logger.LogAction("COLOR_THEME", "Color theme removed — using palette defaults with desktop wallpaper");
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Error removing color theme: {ex.Message}");
            }
        }


        // ═══════════════════════════════════════════════════════════════
        // INTERNAL: FileSystemWatcher for hot-reload
        // ═══════════════════════════════════════════════════════════════

        private void StartWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(_themesDir)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                // Debounce: only refresh after 500ms of no changes
                System.Timers.Timer debounce = new(500) { AutoReset = false };
                debounce.Elapsed += (s, e) =>
                {
                    try
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            RefreshThemeList();
                            // Re-link active theme if it was refreshed
                            if (_activeTheme != null)
                            {
                                var refreshed = AvailableThemes.FirstOrDefault(t =>
                                    t.Name.Equals(_activeTheme.Name, StringComparison.OrdinalIgnoreCase));
                                if (refreshed != null && refreshed.IsValid)
                                {
                                    _activeTheme = refreshed;
                                    ActiveThemeChanged?.Invoke(_activeTheme);
                                }
                            }
                        });
                    }
                    catch { }
                };

                _watcher.Changed += (s, e) => { debounce.Stop(); debounce.Start(); };
                _watcher.Created += (s, e) => { debounce.Stop(); debounce.Start(); };
                _watcher.Deleted += (s, e) => { debounce.Stop(); debounce.Start(); };
                _watcher.Renamed += (s, e) => { debounce.Stop(); debounce.Start(); };

                Logger.LogAction("THEME", "FileSystemWatcher active — themes hot-reload enabled");
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"FileSystemWatcher failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // DEFAULT THEME: A minimal built-in theme as starter template
        // ═══════════════════════════════════════════════════════════════

        private void CreateDefaultTheme()
        {
            try
            {
                string defaultDir = Path.Combine(_themesDir, "flyshelf-default");
                if (Directory.Exists(defaultDir)) return;

                Directory.CreateDirectory(Path.Combine(defaultDir, "sprites"));

                var manifest = new
                {
                    name = "FlyShelf Default",
                    author = "FlyShelf Team",
                    version = "1.0.0",
                    description = "A minimal starter theme. Drop your own GIFs into the sprites folder!",
                    license = "MIT",
                    character = "FlyShelf Mascot",
                    tags = new[] { "default", "starter", "template" },
                    animations = new Dictionary<string, object>
                    {
                        ["idle"] = new
                        {
                            file = "sprites/idle.gif",
                            width = 48,
                            height = 48,
                            placement = "header-right",
                            loop = true,
                            trigger = ""
                        },
                        ["delete"] = new
                        {
                            file = "sprites/delete.gif",
                            width = 64,
                            height = 64,
                            placement = "center-overlay",
                            loop = false,
                            trigger = "on-delete",
                            durationMs = 800
                        }
                    },
                    placements = new Dictionary<string, object>
                    {
                        ["header-right"] = new { anchor = "top-right", offsetX = -60, offsetY = 4 },
                        ["header-left"] = new { anchor = "top-left", offsetX = 8, offsetY = 4 },
                        ["center-overlay"] = new { anchor = "center", offsetX = 0, offsetY = 0 },
                        ["bottom-scroll"] = new { anchor = "bottom-left", offsetX = 10, offsetY = -10 }
                    }
                };

                string json = System.Text.Json.JsonSerializer.Serialize(manifest,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(defaultDir, "manifest.json"), json);

                // Create a README for theme creators
                string readme = @"# FlyShelf Theme Pack — Template

## How to Create Your Own Theme

1. **Create a folder** in this directory with your theme name
2. **Add a `manifest.json`** — copy the one from this folder as a starting point
3. **Add sprite GIFs** to a `sprites/` subfolder
4. **Add a `preview.png`** (256×256) for the theme picker

## Animation Triggers
- `idle` — plays continuously when clipboard is open
- `delete` — plays once when an item is deleted
- `copy` — plays once when content is copied
- `search` — plays while search bar is active
- `running` — loops at the bottom/corner of the clipboard

## Placement Anchors
- `top-left`, `top-right` — beside the toolbar
- `center` — center of the clipboard
- `bottom-left`, `bottom-right` — corners of the list

## Sharing Your Theme
1. Zip your theme folder
2. Rename `.zip` to `.flyshelf-theme`
3. Share it! Users can drag it onto FlyShelf to install.

## Recommended GIF Specs
- **Size**: 32×32 to 64×64 pixels
- **Style**: Pixel art with transparency
- **Format**: GIF with alpha channel
- **FPS**: 8-15 frames per second
- **File size**: Keep under 500KB per animation
";
                File.WriteAllText(Path.Combine(defaultDir, "README.md"), readme);

                Logger.LogAction("THEME", "Created default theme template");
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Failed to create default theme: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }
    }
}
