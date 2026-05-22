// ═══════════════════════════════════════════════════════════════════
// ThemePackage — Data model for a community mascot theme pack.
// Parsed from manifest.json inside each theme folder.
// Open-source format: anyone can create themes without recompiling.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Represents a complete theme package loaded from disk.
    /// Theme packs live in %AppData%/FlyShelf/Themes/{name}/
    /// </summary>
    public class ThemePackage
    {
        // ═══ Metadata ═══
        public string Name { get; set; } = "";
        public string Author { get; set; } = "Unknown";
        public string Version { get; set; } = "1.0.0";
        public string Description { get; set; } = "";
        public string License { get; set; } = "";
        public string Character { get; set; } = "";
        public List<string> Tags { get; set; } = new();

        // ═══ Animations ═══
        public Dictionary<string, ThemeAnimation> Animations { get; set; } = new();

        // ═══ Wallpaper ═══
        /// <summary>Relative path to the theme's wallpaper image (optional).</summary>
        public string Wallpaper { get; set; } = "";

        // ═══ Placement definitions ═══
        public Dictionary<string, ThemePlacement> Placements { get; set; } = new();

        // ═══ Runtime (not from JSON) ═══
        [JsonIgnore] public string ThemePath { get; set; } = "";
        [JsonIgnore] public string PreviewImagePath { get; set; } = "";
        [JsonIgnore] public string WallpaperPath { get; set; } = "";
        [JsonIgnore] public bool IsValid { get; set; } = false;
        [JsonIgnore] public string LoadError { get; set; } = "";

        /// <summary>
        /// Resolves a relative sprite path from manifest to an absolute file path.
        /// </summary>
        public string ResolvePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(ThemePath))
                return "";
            return Path.Combine(ThemePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// Gets the animation for a specific trigger, or null if not defined.
        /// </summary>
        public ThemeAnimation? GetAnimation(string trigger)
        {
            if (Animations.TryGetValue(trigger, out var anim))
                return anim;
            return null;
        }

        /// <summary>
        /// Gets the placement config for a named placement, with defaults.
        /// </summary>
        public ThemePlacement GetPlacement(string placementName)
        {
            if (!string.IsNullOrEmpty(placementName) && Placements.TryGetValue(placementName, out var p))
                return p;
            return new ThemePlacement { Anchor = "top-right", OffsetX = -60, OffsetY = 4 };
        }

        /// <summary>
        /// Load a ThemePackage from a manifest.json file path.
        /// Returns a valid package or an invalid one with LoadError set.
        /// </summary>
        public static ThemePackage LoadFromDirectory(string themeDir)
        {
            var package = new ThemePackage { ThemePath = themeDir };

            try
            {
                string manifestPath = Path.Combine(themeDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    package.LoadError = "manifest.json not found";
                    return package;
                }

                string json = File.ReadAllText(manifestPath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var parsed = JsonSerializer.Deserialize<ThemePackage>(json, options);
                if (parsed == null)
                {
                    package.LoadError = "Failed to parse manifest.json";
                    return package;
                }

                // Copy parsed values
                package.Name = parsed.Name;
                package.Author = parsed.Author;
                package.Version = parsed.Version;
                package.Description = parsed.Description;
                package.License = parsed.License;
                package.Character = parsed.Character;
                package.Tags = parsed.Tags ?? new();
                package.Animations = parsed.Animations ?? new();
                package.Placements = parsed.Placements ?? new();
                package.Wallpaper = parsed.Wallpaper ?? "";

                // Validate required fields
                if (string.IsNullOrWhiteSpace(package.Name))
                {
                    package.Name = Path.GetFileName(themeDir);
                }

                // Resolve and validate animation file paths
                foreach (var kvp in package.Animations)
                {
                    var anim = kvp.Value;
                    string resolvedPath = package.ResolvePath(anim.File);
                    if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                    {
                        anim.ResolvedFilePath = resolvedPath;
                    }
                    else
                    {
                        Logger.LogAction("THEME", $"Animation '{kvp.Key}' file not found: {anim.File} → {resolvedPath}");
                    }
                }

                // Resolve wallpaper path
                if (!string.IsNullOrEmpty(package.Wallpaper))
                {
                    string wpPath = package.ResolvePath(package.Wallpaper);
                    if (File.Exists(wpPath))
                    {
                        package.WallpaperPath = wpPath;
                        Logger.LogAction("THEME", $"Theme wallpaper: {wpPath}");
                    }
                }

                // Check preview image
                string previewPath = Path.Combine(themeDir, "preview.png");
                if (File.Exists(previewPath))
                    package.PreviewImagePath = previewPath;
                else
                {
                    // Fallback: use first available sprite as preview
                    foreach (var anim in package.Animations.Values)
                    {
                        if (!string.IsNullOrEmpty(anim.ResolvedFilePath))
                        {
                            package.PreviewImagePath = anim.ResolvedFilePath;
                            break;
                        }
                    }
                }

                package.IsValid = true;
                Logger.LogAction("THEME", $"Loaded theme: '{package.Name}' by {package.Author} ({package.Animations.Count} animations)");
            }
            catch (Exception ex)
            {
                package.LoadError = $"Parse error: {ex.Message}";
                Logger.LogAction("THEME", $"Failed to load theme from {themeDir}: {ex.Message}");
            }

            return package;
        }
    }

    /// <summary>
    /// Defines a single animation within a theme.
    /// </summary>
    public class ThemeAnimation
    {
        /// <summary>Relative path to the sprite file (GIF or PNG sprite sheet).</summary>
        public string File { get; set; } = "";

        /// <summary>Display width in pixels.</summary>
        public int Width { get; set; } = 48;

        /// <summary>Display height in pixels.</summary>
        public int Height { get; set; } = 48;

        /// <summary>Named placement from the theme's placements dictionary.</summary>
        public string Placement { get; set; } = "header-right";

        /// <summary>Whether the animation loops continuously.</summary>
        public bool Loop { get; set; } = true;

        /// <summary>Event trigger: "on-delete", "on-copy", "on-search", or empty for idle.</summary>
        public string Trigger { get; set; } = "";

        /// <summary>Duration in milliseconds for one-shot animations. 0 = play once through.</summary>
        public int DurationMs { get; set; } = 0;

        /// <summary>Playback speed multiplier (1.0 = normal).</summary>
        public double Speed { get; set; } = 1.0;

        /// <summary>Whether to flip the sprite horizontally when it reaches an edge.</summary>
        public bool FlipOnEdge { get; set; } = false;

        // ═══ For sprite sheet PNGs (non-GIF) ═══
        /// <summary>Number of frames in a sprite sheet (0 = use GIF frames).</summary>
        public int FrameCount { get; set; } = 0;

        /// <summary>Frames per second for sprite sheet animation.</summary>
        public int Fps { get; set; } = 10;

        // ═══ Runtime (not from JSON) ═══
        [JsonIgnore] public string ResolvedFilePath { get; set; } = "";
    }

    /// <summary>
    /// Defines where an animation is placed in the UI.
    /// </summary>
    public class ThemePlacement
    {
        /// <summary>Anchor point: "top-left", "top-right", "center", "bottom-left", "bottom-right"</summary>
        public string Anchor { get; set; } = "top-right";

        /// <summary>X offset from anchor (positive = right).</summary>
        public double OffsetX { get; set; } = 0;

        /// <summary>Y offset from anchor (positive = down).</summary>
        public double OffsetY { get; set; } = 0;
    }
}
