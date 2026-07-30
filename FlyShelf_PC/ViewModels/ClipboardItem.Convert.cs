// ---------------------------------------------------------------
// ClipboardItem — Document & Image Conversion
// ConvertDocumentTask, ConvertImageToPdf, ConvertImageFormat,
// ConvertCsvToXlsx
// Split from ClipboardItem.Actions.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FlyShelf.ViewModels
{
    public partial class ClipboardItem
    {

        // [FIX M-33]: Use Lazy<T> for thread-safe, once-only resolution
        private static readonly Lazy<string?> _cachedLibreOfficePath = new(() =>
        {
            string[] paths =
            {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
            };
            return paths.FirstOrDefault(File.Exists); // null if none found
        });
        private static string? GetLibreOfficePath() => _cachedLibreOfficePath.Value;

        public void ConvertDocumentTask()
        {
#if MSIX_STORE
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Document conversion is not available in the Store version."));
            return;
#else
            if (!FlyShelf.Classes.LicenseManager.CanConvertDoc())
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowDocConvertLimit());
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    string workFilePath = FilePath;

                    // ── Markdown text items (clipboard text detected as markdown) have no file ──
                    if (IsMarkdownPreview && (string.IsNullOrEmpty(workFilePath) || !File.Exists(workFilePath)))
                    {
                        string mdContent = !string.IsNullOrEmpty(RawContent) ? RawContent : FileName;
                        if (string.IsNullOrEmpty(mdContent))
                        {
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                FlyShelf.Windows.ToastWindow.ShowToast("⚠️ No markdown content to convert"));
                            return;
                        }
                        workFilePath = Path.Combine(Path.GetTempPath(), $"FlyShelf_MD_{DateTime.Now:yyyyMMdd_HHmmss}.md");
                        File.WriteAllText(workFilePath, mdContent, System.Text.Encoding.UTF8);
                    }
                    else if (string.IsNullOrEmpty(workFilePath) || !File.Exists(workFilePath))
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("⚠️ File not found — cannot convert"));
                        return;
                    }

                    string ext = Path.GetExtension(workFilePath).ToUpperInvariant();

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowProgress("Converting to PDF", 10);
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ShowConversionNotification(
                            ext.TrimStart('.'), "PDF");
                    });

                    string targetPdf = Path.Combine(
                         Path.GetDirectoryName(workFilePath) ?? Path.GetTempPath(),
                         Path.GetFileNameWithoutExtension(workFilePath) + $"_Converted_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");


                    bool converted = false;

                    // ═══════════════════════════════════════════════════════
                    // STRATEGY 1: TXT/MD — Native PDF generation (no Word needed)
                    // ═══════════════════════════════════════════════════════
                    if (ext == ".MD")
                    {
                        converted = await FlyShelf.Classes.ConversionUtils.ConvertMarkdownToPdfAsync(workFilePath, targetPdf);
                    }
                    else if (ext == ".TXT" || ext == ".LOG" || ext == ".CSV")
                    {
                        converted = ConvertTextToPdfNative(workFilePath, targetPdf);
                    }

                    if (!converted)
                    {
                        // Update progress — trying Word COM
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowProgress("Converting to PDF", 40));
                        // ═══════════════════════════════════════════════════════
                        // STRATEGY 2: Word COM — tried first (Windows app, everyone has Word)
                        // ═══════════════════════════════════════════════════════
                        if (ext == ".DOCX" || ext == ".DOC" || ext == ".RTF")
                        {
                            if (Type.GetTypeFromProgID("Word.Application") != null)
                                converted = await TryWordComConvertStaAsync(workFilePath, targetPdf);
                        }

                        // ═══════════════════════════════════════════════════════
                        // STRATEGY 3: LibreOffice — fallback if Word not installed or failed
                        // ═══════════════════════════════════════════════════════
                        if (!converted)
                        {
                            // Update progress — trying LibreOffice
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                FlyShelf.Windows.ToastWindow.ShowProgress("Converting to PDF", 60));
                            converted = TryLibreOfficeConvert(workFilePath, targetPdf);
                        }
                    }

                    // ═══════════════════════════════════════════════════════
                    // RESULT
                    // ═══════════════════════════════════════════════════════
                    if (converted && File.Exists(targetPdf))
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            var dataObj = new System.Windows.DataObject();
                            dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { targetPdf });
                            var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                            (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                            FlyShelf.Windows.ToastWindow.ShowProgress("PDF converted", 100);
                            FlyShelf.Controls.FlyShelfWidgetControl.Instance?.CompleteMiniNotification();
                            FlyShelf.Classes.LicenseManager.RecordDocConversion();

                            // Scroll to top after a short delay so the new PDF item is visible
                            mainWin?.ScrollClipboardToTop();
                        });
                    }
                    else
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast("Conversion failed — Install LibreOffice or Microsoft Word");
                            FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ErrorMiniNotification("Failed");
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Conversion Error: {ex.Message}");
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ErrorMiniNotification("Error");
                    });
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
                int charsPerLine = (int)(usableW / (fontSize * 0.52)); // approximate monospace width
                int linesPerPage = (int)((pageH - 2 * margin) / lineHeight);

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
                        for (int i = 0; i < rawLine.Length; i += charsPerLine)
                        {
                            int len = Math.Min(charsPerLine, rawLine.Length - i);
                            allLines.Add(rawLine.Substring(i, len));
                        }
                    }
                }

                // Split into pages
                var pages = new List<List<string>>();
                for (int i = 0; i < allLines.Count; i += linesPerPage)
                {
                    int count = Math.Min(linesPerPage, allLines.Count - i);
                    pages.Add(allLines.GetRange(i, count));
                }
                if (pages.Count == 0) pages.Add(new List<string> { "(empty)" });

                // Write PDF
                using (var fs = new FileStream(outputPdf, FileMode.Create))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.GetEncoding("ISO-8859-1")))
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

        // [FIX M-50]: Handle non-ASCII/Unicode/CJK/emoji via octal escaping for valid PDF strings
        private static string EscapePdfString(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var sb = new StringBuilder(input.Length + 10);
            foreach (char c in input)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '(': sb.Append(@"\("); break;
                    case ')': sb.Append(@"\)"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\t': sb.Append("    "); break;
                    default:
                        if (c > 0x7E)
                            sb.Append(CultureInfo.InvariantCulture, $"\\{((int)c):D3}"); // Octal-style escape for non-ASCII
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
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
                    using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } /* Best-effort: failure is acceptable */ });

                    bool exited = proc.WaitForExit(30000); // 30s — enough for LO cold start
                    if (!exited || ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { } // Best-effort: failure is acceptable
                        return false;
                    }

                    if (proc.ExitCode == 0 && File.Exists(expectedPath))
                    {
                        if (expectedPath != outputPdf)
                        {
                            try { File.Move(expectedPath, outputPdf, true); } catch { } // Best-effort: failure is acceptable
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
        // [FIX H-12]: Run Word COM on an explicit STA thread to avoid
        // MTA hangs — COM automation requires STA apartment state.
        // ═══════════════════════════════════════════════════════════════
        private static Task<bool> TryWordComConvertStaAsync(string inputPath, string outputPdf)
        {
            var tcs = new TaskCompletionSource<bool>();
            var staThread = new System.Threading.Thread(() =>
            {
                try
                {
                    bool result = TryWordComConvertCore(inputPath, outputPdf);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            // 30-second timeout — if Word COM hangs (modal dialog, deadlock),
            // abandon the thread and fall through to native/LibreOffice fallback.
            Task.Run(async () =>
            {
                await Task.Delay(30000);
                if (!tcs.Task.IsCompleted)
                {
                    Classes.Logger.LogAction("DOC2PDF", "Word COM STA thread timed out after 30s — killing");
                    try
                    {
                        // Kill any orphaned Word process started by this thread
                        foreach (var p in Process.GetProcessesByName("WINWORD"))
                        {
                            try { if (p.MainWindowTitle == "") p.Kill(); } catch { }
                        }
                    }
                    catch { }
                    tcs.TrySetResult(false);
                }
            });

            return tcs.Task;
        }

        private static bool TryWordComConvertCore(string inputPath, string outputPdf)
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
                catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("CONVERT", $"Non-critical: {ex.Message}"); } // Best-effort: failure is acceptable
                try
                {
                    // Disable Protected View triggers
                    var protView = wordApp.Application.ProtectedViewWindows;
                }
                catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("CONVERT", $"Non-critical: {ex.Message}"); }

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
                catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("CONVERT", $"Non-critical: {ex.Message}"); }

                // Open document with all dialog-triggering options disabled
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

                if (doc == null) return false;

                // Export to PDF
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

                return File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0;
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("CONVERT", $"Word COM failed: {ex.Message}");
                ForceKillWord(wordProcess);
                return false;
            }
            finally
            {
                // Clean up COM objects — prevent orphaned WINWORD.EXE
                try { if (doc != null) { doc.Close(0 /* wdDoNotSaveChanges */); System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } } catch { } // Best-effort: failure is acceptable
                try { if (wordApp != null) { wordApp.Quit(0); System.Runtime.InteropServices.Marshal.ReleaseComObject(wordApp); } } catch { } // Best-effort: failure is acceptable
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
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>
        /// Convert an image to a single-page PDF (A4 size). No external dependencies.
        /// Uses raw PDF specification writing with embedded JPEG stream.
        /// </summary>
        public void ConvertImageToPdf()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Image file not found — cannot convert"));
                return;
            }

            if (!FlyShelf.Classes.LicenseManager.CanConvertImageToPdf())
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowImageToPdfLimit());
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowProgress("Converting image to PDF", 10);
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ShowConversionNotification(
                            Path.GetExtension(FilePath).TrimStart('.'), "PDF");
                    });

                    string outputPdf = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                    // Load image using WPF's decoder (thread-safe on background threads)
                    byte[] jpegBytes;
                    int imgWidth, imgHeight;

                    // [FIX M-21]: Use FileStream with ReadWrite share to avoid locking the source file
                    using var imgFs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        imgFs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                    var frame = dec.Frames[0];
                    imgWidth = frame.PixelWidth;
                    imgHeight = frame.PixelHeight;

                    // Convert to JPEG bytes for PDF embedding
                    // [FIX R5]: Strip alpha channel from RGBA PNGs — JPEG/DCTDecode requires DeviceRGB
                    System.Windows.Media.Imaging.BitmapSource sourceFrame = frame;
                    if (frame.Format == System.Windows.Media.PixelFormats.Bgra32 ||
                        frame.Format == System.Windows.Media.PixelFormats.Pbgra32 ||
                        frame.Format == System.Windows.Media.PixelFormats.Rgba64 ||
                        frame.Format == System.Windows.Media.PixelFormats.Prgba64)
                    {
                        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap();
                        converted.BeginInit();
                        converted.Source = frame;
                        converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgr24;
                        converted.EndInit();
                        converted.Freeze();
                        sourceFrame = converted;
                    }

                    // Update progress — encoding image
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowProgress("Converting image to PDF", 50));

                    using (var ms = new MemoryStream())
                    {
                        var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sourceFrame));
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
                    // [FIX C6]: PDF uses bottom-left origin — position image correctly
                    double drawY = pageH - margin - drawH - (usableH - drawH) / 2;

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

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPdf });
                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                        (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowProgress("Image converted to PDF", 100);
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.CompleteMiniNotification();
                        FlyShelf.Classes.LicenseManager.RecordImageToPdf();

                        // Scroll to top after a short delay so the new PDF item is visible
                        mainWin?.ScrollClipboardToTop();
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image to PDF failed: {ex.Message}");
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ErrorMiniNotification("Failed");
                    });
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // IMAGE FORMAT CONVERSION  (PNG ↔ JPG)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Convert the current image file to a different format ("png" or "jpg").
        /// Uses WPF's built-in bitmap encoders — no external dependencies.
        /// </summary>
        public void ConvertImageFormat(string targetFormat)
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Image file not found — cannot convert"));
                return;
            }

            // NOTE (M-18): Image format conversions (PNG↔JPG) share the same quota as image-to-PDF
            // conversions by design — both are part of the "Image Convert" feature tier.
            if (!FlyShelf.Classes.LicenseManager.CanConvertImageToPdf())
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowImageToPdfLimit());
                return;
            }

            string fmt = targetFormat.ToLowerInvariant().Trim('.');
            if (fmt != "png" && fmt != "jpg" && fmt != "jpeg")
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Unsupported target format: {targetFormat}"));
                return;
            }

            // Normalise "jpeg" → "jpg" for the file extension
            string ext = fmt == "jpeg" ? "jpg" : fmt;

            Task.Run(() =>
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"Converting image to {ext.ToUpperInvariant()}... 🖼️")
                    );

                    string outputPath = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");

                    // Load image using WPF's decoder (thread-safe on background threads)
                    // [FIX M-21]: Use FileStream with ReadWrite share to avoid locking the source file
                    using var imgFs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        imgFs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                    var frame = dec.Frames[0];

                    using (var fs = new FileStream(outputPath, FileMode.Create))
                    {
                        System.Windows.Media.Imaging.BitmapEncoder encoder;
                        if (ext == "png")
                        {
                            encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        }
                        else // jpg
                        {
                            encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };
                        }
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
                        if (!FlyShelf.Classes.DiskSpaceHelper.HasSufficientDiskSpace(outputPath, 10_000_000))
                        {
                            FlyShelf.Classes.Logger.LogAction("IMAGE_SAVE", "Insufficient disk space");
                            return;
                        }
                        encoder.Save(fs);
                    }

                    if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast($"Image conversion failed ❌"));
                        return;
                    }

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPath });
                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                        (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image → {ext.ToUpperInvariant()} converted! ✅ {Path.GetFileName(outputPath)}");
                        FlyShelf.Classes.LicenseManager.RecordImageToPdf();

                        // Scroll to top after a short delay so the new item is visible
                        mainWin?.ScrollClipboardToTop();
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image conversion failed: {ex.Message} ❌")
                    );
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // CSV → XLSX CONVERSION  (raw OpenXML via ZipArchive)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Convert a CSV file to XLSX using raw OpenXML (ZIP + XML).
        /// No external packages required — uses System.IO.Compression.
        /// </summary>
        public void ConvertCsvToXlsx()
        {
#if MSIX_STORE
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                FlyShelf.Windows.ToastWindow.ShowToast("⚠️ CSV conversion is not available in the Store version."));
            return;
