using System;
using System.Windows;
using System.Windows.Media;

namespace FlyShelf
{
    /// <summary>
    /// MainWindow partial — Theme Engine, Wallpaper, Backdrop & Color Accent Management.
    /// Contains: RestoreMicaBlur, ApplyNonMicaBackground, ApplyWallpaper, ExtractDominantColor,
    ///           ApplyDominantColorAccent, ResetSelectionAccent, GetDesktopWallpaperPath, ClearWallpaperLayers.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Clears ALL wallpaper/theme visual layers without touching the window backdrop.
        /// Shared cleanup used by both RestoreMicaBlur and ApplyNonMicaBackground.
        /// </summary>
        private void ClearWallpaperLayers()
        {
            try
            {
                // Clear animated GIF source (XamlAnimatedGif holds onto frames)
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Source = null;
                WallpaperBg.Visibility = Visibility.Collapsed;

                // Clear the radial gradient theme overlay
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;

                // Clear the frosted glass header + its image source
                WallpaperFrostImg.Source = null;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                WallpaperFrostTint.Background = new SolidColorBrush(
                    Color.FromArgb(0x25, 0, 0, 0)); // Reset to default neutral tint

                // Stop mascot
                MascotIdle.StopAnimation();

                // Reset tracking
                _currentLoadedWallpaperPath = "";
            }
            catch { }
        }

        /// <summary>
        /// Restores standard fallback solid background — system Acrylic/Mica blur is completely disabled
        /// to ensure instant, lag-free summoning/spawning on all machines.
        /// </summary>
        private void RestoreMicaBlur()
        {
            ClearWallpaperLayers();

            // ═══ SYSTEM BLUR COMPLETELY DISABLED FOR HIGH PERFORMANCE ═══
            this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
            ApplyPopupBackground();

            // Reset selection accent to default (violet)
            ResetSelectionAccent();
        }

        /// <summary>
        /// Applies a solid dark background with NO system blur — for Glass, Desktop, and Custom themes.
        /// Clears all wallpaper layers, disables SystemBackdropType, and sets a neutral dark bg.
        /// </summary>
        private void ApplyNonMicaBackground()
        {
            ClearWallpaperLayers();

            // Disable system blur/acrylic — only Mica mode gets it
            this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
            ApplyPopupBackground();

            // Reset selection accent to default (violet)
            ResetSelectionAccent();
        }

        /// <summary>
        /// Applies a neutral dark grey background for the popup clipboard
        /// (solid fallback when system blur is disabled or unsupported).
        /// </summary>
        private void ApplyPopupBackground()
        {
            // Clean neutral dark grey — no blue/indigo tint
            var grey = new SolidColorBrush(
                Color.FromRgb(36, 36, 36)); // #242424 — Windows 11 dark surface
            grey.Freeze();
            this.Background = Brushes.Transparent; // Maintain window chrome transparency for flawless fade compositing
            if (RootContent != null) RootContent.Background = grey;
        }

        /// <summary>
        /// Injects the wallpaper's dominant color as selection accent brushes.
        /// Called from ApplyWallpaper() after ExtractDominantColor completes.
        /// </summary>
        private void ApplyDominantColorAccent(Color dominant)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                var selBorder = new SolidColorBrush(
                    Color.FromArgb(0x60, dominant.R, dominant.G, dominant.B));
                selBorder.Freeze();
                var selBg = new SolidColorBrush(
                    Color.FromArgb(0x10, dominant.R, dominant.G, dominant.B));
                selBg.Freeze();
                var focusBorder = new SolidColorBrush(
                    Color.FromArgb(0x80, dominant.R, dominant.G, dominant.B));
                focusBorder.Freeze();

