using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// High-performance thumbnail generation and image loader supporting standard raster formats
    /// (PNG, JPEG, GIF, BMP, WEBP, ICO, TIFF, HEIC) and vector SVG formats via SharpVectors.
    /// Features an ultra-fast in-memory ConcurrentDictionary cache to guarantee instant, flicker-free rendering.
    /// </summary>
    public static class ImageThumbnailManager
    {
        private static readonly string[] _imageExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff", ".tif", ".heic", ".heif"
        };

        private record CachedThumbnail(BitmapSource Bitmap, long LastWriteTicks, long FileLength, int DecodeWidth);
        private static readonly ConcurrentDictionary<string, CachedThumbnail> _thumbnailCache =
            new ConcurrentDictionary<string, CachedThumbnail>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Checks if a file extension corresponds to a supported image type.
        /// </summary>
        public static bool IsImageExtension(string? ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            string cleanExt = ext.StartsWith(".", StringComparison.Ordinal) ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
            return Array.IndexOf(_imageExtensions, cleanExt) >= 0;
        }

        /// <summary>
        /// Checks if raw text is valid SVG markup.
        /// </summary>
        public static bool IsSvgMarkup(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            return (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || 
                    (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
                   && trimmed.Contains("</svg>", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Loads an image file as a thumbnail BitmapSource. Supports both raster images and SVGs.
        /// Guaranteed thread-safe with in-memory caching and multi-tier decoder fallbacks.
        /// </summary>
        public static BitmapSource? LoadThumbnail(string? filePath, int decodeWidth = 300)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var fi = new FileInfo(filePath);
                long currentTicks = fi.LastWriteTimeUtc.Ticks;
                long currentLength = fi.Length;

                if (currentLength > 80_000_000) // 80 MB cap for thumbnails
                {
                    Logger.LogAction("THUMB_SKIP", $"File too large ({currentLength} bytes): {filePath}");
                    return null;
                }

                // 1. Check in-memory cache
                if (_thumbnailCache.TryGetValue(filePath, out var cached))
                {
                    if (cached.LastWriteTicks == currentTicks && cached.FileLength == currentLength && (cached.DecodeWidth >= decodeWidth || decodeWidth <= 0))
                    {
                        return cached.Bitmap;
                    }
                }

                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                // 2. Vector SVG rendering
                if (ext == ".svg")
                {
                    var svgBmp = RenderSvgFromFile(filePath, decodeWidth, decodeWidth);
                    if (svgBmp != null)
                    {
                        _thumbnailCache[filePath] = new CachedThumbnail(svgBmp, currentTicks, currentLength, decodeWidth);
                    }
                    return svgBmp;
                }

                // 3. Standard raster image loading (PNG, JPG, BMP, WEBP, GIF, ICO, TIFF)
                BitmapSource? resultBmp = null;

                try
                {
                    var bmp = new BitmapImage();
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bmp.BeginInit();
                        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bmp.StreamSource = fs;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        if (decodeWidth > 0)
                        {
                            bmp.DecodePixelWidth = decodeWidth;
                        }
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    resultBmp = bmp;
                }
                catch
                {
                    // Fallback to BitmapDecoder for CMYK, progressive, or unusual color spaces
                    try
                    {
                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count > 0)
                        {
                            var frame = decoder.Frames[0];
                            if (decodeWidth > 0 && frame.PixelWidth > decodeWidth)
                            {
                                double scale = (double)decodeWidth / frame.PixelWidth;
                                var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                                transformed.Freeze();
                                resultBmp = transformed;
                            }
                            else
                            {
                                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                                converted.Freeze();
                                resultBmp = converted;
                            }
                        }
                    }
                    catch { }
                }

                if (resultBmp != null)
                {
                    _thumbnailCache[filePath] = new CachedThumbnail(resultBmp, currentTicks, currentLength, decodeWidth);
                    return resultBmp;
                }

                // Fallback to Shell Icon if all decoders failed
                var shellIcon = ShellIconManager.GetIcon(filePath);
                if (shellIcon != null)
                {
                    _thumbnailCache[filePath] = new CachedThumbnail(shellIcon, currentTicks, currentLength, decodeWidth);
                }
                return shellIcon;
            }
            catch (Exception ex)
            {
                Logger.LogAction("THUMB_LOAD_ERR", $"Raster load failed for {filePath}: {ex.Message}");
                try
                {
                    return ShellIconManager.GetIcon(filePath);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Renders an SVG file to a frozen BitmapSource at the specified pixel dimensions.
        /// </summary>
        public static BitmapSource? RenderSvgFromFile(string filePath, int targetWidth = 300, int targetHeight = 300)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var settings = new SharpVectors.Renderers.Wpf.WpfDrawingSettings
                {
                    IncludeRuntime = false,
                    TextAsGeometry = true
                };

                var reader = new SharpVectors.Converters.FileSvgReader(settings);
                DrawingGroup? drawing = reader.Read(filePath);
                if (drawing == null) return null;

                return ConvertDrawingToBitmap(drawing, targetWidth, targetHeight);
            }
            catch (Exception ex)
            {
                Logger.LogAction("SVG_RENDER_ERR", $"Failed to render SVG file {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Renders raw SVG markup XML into a frozen BitmapSource.
        /// </summary>
        public static BitmapSource? RenderSvgFromMarkup(string svgMarkup, int targetWidth = 300, int targetHeight = 300)
        {
            if (string.IsNullOrWhiteSpace(svgMarkup)) return null;

            try
            {
                var settings = new SharpVectors.Renderers.Wpf.WpfDrawingSettings
                {
                    IncludeRuntime = false,
                    TextAsGeometry = true
                };

                var reader = new SharpVectors.Converters.FileSvgReader(settings);
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgMarkup));
                DrawingGroup? drawing = reader.Read(stream);
                if (drawing == null) return null;

                return ConvertDrawingToBitmap(drawing, targetWidth, targetHeight);
            }
            catch (Exception ex)
            {
                Logger.LogAction("SVG_RENDER_ERR", $"Failed to render SVG markup: {ex.Message}");
                return null;
            }
        }

        private static BitmapSource? ConvertDrawingToBitmap(DrawingGroup drawing, int targetWidth, int targetHeight)
        {
            try
            {
                Rect bounds = drawing.Bounds;
                double w = bounds.Width > 0 ? bounds.Width : targetWidth;
                double h = bounds.Height > 0 ? bounds.Height : targetHeight;

                double scale = Math.Min((double)targetWidth / w, (double)targetHeight / h);
                int pixelW = Math.Max(16, (int)Math.Round(w * scale));
                int pixelH = Math.Max(16, (int)Math.Round(h * scale));

                var drawingVisual = new DrawingVisual();
                using (DrawingContext dc = drawingVisual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform((double)pixelW / w, (double)pixelH / h));
                    dc.DrawDrawing(drawing);
                    dc.Pop();
                }

                var rtb = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(drawingVisual);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                Logger.LogAction("SVG_CONV_ERR", $"DrawingGroup to Bitmap conversion error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears the in-memory thumbnail cache.
        /// </summary>
        public static void ClearCache()
        {
            _thumbnailCache.Clear();
        }
    }
}
