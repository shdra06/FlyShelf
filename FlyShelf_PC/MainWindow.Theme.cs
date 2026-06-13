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
                try
                {
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                    animator?.Dispose();
                }
                catch { }
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

                // Clear pre-blurred wallpaper cache (used by selected card frosted glass)
                Resources["PreBlurredWallpaper"] = null;

                // Stop mascot
                MascotIdle.StopAnimation();

                // Reset tracking
                _currentLoadedWallpaperPath = "";
            }
            catch { }
        }

        /// <summary>
        /// Activates Mica blur mode — sets SystemBackdropType to Mica if blur is enabled,
        /// otherwise falls back to a solid background.
        /// </summary>
        private void RestoreMicaBlur()
        {
            ClearWallpaperLayers();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Classes.NativeMethods.DisableCustomAcrylic(hwnd);
            }

            // ═══ SOLID GREY BACKGROUND ═══
            // Mica blur doesn't render well on the clipboard popup, so we use a clean
            // Windows-native dark grey (#2B2B2B) instead of the DWM Mica backdrop.
            // The main PC app (HubWindow) keeps its own Mica blur — only the clipboard is changed.
            this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
            var greyBg = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
            greyBg.Freeze();
            this.Background = greyBg;
            if (RootContent != null)
                RootContent.Background = greyBg;

            ResetSelectionAccent();
        }

        /// <summary>
        /// Activates Acrylic blur mode — sets SystemBackdropType to Acrylic if blur is enabled,
        /// otherwise falls back to a solid background.
        /// </summary>
        private void RestoreAcrylicBlur()
        {
            ClearWallpaperLayers();
            bool blurEnabled = Classes.SettingsManager.Current.EnableBlurBehind 
                               && Classes.NativeMethods.ShouldUseBlur();
            if (blurEnabled)
            {
                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                this.Background = Brushes.Transparent;
                if (RootContent != null)
                    RootContent.Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)); // Near-transparent for hit-testing

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Classes.NativeMethods.EnableCustomAcrylic(hwnd, 0x22242424);
                }
            }
            else
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Classes.NativeMethods.DisableCustomAcrylic(hwnd);
                }

                this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                ApplyPopupBackground(); // solid dark + software blur fallback
            }
            ResetSelectionAccent();
        }

        /// <summary>
        /// Applies a solid dark background for Desktop and Custom themes.
        /// </summary>
        private void ApplyNonMicaBackground()
        {
            ClearWallpaperLayers();
            
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Classes.NativeMethods.DisableCustomAcrylic(hwnd);
            }

            this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
            ApplyPopupBackground(skipSoftwareBlur: true); // Own wallpaper is applied right after
            ResetSelectionAccent();
        }

        /// <summary>
        /// Applies a fallback background for the popup clipboard when system blur is disabled.
        /// Instead of a flat solid color, loads the Windows desktop wallpaper and applies a
        /// heavy software blur to simulate the frosted glass look without DWM compositor.
        /// Falls back to solid dark if no desktop wallpaper is available.
        /// </summary>
        private void ApplyPopupBackground(bool skipSoftwareBlur = false)
        {
            // Read the theme-aware fallback color; defaults to neutral dark grey (#242424)
            SolidColorBrush fallback;
            try
            {
                fallback = Application.Current?.Resources["ThemeWindowFallback"] as SolidColorBrush
                           ?? new SolidColorBrush(Color.FromRgb(36, 36, 36));
            }
            catch
            {
                fallback = new SolidColorBrush(Color.FromRgb(36, 36, 36));
            }

            var bg = new SolidColorBrush(fallback.Color);
            bg.Freeze();
            this.Background = Brushes.Transparent; // Maintain window chrome transparency for flawless fade compositing
            if (RootContent != null) RootContent.Background = bg;

            // ═══ SOFTWARE BLUR FALLBACK ═══
            // When DWM blur is off, simulate frosted glass by showing a heavily blurred
            // version of the desktop wallpaper as the clipboard background.
            if (skipSoftwareBlur) return; // Caller will apply its own wallpaper
            string desktopWp = GetDesktopWallpaperPath();
            if (string.IsNullOrEmpty(desktopWp) || !System.IO.File.Exists(desktopWp))
                return; // No wallpaper available — solid dark is fine

            try
            {
                // Load the desktop wallpaper at reduced resolution for fast blurring
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(desktopWp, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 600; // Low-res for performance
                bmp.EndInit();
                bmp.Freeze();

                // Show the desktop wallpaper as background
                WallpaperBg.Source = bmp;
                WallpaperBg.Visibility = Visibility.Visible;

                // STRICT BLUR RULE: Only blur the desktop wallpaper fallback when blur is enabled.
                // When blur is OFF, show the wallpaper crystal clear.
                bool blurFallbackEnabled = Classes.SettingsManager.Current.EnableBlurBehind 
                                           && Classes.NativeMethods.ShouldUseBlur();
                if (blurFallbackEnabled)
                {
                    WallpaperBg.Opacity = 0.35;
                    // Heavy software blur on background thread (radius 25 for strong frosted effect)
                    var capturedBmp = bmp;
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var blurred = PreBlurBitmap(capturedBmp, 25);
                            Dispatcher.InvokeAsync(() =>
                            {
                                WallpaperBg.Source = blurred;
                                WallpaperBg.Opacity = 0.38;

                                // Apply a dark tint overlay so text remains readable
                                WallpaperThemeOverlay.Visibility = Visibility.Visible;
                                WallpaperRadialBrush.GradientStops[0].Color = Color.FromArgb(80, 20, 20, 30);
                                WallpaperRadialBrush.GradientStops[1].Color = Color.FromArgb(160, 10, 10, 20);
                            });
                        }
                        catch { }
                    });
                }
                else
                {
                    // Crystal clear — no blur, slightly higher opacity for vivid wallpaper
                    WallpaperBg.Opacity = 0.42;
                }

                Classes.Logger.LogAction("THEME", $"Software blur fallback — using desktop wallpaper: {desktopWp}");
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("THEME", $"Software blur fallback failed: {ex.Message}");
            }
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

                byte borderAlpha = 0x95; // Crisp, highly visible selection border alpha (approx 58%)
                byte bgAlpha = 0x25;     // Clean, visible selection background tint alpha (approx 15%)
                byte focusAlpha = 0xB0;

                // 1. Convert dominant color to HSL space to isolate Hue
                RgbToHsl(dominant, out double h, out double s, out double l);

                // 2. Mathematically optimize Saturation & Lightness to create a stunning desaturated frosted-glass glow.
                //    We preserve the wallpaper's EXACT HUE (H), but keep Saturation low (approx 35%) and Lightness high (approx 82%)
                //    so it is always towards a clean, light white-greyish side with an elegant, soft tint of the theme's color.
                s = 0.35; // Soft desaturated saturation (approx 35%) for an elegant, white-greyish tinted accent
                l = 0.82; // Bright frosted glass lightness (approx 82%) to keep selection light and high contrast

                // Convert HSL back to RGB
                dominant = HslToRgb(h, s, l);

                // If the wallpaper is extremely dark, we increase alpha values slightly to make the neon glow extra distinct!
                double originalLuma = 0.299 * dominant.R + 0.587 * dominant.G + 0.114 * dominant.B;
                if (originalLuma < 160)
                {
                    borderAlpha = 0xD8; // Beautiful high-visibility glowing border outline
                    bgAlpha = 0x3E;     // Highly readable translucent selection background overlay
                    focusAlpha = 0xEA;  // Clear, distinct focus outline
                }

                var selBorder = new SolidColorBrush(
                    Color.FromArgb(borderAlpha, dominant.R, dominant.G, dominant.B));
                selBorder.Freeze();
                var selBg = new SolidColorBrush(
                    Color.FromArgb(bgAlpha, dominant.R, dominant.G, dominant.B));
                selBg.Freeze();
                var focusBorder = new SolidColorBrush(
                    Color.FromArgb(focusAlpha, dominant.R, dominant.G, dominant.B));
                focusBorder.Freeze();

                app.Resources["ShelfCardSelectionBorder"] = selBorder;
                app.Resources["ShelfCardSelectionBg"] = selBg;
                app.Resources["ShelfCardFocusBorder"] = focusBorder;
            }
            catch { }
        }

        private void ResetSelectionAccent()
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                // For Mica and Acrylic blur modes, use a stunning, light pastel Lavender/Indigo (#A5B4FC)
                // which matches the Indigo control highlight family but is very bright, so it glows 
                // cleanly without looking dark or muddy on dark Mica window backdrops.
                var selBorder = new SolidColorBrush(
                    Color.FromArgb(0x80, 0xA5, 0xB4, 0xFC)); // High-contrast crisp pastel border (approx 50% opacity)
                selBorder.Freeze();
                var selBg = new SolidColorBrush(
                    Color.FromArgb(0x18, 0xA5, 0xB4, 0xFC)); // Soft glowing pastel overlay (approx 9% opacity)
                selBg.Freeze();
                var focusBorder = new SolidColorBrush(
                    Color.FromArgb(0xA0, 0xA5, 0xB4, 0xFC));
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
                try
                {
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                    animator?.Dispose();
                }
                catch { }
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
                    try
                    {
                        var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                        animator?.Dispose();
                    }
                    catch { }
                    WallpaperBg.Source = null; // Clear static source
                    var uri = new Uri(path, UriKind.Absolute);
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, uri);
                    XamlAnimatedGif.AnimationBehavior.SetRepeatBehavior(WallpaperBg,
                        System.Windows.Media.Animation.RepeatBehavior.Forever);
                    WallpaperBg.Visibility = Visibility.Visible;

                    // For GIF wallpapers, use a themed color directly (can't extract from animated)
                    WallpaperFrostHeader.Visibility = Visibility.Collapsed; // No frost for GIF (looks odd)
                    var themeColor = Color.FromRgb(255, 140, 0); // Cozy dark orange fallback for animated wallpapers
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
                    try
                    {
                        var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                        animator?.Dispose();
                    }
                    catch { }
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null); // Clear any GIF

                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 1200; // High-quality decoded resolution
                    bmp.EndInit();
                    bmp.Freeze();

                    // Show container layers immediately with unblurred preview to prevent flash
                    WallpaperBg.Source = bmp;
                    WallpaperBg.Visibility = Visibility.Visible;

                    // STRICT BLUR RULE: Only pre-blur the custom wallpaper when blur is enabled.
                    // When blur is OFF, the wallpaper is shown crystal clear — no blur processing at all.
                    bool wallpaperBlurEnabled = Classes.SettingsManager.Current.EnableBlurBehind 
                                                && Classes.NativeMethods.ShouldUseBlur();
                    if (wallpaperBlurEnabled)
                    {
                        WallpaperFrostHeader.Visibility = Visibility.Visible;

                        var capturedPathForBlur = path;
                        var bmpForBlur = bmp;
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                // Pre-blur background at a soft radius of 15 for a premium glassmorphic feel
                                var blurredBg = PreBlurBitmap(bmpForBlur, 15);
                                // Pre-blur at radius 12 for frosted header (replaces runtime BlurEffect)
                                var blurredHeader = PreBlurBitmap(bmpForBlur, 12);
                                // Pre-blur at radius 18 for selected card backdrop
                                var blurredCards = PreBlurBitmap(bmpForBlur, 18);
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (_currentLoadedWallpaperPath != capturedPathForBlur) return; // Stale
                                    WallpaperBg.Source = blurredBg;
                                    WallpaperFrostImg.Source = blurredHeader;
                                    Resources["PreBlurredWallpaper"] = blurredCards;
                                });
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        // Crystal clear — no frost header, no pre-blur, just the sharp wallpaper
                        WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                    }

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
                                     var centerColor = Color.FromArgb(50, dominantColor.R, dominantColor.G, dominantColor.B);
                                     var edgeColor = Color.FromArgb(105, (byte)(dominantColor.R / 1.3), (byte)(dominantColor.G / 1.3), (byte)(dominantColor.B / 1.3));
 
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
                try
                {
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(WallpaperBg);
                    animator?.Dispose();
                }
                catch { }
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

        private static void RgbToHsl(Color rgb, out double h, out double s, out double l)
        {
            double r = rgb.R / 255.0;
            double g = rgb.G / 255.0;
            double b = rgb.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));

            h = 0;
            s = 0;
            l = (max + min) / 2.0;

            if (max != min)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

                if (max == r)
                {
                    h = (g - b) / d + (g < b ? 6 : 0);
                }
                else if (max == g)
                {
                    h = (b - r) / d + 2;
                }
                else if (max == b)
                {
                    h = (r - g) / d + 4;
                }

                h /= 6.0;
            }
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double r = l;
            double g = l;
            double b = l;

            if (s != 0)
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;

                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }

            return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1.0;
            if (t > 1) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        /// <summary>
        /// Pre-renders a blurred version of a BitmapImage using a pure pixel-level box blur.
        /// Safe to call from any thread (no WPF DispatcherObjects created).
        /// Returns a frozen BitmapSource safe for cross-thread assignment to Image.Source.
        /// Uses 3-pass box blur to approximate Gaussian blur.
        /// </summary>
        public static System.Windows.Media.Imaging.BitmapSource PreBlurBitmap(
            System.Windows.Media.Imaging.BitmapImage source, int radius)
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];

            // CopyPixels is safe on frozen BitmapImage from any thread
            var formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            formatted.Freeze();
            formatted.CopyPixels(pixels, stride, 0);

            // 3-pass horizontal+vertical box blur approximates Gaussian
            byte[] temp = new byte[pixels.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                // Horizontal pass
                for (int y = 0; y < h; y++)
                {
                    int rowBase = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int rSum = 0, gSum = 0, bSum = 0, aSum = 0, count = 0;
                        int x0 = Math.Max(0, x - radius);
                        int x1 = Math.Min(w - 1, x + radius);
                        for (int kx = x0; kx <= x1; kx++)
                        {
                            int idx = rowBase + kx * 4;
                            bSum += pixels[idx];
                            gSum += pixels[idx + 1];
                            rSum += pixels[idx + 2];
                            aSum += pixels[idx + 3];
                            count++;
                        }
                        int outIdx = rowBase + x * 4;
                        temp[outIdx]     = (byte)(bSum / count);
                        temp[outIdx + 1] = (byte)(gSum / count);
                        temp[outIdx + 2] = (byte)(rSum / count);
                        temp[outIdx + 3] = (byte)(aSum / count);
                    }
                }

                // Vertical pass
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        int rSum = 0, gSum = 0, bSum = 0, aSum = 0, count = 0;
                        int y0 = Math.Max(0, y - radius);
                        int y1 = Math.Min(h - 1, y + radius);
                        for (int ky = y0; ky <= y1; ky++)
                        {
                            int idx = ky * stride + x * 4;
                            bSum += temp[idx];
                            gSum += temp[idx + 1];
                            rSum += temp[idx + 2];
                            aSum += temp[idx + 3];
                            count++;
                        }
                        int outIdx = y * stride + x * 4;
                        pixels[outIdx]     = (byte)(bSum / count);
                        pixels[outIdx + 1] = (byte)(gSum / count);
                        pixels[outIdx + 2] = (byte)(rSum / count);
                        pixels[outIdx + 3] = (byte)(aSum / count);
                    }
                }
            }

            // Create frozen WriteableBitmap from blurred pixel data
            var result = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
    }
}
