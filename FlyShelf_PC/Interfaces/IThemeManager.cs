// ---------------------------------------------------------------
// IThemeManager — Interface for theme and appearance management.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
using System.Windows.Media;

namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Manages application themes, wallpapers, and color schemes.
    /// </summary>
    public interface IThemeManager
    {
        string CurrentThemeName { get; }
        bool IsDarkMode { get; }
        void ApplyTheme(string themeName);
        void RefreshTheme();

        // Color utilities (extracted from MainWindow.Theme.cs)
        Color ExtractDominantColor(System.Windows.Media.Imaging.BitmapImage bmp);
        void RgbToHsl(Color rgb, out double h, out double s, out double l);
        Color HslToRgb(double h, double s, double l);
        string GetDesktopWallpaperPath();
    }
}