                app.Resources["ShelfCardSelectionBorder"] = selBorder;
                app.Resources["ShelfCardSelectionBg"] = selBg;
                app.Resources["ShelfCardFocusBorder"] = focusBorder;
            }
            catch { }
        }

        /// <summary>
        /// Resets selection accent brushes to the default violet.
        /// Called when switching to Mica or non-wallpaper modes.
        /// </summary>
        private void ResetSelectionAccent()
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                var selBorder = new SolidColorBrush(
                    Color.FromArgb(0x60, 0xA7, 0x8B, 0xFA)); // #60A78BFA
                selBorder.Freeze();
                var selBg = new SolidColorBrush(
                    Color.FromArgb(0x10, 0xA7, 0x8B, 0xFA)); // #10A78BFA
                selBg.Freeze();
                var focusBorder = new SolidColorBrush(
                    Color.FromArgb(0x80, 0xA7, 0x8B, 0xFA)); // #80A78BFA
                focusBorder.Freeze();

                app.Resources["ShelfCardSelectionBorder"] = selBorder;
                app.Resources["ShelfCardSelectionBg"] = selBg;
                app.Resources["ShelfCardFocusBorder"] = focusBorder;
            }
            catch { }
        }

        /// <summary>Gets current Windows desktop wallpaper path from registry (cached).</summary>
        private static string GetDesktopWallpaperPath()
        {
            if (_cachedDesktopWallpaperPath != null)
                return _cachedDesktopWallpaperPath;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    _cachedDesktopWallpaperPath = key?.GetValue("Wallpaper") as string ?? "";
                    return _cachedDesktopWallpaperPath;
                }
            }
            catch 
            { 
                _cachedDesktopWallpaperPath = "";
                return ""; 
            }
        }

        private void ApplyWallpaper()
        {
            string path = Classes.SettingsManager.Current.ClipboardWallpaperPath;

            // If no wallpaper path set, clear all layers
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                if (_currentLoadedWallpaperPath == "" && WallpaperBg.Visibility == Visibility.Collapsed) return;
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Source = null;
                WallpaperBg.Visibility = Visibility.Collapsed;
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                _currentLoadedWallpaperPath = "";
                return;
            }


            if (path == _currentLoadedWallpaperPath)
            {
                return; // Already loaded! Bypasses heavy disk I/O and visual changes.
            }

            try
            {
                _currentLoadedWallpaperPath = path;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                bool isGif = ext == ".gif";

                if (isGif)
                {
                    // ═══ LIVE WALLPAPER: Animated GIF via XamlAnimatedGif ═══
                    WallpaperBg.Source = null; // Clear static source
                    var uri = new Uri(path, UriKind.Absolute);
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, uri);
                    XamlAnimatedGif.AnimationBehavior.SetRepeatBehavior(WallpaperBg,
                        System.Windows.Media.Animation.RepeatBehavior.Forever);
                    WallpaperBg.Visibility = Visibility.Visible;

                    // For GIF wallpapers, use a themed color directly (can't extract from animated)
                    WallpaperFrostHeader.Visibility = Visibility.Collapsed; // No frost for GIF (looks odd)
                    var themeColor = Color.FromRgb(255, 140, 0); // Cozy dark orange / Gravity Cat
                    var centerColor = Color.FromArgb(30, themeColor.R, themeColor.G, themeColor.B);
                    var edgeColor = Color.FromArgb(120, (byte)(themeColor.R / 5), (byte)(themeColor.G / 5), (byte)(themeColor.B / 5));
                    WallpaperRadialBrush.GradientStops[0].Color = centerColor;
                    WallpaperRadialBrush.GradientStops[1].Color = edgeColor;
                    WallpaperThemeOverlay.Visibility = Visibility.Visible;

                    Classes.Logger.LogAction("WALLPAPER", $"Live animated wallpaper: {path}");
                }
                else
                {
                    // ═══ STATIC WALLPAPER: PNG/JPG via BitmapImage ═══
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null); // Clear any GIF

                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300; // Keep it lightweight
                    bmp.EndInit();
                    bmp.Freeze();

                    // Layer 1: Background image
                    WallpaperBg.Source = bmp;
                    WallpaperBg.Visibility = Visibility.Visible;

                    // Layer 3: Frosted glass header
                    WallpaperFrostImg.Source = bmp;
                    WallpaperFrostHeader.Visibility = Visibility.Visible;

                    // Extract dominant color for theme gradient asynchronously to prevent UI stutter
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            return ExtractDominantColor(bmp);
                        }
                        catch
                        {
                            return Color.FromRgb(99, 102, 241); // Fallback indigo
                        }
                    }).ContinueWith(t =>
                    {
                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                        {
                            var dominantColor = t.Result;
                             Dispatcher.InvokeAsync(() =>
                             {
                                 try
                                 {
                                     // GUARD: If the window is no longer summoned or is currently dismissing,
                                     // do NOT apply the wallpaper theme updates!
                                     if (!_isCurrentlySummoned || _isAnimatingHide) return;

                                     var centerColor = Color.FromArgb(50, dominantColor.R, dominantColor.G, dominantColor.B);
                                     var edgeColor = Color.FromArgb(140, (byte)(dominantColor.R / 1.8), (byte)(dominantColor.G / 1.8), (byte)(dominantColor.B / 1.8));
 
                                     WallpaperRadialBrush.GradientStops[0].Color = centerColor;
                                     WallpaperRadialBrush.GradientStops[1].Color = edgeColor;
                                     WallpaperThemeOverlay.Visibility = Visibility.Visible;
 
                                     // Tint the frost header with the theme color
                                     WallpaperFrostTint.Background = new SolidColorBrush(
                                         Color.FromArgb(60, dominantColor.R, dominantColor.G, dominantColor.B));
 
                                     // Inject wallpaper dominant color as selection accent
                                     ApplyDominantColorAccent(dominantColor);
                                 }
                                 catch { }
                             });
                        }
                    });
                }
            }
            catch
            {
                _currentLoadedWallpaperPath = "";
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Visibility = Visibility.Collapsed;
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Quick dominant color extraction by sampling a few pixels from the center.
        /// </summary>
        private static Color ExtractDominantColor(System.Windows.Media.Imaging.BitmapImage bmp)
        {
            try
            {
                var formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int w = formatted.PixelWidth;
                int h = formatted.PixelHeight;
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];
                formatted.CopyPixels(pixels, stride, 0);

                // Sample 9 points in center region
                int totalR = 0, totalG = 0, totalB = 0, count = 0;
                int[] xs = { w / 4, w / 2, 3 * w / 4 };
                int[] ys = { h / 4, h / 2, 3 * h / 4 };

                foreach (int x in xs)
                    foreach (int y in ys)
                    {
                        int idx = y * stride + x * 4;
                        if (idx + 2 < pixels.Length)
                        {
                            totalB += pixels[idx];
                            totalG += pixels[idx + 1];
                            totalR += pixels[idx + 2];
                            count++;
                        }
                    }

                if (count > 0)
                    return Color.FromRgb((byte)(totalR / count), (byte)(totalG / count), (byte)(totalB / count));
            }
            catch { }

            return Color.FromRgb(99, 102, 241); // Fallback indigo
        }
    }
}
