using System;
using System.Windows;
using System.Windows.Media;
using FlyShelf.Helpers;

namespace FlyShelf
{
    /// <summary>
    /// MainWindow partial — Theme Engine, Wallpaper, Backdrop &amp; Color Accent Management.
    /// 
    /// ═══════════════════════════════════════════════════════════════════════════════
    /// ARCHITECTURE NOTE — Why this is NOT extracted to a standalone ThemeController
    /// ═══════════════════════════════════════════════════════════════════════════════
    /// 
    /// Analysis (2026-06-28) found 15+ tight MainWindow dependencies that make
    /// extraction impractical without degrading the design:
    /// 
    /// XAML Named Elements accessed (8):
    ///   WallpaperBg, WallpaperThemeOverlay, WallpaperFrostImg, WallpaperFrostHeader,
    ///   WallpaperFrostTint, WallpaperRadialBrush, RootContent, MascotIdle
    /// 
    /// Window Properties mutated (4):
    ///   this.SystemBackdropType, this.Background,
    ///   WindowInteropHelper(this).Handle, VisualTreeHelper.GetDpi(this)
    /// 
    /// Threading:
    ///   Dispatcher.InvokeAsync (4 call sites — background blur + wallpaper refresh)
    /// 
    /// Shared Fields (declared in MainWindow.xaml.cs, used here + WndProc + Lifecycle):
    ///   _currentLoadedWallpaperPath — 9 refs here, 2 in xaml.cs
    ///   _cachedDesktopWallpaperPath — 7 refs here, 1 in WndProc.cs, 1 in xaml.cs
    /// 
    /// Methods called FROM other partials (public interface):
    ///   RestoreMicaBlur()                — 5 calls from MainWindow.xaml.cs
    ///   RestoreAcrylicBlur()             — 2 calls from MainWindow.xaml.cs
    ///   ApplyNonMicaBackground()         — 6 calls from MainWindow.xaml.cs
    ///   ApplyWallpaper()                 — 11 calls from MainWindow.xaml.cs, 1 internal
    ///   ApplyPopupBackground()           — 1 call from MainWindow.xaml.cs, 2 internal
    ///   GetDesktopWallpaperPath()        — 2 calls from MainWindow.xaml.cs
    ///   RefreshDesktopWallpaperIfChanged — 1 call from Lifecycle.cs, 1 from WndProc.cs
    ///   StartWallpaperFileWatcher()      — 1 call from MainWindow.xaml.cs
    ///   StopWallpaperFileWatcher()       — 1 call from MainWindow.xaml.cs
    /// 
    /// A ThemeController would need the entire MainWindow + 8 XAML element references
    /// passed in, making it a worse abstraction than the current partial class approach.
    /// ═══════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public partial class MainWindow
    {
        #region ═══ Layer Cleanup ═══

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
                catch { } // Best-effort: failure is acceptable
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
            catch { } // Best-effort: failure is acceptable
        }

        #endregion

        #region ═══ Backdrop Modes (Mica / Acrylic / Non-Mica) ═══

