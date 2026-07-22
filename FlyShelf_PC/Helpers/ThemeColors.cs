using System.Windows.Media;

namespace FlyShelf.Helpers
{
    /// <summary>
    /// Centralized color constants used across code-built UI.
    /// Provides named, semantic colors for consistency and easy theming.
    /// </summary>
    internal static class ThemeColors
    {
        // ═══ Dark backgrounds ═══
        internal static readonly Color NetworkCardBg          = Color.FromRgb(0x0E, 0x13, 0x26);
        internal static readonly Color NetworkCardBorder      = Color.FromRgb(0x1E, 0x29, 0x3B);
        internal static readonly Color CatppuccinSurface      = Color.FromRgb(0x1E, 0x1E, 0x2E);
        internal static readonly Color DarkSurface            = Color.FromRgb(0x1A, 0x1A, 0x2E);
        internal static readonly Color NavyDark               = Color.FromRgb(0x1A, 0x1F, 0x3D);
        internal static readonly Color DarkGray25             = Color.FromRgb(25, 25, 25);
        internal static readonly Color DarkGray60             = Color.FromRgb(60, 60, 60);

        // ═══ Indigo / brand palette ═══
        internal static readonly Color IndigoAccent           = Color.FromRgb(99, 102, 241);      // Indigo-500
        internal static readonly Color IndigoMid              = Color.FromRgb(0x63, 0x66, 0xF1);
        internal static readonly Color IndigoDeep             = Color.FromRgb(0x31, 0x2E, 0x81);
        internal static readonly Color IndigoLight            = Color.FromRgb(0x81, 0x8C, 0xF8);

        // ═══ Accent colors ═══
        internal static readonly Color VioletAccent           = Color.FromRgb(0x8B, 0x5C, 0xF6);  // Violet-500
        internal static readonly Color VioletLight            = Color.FromRgb(0xA7, 0x8B, 0xFA);  // Violet-400
        internal static readonly Color Blue500                = Color.FromRgb(0x3B, 0x82, 0xF6);

        // ═══ Status colors ═══
        internal static readonly Color ErrorRed               = Color.FromRgb(0xEF, 0x44, 0x44);  // Red-500
        internal static readonly Color SuccessGreen           = Color.FromRgb(0x10, 0xB9, 0x81);  // Emerald-500
        internal static readonly Color WarningAmber           = Color.FromRgb(0xF5, 0x9E, 0x0B);  // Amber-500
        internal static readonly Color AmberYellow            = Color.FromRgb(234, 179, 8);       // Yellow-600

        // ═══ Text / UI chrome ═══
        internal static readonly Color CatppuccinText         = Color.FromRgb(0xCD, 0xD6, 0xF4);
        internal static readonly Color LightSlate             = Color.FromRgb(0xE2, 0xE8, 0xF0);  // Slate-200
        internal static readonly Color LightLavender          = Color.FromRgb(0xE8, 0xE8, 0xF0);
        internal static readonly Color SlateGray              = Color.FromRgb(0x64, 0x74, 0x8B);  // Slate-500
        internal static readonly Color SlateDark              = Color.FromRgb(0x47, 0x55, 0x69);  // Slate-600
        internal static readonly Color GrayMuted              = Color.FromRgb(156, 163, 175);     // Gray-400

        // ═══ Semi-transparent accent overlays (violet) ═══
        internal static readonly Color VioletAccentA60        = Color.FromArgb(0x60, 0x8B, 0x5C, 0xF6);
        internal static readonly Color VioletAccentA40        = Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6);
        internal static readonly Color VioletAccentA2A        = Color.FromArgb(0x2A, 0x8B, 0x5C, 0xF6);
    }
}