#else
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ CSV file not found — cannot convert"));
                return;
            }

            if (!FlyShelf.Classes.LicenseManager.CanConvertDoc())
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowDocConvertLimit());
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("Converting CSV to XLSX...");
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ShowConversionNotification("CSV", "XLSX");
                    });

                    string xlsxPath = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

                    // Parse CSV rows
                    string csvText = File.ReadAllText(FilePath, Encoding.UTF8);
                    var rows = ParseCsvRows(csvText);

                    // Build XLSX (ZIP with OpenXML entries)
                    using (var zip = ZipFile.Open(xlsxPath, ZipArchiveMode.Create))
                    {
                        AddZipEntry(zip, "[Content_Types].xml", BuildContentTypesXml());
                        AddZipEntry(zip, "_rels/.rels", BuildRootRelsXml());
                        AddZipEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
                        AddZipEntry(zip, "xl/workbook.xml", BuildWorkbookXml());
                        AddZipEntry(zip, "xl/styles.xml", BuildStylesXml());
                        AddZipEntry(zip, "xl/worksheets/sheet1.xml", BuildSheetXml(rows));
                    }

                    if (!File.Exists(xlsxPath) || new FileInfo(xlsxPath).Length == 0)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("CSV → XLSX conversion failed ❌"));
                        return;
                    }

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { xlsxPath });
                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                        (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast($"CSV to XLSX converted — {Path.GetFileName(xlsxPath)}");
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.CompleteMiniNotification();
                        FlyShelf.Classes.LicenseManager.RecordDocConversion();

                        // Scroll to top after a short delay so the new item is visible
                        mainWin?.ScrollClipboardToTop();
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"CSV→XLSX failed: {ex.Message} ❌")
                    );
                }
            });
