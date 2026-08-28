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

            string fullPath = Path.Combine(fontsDir, fileName);
            if (!File.Exists(fullPath))
            {
                // Fallbacks in order: Arial -> Segoe UI
                fullPath = Path.Combine(fontsDir, "arial.ttf");
                if (!File.Exists(fullPath)) fullPath = Path.Combine(fontsDir, "segoeui.ttf");
            }

            if (File.Exists(fullPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(fullPath);
                    _fontCache[lowerFace] = bytes;
                    return bytes;
                }
                catch { }
            }

            return null;
        }
    }
}
