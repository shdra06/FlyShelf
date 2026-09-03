using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FlyShelf.Classes.Utils;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FlyShelf.Classes
{
    public static class ConversionUtils
    {
        // ═══════════════════════════════════════════════════════════════════
        // IMAGE TO PDF CONVERSION (Fail-Proof Multi-Format & EXIF Aware)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts any image (PNG, JPG, WebP, BMP, GIF, TIFF, ICO) to a crisp PDF.
        /// Resolves EXIF rotation, strips incompatible alpha channels, uses non-blocking FileShare,
        /// and embeds directly using PDFsharp.
        /// </summary>
        public static string ConvertImageToPdf(string imagePath, string outputPath = null)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return null;

            if (string.IsNullOrEmpty(outputPath))
            {
                string outputDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "FlyShelf", "Converted");
                Directory.CreateDirectory(outputDir);

                outputPath = Path.Combine(outputDir,
                    Path.GetFileNameWithoutExtension(imagePath) + "_" + Guid.NewGuid().ToString()[..4] + ".pdf");
            }

            try
            {
                // 1. Read bytes safely with FileShare.ReadWrite to avoid file locks
                byte[] rawBytes;
                using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    rawBytes = ms.ToArray();
                }

                if (rawBytes.Length == 0) return null;

                // 2. Decode image using WPF BitmapDecoder
                using var inputMs = new MemoryStream(rawBytes);
                var decoder = BitmapDecoder.Create(inputMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return null;

                var frame = decoder.Frames[0];
                BitmapSource sourceFrame = frame;

                // 3. Auto-handle EXIF Orientation (rotation/flip)
                int rotationAngle = 0;
                if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("/app1/ifd/{ushort=274}"))
                {
                    try
                    {
                        var orientVal = meta.GetQuery("/app1/ifd/{ushort=274}");
                        if (orientVal is ushort orientation)
                        {
                            rotationAngle = orientation switch
                            {
                                6 => 90,   // Rotate 90 CW
                                3 => 180,  // Rotate 180
                                8 => 270,  // Rotate 270 CW (90 CCW)
                                _ => 0
                            };
                        }
                    }
                    catch { }
                }

                if (rotationAngle != 0)
                {
                    var rotated = new TransformedBitmap(sourceFrame, new System.Windows.Media.RotateTransform(rotationAngle));
                    rotated.Freeze();
                    sourceFrame = rotated;
                }

                // 4. H1 fix: Composite alpha images onto white background before converting to Bgr24
                if (sourceFrame.Format == System.Windows.Media.PixelFormats.Bgra32 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Pbgra32 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Rgba64 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Prgba64)
                {
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(System.Windows.Media.Brushes.White, null,
                            new System.Windows.Rect(0, 0, sourceFrame.PixelWidth, sourceFrame.PixelHeight));
                        dc.DrawImage(sourceFrame,
                            new System.Windows.Rect(0, 0, sourceFrame.PixelWidth, sourceFrame.PixelHeight));
                    }
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        sourceFrame.PixelWidth, sourceFrame.PixelHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    var composited = new FormatConvertedBitmap();
                    composited.BeginInit();
                    composited.Source = rtb;
                    composited.DestinationFormat = System.Windows.Media.PixelFormats.Bgr24;
                    composited.EndInit();
                    composited.Freeze();
                    sourceFrame = composited;
                }
                else if (sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed8 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed4 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed2 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed1)
                {
                    var converted = new FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = sourceFrame;
                    converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgr24;
                    converted.EndInit();
                    converted.Freeze();
                    sourceFrame = converted;
                }

                // 5. Encode normalized image into high-quality JPEG stream for PDFsharp
                using var jpegMs = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(sourceFrame));
                encoder.Save(jpegMs);
                jpegMs.Position = 0;

                // 6. Create PDF with PDFsharp matching the image's aspect ratio and dimensions
                using (var doc = new PdfDocument())
                {
                    doc.Info.Title = Path.GetFileNameWithoutExtension(imagePath);
                    doc.Info.Creator = "FlyShelf PDF Engine";

                    var page = doc.AddPage();
                    using (var xImg = XImage.FromStream(jpegMs))
                    {
                        double imgW = xImg.PointWidth;
                        double imgH = xImg.PointHeight;

                        if (imgW <= 0 || imgH <= 0)
                        {
                            imgW = sourceFrame.PixelWidth * 72.0 / 96.0;
                            imgH = sourceFrame.PixelHeight * 72.0 / 96.0;
                        }

                        // Set page size exactly to image dimensions
                        page.Width = XUnit.FromPoint(imgW);
                        page.Height = XUnit.FromPoint(imgH);

                        using (var gfx = XGraphics.FromPdfPage(page))
                        {
                            gfx.DrawImage(xImg, 0, 0, page.Width.Point, page.Height.Point);
                        }
                    }
                    doc.Save(outputPath);
                }

                return File.Exists(outputPath) ? outputPath : null;
            }
            catch (Exception ex)
            {
                Logger.LogAction("IMAGE2PDF_ERR", $"ConvertImageToPdf error on {Path.GetFileName(imagePath)}: {ex.Message}");

                // Fallback attempt: Basic direct PDFsharp image load
                try
                {
                    using var doc = new PdfDocument();
                    var page = doc.AddPage();
                    using var img = XImage.FromFile(imagePath);
                    page.Width = XUnit.FromPoint(img.PointWidth);
                    page.Height = XUnit.FromPoint(img.PointHeight);
                    using (var gfx = XGraphics.FromPdfPage(page))
                    {
                        gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                    }
                    doc.Save(outputPath);
                    return File.Exists(outputPath) ? outputPath : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // DOC / DOCX TO PDF CONVERSION (Multi-Tier with Zero-Dependency C#)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a DOC/DOCX/RTF/ODT file to PDF with fail-proof multi-tier fallback.
        /// Fully handles files that are currently OPEN in Word or other editors by creating
        /// an isolated, non-locking shadow copy in %TEMP%.
        /// Tier 1: Word COM (isolated STA thread with strict 6s timeout & full dialog suppression).
        /// Tier 2: LibreOffice Headless (if installed).
        /// Tier 3: Pure C# OpenXml Native Engine (100% offline, zero external dependencies).
        /// Tier 4: Pure C# Text / Structure Fallback Engine.
        /// </summary>
        public static async Task<string> ConvertDocToPdfAsync(string docPath, string outputPath = null)
        {
            if (string.IsNullOrEmpty(docPath) || !File.Exists(docPath)) return null;

            if (string.IsNullOrEmpty(outputPath))
            {
                string outputDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "FlyShelf", "Converted");
                Directory.CreateDirectory(outputDir);

                outputPath = Path.Combine(outputDir,
                    Path.GetFileNameWithoutExtension(docPath) + "_" + Guid.NewGuid().ToString()[..4] + ".pdf");
            }

            string ext = Path.GetExtension(docPath).ToLowerInvariant();

            // 1. Create a safe shadow copy to completely isolate from file locks / active Word editing
            string shadowDocPath = CreateSafeShadowCopy(docPath);
            string workingDocPath = shadowDocPath ?? docPath;

            try
            {
                // ── Tier 1: Try Word COM on isolated STA thread with strict 6s timeout ──
                if (ext == ".docx" || ext == ".doc" || ext == ".rtf")
                {
                    if (Type.GetTypeFromProgID("Word.Application") != null)
                    {
                        try
                        {
                            bool wordSuccess = await TryWordComConvertAsync(workingDocPath, outputPath);
                            if (wordSuccess && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                            {
                                Logger.LogAction("DOC2PDF_WORD", $"Word COM converted {Path.GetFileName(docPath)} successfully.");
                                return outputPath;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("DOC2PDF_WORD_FAIL", $"Word COM failed for {Path.GetFileName(docPath)}: {ex.Message}");
                        }
                    }
                }

                // ── Tier 2: Try LibreOffice Headless if installed ──
                try
                {
                    bool libreSuccess = TryLibreOfficeConvert(workingDocPath, outputPath);
                    if (libreSuccess && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        Logger.LogAction("DOC2PDF_LIBRE", $"LibreOffice converted {Path.GetFileName(docPath)} successfully.");
                        return outputPath;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DOC2PDF_LIBRE_FAIL", $"LibreOffice failed: {ex.Message}");
                }

                // ── Tier 3: Pure C# Native DOCX Engine (Zero external dependencies) ──
                if (ext == ".docx")
                {
                    try
                    {
                        Logger.LogAction("DOC2PDF_NATIVE", $"Converting {Path.GetFileName(docPath)} using Native OpenXml Engine...");
                        bool nativeSuccess = DocxToPdfConverter.Convert(workingDocPath, outputPath);
                        if (nativeSuccess && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                        {
                            Logger.LogAction("DOC2PDF_NATIVE_OK", $"Native OpenXml converted {Path.GetFileName(docPath)} successfully.");
                            return outputPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("DOC2PDF_NATIVE_FAIL", $"Native OpenXml engine error: {ex.Message}");
                    }
                }

                // ── Tier 4: Text fallback for .docx / .doc / .rtf / .odt if Office/OpenXml fails ──
                try
                {
                    string txtContent = null;
                    if (ext == ".docx")
                    {
                        txtContent = DocxToPdfConverter.ExtractTextFallback(workingDocPath);
                    }
                    else if (ext == ".rtf")
                    {
                        txtContent = ExtractTextFromRtf(workingDocPath);
                    }
                    else if (ext == ".doc" || ext == ".odt")
                    {
                        txtContent = ExtractTextFromDoc(workingDocPath);
                    }

                    if (!string.IsNullOrEmpty(txtContent))
                    {
                        Logger.LogAction("DOC2PDF_TEXT_FALLBACK", $"Using text fallback engine for {Path.GetFileName(docPath)}...");
                        bool txtSuccess = ConvertTextToPdf(txtContent, outputPath, Path.GetFileNameWithoutExtension(docPath));
                        if (txtSuccess && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                        {
                            return outputPath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DOC2PDF_FALLBACK_FAIL", $"Text fallback failed: {ex.Message}");
                }
            }
            finally
            {
                // Clean up shadow copy
                if (!string.IsNullOrEmpty(shadowDocPath) && File.Exists(shadowDocPath))
                {
                    try { File.Delete(shadowDocPath); } catch { }
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts Word COM conversion on an STA thread with a strict 6-second timeout
        /// and complete dialog suppression to avoid blocking or hanging on open Word instances.
        /// </summary>
        private static Task<bool> TryWordComConvertAsync(string inputPath, string outputPdf)
        {
            var tcs = new TaskCompletionSource<bool>();
            var staThread = new System.Threading.Thread(() =>
            {
                object wordAppObj = null;
                dynamic wordApp = null;
                dynamic doc = null;
                try
                {
                    var wordType = Type.GetTypeFromProgID("Word.Application");
                    if (wordType == null)
                    {
                        tcs.TrySetResult(false);
                        return;
                    }

                    wordAppObj = Activator.CreateInstance(wordType);
                    if (wordAppObj == null)
                    {
                        tcs.TrySetResult(false);
                        return;
                    }

                    wordApp = wordAppObj;

                    // ── SUPPRESS ALL DIALOGS, MACROS, POPUPS & AUTO-UPDATES ──
                    wordApp.Visible = false;
                    wordApp.DisplayAlerts = 0;              // wdAlertsNone
                    wordApp.AutomationSecurity = 3;          // msoAutomationSecurityForceDisable
                    wordApp.Options.DoNotPromptForConvert = true;
                    try { wordApp.Options.WarnBeforeSavingPrintOrMailMerge = false; } catch { }
                    try { wordApp.FeatureInstall = 0; } catch { }

                    // Open document with full automation flags
                    doc = wordApp.Documents.Open(
                        inputPath,              // FileName
                        false,                  // ConfirmConversions
                        true,                   // ReadOnly
                        false,                  // AddToRecentFiles
                        "",                     // PasswordDocument
                        "",                     // PasswordTemplate
                        true,                   // Revert
                        "",                     // WritePasswordDocument
                        "",                     // WritePasswordTemplate
                        Type.Missing,           // Format
                        Type.Missing,           // Encoding
                        false,                  // Visible
                        false,                  // OpenAndRepair
                        Type.Missing,           // DocumentDirection
                        true,                   // NoEncodingDialog
                        Type.Missing            // XMLTransform
                    );

                    if (doc == null)
                    {
                        tcs.TrySetResult(false);
                        return;
                    }

                    string dir = Path.GetDirectoryName(outputPdf);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // Export to PDF (wdExportFormatPDF = 17)
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
                        false                   // UseISO19005_1
                    );

                    bool success = File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0;
                    tcs.TrySetResult(success);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("WORD_COM_ERR", $"Word COM exception: {ex.Message}");
                    tcs.TrySetResult(false);
                }
                finally
                {
                    if (doc != null)
                    {
                        try { doc.Close(0 /* wdDoNotSaveChanges */); } catch { }
                        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } catch { }
                    }
                    if (wordApp != null)
                    {
                        try { wordApp.Quit(0); } catch { }
                    }
                    if (wordAppObj != null)
                    {
                        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(wordAppObj); } catch { }
                    }
                }
            });

            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            // Strict 6-second timeout — fail fast to Pure C# Native DOCX engine
            // H3 fix: Also kill orphaned Word COM processes on timeout
            Task.Run(async () =>
            {
                await Task.Delay(6000);
                if (!tcs.Task.IsCompleted)
                {
                    Logger.LogAction("WORD_COM_TIMEOUT", "Word COM timed out after 6s — falling back immediately.");
                    tcs.TrySetResult(false);

                    // Give the STA thread 2 more seconds to clean up, then force-kill any orphaned Word
                    await Task.Delay(2000);
                    try
                    {
                        foreach (var proc in Process.GetProcessesByName("WINWORD"))
                        {
                            try
                            {
                                if (proc.MainWindowHandle == IntPtr.Zero) // Invisible/automation instance
                                {
                                    proc.Kill();
                                    Logger.LogAction("WORD_COM_KILLED", $"Killed orphaned WINWORD.EXE (PID {proc.Id}).");
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Batch converts multiple DOC/DOCX files to PDF linearly.
        /// Completely avoids recursion while ensuring each file benefits from the full multi-tier fallback pipeline.
        /// </summary>
        public static async Task<string[]> ConvertDocsToPdfsAsync(string[] docPaths)
        {
            if (docPaths == null || docPaths.Length == 0) return Array.Empty<string>();

            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Converted");
            Directory.CreateDirectory(outputDir);

            var convertedPaths = new List<string>();

            foreach (string docPath in docPaths)
            {
                if (string.IsNullOrEmpty(docPath) || !File.Exists(docPath)) continue;

                string pdfPath = Path.Combine(outputDir,
                    Path.GetFileNameWithoutExtension(docPath) + "_" + Guid.NewGuid().ToString()[..4] + ".pdf");

                string result = await ConvertDocToPdfAsync(docPath, pdfPath);
                if (!string.IsNullOrEmpty(result) && File.Exists(result))
                {
                    convertedPaths.Add(result);
                }
            }

            return convertedPaths.ToArray();
        }

        private static bool TryLibreOfficeConvert(string docPath, string outputPdf)
        {
            string[] libreOfficePaths = new[] {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
            };

            string sofficePath = libreOfficePaths.FirstOrDefault(File.Exists);
            if (sofficePath == null) return false;

            string outDir = Path.GetDirectoryName(outputPdf) ?? Path.GetTempPath();
            var psi = new ProcessStartInfo
            {
                FileName = sofficePath,
                Arguments = $"--headless --norestore --nofirststartwizard --convert-to pdf --outdir \"{outDir}\" \"{docPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            bool exited = proc.WaitForExit(15000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            string expectedPdf = Path.Combine(outDir, Path.GetFileNameWithoutExtension(docPath) + ".pdf");
            if (File.Exists(expectedPdf))
            {
                if (expectedPdf != outputPdf)
                {
                    try { File.Copy(expectedPdf, outputPdf, true); } catch { }
                    // M3 fix: Clean up intermediate PDF to prevent temp file leak
                    try { File.Delete(expectedPdf); } catch { }
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates an isolated temporary shadow copy in %TEMP% using non-blocking FileShare.ReadWrite.
        /// Guarantees that converters never fail from locks held by active Word/Excel/IDE processes.
        /// </summary>
        public static string CreateSafeShadowCopy(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;

                string ext = Path.GetExtension(sourcePath);
                string tempCopy = Path.Combine(Path.GetTempPath(), $"FlyShelf_Shadow_{Guid.NewGuid():N}{ext}");

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = new FileStream(tempCopy, FileMode.Create, FileAccess.Write))
                        {
                            src.CopyTo(dst);
                        }
                        if (File.Exists(tempCopy) && new FileInfo(tempCopy).Length > 0)
                        {
                            return tempCopy;
                        }
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(50 * (1 << attempt));
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadFileBytesSafe(string filePath)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(50 * (1 << attempt));
                }
                catch
                {
                    break;
                }
            }
            return null;
        }

        private static string ReadFileTextSafe(string filePath)
        {
            byte[] bytes = ReadFileBytesSafe(filePath);
            if (bytes == null || bytes.Length == 0) return string.Empty;
            return Encoding.UTF8.GetString(bytes);
        }

        // ═══════════════════════════════════════════════════════════════════
        // MARKDOWN TO PDF CONVERSION (Multi-Tier with Zero-Dependency C#)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a Markdown (.md) file to a styled PDF.
        /// Tier 1: Edge WebView2 (High fidelity).
        /// Tier 2: Pure C# MarkdownToPdfConverter (100% offline, guaranteed fallback).
        /// </summary>
        public static async Task<bool> ConvertMarkdownToPdfAsync(string mdPath, string outputPdf)
        {
            if (!File.Exists(mdPath)) return false;

            string mdContent = ReadFileTextSafe(mdPath);
            if (string.IsNullOrEmpty(mdContent))
            {
                try { mdContent = File.ReadAllText(mdPath); } catch { return false; }
            }

            // ── Tier 1: Try WebView2 Converter ──
            try
            {
                bool webViewSuccess = await WebView2Converter.ConvertMarkdownToPdfAsync(mdContent, outputPdf, mdPath);
                if (webViewSuccess && File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD2PDF_WEBVIEW_FAIL", $"WebView2 conversion failed: {ex.Message}");
            }

            // ── Tier 2: Native C# Markdown Engine (Guaranteed 100% Offline) ──
            try
            {
                Logger.LogAction("MD2PDF_NATIVE", $"Falling back to Native C# Markdown Engine for {Path.GetFileName(mdPath)}...");
                bool nativeSuccess = MarkdownToPdfConverter.Convert(mdPath, outputPdf);
                if (nativeSuccess && File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD2PDF_NATIVE_ERR", $"Native Markdown converter error: {ex.Message}");
            }

            // ── Tier 3: Native Text Fallback ──
            try
            {
                return ConvertTextToPdf(mdContent, outputPdf, Path.GetFileName(mdPath));
            }
            catch { return false; }
        }

        // ═══════════════════════════════════════════════════════════════════
        // TEXT / CODE / CSV TO PDF CONVERSION (Unicode & Table Grid Aware)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts raw text, code files, logs, or CSV data into a clean, paginated PDF.
        /// Handles Unicode characters, word wrapping, syntax cards for code, and tables for CSV.
        /// </summary>
        public static bool ConvertTextToPdf(string textOrPath, string outputPdf, string documentTitle = null)
        {
            try
            {
                FlyShelfFontResolver.EnsureRegistered();
                string text = textOrPath;
                if (File.Exists(textOrPath))
                {
                    if (string.IsNullOrEmpty(documentTitle)) documentTitle = Path.GetFileName(textOrPath);
                    text = ReadFileTextSafe(textOrPath);
                    if (string.IsNullOrEmpty(text))
                    {
                        try { text = File.ReadAllText(textOrPath); } catch { }
                    }
                }

                if (string.IsNullOrEmpty(text)) text = "(empty document)";
                if (string.IsNullOrEmpty(documentTitle)) documentTitle = "Document";

                bool isCsv = documentTitle.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

                using var pdfDoc = new PdfDocument();
                pdfDoc.Info.Title = documentTitle;
                pdfDoc.Info.Creator = "FlyShelf Native Text Engine";

                double pageW = 595.28;
                double pageH = 841.89;
                double margin = 45.0;
                double usableW = pageW - (margin * 2);
                double usableH = pageH - (margin * 2);

                if (isCsv)
                {
                    RenderCsvAsPdf(text, pdfDoc, pageW, pageH, margin, usableW, documentTitle);
                }
                else
                {
                    RenderPlainTextAsPdf(text, pdfDoc, pageW, pageH, margin, usableW, documentTitle);
                }

                // Render page footers
                var footerFont = new XFont("Segoe UI", 8.5, XFontStyleEx.Regular);
                var footerBrush = new XSolidBrush(XColor.FromArgb(148, 163, 184));
                for (int i = 0; i < pdfDoc.PageCount; i++)
                {
                    var page = pdfDoc.Pages[i];
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    string pageNum = $"{i + 1} / {pdfDoc.PageCount}";
                    var sz = gfx.MeasureString(pageNum, footerFont);
                    gfx.DrawString(pageNum, footerFont, footerBrush, new XPoint((pageW - sz.Width) / 2.0, pageH - 22.0));
                }

                string dir = Path.GetDirectoryName(outputPdf);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (pdfDoc.PageCount == 0)
                {
                    pdfDoc.AddPage();
                }
                pdfDoc.Save(outputPdf);
                return File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 0;
            }
            catch (Exception ex)
            {
                Logger.LogAction("TXT2PDF_ERR", $"ConvertTextToPdf error: {ex.Message}");
                return false;
            }
        }

        private static void RenderPlainTextAsPdf(string text, PdfDocument doc, double pageW, double pageH, double margin, double usableW, string title)
        {
            var headerFont = new XFont("Segoe UI", 12.0, XFontStyleEx.Bold);
            var headerBrush = new XSolidBrush(XColor.FromArgb(30, 41, 59));
            var font = new XFont("Consolas", 9.5, XFontStyleEx.Regular);
            var textBrush = new XSolidBrush(XColor.FromArgb(15, 23, 42));
            var lineNumBrush = new XSolidBrush(XColor.FromArgb(148, 163, 184));
            var divPen = new XPen(XColor.FromArgb(226, 232, 240), 0.75);

            double lineHeight = 13.5;
            double curY = margin;
            int lineNum = 1;

            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(pageW);
            page.Height = XUnit.FromPoint(pageH);
            var gfx = XGraphics.FromPdfPage(page);

            // Document Header on Page 1
            gfx.DrawString(title, headerFont, headerBrush, new XPoint(margin, curY + 12.0));
            curY += 20.0;
            gfx.DrawLine(divPen, margin, curY, margin + usableW, curY);
            curY += 12.0;

            var rawLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            double lineNumWidth = 28.0;
            double contentW = usableW - lineNumWidth;

            foreach (var rawLine in rawLines)
            {
                var wrapped = WrapText(rawLine, font, contentW, gfx);
                if (wrapped.Count == 0) wrapped.Add("");

                bool isFirstSubLine = true;
                foreach (var subLine in wrapped)
                {
                    if (curY + lineHeight > pageH - margin)
                    {
                        gfx.Dispose();
                        page = doc.AddPage();
                        page.Width = XUnit.FromPoint(pageW);
                        page.Height = XUnit.FromPoint(pageH);
                        gfx = XGraphics.FromPdfPage(page);
                        curY = margin;
                    }

                    if (isFirstSubLine)
                    {
                        gfx.DrawString(lineNum.ToString().PadLeft(3), font, lineNumBrush, new XPoint(margin, curY + lineHeight * 0.75));
                        isFirstSubLine = false;
                    }

                    gfx.DrawString(subLine, font, textBrush, new XPoint(margin + lineNumWidth, curY + lineHeight * 0.75));
                    curY += lineHeight;
                }
                lineNum++;
            }

            gfx.Dispose();
        }

        private static void RenderCsvAsPdf(string csvText, PdfDocument doc, double pageW, double pageH, double margin, double usableW, string title)
        {
            var parsedRows = ParseCsvRows(csvText);
            if (parsedRows.Count == 0) return;

            int colCount = parsedRows.Max(r => r.Count);
            if (colCount == 0) return;

            double colW = usableW / colCount;
            var cellFont = new XFont("Segoe UI", 9.0, XFontStyleEx.Regular);
            var headerFont = new XFont("Segoe UI", 9.0, XFontStyleEx.Bold);
            var borderPen = new XPen(XColor.FromArgb(203, 213, 225), 0.75);
            var headerBg = new XSolidBrush(XColor.FromArgb(241, 245, 249));

            double curY = margin;
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(pageW);
            page.Height = XUnit.FromPoint(pageH);
            var gfx = XGraphics.FromPdfPage(page);

            bool isHeader = true;
            foreach (var row in parsedRows)
            {
                double maxCellHeight = 18.0;
                var wrappedCells = new List<List<string>>();

                for (int c = 0; c < colCount; c++)
                {
                    string text = c < row.Count ? row[c] : "";
                    var font = isHeader ? headerFont : cellFont;
                    var wrapped = WrapText(text, font, colW - 8.0, gfx);
                    wrappedCells.Add(wrapped);
                    double cellH = Math.Max(18.0, (wrapped.Count * 12.0) + 6.0);
                    if (cellH > maxCellHeight) maxCellHeight = cellH;
                }

                if (curY + maxCellHeight > pageH - margin)
                {
                    gfx.Dispose();
                    page = doc.AddPage();
                    page.Width = XUnit.FromPoint(pageW);
                    page.Height = XUnit.FromPoint(pageH);
                    gfx = XGraphics.FromPdfPage(page);
                    curY = margin;
                }

                double rowX = margin;
                for (int c = 0; c < colCount; c++)
                {
                    var rect = new XRect(rowX, curY, colW, maxCellHeight);
                    if (isHeader) gfx.DrawRectangle(headerBg, rect);
                    gfx.DrawRectangle(borderPen, rect);

                    if (c < wrappedCells.Count)
                    {
                        var cellLines = wrappedCells[c];
                        var font = isHeader ? headerFont : cellFont;
                        var brush = isHeader ? new XSolidBrush(XColor.FromArgb(30, 41, 59)) : new XSolidBrush(XColor.FromArgb(51, 65, 85));

                        double textY = curY + 3.0;
                        foreach (var l in cellLines)
                        {
                            gfx.DrawString(l, font, brush, new XPoint(rowX + 4.0, textY + 9.0));
                            textY += 12.0;
                        }
                    }

                    rowX += colW;
                }

                curY += maxCellHeight;
                isHeader = false;
            }

            gfx.Dispose();
        }

        private static List<string> WrapText(string text, XFont font, double maxWidth, XGraphics gfx)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.None);
            string current = "";

            foreach (var w in words)
            {
                string test = string.IsNullOrEmpty(current) ? w : current + " " + w;
                var size = gfx.MeasureString(test, font);
                if (size.Width > maxWidth && !string.IsNullOrEmpty(current))
                {
                    lines.Add(current);
                    current = w;
                }
                else
                {
                    current = test;
                }
            }
            if (!string.IsNullOrEmpty(current)) lines.Add(current);

            return lines;
        }

        private static string ExtractTextFromRtf(string rtfPath)
        {
            try
            {
                string rtf = ReadFileTextSafe(rtfPath);
                if (string.IsNullOrEmpty(rtf))
                {
                    try { rtf = File.ReadAllText(rtfPath); } catch { return null; }
                }
                return System.Text.RegularExpressions.Regex.Replace(rtf, @"\\[a-zA-Z0-9\-]+ ?|[{}]", "");
            }
            catch { return null; }
        }

        private static string ExtractTextFromDoc(string docPath)
        {
            try
            {
                byte[] bytes = ReadFileBytesSafe(docPath);
                if (bytes == null || bytes.Length == 0)
                {
                    try { bytes = File.ReadAllBytes(docPath); } catch { return null; }
                }
                if (bytes == null || bytes.Length == 0) return null;
                // Extract printable ASCII/Unicode runs
                var sb = new System.Text.StringBuilder();
                foreach (byte b in bytes)
                {
                    if ((b >= 32 && b <= 126) || b == 10 || b == 13 || b == 9)
                    {
                        sb.Append((char)b);
                    }
                }
                return sb.ToString();
            }
            catch { return null; }
        }
        private static List<List<string>> ParseCsvRows(string csvText)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrEmpty(csvText)) return rows;

            var lines = csvText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (lines.Length == 0) return rows;

            // Auto-detect delimiter
            char delimiter = ',';
            if (lines[0].Count(c => c == '\t') > lines[0].Count(c => c == ',')) delimiter = '\t';
            else if (lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',')) delimiter = ';';

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cells = new List<string>();
                var cur = new System.Text.StringBuilder();
                bool inQuotes = false;

                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c == '"')
                    {
                        if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                        {
                            cur.Append('"');
                            i++; // skip escaped quote
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                        }
                    }
                    else if (c == delimiter && !inQuotes)
                    {
                        cells.Add(cur.ToString().Trim());
                        cur.Clear();
                    }
                    else
                    {
                        cur.Append(c);
                    }
                }
                cells.Add(cur.ToString().Trim());
                rows.Add(cells);
            }
            return rows;
        }
    }
}
