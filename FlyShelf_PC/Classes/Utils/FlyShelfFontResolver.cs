// ═══════════════════════════════════════════════════════════════════════
// FlyShelfFontResolver.cs — Native High-Speed Font Resolver for PDFsharp 6.x
// Maps system fonts (Segoe UI, Arial, Consolas, Calibri, etc.) directly
// from Windows Fonts directory with zero external native DLL dependencies.
// Features thread-safe ConcurrentDictionary caching for lightning-fast batch conversions.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.IO;
using PdfSharp.Fonts;

namespace FlyShelf.Classes.Utils
{
    public class FlyShelfFontResolver : IFontResolver
    {
        private static bool _isRegistered = false;
        private static readonly object _lock = new object();
        private static readonly ConcurrentDictionary<string, byte[]> _fontCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Ensures the font resolver is registered globally with PDFsharp once.
        /// </summary>
        public static void EnsureRegistered()
        {
            if (_isRegistered) return;
            lock (_lock)
            {
                if (!_isRegistered)
                {
                    try
                    {
                        GlobalFontSettings.FontResolver = new FlyShelfFontResolver();
                        _isRegistered = true;
                    }
                    catch { }
                }
            }
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string lower = familyName?.ToLowerInvariant() ?? "";
            string suffix = "";
            if (isBold && isItalic) suffix = "bi";
            else if (isBold) suffix = "bd";
            else if (isItalic) suffix = "i";

            if (lower.Contains("consolas") || lower.Contains("monospace") || lower.Contains("courier") || lower.Contains("code"))
                return new FontResolverInfo("consolas" + suffix);

            if (lower.Contains("arial") || lower.Contains("helvetica") || lower.Contains("sans-serif"))
                return new FontResolverInfo("arial" + suffix);

            if (lower.Contains("calibri"))
                return new FontResolverInfo("calibri" + suffix);

            if (lower.Contains("times") || lower.Contains("serif") || lower.Contains("georgia"))
                return new FontResolverInfo("times" + suffix);

            // Default to Segoe UI (standard modern Windows UI font)
            return new FontResolverInfo("segoe" + suffix);
        }

        public byte[] GetFont(string faceName)
        {
            string lowerFace = faceName?.ToLowerInvariant() ?? "";
            if (_fontCache.TryGetValue(lowerFace, out var cachedBytes))
            {
                return cachedBytes;
            }

            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            // M6 fix: Also check per-user font directory (Windows 10 1809+)
            string userFontsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");

            string fileName = lowerFace switch
            {
                "segoebd" => "segoeuib.ttf",
                "segoei" => "segoeuii.ttf",
                "segoebi" => "segoeuiz.ttf",
                "segoe" => "segoeui.ttf",
                "arialbd" => "arialbd.ttf",
                "ariali" => "ariali.ttf",
                "arialbi" => "arialbi.ttf",
                "arial" => "arial.ttf",
                "consolasbd" => "consolab.ttf",
                "consolasi" => "consolai.ttf",
                "consolasbi" => "consolaz.ttf",
                "consolas" => "consola.ttf",
                "calibribd" => "calibrib.ttf",
                "calibrii" => "calibrii.ttf",
                "calibribi" => "calibriz.ttf",
                "calibri" => "calibri.ttf",
                "timesbd" => "timesbd.ttf",
                "timesi" => "timesi.ttf",
                "timesbi" => "timesbi.ttf",
                "times" => "times.ttf",
                _ => "segoeui.ttf"
            };

            // Try both system and user font directories
            string fullPath = Path.Combine(fontsDir, fileName);
            if (!File.Exists(fullPath))
            {
                string userPath = Path.Combine(userFontsDir, fileName);
                if (File.Exists(userPath)) fullPath = userPath;
            }

            if (!File.Exists(fullPath))
            {
                // M4 fix: Try the base (regular) variant of the same family before generic fallback
                string baseFontFile = null;
                if (lowerFace.StartsWith("consolas")) baseFontFile = "consola.ttf";
                else if (lowerFace.StartsWith("calibri")) baseFontFile = "calibri.ttf";
                else if (lowerFace.StartsWith("times")) baseFontFile = "times.ttf";
                else if (lowerFace.StartsWith("arial")) baseFontFile = "arial.ttf";
                else if (lowerFace.StartsWith("segoe")) baseFontFile = "segoeui.ttf";

                if (baseFontFile != null)
                {
                    var basePath = Path.Combine(fontsDir, baseFontFile);
                    if (File.Exists(basePath)) fullPath = basePath;
                    else
                    {
                        basePath = Path.Combine(userFontsDir, baseFontFile);
                        if (File.Exists(basePath)) fullPath = basePath;
                    }
                }
            }

            if (!File.Exists(fullPath))
            {
                // Generic fallback chain
                string[] fallbacks = { "segoeui.ttf", "arial.ttf", "tahoma.ttf", "times.ttf", "calibri.ttf" };
                foreach (var fb in fallbacks)
                {
                    string fbPath = Path.Combine(fontsDir, fb);
                    if (File.Exists(fbPath)) { fullPath = fbPath; break; }
                    fbPath = Path.Combine(userFontsDir, fb);
                    if (File.Exists(fbPath)) { fullPath = fbPath; break; }
                }
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(fullPath);
                    if (bytes.Length > 0)
                    {
                        _fontCache[lowerFace] = bytes;
                        return bytes;
                    }
                }
                catch { }
            }

            // Ultimate fallback: Find ANY valid .ttf font in Windows Fonts directory
            foreach (var dir in new[] { fontsDir, userFontsDir })
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var anyFont = Directory.EnumerateFiles(dir, "*.ttf").FirstOrDefault();
                        if (anyFont != null)
                        {
                            byte[] bytes = File.ReadAllBytes(anyFont);
                            if (bytes.Length > 0)
                            {
                                _fontCache[lowerFace] = bytes;
                                return bytes;
                            }
                        }
                    }
                }
                catch { }
            }

            // C3 fix: Throw instead of returning empty byte[] which crashes PDFsharp font parser
            Logger.LogAction("FONT_RESOLVE_FAIL", $"Could not resolve font '{faceName}' — no TTF fonts found.");
            throw new InvalidOperationException($"FlyShelf FontResolver: Cannot resolve font '{faceName}'. No TrueType fonts found in system or user font directories.");
        }
    }
}
