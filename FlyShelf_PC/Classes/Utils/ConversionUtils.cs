using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Drawing;

namespace FlyShelf.Classes
{
    public static class ConversionUtils
    {
        /// <summary>Converts an image to PDF using PDFsharp natively.</summary>
        public static string ConvertImageToPdf(string imagePath)
        {
            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Converted");
            Directory.CreateDirectory(outputDir);

            string pdfPath = Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(imagePath) + "_" + Guid.NewGuid().ToString().Substring(0, 4) + ".pdf");

            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                using (var img = XImage.FromFile(imagePath))
                {
                    page.Width = XUnit.FromPoint(img.PointWidth);
                    page.Height = XUnit.FromPoint(img.PointHeight);
                    using (var gfx = XGraphics.FromPdfPage(page))
                    {
                        gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                    }
                }
                doc.Save(pdfPath);
            }
            return pdfPath;
        }

        /// <summary>Converts a DOC/DOCX file to PDF using Word COM via PowerShell. Returns the output path or null.</summary>
        public static async Task<string> ConvertDocToPdfAsync(string docPath)
        {
#if MSIX_STORE
            await Task.CompletedTask; // suppress async warning
            return null; // PowerShell-based conversion not available in Store version
#else
            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Converted");
            Directory.CreateDirectory(outputDir);

            string pdfPath = Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(docPath) + ".pdf");

            bool success = await Task.Run(() =>
            {
                try
                {
                    // wdFormatPDF = 17
                    string script = $@"
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$doc = $word.Documents.Open('{docPath.Replace("'", "''")}')
$doc.SaveAs([ref]'{pdfPath.Replace("'", "''")}', [ref]17)
$doc.Close()
$word.Quit()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy RemoteSigned -Command \"{script}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(120000); // 2 min timeout
                    return proc?.ExitCode == 0;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DOC2PDF", $"Conversion error: {ex.Message}");
                    return false;
                }
            });

            return (success && File.Exists(pdfPath)) ? pdfPath : null;
#endif
        }
    }
}
