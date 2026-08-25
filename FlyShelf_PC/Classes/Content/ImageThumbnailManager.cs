using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// High-performance thumbnail generation and image loader supporting standard raster formats
    /// (PNG, JPEG, GIF, BMP, WEBP, ICO, TIFF) and vector SVG formats via SharpVectors.
    /// </summary>
    public static class ImageThumbnailManager
    {
        private static readonly string[] _imageExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff", ".tif", ".heic", ".heif"
        };

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
        /// </summary>
        public static BitmapSource? LoadThumbnail(string? filePath, int decodeWidth = 300)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                // 1. Vector SVG rendering
                if (ext == ".svg")
                {
                    return RenderSvgFromFile(filePath, decodeWidth, decodeWidth);
                }

                // 2. Standard raster image loading (PNG, JPG, BMP, WEBP, GIF, ICO, TIFF)
                var fi = new FileInfo(filePath);
                if (fi.Length > 80_000_000) // 80 MB cap for thumbnails
                {
                    Logger.LogAction("THUMB_SKIP", $"File too large ({fi.Length} bytes): {filePath}");
                    return null;
                }

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
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.LogAction("THUMB_LOAD_ERR", $"Raster load failed for {filePath}: {ex.Message}");

                // Fallback to Shell Icon if direct decode failed
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
    }
}
