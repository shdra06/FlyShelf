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

        // ── Cached LibreOffice path (null = not found, "" = not yet checked) ──
        private static string? _cachedLibreOfficePath = "";
        private static string? GetLibreOfficePath()
        {
            if (_cachedLibreOfficePath != "")
                return _cachedLibreOfficePath; // null means "not installed"
            string[] paths =
            {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
            };
            _cachedLibreOfficePath = paths.FirstOrDefault(File.Exists); // null if none found
            return _cachedLibreOfficePath;
        }

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

            Task.Run(async () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("⚠️ File not found — cannot convert"));
                        return;
                    }

                    string ext = Path.GetExtension(FilePath).ToUpperInvariant();

                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast("Converting to PDF... ♻️")
                    );

                    string targetPdf = Path.Combine(
                         Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                         Path.GetFileNameWithoutExtension(FilePath) + $"_Converted_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                    bool converted = false;

                    // ═══════════════════════════════════════════════════════
                    // STRATEGY 1: TXT/MD — Native PDF generation (no Word needed)
                    // ═══════════════════════════════════════════════════════
                    if (ext == ".MD")
                    {
                        converted = await FlyShelf.Classes.ConversionUtils.ConvertMarkdownToPdfAsync(FilePath, targetPdf);
                    }
                    else if (ext == ".TXT" || ext == ".LOG" || ext == ".CSV")
                    {
                        converted = ConvertTextToPdfNative(FilePath, targetPdf);
                    }

                    if (!converted)
                    {
                        // ═══════════════════════════════════════════════════════
                        // STRATEGY 2: Word COM — tried first (Windows app, everyone has Word)
                        // ═══════════════════════════════════════════════════════
                        if (ext == ".DOCX" || ext == ".DOC" || ext == ".RTF")
                        {
                            if (Type.GetTypeFromProgID("Word.Application") != null)
                                converted = TryWordComConvert(FilePath, targetPdf);
                        }

                        // ═══════════════════════════════════════════════════════
                        // STRATEGY 3: LibreOffice — fallback if Word not installed or failed
                        // ═══════════════════════════════════════════════════════
                        if (!converted)
                            converted = TryLibreOfficeConvert(FilePath, targetPdf);
                    }

                    // ═══════════════════════════════════════════════════════
                    // RESULT
                    // ═══════════════════════════════════════════════════════
                    if (converted && File.Exists(targetPdf))
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var dataObj = new System.Windows.DataObject();
                            dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { targetPdf });
                            var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                            (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                            FlyShelf.Windows.ToastWindow.ShowToast("PDF Converted Successfully ✅");
                            FlyShelf.Classes.LicenseManager.RecordDocConversion();

                            // Scroll to top after a short delay so the new PDF item is visible
                            mainWin?.ScrollClipboardToTop();
                        });
                    }
                    else
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Conversion Failed: Install LibreOffice or Microsoft Word ❌")
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

        // ═══════════════════════════════════════════════════════════════
        // NATIVE TXT/MD → PDF  (no external dependencies at all)
        // ═══════════════════════════════════════════════════════════════
        private static bool ConvertTextToPdfNative(string inputPath, string outputPdf)
        {
            try
            {
                string text = File.ReadAllText(inputPath);
                if (string.IsNullOrEmpty(text)) text = "(empty file)";

                // PDF page constants (A4 in points)
                double pageW = 595.28, pageH = 841.89;
                double margin = 50;
                double usableW = pageW - 2 * margin;
                double fontSize = 10;
                double lineHeight = fontSize * 1.4;
                double charsPerLine = (int)(usableW / (fontSize * 0.52)); // approximate monospace width
                double linesPerPage = (int)((pageH - 2 * margin) / lineHeight);

                // Word-wrap and paginate
                var allLines = new List<string>();
                foreach (var rawLine in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                {
                    if (rawLine.Length <= charsPerLine)
                    {
                        allLines.Add(rawLine);
                    }
                    else
                    {
                        // Wrap long lines
                        for (int i = 0; i < rawLine.Length; i += (int)charsPerLine)
                        {
                            int len = Math.Min((int)charsPerLine, rawLine.Length - i);
                            allLines.Add(rawLine.Substring(i, len));
                        }
                    }
                }

                // Split into pages
                var pages = new List<List<string>>();
                for (int i = 0; i < allLines.Count; i += (int)linesPerPage)
                {
                    int count = Math.Min((int)linesPerPage, allLines.Count - i);
                    pages.Add(allLines.GetRange(i, count));
                }
                if (pages.Count == 0) pages.Add(new List<string> { "(empty)" });

                // Write PDF
                using (var fs = new FileStream(outputPdf, FileMode.Create))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.ASCII))
                {
                    var offsets = new List<long>();
                    writer.Write("%PDF-1.4\n");
                    writer.Flush();

                    // Obj 1: Catalog
                    offsets.Add(fs.Position);
                    writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                    writer.Flush();

                    // Obj 2: Pages
                    offsets.Add(fs.Position);
                    string kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + i * 2} 0 R"));
                    writer.Write($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");
                    writer.Flush();

                    int nextObj = 3;
                    // Font object (Helvetica — built-in, always available)
                    int fontObj = nextObj + pages.Count * 2;
                    
                    for (int p = 0; p < pages.Count; p++)
                    {
                        // Page object
                        int pageObj = nextObj + p * 2;
                        int contentObj = pageObj + 1;

                        // Build content stream
                        var contentLines = new List<string>();
                        contentLines.Add($"BT\n/F1 {fontSize:F0} Tf\n{margin:F2} {(pageH - margin):F2} Td\n{lineHeight:F2} TL\n");
                        foreach (var line in pages[p])
                        {
                            contentLines.Add($"({EscapePdfString(line)}) '\n");
                        }
                        contentLines.Add("ET\n");
                        string contentStream = string.Join("", contentLines);
                        byte[] contentBytes = System.Text.Encoding.ASCII.GetBytes(contentStream);

                        offsets.Add(fs.Position);
                        writer.Write($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW:F2} {pageH:F2}] /Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>\nendobj\n");
                        writer.Flush();

                        offsets.Add(fs.Position);
                        writer.Write($"{contentObj} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
                        writer.Flush();
                        fs.Write(contentBytes, 0, contentBytes.Length);
                        writer.Write("endstream\nendobj\n");
                        writer.Flush();
                    }

                    // Font object
                    offsets.Add(fs.Position);
                    writer.Write($"{fontObj} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
                    writer.Flush();

                    // xref table
                    long xrefOffset = fs.Position;
                    int totalObjs = offsets.Count + 1;
                    writer.Write($"xref\n0 {totalObjs}\n");
                    writer.Write("0000000000 65535 f \n");
                    foreach (var off in offsets)
                        writer.Write($"{off:D10} 00000 n \n");
                    writer.Write($"trailer\n<< /Size {totalObjs} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
                    writer.Flush();
                }

                return File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0;
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("CONVERT", $"Native TXT->PDF failed: {ex.Message}");
                return false;
            }
        }

        private static string EscapePdfString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s
                .Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)")
                .Replace("\t", "    ");
        }

        // ═══════════════════════════════════════════════════════════════
        // LIBREOFFICE HEADLESS — Fully silent, no GUI, no popups
        // ═══════════════════════════════════════════════════════════════
        private static bool TryLibreOfficeConvert(string inputPath, string outputPdf,
            System.Threading.CancellationToken ct = default)
        {
            try
            {
                string? sofficePath = GetLibreOfficePath();
                if (sofficePath == null) return false;

                string outDir = Path.GetDirectoryName(outputPdf) ?? Path.GetTempPath();
                string expectedName = Path.GetFileNameWithoutExtension(inputPath) + ".pdf";
                string expectedPath = Path.Combine(outDir, expectedName);

                var psi = new ProcessStartInfo
                {
                    FileName = sofficePath,
                    Arguments = $"--headless --norestore --nofirststartwizard --convert-to pdf --outdir \"{outDir}\" \"{inputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;

                    // Register cancellation to kill LibreOffice if the other converter wins
                    ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } });

                    bool exited = proc.WaitForExit(30000); // 30s — enough for LO cold start
                    if (!exited || ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        return false;
                    }

                    if (proc.ExitCode == 0 && File.Exists(expectedPath))
                    {
                        if (expectedPath != outputPdf)
                        {
                            try { File.Move(expectedPath, outputPdf, true); } catch { }
                        }
                        return File.Exists(outputPdf);
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("CONVERT", $"LibreOffice conversion failed: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // WORD COM — Full dialog suppression + cancellation support
        // ═══════════════════════════════════════════════════════════════
        private static bool TryWordComConvert(string inputPath, string outputPdf,
            System.Threading.CancellationToken ct = default)
        {
            dynamic? wordApp = null;
            dynamic? doc = null;
            Process? wordProcess = null;

            try
            {
                var wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null) return false;

                wordApp = Activator.CreateInstance(wordType);

                // ── SUPPRESS ALL DIALOGS AND POPUPS ──
                wordApp.Visible = false;
                wordApp.DisplayAlerts = 0;              // wdAlertsNone
                wordApp.AutomationSecurity = 3;          // msoAutomationSecurityForceDisable
                wordApp.Options.DoNotPromptForConvert = true;

                // Suppress Protected View for all sources
                try
                {
                    wordApp.Options.WarnBeforeSavingPrintOrMailMerge = false;
                }
                catch { }
                try
                {
                    // Disable Protected View triggers
                    var protView = wordApp.Application.ProtectedViewWindows;
                }
                catch { }

                // Track the Word process for timeout kill
                try
                {
                    int hwnd = wordApp.Application.Hwnd;
                    if (hwnd != 0)
                    {
                        wordProcess = Process.GetProcesses()
                            .Where(p => p.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(p => p.StartTime)
                            .FirstOrDefault();
                    }
                }
                catch { }

                // Open document with all dialog-triggering options disabled
                var openTask = Task.Run(() =>
                {
                    doc = wordApp.Documents.Open(
                        inputPath,              // FileName
                        false,                  // ConfirmConversions — NO conversion dialog
                        true,                   // ReadOnly
                        false,                  // AddToRecentFiles
                        "",                     // PasswordDocument — empty string, not Missing
                        "",                     // PasswordTemplate
                        true,                   // Revert (don't ask to revert)
                        "",                     // WritePasswordDocument
                        "",                     // WritePasswordTemplate
                        Type.Missing,           // Format
                        Type.Missing,           // Encoding
                        false,                  // Visible
                        false,                  // OpenAndRepair
                        Type.Missing,           // DocumentDirection
                        true,                   // NoEncodingDialog — suppress encoding dialog
                        Type.Missing            // XMLTransform
                    );
                }, ct);

                // Wait with timeout — if Word shows a dialog, it blocks
                if (ct.IsCancellationRequested) return false;
                if (!openTask.Wait(TimeSpan.FromSeconds(20)))
                {
                    FlyShelf.Classes.Logger.LogAction("CONVERT", "Word open timed out (likely dialog)");
                    ForceKillWord(wordProcess);
                    return false;
                }

                if (doc == null) return false;

                // Export to PDF with timeout
                var exportTask = Task.Run(() =>
                {
                    doc.ExportAsFixedFormat(
                        outputPdf,              // OutputFileName
                        17,                     // wdExportFormatPDF
                        false,                  // OpenAfterExport
                        0,                      // OptimizeFor: wdExportOptimizeForPrint
                        0,                      // Range: wdExportAllDocument
                        1,                      // From
                        1,                      // To
                        0,                      // Item: wdExportDocumentContent
                        true,                   // IncludeDocProps
                        true,                   // KeepIRM
                        0,                      // CreateBookmarks: wdExportCreateNoBookmarks
                        true,                   // DocStructureTags
                        true,                   // BitmapMissingFonts
                        false                   // UseISO19005_1 (PDF/A)
                    );
                });

                if (ct.IsCancellationRequested) return false;
                if (!exportTask.Wait(TimeSpan.FromSeconds(30)))
                {
                    FlyShelf.Classes.Logger.LogAction("CONVERT", "Word export timed out (likely dialog)");
                    ForceKillWord(wordProcess);
                    return false;
                }

                return File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0;
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("CONVERT", $"Word COM failed: {ex.Message}");
                return false;
            }
            finally
            {
                // Clean up COM objects — prevent orphaned WINWORD.EXE
                try { if (doc != null) { doc.Close(0 /* wdDoNotSaveChanges */); System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } } catch { }
                try { if (wordApp != null) { wordApp.Quit(0); System.Runtime.InteropServices.Marshal.ReleaseComObject(wordApp); } } catch { }
            }
        }

        /// <summary>
        /// Force kill a Word process that is likely stuck on a dialog.
        /// </summary>
        private static void ForceKillWord(Process? wordProcess)
        {
            try
            {
                if (wordProcess != null && !wordProcess.HasExited)
                {
                    wordProcess.Kill();
                    FlyShelf.Classes.Logger.LogAction("CONVERT", $"Force-killed WINWORD PID {wordProcess.Id}");
                }
            }
            catch { }
        }

        /// <summary>
        /// Convert an image to a single-page PDF (A4 size). No external dependencies.
        /// Uses raw PDF specification writing with embedded JPEG stream.
        /// </summary>
        public void ConvertImageToPdf()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Image file not found — cannot convert"));
                return;
            }

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

                    // Load image using WPF's decoder (thread-safe on background threads)
                    byte[] jpegBytes;
                    int imgWidth, imgHeight;

                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        new Uri(FilePath, UriKind.Absolute),
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                    var frame = dec.Frames[0];
                    imgWidth = frame.PixelWidth;
                    imgHeight = frame.PixelHeight;

                    // Convert to JPEG bytes for PDF embedding
                    using (var ms = new MemoryStream())
                    {
                        var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                        enc.Save(ms);
                        jpegBytes = ms.ToArray();
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

                        // Scroll to top after a short delay so the new PDF item is visible
                        mainWin?.ScrollClipboardToTop();
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
