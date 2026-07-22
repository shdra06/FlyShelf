// ---------------------------------------------------------------
// ThemeColorService — Pure color computation utilities.
// Extracted from MainWindow.Theme.cs — no UI dependencies.
// Can be unit-tested and reused across windows.
// ---------------------------------------------------------------
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.Helpers;

namespace FlyShelf.Services
{
    /// <summary>
    /// Pure color computation utilities extracted from MainWindow.Theme.cs.
    /// All methods are static and thread-safe (no UI dependencies).
    /// </summary>
    public static class ThemeColorService
    {
        /// <summary>
        /// Quick dominant color extraction by sampling 9 pixels from the center of a bitmap.
        /// </summary>
        public static Color ExtractDominantColor(BitmapImage bmp)
        {
            try
            {
                var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                formatted.Freeze();
                int w = formatted.PixelWidth;
                int h = formatted.PixelHeight;

                int totalR = 0, totalG = 0, totalB = 0, count = 0;
                int[] xs = { w / 4, w / 2, 3 * w / 4 };
                int[] ys = { h / 4, h / 2, 3 * h / 4 };
                byte[] singlePixel = new byte[4];

                foreach (int x in xs)
                    foreach (int y in ys)
                    {
                        if (x >= 0 && x < w && y >= 0 && y < h)
                        {
                            formatted.CopyPixels(
                                new System.Windows.Int32Rect(x, y, 1, 1),
                                singlePixel, 4, 0);
                            totalB += singlePixel[0];
                            totalG += singlePixel[1];
                            totalR += singlePixel[2];
                            count++;
                        }
                    }

                if (count > 0)
                    return Color.FromRgb((byte)(totalR / count), (byte)(totalG / count), (byte)(totalB / count));
            }
            catch { } // Best-effort: failure is acceptable

            return ThemeColors.IndigoAccent; // Fallback indigo
        }

        /// <summary>Converts an RGB color to HSL components.</summary>
        public static void RgbToHsl(Color rgb, out double h, out double s, out double l)
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
                    h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g)
                    h = (b - r) / d + 2;
                else if (max == b)
                    h = (r - g) / d + 4;

                h /= 6.0;
            }
        }

        /// <summary>Converts HSL components to an RGB color.</summary>
        public static Color HslToRgb(double h, double s, double l)
        {
            double r = l, g = l, b = l;

            if (s != 0)
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;

                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }

            return Color.FromRgb(
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(b * 255));
        }

        /// <summary>Helper for HSL-to-RGB conversion.</summary>
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
        /// Gets the current desktop wallpaper path from the Windows registry.
        /// </summary>
        public static string GetDesktopWallpaperPath()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop");
                return key?.GetValue("Wallpaper") as string ?? "";
            }
            catch { return ""; }
        }
    }
}
