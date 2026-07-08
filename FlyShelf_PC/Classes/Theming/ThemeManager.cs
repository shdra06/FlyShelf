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
    public class ThemeManager : IDisposable
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

        /// <summary>
        /// Unconditionally fires ActiveThemeChanged with the current active theme.
        /// Use this to force the MainWindow theme handler to re-apply wallpaper/backdrop
        /// when SettingsManager.SetProperty wouldn't fire (value didn't actually change).
        /// </summary>
        public void ForceThemeRefresh()
        {
            ActiveThemeChanged?.Invoke(_activeTheme);
        }

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
            catch { } // Best-effort: failure is acceptable
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
        // accent/surface/text colors (Midnight/Ocean/Sunset/Emerald/Lavender/ArcticSnow)
        // ═══════════════════════════════════════════════════════════════

        private const string ColorThemePrefix = "pack://application:,,,/Resources/Themes/Theme.";
        private static readonly string[] ValidColorThemes = { "Midnight", "Ocean", "Sunset", "Emerald", "Lavender", "ArcticSnow" };
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

                // ═══ v3.7.0: Switch WPF-UI and MicaWPF base theme (Light/Dark) ═══
                // ArcticSnow is a light theme — needs Light mode Mica backdrop + system controls
                bool isLightTheme = themeName.Equals("ArcticSnow", StringComparison.OrdinalIgnoreCase);
                SwitchSystemThemeMode(app, isLightTheme);

                // Auto-apply matching wallpaper for dark themes
                // ArcticSnow and Default use desktop wallpaper (handled by clearing path)
                ApplyColorThemeWallpaper(themeName);

                Logger.LogAction("COLOR_THEME", $"Applied color theme: '{themeName}' (mode: {(isLightTheme ? "Light" : "Dark")})");

                // Update Aero UI resources to match the active color theme
                ApplyAeroThemeOverrides(themeName);
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Failed to apply color theme '{themeName}': {ex.Message}");
            }
        }

        // ═══ Color Theme Wallpaper ═══

        /// <summary>
        /// Map of color theme names to their embedded wallpaper resource names.
        /// ArcticSnow and Default have no wallpaper (use desktop).
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
        /// For ArcticSnow/Default, clears the wallpaper.
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
                    // Always update the path so the correct theme wallpaper is ready.
                    // The display mode handler (mica/glass/desktop/theme) controls whether
                    // the wallpaper is actually shown or hidden.
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

        // ═══════════════════════════════════════════════════════════════
        // SYSTEM THEME MODE SWITCH (v3.7.0)
        // Switches WPF-UI and MicaWPF base theme dictionaries between Light/Dark.
        // Required because ArcticSnow is a light theme but the app defaults to Dark.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Switches the WPF-UI ThemesDictionary and MicaWPF ThemeDictionary
        /// between Light and Dark mode at runtime.
        /// </summary>
        private void SwitchSystemThemeMode(System.Windows.Application app, bool toLight)
        {
            try
            {
                string targetMode = toLight ? "Light" : "Dark";
                string oppositeMode = toLight ? "Dark" : "Light";
                var dicts = app.Resources.MergedDictionaries;

                // ─── Replace WPF-UI ThemesDictionary ───
                System.Windows.ResourceDictionary? wpfUiThemeDict = null;
                foreach (var d in dicts)
                {
                    if (d.Source != null && d.Source.OriginalString.Contains("Wpf.Ui") && d.Source.OriginalString.Contains("Theme"))
                    {
                        wpfUiThemeDict = d;
                        break;
                    }
                }
                // Also check by type name for WPF-UI 4.x
                if (wpfUiThemeDict == null)
                {
                    foreach (var d in dicts)
                    {
                        if (d.GetType().FullName?.Contains("ThemesDictionary") == true)
                        {
                            wpfUiThemeDict = d;
                            break;
                        }
                    }
                }
                if (wpfUiThemeDict != null)
                {
                    int idx = dicts.IndexOf(wpfUiThemeDict);
                    dicts.Remove(wpfUiThemeDict);
                    var newWpfUi = new Wpf.Ui.Markup.ThemesDictionary { Theme = toLight ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark };
                    dicts.Insert(idx, newWpfUi);
                }

                // ─── Replace MicaWPF ThemeDictionary ───
                System.Windows.ResourceDictionary? micaThemeDict = null;
                foreach (var d in dicts)
                {
                    if (d.Source != null && d.Source.OriginalString.Contains("MicaWPF") && d.Source.OriginalString.Contains("Theme"))
                    {
                        micaThemeDict = d;
                        break;
                    }
                }
                if (micaThemeDict == null)
                {
                    foreach (var d in dicts)
                    {
                        if (d.GetType().FullName?.Contains("MicaWPF") == true && d.GetType().Name.Contains("ThemeDictionary"))
                        {
                            micaThemeDict = d;
                            break;
                        }
                    }
                }
                if (micaThemeDict != null)
                {
                    int idx = dicts.IndexOf(micaThemeDict);
                    dicts.Remove(micaThemeDict);
                    var newMica = new MicaWPF.Styles.ThemeDictionary { Theme = toLight ? MicaWPF.Core.Enums.WindowsTheme.Light : MicaWPF.Core.Enums.WindowsTheme.Dark };
                    dicts.Insert(idx, newMica);
                }

                // ─── Also update Mica backdrop on HubWindow if open ───
                try
                {
                    foreach (System.Windows.Window win in System.Windows.Application.Current.Windows)
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20
                            int useDarkMode = toLight ? 0 : 1;
                            DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int));
                        }
                    }
                }
                catch { }

                Logger.LogAction("COLOR_THEME", $"System theme mode switched to {targetMode}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"System theme mode switch failed: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

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

                // v3.7.0: Restore Dark system theme when switching away from ArcticSnow
                SwitchSystemThemeMode(app, false);

                // Reset Aero UI resources to light defaults
                ApplyAeroThemeOverrides("Default");

                Logger.LogAction("COLOR_THEME", "Color theme removed — using Windows native palette defaults");
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME", $"Error removing color theme: {ex.Message}");
            }
        }


        // ═══════════════════════════════════════════════════════════════
        // AERO UI THEME OVERRIDES — per-color-theme brush injection
        // Updates DynamicResource brushes consumed by AltClipboardStyles.xaml
        // so the Aero card/sidebar/bottom-bar colors match the active palette.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Applies Aero-specific DynamicResource overrides based on the active color theme.
        /// Called automatically when the color theme changes.
        /// </summary>
        public void ApplyAeroThemeOverrides(string themeName)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;

            // Default / ArcticSnow — light mode defaults from AltClipboardStyles.xaml
            if (string.IsNullOrEmpty(themeName) || themeName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || themeName.Equals("ArcticSnow", StringComparison.OrdinalIgnoreCase))
            {
                SetAeroResource(app, "AltCardBg", "#B8FFFFFF");
                SetAeroResource(app, "AltCardBgHover", "#E0FFFFFF");
                SetAeroResource(app, "AltCardBorder", "#18000000");
                SetAeroResource(app, "AltCardBorderHover", "#28000000");
                SetAeroResource(app, "AltTextPrimary", "#1E293B");
                SetAeroResource(app, "AltTextSecondary", "#475569");
                SetAeroResource(app, "AltTextTertiary", "#94A3B8");
                SetAeroResource(app, "AltSearchBg", "#90FFFFFF");
                SetAeroResource(app, "AltSearchBorder", "#20000000");
                SetAeroResource(app, "AltSearchFg", "#64748B");
                SetAeroResource(app, "AltBottomBarBg", "#D8F0F4F8");
                SetAeroResource(app, "AltBottomBarBorder", "#20000000");
                SetAeroResource(app, "AltSidebarHover", "#18000000");
                SetAeroResource(app, "AltTimestampFg", "#64748B");
                SetAeroResource(app, "AltSubtitleFg", "#64748B");
            }

            // Themed modes — vibrant tinted backgrounds with dark text for each theme
            switch (themeName)
            {
                case "ArcticSnow":
                    // Warm cream/ivory — cozy paper-like light theme
                    SetAeroResource(app, "AltCardBg", "#C0FBF5EC");
                    SetAeroResource(app, "AltCardBgHover", "#E8FFF8F0");
                    SetAeroResource(app, "AltCardBorder", "#186B5B3A");
                    SetAeroResource(app, "AltCardBorderHover", "#286B5B3A");
                    SetAeroResource(app, "AltTextPrimary", "#2C1810");
                    SetAeroResource(app, "AltTextSecondary", "#5C4A3A");
                    SetAeroResource(app, "AltTextTertiary", "#9C8A7A");
                    SetAeroResource(app, "AltSearchBg", "#A0FBF5EC");
                    SetAeroResource(app, "AltSearchBorder", "#1C6B5B3A");
                    SetAeroResource(app, "AltSearchFg", "#7A6A5A");
                    SetAeroResource(app, "AltBottomBarBg", "#D8F5EFE4");
                    SetAeroResource(app, "AltBottomBarBorder", "#1C6B5B3A");
                    SetAeroResource(app, "AltSidebarHover", "#186B5B3A");
                    SetAeroResource(app, "AltTimestampFg", "#7A6A5A");
                    SetAeroResource(app, "AltSubtitleFg", "#7A6A5A");
                    break;
                case "Midnight":
                    // Vibrant indigo-tinted — luminous periwinkle cards
                    SetAeroResource(app, "AltCardBg", "#C0E8ECFF");
                    SetAeroResource(app, "AltCardBgHover", "#E8F0F4FF");
                    SetAeroResource(app, "AltCardBorder", "#35506AE8");
                    SetAeroResource(app, "AltCardBorderHover", "#486080F0");
                    SetAeroResource(app, "AltTextPrimary", "#1A1A2E");
                    SetAeroResource(app, "AltTextSecondary", "#2D2D44");
                    SetAeroResource(app, "AltTextTertiary", "#555570");
                    SetAeroResource(app, "AltSearchBg", "#A0E8ECFF");
                    SetAeroResource(app, "AltSearchBorder", "#30506AE8");
                    SetAeroResource(app, "AltSearchFg", "#3D3D58");
                    SetAeroResource(app, "AltBottomBarBg", "#E0D0D8F8");
                    SetAeroResource(app, "AltBottomBarBorder", "#30506AE8");
                    SetAeroResource(app, "AltSidebarHover", "#206366F1");
                    SetAeroResource(app, "AltTimestampFg", "#4A4A68");
                    SetAeroResource(app, "AltSubtitleFg", "#4A4A68");
                    break;
                case "Ocean":
                    // Vibrant teal-tinted — luminous aqua cards
                    SetAeroResource(app, "AltCardBg", "#C0E0F8FF");
                    SetAeroResource(app, "AltCardBgHover", "#E8F0FBFF");
                    SetAeroResource(app, "AltCardBorder", "#35189098");
                    SetAeroResource(app, "AltCardBorderHover", "#4820A0B0");
                    SetAeroResource(app, "AltTextPrimary", "#0A2028");
                    SetAeroResource(app, "AltTextSecondary", "#1A3A44");
                    SetAeroResource(app, "AltTextTertiary", "#3A6070");
                    SetAeroResource(app, "AltSearchBg", "#A0E0F8FF");
                    SetAeroResource(app, "AltSearchBorder", "#30189098");
                    SetAeroResource(app, "AltSearchFg", "#2A4A58");
                    SetAeroResource(app, "AltBottomBarBg", "#E0C8F0F8");
                    SetAeroResource(app, "AltBottomBarBorder", "#30189098");
                    SetAeroResource(app, "AltSidebarHover", "#200EA5B5");
                    SetAeroResource(app, "AltTimestampFg", "#3A5A68");
                    SetAeroResource(app, "AltSubtitleFg", "#3A5A68");
                    break;
                case "Sunset":
                    // Vibrant amber-tinted — luminous golden cards
                    SetAeroResource(app, "AltCardBg", "#C0FFF4E0");
                    SetAeroResource(app, "AltCardBgHover", "#E8FFF8E8");
                    SetAeroResource(app, "AltCardBorder", "#35C08020");
                    SetAeroResource(app, "AltCardBorderHover", "#48D09030");
                    SetAeroResource(app, "AltTextPrimary", "#2A1A08");
                    SetAeroResource(app, "AltTextSecondary", "#4A3018");
                    SetAeroResource(app, "AltTextTertiary", "#6A5030");
                    SetAeroResource(app, "AltSearchBg", "#A0FFF4E0");
                    SetAeroResource(app, "AltSearchBorder", "#30C08020");
                    SetAeroResource(app, "AltSearchFg", "#5A4020");
                    SetAeroResource(app, "AltBottomBarBg", "#E0F8E8C8");
                    SetAeroResource(app, "AltBottomBarBorder", "#30C08020");
                    SetAeroResource(app, "AltSidebarHover", "#20D09000");
                    SetAeroResource(app, "AltTimestampFg", "#6A5030");
                    SetAeroResource(app, "AltSubtitleFg", "#6A5030");
                    break;
                case "Emerald":
                    // Vibrant mint-tinted — luminous jade cards
                    SetAeroResource(app, "AltCardBg", "#C0E0FFE8");
                    SetAeroResource(app, "AltCardBgHover", "#E8F0FFF0");
                    SetAeroResource(app, "AltCardBorder", "#35109E65");
                    SetAeroResource(app, "AltCardBorderHover", "#4818B878");
                    SetAeroResource(app, "AltTextPrimary", "#0A2018");
                    SetAeroResource(app, "AltTextSecondary", "#1A3A28");
                    SetAeroResource(app, "AltTextTertiary", "#3A6048");
                    SetAeroResource(app, "AltSearchBg", "#A0E0FFE8");
                    SetAeroResource(app, "AltSearchBorder", "#30109E65");
                    SetAeroResource(app, "AltSearchFg", "#2A4A38");
                    SetAeroResource(app, "AltBottomBarBg", "#E0C8F8D8");
                    SetAeroResource(app, "AltBottomBarBorder", "#30109E65");
                    SetAeroResource(app, "AltSidebarHover", "#2010A068");
                    SetAeroResource(app, "AltTimestampFg", "#3A5A48");
                    SetAeroResource(app, "AltSubtitleFg", "#3A5A48");
                    break;
                case "Lavender":
                    // Vibrant purple-tinted — luminous orchid cards
                    SetAeroResource(app, "AltCardBg", "#C0F0E8FF");
                    SetAeroResource(app, "AltCardBgHover", "#E8F5F0FF");
                    SetAeroResource(app, "AltCardBorder", "#357850C8");
                    SetAeroResource(app, "AltCardBorderHover", "#489060D8");
                    SetAeroResource(app, "AltTextPrimary", "#1A1028");
                    SetAeroResource(app, "AltTextSecondary", "#302040");
                    SetAeroResource(app, "AltTextTertiary", "#584870");
                    SetAeroResource(app, "AltSearchBg", "#A0F0E8FF");
                    SetAeroResource(app, "AltSearchBorder", "#307850C8");
                    SetAeroResource(app, "AltSearchFg", "#403058");
                    SetAeroResource(app, "AltBottomBarBg", "#E0E0D0F8");
                    SetAeroResource(app, "AltBottomBarBorder", "#307850C8");
                    SetAeroResource(app, "AltSidebarHover", "#207B50C8");
                    SetAeroResource(app, "AltTimestampFg", "#504068");
                    SetAeroResource(app, "AltSubtitleFg", "#504068");
                    break;
                case "__glass__":
                    // ═══ GLASS SLAB — Fully translucent frosted Aero clipboard ═══
                    // Light text on dark/transparent cards, matching GlassTheme.xaml aesthetics
                    SetAeroResource(app, "AltCardBg", "#15FFFFFF");
                    SetAeroResource(app, "AltCardBgHover", "#25FFFFFF");
                    SetAeroResource(app, "AltCardBorder", "#20FFFFFF");
                    SetAeroResource(app, "AltCardBorderHover", "#38FFFFFF");
                    SetAeroResource(app, "AltTextPrimary", "#F0FFFFFF");
                    SetAeroResource(app, "AltTextSecondary", "#B0FFFFFF");
                    SetAeroResource(app, "AltTextTertiary", "#70FFFFFF");
                    SetAeroResource(app, "AltSearchBg", "#12FFFFFF");
                    SetAeroResource(app, "AltSearchBorder", "#25FFFFFF");
                    SetAeroResource(app, "AltSearchFg", "#80FFFFFF");
                    SetAeroResource(app, "AltBottomBarBg", "#10FFFFFF");
                    SetAeroResource(app, "AltBottomBarBorder", "#20FFFFFF");
                    SetAeroResource(app, "AltSidebarHover", "#18FFFFFF");
                    SetAeroResource(app, "AltTimestampFg", "#70FFFFFF");
                    SetAeroResource(app, "AltSubtitleFg", "#70FFFFFF");
                    break;
                default:
                    // Unrecognized theme — treat as light
                    break;
            }

            // Update background gradients and overlay programmatically
            try
            {
                var mainWin = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (mainWin == null) return;

                // Find the AltArcticOverlay and toggle its visibility based on theme
                var arcticOverlay = mainWin.FindName("AltArcticOverlay") as System.Windows.Controls.Border;
                bool isLight = string.IsNullOrEmpty(themeName) || themeName == "Default" || themeName == "ArcticSnow";
                bool isGlass = themeName == "__glass__";

                if (arcticOverlay != null)
                {
                    arcticOverlay.Visibility = (isLight && !isGlass) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                }

                // Find the main AltClipboardPanel to set background gradient on the first Border child
                var altPanel = mainWin.FindName("AltClipboardPanel") as System.Windows.Controls.Grid;
                if (altPanel != null && altPanel.Children.Count > 0)
                {
                    var bgBorder = altPanel.Children[0] as System.Windows.Controls.Border;
                    if (bgBorder != null)
                    {
                        var grad = new System.Windows.Media.LinearGradientBrush();
                        grad.StartPoint = new System.Windows.Point(0, 0);
                        grad.EndPoint = new System.Windows.Point(0.3, 1);

                        // Vibrant saturated gradients — rich luminous colors with strong visual identity
                        switch (themeName)
                        {
                            case "Midnight":
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF9EA8E8"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFB0B8F0"), 0.4));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFC8CEFF"), 0.8));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF8890D8"), 1.0));
                                break;
                            case "Ocean":
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF70D8E8"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF88E0F0"), 0.4));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFA0E8F5"), 0.8));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF58C8D8"), 1.0));
                                break;
                            case "Sunset":
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFFB860"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFFC878"), 0.4));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFFD898"), 0.8));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFFA850"), 1.0));
                                break;
                            case "Emerald":
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF60D8A0"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF78E0B0"), 0.4));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF90E8C0"), 0.8));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FF48D090"), 1.0));
                                break;
                            case "Lavender":
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFB890E8"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFC8A0F0"), 0.4));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFD8B0F8"), 0.8));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFA078D8"), 1.0));
                                break;
                            case "__glass__":
                                // Near-transparent — lets the system acrylic blur shine through
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#08FFFFFF"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#05FFFFFF"), 0.5));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#08FFFFFF"), 1.0));
                                break;
                            case "ArcticSnow":
                                // Warm creamy ivory gradient — cozy paper feel
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFBF5EC"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFF8F2E8"), 0.4));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFFF8F0"), 0.8));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFF5EFE4"), 1.0));
                                break;
                            default: // Default — light gradient handled by overlay
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFF0F7FF"), 0.0));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFF5F5F5"), 0.5));
                                grad.GradientStops.Add(new System.Windows.Media.GradientStop(ColorFromHex("#FFFAFAFA"), 1.0));
                                break;
                        }
                        bgBorder.Background = grad;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("AERO_THEME", $"Failed to update gradients: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets a single Aero DynamicResource brush from a hex color string.
        /// </summary>
        private static void SetAeroResource(System.Windows.Application app, string key, string hexColor)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
                app.Resources[key] = new System.Windows.Media.SolidColorBrush(color);
            }
            catch { } // Best-effort: failure is acceptable
        }

        private static System.Windows.Media.Color ColorFromHex(string hex)
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
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
                    catch { } // Best-effort: failure is acceptable
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
