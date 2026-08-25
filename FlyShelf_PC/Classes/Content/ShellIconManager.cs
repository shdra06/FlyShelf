using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Centralized high-performance Shell Icon Manager.
    /// Pre-caches and extracts the operating system's registered default application icons
    /// for common file types (APK, PDF, DOCX, ZIP, Media, etc.) on application startup.
    /// </summary>
    public static class ShellIconManager
    {
        private static readonly ConcurrentDictionary<string, BitmapSource?> _extensionIconCache = new(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _isWarmedUp = false;
        private static readonly object _warmupLock = new();

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        /// <summary>
        /// Common file extensions to pre-cache on application startup.
        /// </summary>
        private static readonly string[] s_commonExtensions = new[]
        {
            // Android & Mobile Packages
            ".apk", ".aab", ".xapk", ".apks", ".ipa",
            
            // Documents & PDFs
            ".pdf", ".docx", ".doc", ".txt", ".rtf", ".odt", ".epub", ".pages",
            
            // Spreadsheets & Data
            ".xlsx", ".xls", ".csv", ".ods", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".sql",
            
            // Presentations
            ".pptx", ".ppt", ".odp", ".key",
            
            // Archives & Compressed
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".dmg", ".pkg", ".tgz", ".zst",
            
            // Executables & Scripts
            ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".sh",
            
            // Media (Video & Audio)
            ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v",
            ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma",
            
            // Code & Development
            ".cs", ".cpp", ".c", ".h", ".hpp", ".py", ".js", ".ts", ".tsx", ".jsx",
            ".java", ".kt", ".rs", ".go", ".php", ".rb", ".swift", ".dart",
            ".html", ".htm", ".css", ".scss",
            
            // Images
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff", ".heic", ".psd", ".ai"
        };

        /// <summary>
        /// Asynchronously warms up all default file icons on application startup.
        /// Performs one-time OS shell queries and stores frozen BitmapSources in memory.
        /// </summary>
        public static void WarmupCommonIcons()
        {
            if (_isWarmedUp) return;

            Task.Run(() =>
            {
                lock (_warmupLock)
                {
                    if (_isWarmedUp) return;
                    try
                    {
                        foreach (string ext in s_commonExtensions)
                        {
                            if (!_extensionIconCache.ContainsKey(ext))
                            {
                                var icon = ExtractShellIconForExtension(ext);
                                if (icon != null)
                                {
                                    _extensionIconCache.TryAdd(ext, icon);
                                }
                            }
                        }
                        _isWarmedUp = true;
                        Logger.LogAction("SHELL_ICON", $"Pre-cached {_extensionIconCache.Count} default file icons on startup");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("SHELL_ICON_ERR", $"Warmup failed: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Gets the default application icon for a given file path or extension.
        /// Guaranteed thread-safe, non-blocking, and returns a frozen BitmapSource.
        /// </summary>
        public static BitmapSource? GetIcon(string? filePath, string? extension = null)
        {
            string ext = "";
            if (!string.IsNullOrEmpty(filePath))
            {
                try { ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? ""; } catch { }
            }
            if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(extension))
            {
                ext = extension.StartsWith(".", StringComparison.Ordinal) ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
            }

            // 1. If file exists on physical disk, try querying the real file first (e.g. custom EXE/DLL icons or per-file icons)
            if (!string.IsNullOrEmpty(filePath) && (ext is ".exe" or ".ico" or ".lnk" || (File.Exists(filePath) && ext != ".pdf" && ext != ".docx")))
            {
                try
                {
                    var shinfo = new NativeMethods.SHFILEINFO();
                    IntPtr res = NativeMethods.SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);
                    if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        try
                        {
                            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                                shinfo.hIcon,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            bitmapSource.Freeze();
                            if (!string.IsNullOrEmpty(ext) && ext != ".exe")
                            {
                                _extensionIconCache.TryAdd(ext, bitmapSource);
                            }
                            return bitmapSource;
                        }
                        finally
                        {
                            NativeMethods.DestroyIcon(shinfo.hIcon);
                        }
                    }
                }
                catch { }
            }

            // 2. Check the in-memory extension cache (0ms lookup)
            if (!string.IsNullOrEmpty(ext))
            {
                if (_extensionIconCache.TryGetValue(ext, out var cached))
                {
                    return cached;
                }

                // 3. Not cached yet — extract via SHGetFileInfo with SHGFI_USEFILEATTRIBUTES
                var extracted = ExtractShellIconForExtension(ext);
                if (extracted != null)
                {
                    _extensionIconCache.TryAdd(ext, extracted);
                    return extracted;
                }
            }

            return null;
        }

        private static BitmapSource? ExtractShellIconForExtension(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return null;
            if (!ext.StartsWith(".", StringComparison.Ordinal)) ext = "." + ext;

            try
            {
                string dummyFile = "flyshelf_probe" + ext;
                var shinfo = new NativeMethods.SHFILEINFO();
                IntPtr res = NativeMethods.SHGetFileInfo(
                    dummyFile,
                    FILE_ATTRIBUTE_NORMAL,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze();
                        return bitmapSource;
                    }
                    finally
                    {
                        NativeMethods.DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SHELL_ICON", $"Extraction failed for {ext}: {ex.Message}");
            }

            return null;
        }
    }
}
