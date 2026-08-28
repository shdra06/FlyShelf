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
                FlyShelf.Windows.ToastWindow.ShowToast("Document conversion is not available in the Store version."));
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
                                FlyShelf.Windows.ToastWindow.ShowToast("No markdown content to convert"));
                            return;
                        }
                        workFilePath = Path.Combine(Path.GetTempPath(), $"FlyShelf_MD_{DateTime.Now:yyyyMMdd_HHmmss}.md");
                        File.WriteAllText(workFilePath, mdContent, System.Text.Encoding.UTF8);
                    }
                    else if ((ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code) && (string.IsNullOrEmpty(workFilePath) || !File.Exists(workFilePath)))
                    {
                        string txtContent = !string.IsNullOrEmpty(RawContent) ? RawContent : FileName;
                        if (string.IsNullOrEmpty(txtContent))
                        {
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                FlyShelf.Windows.ToastWindow.ShowToast("No text content to convert"));
                            return;
                        }
                        workFilePath = Path.Combine(Path.GetTempPath(), $"FlyShelf_TXT_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                        File.WriteAllText(workFilePath, txtContent, System.Text.Encoding.UTF8);
                    }
                    else if (string.IsNullOrEmpty(workFilePath) || !File.Exists(workFilePath))
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("File not found — cannot convert"));
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
                    // ROBUST MULTI-TIER PDF CONVERSION ENGINE
                    // ═══════════════════════════════════════════════════════
                    if (ext == ".MD")
                    {
                        converted = await FlyShelf.Classes.ConversionUtils.ConvertMarkdownToPdfAsync(workFilePath, targetPdf);
                    }
                    else if (ext == ".TXT" || ext == ".LOG" || ext == ".CSV" || ext == ".JSON" || ext == ".XML" || ext == ".CS" || ext == ".PY" || ext == ".JS" || ext == ".CPP" || ext == ".H" || ext == ".TS" || ext == ".HTML" || ext == ".CSS")
                    {
                        converted = FlyShelf.Classes.ConversionUtils.ConvertTextToPdf(workFilePath, targetPdf);
                    }
                    else if (ext == ".DOCX" || ext == ".DOC" || ext == ".RTF")
                    {
                        string res = await FlyShelf.Classes.ConversionUtils.ConvertDocToPdfAsync(workFilePath, targetPdf);
                        converted = !string.IsNullOrEmpty(res) && File.Exists(res);
                    }
                    else if (ext == ".PNG" || ext == ".JPG" || ext == ".JPEG" || ext == ".WEBP" || ext == ".BMP" || ext == ".GIF" || ext == ".TIFF" || ext == ".ICO")
                    {
                        string res = FlyShelf.Classes.ConversionUtils.ConvertImageToPdf(workFilePath, targetPdf);
                        converted = !string.IsNullOrEmpty(res) && File.Exists(res);
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
                            FlyShelf.Windows.ToastWindow.ShowToast("Document conversion failed — check document format");
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
        // NATIVE TXT/MD → PDF (delegated to robust ConversionUtils engine)
        // ═══════════════════════════════════════════════════════════════
        private static bool ConvertTextToPdfNative(string inputPath, string outputPdf)
        {
            return FlyShelf.Classes.ConversionUtils.ConvertTextToPdf(inputPath, outputPdf);
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
        /// Convert an image to a PDF file using the robust ConversionUtils engine.
        /// Handles PNG, JPG, WebP, BMP, GIF, TIFF, ICO, EXIF rotation, and non-blocking FileShare.
        /// </summary>
        public void ConvertImageToPdf()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("Image file not found — cannot convert"));
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
                        FlyShelf.Windows.ToastWindow.ShowProgress("Converting image to PDF", 20);
                        FlyShelf.Controls.FlyShelfWidgetControl.Instance?.ShowConversionNotification(
                            Path.GetExtension(FilePath).TrimStart('.'), "PDF");
                    });

                    string outputPdf = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                    string resultPath = FlyShelf.Classes.ConversionUtils.ConvertImageToPdf(FilePath, outputPdf);
                    if (string.IsNullOrEmpty(resultPath) || !File.Exists(resultPath))
                    {
                        throw new Exception("Image conversion engine failed to produce valid PDF");
                    }

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { resultPath });
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
                    FlyShelf.Windows.ToastWindow.ShowToast("Image file not found — cannot convert"));
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
                    FlyShelf.Windows.ToastWindow.ShowToast($"Unsupported target format: {targetFormat}"));
                return;
            }

            // Normalise "jpeg" → "jpg" for the file extension
            string ext = fmt == "jpeg" ? "jpg" : fmt;

            Task.Run(() =>
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"Converting image to {ext.ToUpperInvariant()}...")
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
                            FlyShelf.Windows.ToastWindow.ShowToast($"Image conversion failed"));
                        return;
                    }

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPath });
                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                        (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image → {ext.ToUpperInvariant()} converted! {Path.GetFileName(outputPath)}");
                        FlyShelf.Classes.LicenseManager.RecordImageToPdf();

                        // Scroll to top after a short delay so the new item is visible
                        mainWin?.ScrollClipboardToTop();
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"Image conversion failed: {ex.Message}")
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
                FlyShelf.Windows.ToastWindow.ShowToast("CSV conversion is not available in the Store version."));
            return;
#else
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("CSV file not found — cannot convert"));
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
                            FlyShelf.Windows.ToastWindow.ShowToast("CSV → XLSX conversion failed"));
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
                        FlyShelf.Windows.ToastWindow.ShowToast($"CSV→XLSX failed: {ex.Message}")
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