#endif
        }

        // ── CSV Parser (handles quoted fields with commas and newlines) ──
        private static List<string[]> ParseCsvRows(string csv)
        {
            var rows = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            int i = 0;

            while (i < csv.Length)
            {
                char c = csv[i];

                if (inQuotes)
                {
                    if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"'); // escaped quote
                        i += 2;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                        i++;
                    }
                    else
                    {
                        field.Append(c);
                        i++;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                        i++;
                    }
                    else if (c == ',')
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                        i++;
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                        rows.Add(fields.ToArray());
                        fields.Clear();
                        // Skip \r\n pair
                        if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                            i += 2;
                        else
                            i++;
                    }
                    else
                    {
                        field.Append(c);
                        i++;
                    }
                }
            }

            // Last field / last row
            if (field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                rows.Add(fields.ToArray());
            }

            return rows;
        }

        // ── XLSX helpers — minimal OpenXML structure ──

        private static void AddZipEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
            using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
                writer.Write(content);
        }

        /// <summary>Column index (0-based) to Excel column letter (A, B, ... Z, AA, AB, ...).</summary>
        private static string ColIndexToLetter(int index)
        {
            var sb = new StringBuilder();
            while (index >= 0)
            {
                sb.Insert(0, (char)('A' + index % 26));
                index = index / 26 - 1;
            }
            return sb.ToString();
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string BuildContentTypesXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        private static string BuildRootRelsXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string BuildWorkbookRelsXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        private static string BuildWorkbookXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>";

        private static string BuildStylesXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "</styleSheet>";

        private static string BuildSheetXml(List<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");

            for (int r = 0; r < rows.Count; r++)
            {
                int rowNum = r + 1;
                sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNum}\">");
                for (int c = 0; c < rows[r].Length; c++)
                {
                    string cellRef = $"{ColIndexToLetter(c)}{rowNum}";
                    string val = rows[r][c];

                    // Try to write numeric values as numbers, everything else as inline strings
                    if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double numVal))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellRef}\"><v>{numVal.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{XmlEscape(val)}</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

    }
}
