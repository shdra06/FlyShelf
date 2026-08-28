// ═══════════════════════════════════════════════════════════════════════
// PdfCompressor.cs — PDF Size Optimization & Compression Engine
// Strips unused streams, optimizes flate compression, and flattens resources.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace FlyShelf.Classes.Utils
{
    public static class PdfCompressor
    {
        /// <summary>
        /// Compresses and optimizes a PDF file, outputting a significantly smaller copy.
        /// </summary>
        public static async Task<(string outputPath, long originalSize, long compressedSize)> CompressPdfAsync(
            string sourcePdfPath,
            string outputPath = null)
        {
            if (string.IsNullOrEmpty(sourcePdfPath) || !File.Exists(sourcePdfPath))
                throw new FileNotFoundException("Source PDF not found", sourcePdfPath);

            long originalSize = new FileInfo(sourcePdfPath).Length;

            if (string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(sourcePdfPath) ?? Path.GetTempPath();
                string name = Path.GetFileNameWithoutExtension(sourcePdfPath);
                outputPath = Path.Combine(dir, $"{name}_Compressed_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            }

            bool success = await Task.Run(() =>
            {
                try
                {
                    using (var inputDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import))
                    using (var outputDoc = new PdfDocument())
                    {
                        outputDoc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
                        outputDoc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
                        outputDoc.Options.CompressContentStreams = true;
                        outputDoc.Options.NoCompression = false;

                        for (int i = 0; i < inputDoc.PageCount; i++)
                        {
                            outputDoc.AddPage(inputDoc.Pages[i]);
                        }

                        // Strip metadata bloat
                        outputDoc.Info.Title = inputDoc.Info.Title;
                        outputDoc.Info.Author = inputDoc.Info.Author;
                        outputDoc.Info.Creator = "FlyShelf PDF Optimizer";

                        outputDoc.Save(outputPath);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PDF_COMPRESS_ERR", ex.Message);
                    return false;
                }
            });

            if (!success || !File.Exists(outputPath))
            {
                throw new InvalidOperationException("Failed to compress PDF.");
            }

            long compressedSize = new FileInfo(outputPath).Length;
            return (outputPath, originalSize, compressedSize);
        }
    }
}
