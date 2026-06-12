// ---------------------------------------------------------------
// ClipboardItem — Document & Image Conversion
// ConvertDocumentTask, ConvertImageToPdf
// Split from ClipboardItem.Actions.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FlyShelf.ViewModels
{
    public partial class ClipboardItem
    {

        public void ConvertDocumentTask()
        {
#if MSIX_STORE
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Document conversion is not available in the Store version."));
            return;
#else
            if (!FlyShelf.Classes.LicenseManager.CanConvertDoc())
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowDocConvertLimit());
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;

                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast("Converting to PDF... ♻️")
                    );

                    string targetPdf = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_Converted_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                    // Use Word COM interop directly via dynamic — no PowerShell window, no dialogs
                    dynamic? wordApp = null;
                    dynamic? doc = null;
                    bool converted = false;

                    try
                    {
                        var wordType = Type.GetTypeFromProgID("Word.Application");
                        if (wordType == null)
                        {
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                FlyShelf.Windows.ToastWindow.ShowToast("Microsoft Word is not installed ❌"));
                            return;
                        }

                        wordApp = Activator.CreateInstance(wordType);
                        wordApp.Visible = false;
                        wordApp.DisplayAlerts = 0;           // wdAlertsNone — suppress ALL dialogs
                        wordApp.AutomationSecurity = 3;      // msoAutomationSecurityForceDisable — block macros/security prompts

                        // Open with all dialog-triggering options disabled
                        doc = wordApp.Documents.Open(
                            FilePath,                   // FileName
                            false,                      // ConfirmConversions
                            true,                       // ReadOnly
                            false,                      // AddToRecentFiles
                            Type.Missing,               // PasswordDocument
                            Type.Missing,               // PasswordTemplate
                            Type.Missing,               // Revert
                            Type.Missing,               // WritePasswordDocument
                            Type.Missing,               // WritePasswordTemplate
                            Type.Missing,               // Format
                            Type.Missing,               // Encoding
                            false,                      // Visible — don't show the document window
                            false,                      // OpenAndRepair
                            Type.Missing,               // DocumentDirection
                            true,                       // NoEncodingDialog — suppress encoding dialog
                            Type.Missing                // XMLTransform
                        );

                        // ExportAsFixedFormat produces PDF natively without any save dialogs
                        doc.ExportAsFixedFormat(
                            targetPdf,                  // OutputFileName
                            17,                         // wdExportFormatPDF
                            false,                      // OpenAfterExport
                            0,                          // OptimizeFor: wdExportOptimizeForPrint
                            0,                          // Range: wdExportAllDocument
                            1,                          // From
                            1,                          // To
                            0,                          // Item: wdExportDocumentContent
                            true,                       // IncludeDocProps
                            true,                       // KeepIRM
                            0,                          // CreateBookmarks: wdExportCreateNoBookmarks
                            true,                       // DocStructureTags
                            true,                       // BitmapMissingFonts
                            false                       // UseISO19005_1 (PDF/A)
                        );

                        converted = File.Exists(targetPdf);
                    }
                    finally
                    {
                        // Clean up COM objects — prevent orphaned WINWORD.EXE processes
                        try { if (doc != null) { doc.Close(0 /* wdDoNotSaveChanges */); System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } } catch { }
                        try { if (wordApp != null) { wordApp.Quit(0); System.Runtime.InteropServices.Marshal.ReleaseComObject(wordApp); } } catch { }
                    }

                    if (converted)
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var dataObj = new System.Windows.DataObject();
                            dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { targetPdf });
                            var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                            (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                            FlyShelf.Windows.ToastWindow.ShowToast("PDF Converted Successfully ✅");
                            FlyShelf.Classes.LicenseManager.RecordDocConversion();
                        });
                    }
                    else
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Conversion Failed: Could not create PDF ❌")
                        );
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"Conversion Error: {ex.Message} ❌")
                    );
                }
            });
#endif
        }

        /// <summary>
        /// Convert an image to a single-page PDF (A4 size). No external dependencies.
        /// Uses raw PDF specification writing with embedded JPEG stream.
        /// </summary>
        public void ConvertImageToPdf()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;

            if (!FlyShelf.Classes.LicenseManager.CanConvertImageToPdf())
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowImageToPdfLimit());
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast("Converting Image to PDF... 📄")
                    );

                    string outputPdf = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                    // Load image to get dimensions
                    byte[] jpegBytes;
                    int imgWidth, imgHeight;
                    using (var bmp = new System.Drawing.Bitmap(FilePath))
                    {
                        imgWidth = bmp.Width;
                        imgHeight = bmp.Height;

                        // Convert to JPEG for PDF embedding
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                            jpegBytes = ms.ToArray();
                        }
                    }

                    // A4 page size in points (72 dpi): 595.28 x 841.89
                    double pageW = 595.28, pageH = 841.89;
                    double margin = 36; // 0.5 inch margin
                    double usableW = pageW - 2 * margin;
                    double usableH = pageH - 2 * margin;

                    // Scale image to fit page while maintaining aspect ratio
                    double scale = Math.Min(usableW / imgWidth, usableH / imgHeight);
                    double drawW = imgWidth * scale;
                    double drawH = imgHeight * scale;
                    double drawX = margin + (usableW - drawW) / 2;
                    double drawY = margin + (usableH - drawH) / 2;

                    // Write a minimal valid PDF
                    using (var fs = new FileStream(outputPdf, FileMode.Create))
                    using (var writer = new StreamWriter(fs, System.Text.Encoding.ASCII))
                    {
                        var offsets = new List<long>();
                        writer.Write("%PDF-1.4\n");
                        writer.Flush();

                        offsets.Add(fs.Position);
                        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                        writer.Flush();

                        offsets.Add(fs.Position);
                        writer.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
                        writer.Flush();

                        offsets.Add(fs.Position);
                        writer.Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW:F2} {pageH:F2}] /Contents 4 0 R /Resources << /XObject << /Img1 5 0 R >> >> >>\nendobj\n");
                        writer.Flush();

                        string contentStream = $"q\n{drawW:F2} 0 0 {drawH:F2} {drawX:F2} {drawY:F2} cm\n/Img1 Do\nQ\n";
                        offsets.Add(fs.Position);
                        writer.Write($"4 0 obj\n<< /Length {contentStream.Length} >>\nstream\n{contentStream}endstream\nendobj\n");
                        writer.Flush();

                        offsets.Add(fs.Position);
                        writer.Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgWidth} /Height {imgHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegBytes.Length} >>\nstream\n");
                        writer.Flush();
                        fs.Write(jpegBytes, 0, jpegBytes.Length);
                        writer.Write("\nendstream\nendobj\n");
                        writer.Flush();

                        long xrefOffset = fs.Position;
                        writer.Write($"xref\n0 {offsets.Count + 1}\n");
                        writer.Write("0000000000 65535 f \n");
                        foreach (var off in offsets)
                            writer.Write($"{off:D10} 00000 n \n");
                        writer.Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
                        writer.Flush();
                    }

                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPdf });
                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                        (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image → PDF converted! ✅ {Path.GetFileName(outputPdf)}");
                        FlyShelf.Classes.LicenseManager.RecordImageToPdf();
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image→PDF failed: {ex.Message} ❌")
                    );
                }
            });
        }

    }
}