        /// <summary>
        /// Applies a DWM system backdrop type directly via native DWM API.
        /// MicaWPF's SystemBackdropType property only works at window creation —
        /// changing it at runtime on an already-visible window does nothing.
        /// This method calls the DWM APIs directly for immediate effect.
        /// backdropType: 1=None, 2=Mica, 3=Acrylic, 4=MicaAlt
        /// </summary>
        private void ApplyDwmBackdrop(int backdropType)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Classes.Logger.LogAction("DWM_BACKDROP", "FAILED: hwnd is IntPtr.Zero");
                return;
            }

            try
            {
                // Extend DWM frame into entire client area (required for backdrop to render)
                var margins = new Classes.NativeMethods.MARGINS
                {
                    cxLeftWidth = -1,
                    cxRightWidth = -1,
                    cyTopHeight = -1,
                    cyBottomHeight = -1
                };
                int hrMargins = Classes.NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

                // Set immersive dark mode for dark backdrop tint
                int darkMode = 1;
                int hrDark = Classes.NativeMethods.DwmSetWindowAttribute(hwnd,
                    Classes.NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // Set the actual backdrop type
                int hrBackdrop = Classes.NativeMethods.DwmSetWindowAttribute(hwnd,
                    Classes.NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

                Classes.Logger.LogAction("DWM_BACKDROP",
                    $"Applied backdropType={backdropType} hwnd=0x{hwnd:X} " +
                    $"hrMargins=0x{hrMargins:X8} hrDark=0x{hrDark:X8} hrBackdrop=0x{hrBackdrop:X8} " +
                    $"WindowBg={this.Background} RootContentBg={RootContent?.Background}");

                // Fallback: If DWM backdrop failed (RDP, Basic Display, Windows Server),
                // set a solid dark background so the window doesn't render invisible.
                if (hrBackdrop != 0)
                {
                    this.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x16, 0x16, 0x2A));
                    Classes.Logger.LogAction("DWM_BACKDROP", "Fallback: DWM backdrop not supported, using solid dark background");
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("DWM_BACKDROP", $"EXCEPTION: {ex.Message}");
                // Ensure window is visible even on crash
                this.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x16, 0x16, 0x2A));
            }
        }

        /// <summary>
        /// Activates Mica blur mode — uses the system Mica backdrop which creates a
        /// subtle tinted surface that imitates the desktop wallpaper colors.
        /// Falls back to solid dark background when blur is not enabled/supported.
        /// </summary>
        private void RestoreMicaBlur()
        {
            ClearWallpaperLayers();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Classes.NativeMethods.DisableCustomAcrylic(hwnd);
            }

            bool blurEnabled = Classes.SettingsManager.Current.EnableBlurBehind 
                               && Classes.NativeMethods.ShouldUseBlur();

            if (blurEnabled)
            {
                // Apply Mica backdrop via direct DWM API (backdropType 2 = Mica)
                // Background MUST be fully Transparent (not null) for DWM backdrop to show
                this.Background = Brushes.Transparent;
                if (RootContent != null)
                    RootContent.Background = Brushes.Transparent;
                ApplyDwmBackdrop(2); // DWMSBT_MAINWINDOW = Mica
            }
            else
            {
                // Solid dark fallback when blur is disabled
                ApplyDwmBackdrop(1); // DWMSBT_NONE
                var greyBg = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x2A));
                greyBg.Freeze();
                this.Background = greyBg;
                if (RootContent != null)
                    RootContent.Background = greyBg;
            }

            ResetSelectionAccent();
        }

        /// <summary>
        /// Activates Acrylic blur mode — uses the system Acrylic backdrop which creates
        /// a real see-through frosted glass effect showing blurred content behind the window.
        /// Falls back to solid background when blur is not supported/disabled.
        /// </summary>
        private void RestoreAcrylicBlur()
        {
            ClearWallpaperLayers();
            bool blurEnabled = Classes.SettingsManager.Current.EnableBlurBehind 
                               && Classes.NativeMethods.ShouldUseBlur();
            if (blurEnabled)
            {
                // v3.0.0 proven approach: Disable MicaWPF backdrop, set transparent background,
                // then apply acrylic via legacy SetWindowCompositionAttribute API.
                // The modern DWM SYSTEMBACKDROP_TYPE and MicaWPF BackdropType.Acrylic both fail
                // for the MainWindow due to its special window styles (WS_EX_NOACTIVATE etc).
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

        #endregion

        #region ═══ Fallback Background (Popup Software Blur) ═══

        /// <summary>
        /// Applies a fallback background for the popup clipboard when system blur is disabled.
        /// Instead of a flat solid color, loads the Windows desktop wallpaper and applies a
        /// heavy software blur to simulate the frosted glass look without DWM compositor.
        /// Falls back to solid dark if no desktop wallpaper is available.
        /// </summary>
        private async void ApplyPopupBackground(bool skipSoftwareBlur = false)
        {
            // [FIX BTN-1]: Outer try/catch — async void must not throw unhandled exceptions.
            try
            {
            // Read the theme-aware fallback color; defaults to signature FlyShelf dark navy (#16162A)
            SolidColorBrush fallback;
            try
            {
                fallback = Application.Current?.Resources["ThemeWindowFallback"] as SolidColorBrush
                           ?? new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x2A));
            }
            catch
            {
                fallback = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x2A));
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
                // PERF: BitmapImage.EndInit with UriSource performs synchronous file read —
                // offload to background thread and freeze so result is cross-thread safe.
                var bmp = await System.Threading.Tasks.Task.Run(() =>
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(desktopWp, UriKind.Absolute);
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    b.DecodePixelWidth = 400; // Low-res for performance (panel is max 850px, blurred = 400 is plenty)
                    b.EndInit();
                    b.Freeze();
                    return b;
                });

                // Show the desktop wallpaper as background
                WallpaperBg.Source = bmp;
                WallpaperBg.Visibility = Visibility.Visible;

                // STRICT BLUR RULE: Only blur the desktop wallpaper fallback when blur is enabled.
                // When blur is OFF, show the wallpaper crystal clear.
                // NOTE: This is software GaussianBlur, not DWM — works without Windows transparency.
                bool blurFallbackEnabled = Classes.SettingsManager.Current.EnableBlurBehind;
                if (blurFallbackEnabled)
                {
                    WallpaperBg.Opacity = 0.35;
                    // Heavy software blur on background thread (radius 25 for strong frosted effect)
                    var capturedBmp = bmp;
                    // Capture actual monitor DPI on UI thread — PreBlurBitmap runs on background thread
                    var dpiForBlur = VisualTreeHelper.GetDpi(this);
                    double capturedDpiX = dpiForBlur.PixelsPerInchX;
                    double capturedDpiY = dpiForBlur.PixelsPerInchY;
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var blurred = PreBlurBitmap(capturedBmp, 25, capturedDpiX, capturedDpiY);
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
                        catch { } // Best-effort: failure is acceptable
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
            } // end outer try
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyPopupBackground failed: {ex.Message}");
            }
        }

        #endregion

        #region ═══ Selection Accent Colors ═══

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

                // If the wallpaper is extremely dark, we increase alpha values slightly to make the neon glow extra distinct!
                // NOTE: Must compute luma BEFORE the HSL adjustment overwrites `dominant` with a normalized pastel.
                double originalLuma = 0.299 * dominant.R + 0.587 * dominant.G + 0.114 * dominant.B;
                if (originalLuma < 160)
                {
                    borderAlpha = 0xD8; // Beautiful high-visibility glowing border outline
                    bgAlpha = 0x3E;     // Highly readable translucent selection background overlay
                    focusAlpha = 0xEA;  // Clear, distinct focus outline
                }

                // 1. Convert dominant color to HSL space to isolate Hue
                RgbToHsl(dominant, out double h, out double s, out double l);

                // 2. Mathematically optimize Saturation & Lightness to create a stunning desaturated frosted-glass glow.
                //    We preserve the wallpaper's EXACT HUE (H), but keep Saturation low (approx 35%) and Lightness high (approx 82%)
                //    so it is always towards a clean, light white-greyish side with an elegant, soft tint of the theme's color.
                s = 0.35; // Soft desaturated saturation (approx 35%) for an elegant, white-greyish tinted accent
                l = 0.82; // Bright frosted glass lightness (approx 82%) to keep selection light and high contrast

                // Convert HSL back to RGB
                dominant = HslToRgb(h, s, l);

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
            catch { } // Best-effort: failure is acceptable
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
            catch { } // Best-effort: failure is acceptable
        }

        #endregion

        #region ═══ Desktop Wallpaper Registry ═══

        /// <summary>Gets current Windows desktop wallpaper path from registry (cached).</summary>
        // [FIX M-57]: TODO: Consider consolidating with ThemeColorService.GetDesktopWallpaperPath
        // (this version includes caching via _cachedDesktopWallpaperPath field)
        private static string GetDesktopWallpaperPath()
        {
            if (_cachedDesktopWallpaperPath != null)
                return _cachedDesktopWallpaperPath;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    string wp = key?.GetValue("Wallpaper") as string ?? "";
                    if (!string.IsNullOrEmpty(wp) && System.IO.File.Exists(wp))
                    {
                        _cachedDesktopWallpaperPath = wp;
                        return _cachedDesktopWallpaperPath;
                    }
                }
            }
            catch { }

            // Fallback: TranscodedWallpaper (Windows active wallpaper cache)
            try
            {
                string transcoded = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                if (System.IO.File.Exists(transcoded))
                {
                    _cachedDesktopWallpaperPath = transcoded;
                    return _cachedDesktopWallpaperPath;
                }
            }
            catch { }

            _cachedDesktopWallpaperPath = "";
            return "";
        }

        #endregion

        #region ═══ Wallpaper Application (Static + Animated GIF) ═══

        private int _wallpaperLoadGeneration;

        private async void ApplyWallpaper()
        {
            int thisGen = ++_wallpaperLoadGeneration;
            // [FIX BTN-2]: Outer try/catch — async void must not throw unhandled exceptions.
            try
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
                catch { } // Best-effort: failure is acceptable
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
                    catch { } // Best-effort: failure is acceptable
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
                    catch { } // Best-effort: failure is acceptable
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null); // Clear any GIF

                    // PERF: BitmapImage.EndInit with UriSource performs synchronous file read —
                    // offload to background thread and freeze so result is cross-thread safe.
                    var bmp = await System.Threading.Tasks.Task.Run(() =>
                    {
                        var b = new System.Windows.Media.Imaging.BitmapImage();
                        b.BeginInit();
                        b.UriSource = new Uri(path, UriKind.Absolute);
                        b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        b.DecodePixelWidth = 800; // Panel is max 850px — 800px is plenty for a background image
                        b.EndInit();
                        b.Freeze();
                        return b;
                    });

                    // Show container layers immediately with unblurred preview to prevent flash
                    if (_wallpaperLoadGeneration != thisGen) return; // Superseded by newer load
                    WallpaperBg.Source = bmp;
                    WallpaperBg.Visibility = Visibility.Visible;

                    // STRICT BLUR RULE: Only pre-blur the custom wallpaper when blur is enabled.
                    // When blur is OFF, the wallpaper is shown crystal clear — no blur processing at all.
                    // NOTE: Wallpaper pre-blur is a software GaussianBlur (not DWM compositor),
                    // so it works regardless of Windows transparency setting — only check user pref.
                    bool wallpaperBlurEnabled = Classes.SettingsManager.Current.EnableBlurBehind;
                    if (wallpaperBlurEnabled)
                    {
                        WallpaperFrostHeader.Visibility = Visibility.Visible;

                        var capturedPathForBlur = path;
                        var bmpForBlur = bmp;
                        // Capture actual monitor DPI on UI thread — PreBlurBitmap runs on background thread
                        var dpiForWpBlur = VisualTreeHelper.GetDpi(this);
                        double wpDpiX = dpiForWpBlur.PixelsPerInchX;
                        double wpDpiY = dpiForWpBlur.PixelsPerInchY;
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                // MEMORY FIX: Single blur at radius 15, reused for both background and header.
                                // Previously created 3 separate blurred copies (radii 12, 15, 18) each allocating
                                // ~5.5MB pixel buffers = ~33MB peak. Now a single blur = ~5.5MB peak.
                                // Visual difference between radius 12–18 is imperceptible at 800px decode.
                                var blurred = PreBlurBitmap(bmpForBlur, 15, wpDpiX, wpDpiY);
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (_currentLoadedWallpaperPath != capturedPathForBlur) return; // Stale
                                    WallpaperBg.Source = blurred;
                                    WallpaperFrostImg.Source = blurred; // Reuse same blur for frost header
                                    // PreBlurredWallpaper resource removed — was never consumed by XAML or code
                                });
                            }
                            catch { } // Best-effort: failure is acceptable
                        });
                    }
                    else
                    {
                        // Crystal clear — no frost header, no pre-blur, just the sharp wallpaper
                        WallpaperFrostHeader.Visibility = Visibility.Collapsed;
                    }

                    // Extract dominant color for theme gradient asynchronously to prevent UI stutter
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            return ExtractDominantColor(bmp);
                        }
                        catch
                        {
                            return ThemeColors.IndigoAccent; // Fallback indigo
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
                                 catch { } // Best-effort: failure is acceptable
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
                catch { } // Best-effort: failure is acceptable
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(WallpaperBg, null);
                WallpaperBg.Visibility = Visibility.Collapsed;
                WallpaperThemeOverlay.Visibility = Visibility.Collapsed;
                WallpaperFrostHeader.Visibility = Visibility.Collapsed;
            }
            } // end outer try
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyWallpaper failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Quick dominant color extraction by sampling a few pixels from the center.
        /// </summary>
        // [FIX M-55]: Delegated to shared ThemeColorService
        private static Color ExtractDominantColor(System.Windows.Media.Imaging.BitmapImage bmp)
            => Services.ThemeColorService.ExtractDominantColor(bmp);

        #endregion

        #region ═══ Color Conversion Utilities ═══

        // [FIX M-56]: Delegated to shared ThemeColorService
        private static void RgbToHsl(Color rgb, out double h, out double s, out double l)
            => Services.ThemeColorService.RgbToHsl(rgb, out h, out s, out l);

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

        #endregion

        #region ═══ Bitmap Software Blur ═══

        /// <summary>
        /// Pre-renders a blurred version of a BitmapImage using a pure pixel-level box blur.
        /// Safe to call from any thread (no WPF DispatcherObjects created).
        /// Returns a frozen BitmapSource safe for cross-thread assignment to Image.Source.
        /// Uses 3-pass box blur to approximate Gaussian blur.
        /// 
        /// dpiX/dpiY: The monitor DPI to use for the output bitmap. Must be captured on the UI
        /// thread via VisualTreeHelper.GetDpi() before calling this on a background thread.
        /// Defaults to 96 for backward compatibility if not specified.
        /// </summary>
        public static System.Windows.Media.Imaging.BitmapSource PreBlurBitmap(
            System.Windows.Media.Imaging.BitmapImage source, int radius,
            double dpiX = 96, double dpiY = 96)
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];

            // CopyPixels is safe on frozen BitmapImage from any thread
            if (!source.IsFrozen && source.Dispatcher != null)
            {
                source.Dispatcher.Invoke(() =>
                {
                    var formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                    formatted.Freeze();
                    formatted.CopyPixels(pixels, stride, 0);
                });
            }
            else
            {
                var formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                formatted.Freeze();
                formatted.CopyPixels(pixels, stride, 0);
            }

            // 3-pass horizontal+vertical box blur approximates Gaussian
            // Uses sliding-window running sums for O(w·h) per pass instead of O(w·h·r)
            byte[] temp = new byte[pixels.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                // Horizontal pass
                for (int y = 0; y < h; y++)
                {
                    int rowBase = y * stride;
                    // Initialize running sums for first pixel's window [0, min(radius, w-1)]
                    int bSum = 0, gSum = 0, rSum = 0, aSum = 0;
                    int windowRight = Math.Min(radius, w - 1);
                    for (int kx = 0; kx <= windowRight; kx++)
                    {
                        int idx = rowBase + kx * 4;
                        bSum += pixels[idx];
                        gSum += pixels[idx + 1];
                        rSum += pixels[idx + 2];
                        aSum += pixels[idx + 3];
                    }

                    for (int x = 0; x < w; x++)
                    {
                        int count = Math.Min(x + radius, w - 1) - Math.Max(x - radius, 0) + 1;
                        int outIdx = rowBase + x * 4;
                        temp[outIdx]     = (byte)(bSum / count);
                        temp[outIdx + 1] = (byte)(gSum / count);
                        temp[outIdx + 2] = (byte)(rSum / count);
                        temp[outIdx + 3] = (byte)(aSum / count);

                        // Add incoming pixel on the right edge
                        int addX = x + radius + 1;
                        if (addX < w)
                        {
                            int addIdx = rowBase + addX * 4;
                            bSum += pixels[addIdx];
                            gSum += pixels[addIdx + 1];
                            rSum += pixels[addIdx + 2];
                            aSum += pixels[addIdx + 3];
                        }
                        // Remove outgoing pixel on the left edge
                        int remX = x - radius;
                        if (remX >= 0)
                        {
                            int remIdx = rowBase + remX * 4;
                            bSum -= pixels[remIdx];
                            gSum -= pixels[remIdx + 1];
                            rSum -= pixels[remIdx + 2];
                            aSum -= pixels[remIdx + 3];
                        }
                    }
                }

                // Vertical pass
                for (int x = 0; x < w; x++)
                {
                    int colBase = x * 4;
                    // Initialize running sums for first pixel's window [0, min(radius, h-1)]
                    int bSum = 0, gSum = 0, rSum = 0, aSum = 0;
                    int windowBottom = Math.Min(radius, h - 1);
                    for (int ky = 0; ky <= windowBottom; ky++)
                    {
                        int idx = ky * stride + colBase;
                        bSum += temp[idx];
                        gSum += temp[idx + 1];
                        rSum += temp[idx + 2];
                        aSum += temp[idx + 3];
                    }

                    for (int y = 0; y < h; y++)
                    {
                        int count = Math.Min(y + radius, h - 1) - Math.Max(y - radius, 0) + 1;
                        int outIdx = y * stride + colBase;
                        pixels[outIdx]     = (byte)(bSum / count);
                        pixels[outIdx + 1] = (byte)(gSum / count);
                        pixels[outIdx + 2] = (byte)(rSum / count);
                        pixels[outIdx + 3] = (byte)(aSum / count);

                        // Add incoming pixel on the bottom edge
                        int addY = y + radius + 1;
                        if (addY < h)
                        {
                            int addIdx = addY * stride + colBase;
                            bSum += temp[addIdx];
                            gSum += temp[addIdx + 1];
                            rSum += temp[addIdx + 2];
                            aSum += temp[addIdx + 3];
                        }
                        // Remove outgoing pixel on the top edge
                        int remY = y - radius;
                        if (remY >= 0)
                        {
                            int remIdx = remY * stride + colBase;
                            bSum -= temp[remIdx];
                            gSum -= temp[remIdx + 1];
                            rSum -= temp[remIdx + 2];
                            aSum -= temp[remIdx + 3];
                        }
                    }
                }
            }

            // Create frozen WriteableBitmap from blurred pixel data using actual monitor DPI
            var result = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, dpiX, dpiY, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        #endregion

        #region ═══ Desktop Wallpaper Auto-Refresh ═══

        /// <summary>
        /// Checks if the Windows desktop wallpaper has changed since the last time we loaded it,
        /// and if so, auto-applies the new wallpaper immediately.
        /// Call this on every window activation (OnActivated) when in FlyShelf/desktop mode.
        /// The registry read is instant (~0ms) so this is safe to call frequently.
        /// </summary>
        private void RefreshDesktopWallpaperIfChanged()
        {
            try
            {
                string mode = Classes.SettingsManager.Current.ThemeDisplayMode ?? "desktop";
                if (mode != "desktop") return;

                // If user has manually set a wallpaper, don't override it
                string manualWp = Classes.SettingsManager.Current.ManualWallpaperPath ?? "";
                if (!string.IsNullOrEmpty(manualWp) && System.IO.File.Exists(manualWp)) return;

                // If a color theme wallpaper is active, don't override it
                string currentWp = Classes.SettingsManager.Current.ClipboardWallpaperPath ?? "";
                if (currentWp.Contains("ColorThemeWallpapers", StringComparison.OrdinalIgnoreCase)) return;

                // Read wallpaper path fresh from registry (bypass cache)
                string freshWp = GetDesktopWallpaperPathUncached();
                if (string.IsNullOrEmpty(freshWp) || !System.IO.File.Exists(freshWp)) return;

                // Check if wallpaper actually changed
                if (string.Equals(freshWp, currentWp, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(freshWp, _currentLoadedWallpaperPath, StringComparison.OrdinalIgnoreCase))
                    return; // Same wallpaper — nothing to do

                // Wallpaper has changed — apply it
                _cachedDesktopWallpaperPath = freshWp;
                Classes.SettingsManager.Current.ClipboardWallpaperPath = freshWp;
                _currentLoadedWallpaperPath = ""; // Force reload
                ApplyWallpaper();
                Classes.Logger.LogAction("WALLPAPER", $"Auto-refreshed desktop wallpaper: {freshWp}");
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Reads the desktop wallpaper path directly from the registry, bypassing the cache.
        /// Used by RefreshDesktopWallpaperIfChanged for instant change detection.
        /// </summary>
        private static string GetDesktopWallpaperPathUncached()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    string wp = key?.GetValue("Wallpaper") as string ?? "";
                    if (!string.IsNullOrEmpty(wp) && System.IO.File.Exists(wp))
                        return wp;
                }
            }
            catch { }

            try
            {
                string transcoded = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                if (System.IO.File.Exists(transcoded))
                    return transcoded;
            }
            catch { }

            return "";
        }

        // ═══ TranscodedWallpaper File Watcher ═══
        // Windows writes the active wallpaper image to %APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper
        // every time the wallpaper changes. FileSystemWatcher catches Spotlight, slideshow, Bing, etc.
        // that WM_SETTINGCHANGE sometimes misses.

        private System.IO.FileSystemWatcher? _wallpaperFileWatcher;
        private System.IO.FileSystemWatcher? _wallpaperCachedFilesWatcher;
        private System.Windows.Threading.DispatcherTimer? _wallpaperDebounceTimer;

        /// <summary>
        /// Start monitoring the TranscodedWallpaper file and CachedFiles subfolder for changes.
        /// Call once during initialization.
        /// </summary>
        internal void StartWallpaperFileWatcher()
        {
            try
            {
                string themesDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes");

                if (!System.IO.Directory.Exists(themesDir)) return;

                _wallpaperFileWatcher = new System.IO.FileSystemWatcher(themesDir)
                {
                    Filter = "TranscodedWallpaper",
                    NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _wallpaperFileWatcher.Changed += OnWallpaperFileChanged;

                // Watch the CachedFiles subfolder (Windows 10/11 puts slideshow wallpapers here)
                string cachedFilesDir = System.IO.Path.Combine(themesDir, "CachedFiles");
                if (System.IO.Directory.Exists(cachedFilesDir))
                {
                    _wallpaperCachedFilesWatcher = new System.IO.FileSystemWatcher(cachedFilesDir)
                    {
                        Filter = "*",
                        NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    _wallpaperCachedFilesWatcher.Changed += OnWallpaperFileChanged;
                    _wallpaperCachedFilesWatcher.Created += OnWallpaperFileChanged;
                }

                // Debounce timer — 800ms delay to coalesce rapid filesystem events
                _wallpaperDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(800)
                };
                _wallpaperDebounceTimer.Tick += (s, e) =>
                {
                    _wallpaperDebounceTimer.Stop();
                    RefreshDesktopWallpaperIfChanged();
                };
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WALLPAPER", $"File watcher init failed: {ex.Message}");
            }
        }

        private void OnWallpaperFileChanged(object sender, System.IO.FileSystemEventArgs e)
        {
            // FileSystemWatcher fires on a background thread — marshal to UI thread
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    // Clear cached path so GetDesktopWallpaperPath reads fresh from registry
                    _cachedDesktopWallpaperPath = null;

                    // Restart debounce timer (coalesces rapid filesystem events)
                    _wallpaperDebounceTimer?.Stop();
                    _wallpaperDebounceTimer?.Start();
                });
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Stops the wallpaper file watcher and debounce timer.
        /// </summary>
        internal void StopWallpaperFileWatcher()
        {
            try
            {
                _wallpaperDebounceTimer?.Stop();
                _wallpaperDebounceTimer = null;

                if (_wallpaperFileWatcher != null)
                {
                    _wallpaperFileWatcher.EnableRaisingEvents = false;
                    _wallpaperFileWatcher.Changed -= OnWallpaperFileChanged;
                    _wallpaperFileWatcher.Dispose();
                    _wallpaperFileWatcher = null;
                }

                if (_wallpaperCachedFilesWatcher != null)
                {
                    _wallpaperCachedFilesWatcher.EnableRaisingEvents = false;
                    _wallpaperCachedFilesWatcher.Changed -= OnWallpaperFileChanged;
                    _wallpaperCachedFilesWatcher.Created -= OnWallpaperFileChanged;
                    _wallpaperCachedFilesWatcher.Dispose();
                    _wallpaperCachedFilesWatcher = null;
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        #endregion
    }
}

