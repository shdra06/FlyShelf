// ═══════════════════════════════════════════════════════════════════════
// PdfToImageExporter.cs — High-resolution PDF page extraction to PNG/JPEG
// Uses native Windows.Data.Pdf runtime engine for pristine rendering.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WinPdf = global::Windows.Data.Pdf;
using global::Windows.Storage;
using global::Windows.Storage.Streams;

namespace FlyShelf.Classes.Utils
{
    public static class PdfToImageExporter
    {
        /// <summary>
        /// Renders all pages of a PDF to high-resolution PNG image files.
        /// </summary>
        public static async Task<List<string>> ExportPagesToImagesAsync(
            string pdfPath,
            string outputDirectory = null,
            uint destinationWidth = 2048,
            int maxPages = 100)
        {
            var outputFiles = new List<string>();
            if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return outputFiles;

            if (string.IsNullOrEmpty(outputDirectory))
            {
                outputDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "FlyShelf", "ExtractedImages");
            }
            Directory.CreateDirectory(outputDirectory);

            string baseName = Path.GetFileNameWithoutExtension(pdfPath);

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(pdfPath);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                uint pagesToRender = Math.Min(pdfDoc.PageCount, (uint)maxPages);

                for (uint i = 0; i < pagesToRender; i++)
                {
                    using var page = pdfDoc.GetPage(i);
                    using var stream = new InMemoryRandomAccessStream();
                    
                    var options = new WinPdf.PdfPageRenderOptions
                    {
                        DestinationWidth = destinationWidth,
                        BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                    };

                    await page.RenderToStreamAsync(stream, options);

                    string outFilePath = Path.Combine(outputDirectory, $"{baseName}_Page_{i + 1:D2}.png");

                    // Read from UWP stream and save to standard file
                    using (var netStream = stream.AsStream())
                    using (var fileStream = new FileStream(outFilePath, FileMode.Create, FileAccess.Write))
                    {
                        netStream.Seek(0, SeekOrigin.Begin);
                        await netStream.CopyToAsync(fileStream);
                    }

                    if (File.Exists(outFilePath))
                    {
                        outputFiles.Add(outFilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PDF_TO_IMG_ERR", $"Error exporting {pdfPath}: {ex.Message}");
            }

            return outputFiles;
        }

        /// <summary>
        /// Renders a single page of a PDF to a high-resolution PNG image.
        /// </summary>
        public static async Task<string> ExportSinglePageToImageAsync(
            string pdfPath,
            int pageIndex,
            string outputDirectory = null,
            uint destinationWidth = 2048)
        {
            if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return null;

            if (string.IsNullOrEmpty(outputDirectory))
            {
                outputDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "FlyShelf", "ExtractedImages");
            }
            Directory.CreateDirectory(outputDirectory);

            string baseName = Path.GetFileNameWithoutExtension(pdfPath);
            string outFilePath = Path.Combine(outputDirectory, $"{baseName}_Page_{pageIndex + 1:D2}.png");

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(pdfPath);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                if (pageIndex < 0 || pageIndex >= pdfDoc.PageCount) return null;

                using var page = pdfDoc.GetPage((uint)pageIndex);
                using var stream = new InMemoryRandomAccessStream();

                var options = new WinPdf.PdfPageRenderOptions
                {
                    DestinationWidth = destinationWidth,
                    BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                };

                await page.RenderToStreamAsync(stream, options);

                using (var netStream = stream.AsStream())
                using (var fileStream = new FileStream(outFilePath, FileMode.Create, FileAccess.Write))
                {
                    netStream.Seek(0, SeekOrigin.Begin);
                    await netStream.CopyToAsync(fileStream);
                }

                return File.Exists(outFilePath) ? outFilePath : null;
            }
            catch (Exception ex)
            {
                Logger.LogAction("PDF_SINGLE_IMG_ERR", ex.Message);
                return null;
            }
        }
    }
}
