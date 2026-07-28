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
                Path.GetFileNameWithoutExtension(imagePath) + "_" + Guid.NewGuid().ToString()[..4] + ".pdf");

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

        /// <summary>Converts a DOC/DOCX file to PDF using Word COM via dynamic late-binding. Eliminates PowerShell overhead.</summary>
        public static async Task<string> ConvertDocToPdfAsync(string docPath)
        {
            var results = await ConvertDocsToPdfsAsync(new[] { docPath });
            return results.Length > 0 ? results[0] : null;
        }

        /// <summary>Batch converts multiple DOC/DOCX files to PDF. Reuses a single Word instance for maximum speed and zero lag.
        /// [FIX C1/C2/C3/R2]: Runs on explicit STA thread, has 60s timeout, full dialog suppression, ExportAsFixedFormat.</summary>
        public static async Task<string[]> ConvertDocsToPdfsAsync(string[] docPaths)
        {
#if MSIX_STORE
            await Task.CompletedTask;
            return Array.Empty<string>();
#else
            if (docPaths == null || docPaths.Length == 0) return Array.Empty<string>();

            string outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Converted");
            Directory.CreateDirectory(outputDir);

            var tcs = new TaskCompletionSource<string[]>();
            var staThread = new System.Threading.Thread(() =>
            {
                object word = null;
                var convertedPaths = new System.Collections.Generic.List<string>();
                try
                {
                    Type wordType = Type.GetTypeFromProgID("Word.Application");
                    if (wordType == null)
                    {
                        tcs.TrySetResult(Array.Empty<string>());
                        return;
                    }

                    word = Activator.CreateInstance(wordType);
                    dynamic dynamicWord = word;

                    // ── FULL DIALOG SUPPRESSION (matches TryWordComConvertCore) ──
                    dynamicWord.Visible = false;
                    dynamicWord.DisplayAlerts = 0;              // wdAlertsNone
                    dynamicWord.AutomationSecurity = 3;          // msoAutomationSecurityForceDisable
                    dynamicWord.Options.DoNotPromptForConvert = true;
                    try { dynamicWord.Options.WarnBeforeSavingPrintOrMailMerge = false; } catch { }

                    foreach (string docPath in docPaths)
                    {
                        if (!File.Exists(docPath)) continue;

                        dynamic doc = null;
                        try
                        {
                            string pdfPath = Path.Combine(outputDir,
                                Path.GetFileNameWithoutExtension(docPath) + "_" + Guid.NewGuid().ToString()[..4] + ".pdf");

                            // Full 16-param Open with all dialog-triggering options disabled
                            doc = dynamicWord.Documents.Open(
                                docPath,                // FileName
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

                            // ExportAsFixedFormat — reliable silent PDF export
                            doc.ExportAsFixedFormat(
                                pdfPath,                // OutputFileName
                                17,                     // wdExportFormatPDF
                                false,                  // OpenAfterExport
                                0,                      // OptimizeFor: wdExportOptimizeForPrint
                                0,                      // Range: wdExportAllDocument
                                1, 1,                   // From, To
                                0,                      // Item: wdExportDocumentContent
                                true,                   // IncludeDocProps
                                true,                   // KeepIRM
                                0,                      // CreateBookmarks: wdExportCreateNoBookmarks
                                true,                   // DocStructureTags
                                true,                   // BitmapMissingFonts
                                false                   // UseISO19005_1
                            );

                            doc.Close(0 /* wdDoNotSaveChanges */);
                            doc = null;

                            if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
                                convertedPaths.Add(pdfPath);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("DOC2PDF_BATCH_ERR", $"Failed to convert {Path.GetFileName(docPath)}: {ex.Message}");
                        }
                        finally
                        {
                            if (doc != null)
                            {
                                try { doc.Close(0); } catch { }
                                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } catch { }
                            }
                        }
                    }

                    try { dynamicWord.Quit(0); } catch { }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("DOC2PDF_FATAL", $"Fatal Word Interop error: {ex.Message}");
                }
                finally
                {
                    if (word != null)
                    {
                        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(word); } catch { }
                    }
                    tcs.TrySetResult(convertedPaths.ToArray());
                }
            });

            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            // 60-second global timeout — kill Word if it hangs
            _ = Task.Run(async () =>
            {
                await Task.Delay(60000);
                if (!tcs.Task.IsCompleted)
                {
                    Logger.LogAction("DOC2PDF_BATCH", "Batch Word COM timed out after 60s — killing orphaned WINWORD");
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("WINWORD"))
                        {
                            try { if (p.MainWindowTitle == "") p.Kill(); } catch { }
                        }
                    }
                    catch { }
                    tcs.TrySetResult(Array.Empty<string>());
                }
            });

            return await tcs.Task;
#endif
        }


        /// <summary>
        /// Converts a Markdown (.md) file to a styled, highlighted PDF.
        /// Chooses Node.js (Option C) if installed, falling back to Edge WebView2 (Option A).
        /// </summary>
        public static async Task<bool> ConvertMarkdownToPdfAsync(string mdPath, string outputPdf)
        {
            try
            {
                if (!File.Exists(mdPath)) return false;

                bool nodeInstalled = CheckIfNodeInstalled();
                if (nodeInstalled)
                {
                    Logger.LogAction("MD2PDF", "Node.js detected. Attempting Option C (Node compiler)...");
                    bool success = await ConvertMarkdownUsingNodeAsync(mdPath, outputPdf);
                    if (success && File.Exists(outputPdf))
                    {
                        return true;
                    }
                    Logger.LogAction("MD2PDF", "Option C failed. Falling back to Option A (WebView2)...");
                }
                else
                {
                    Logger.LogAction("MD2PDF", "Node.js not detected. Using Option A (WebView2)...");
                }

                // Fallback / Option A: WebView2
                string mdContent = File.ReadAllText(mdPath);
                return await WebView2Converter.ConvertMarkdownToPdfAsync(mdContent, outputPdf, mdPath);
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD2PDF_ERROR", $"Markdown conversion failed: {ex.Message}");
                return false;
            }
        }

        private static bool CheckIfNodeInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    bool exited = proc.WaitForExit(3000); // 3s timeout
                    return exited && proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> ConvertMarkdownUsingNodeAsync(string mdPath, string outputPdf)
        {
            try
            {
                string toolsDir = FindToolsDirectory();
                if (string.IsNullOrEmpty(toolsDir))
                {
                    Logger.LogAction("MD2PDF_NODE", "Could not find tools/md2pdf directory.");
                    return false;
                }

                string toolScript = Path.Combine(toolsDir, "convert.js");
                if (!File.Exists(toolScript))
                {
                    Logger.LogAction("MD2PDF_NODE", $"convert.js not found at: {toolScript}");
                    return false;
                }

                // Check if node_modules exists. If not, trigger a silent npm install first
                string nodeModulesDir = Path.Combine(toolsDir, "node_modules");
                if (!Directory.Exists(nodeModulesDir))
                {
                    Logger.LogAction("MD2PDF_NODE", "node_modules not found. Bootstrapping npm dependencies...");
                    var npmPsi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c npm install --no-audit --no-fund",
                        WorkingDirectory = toolsDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var npmProc = Process.Start(npmPsi))
                    {
                        if (npmProc != null)
                        {
                            // [FIX M-32]: Kill npm if it doesn't exit in time
                            bool npmExited = await Task.Run(() => npmProc.WaitForExit(45000));
                            if (!npmExited)
                            {
                                try { npmProc.Kill(); } catch { }
                                Logger.LogAction("MD2PDF_NPM", "npm install timed out — killed.");
                            }
                        }
                    }
                }

                var nodePsi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{toolScript}\" \"{mdPath}\" \"{outputPdf}\"",
                    WorkingDirectory = toolsDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                bool success = await Task.Run(() =>
                {
                    using (var proc = Process.Start(nodePsi))
                    {
                        if (proc == null) return false;
                        // [FIX M-22]: Read error before WaitForExit to prevent pipe buffer deadlock
                        string err = proc.StandardError.ReadToEnd();
                        bool exited = proc.WaitForExit(30000); // 30s timeout
                        if (!exited)
                        {
                            try { proc.Kill(); } catch { } // Best-effort: failure is acceptable
                            Logger.LogAction("MD2PDF_NODE", "Node process timed out.");
                            return false;
                        }
                        if (proc.ExitCode != 0)
                        {
                            Logger.LogAction("MD2PDF_NODE", $"Node process exited with code {proc.ExitCode}. Error: {err}");
                            return false;
                        }
                        return true;
                    }
                });

                return success && File.Exists(outputPdf);
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD2PDF_NODE_ERR", $"Node-based conversion failed: {ex.Message}");
                return false;
            }
        }

        private static string FindToolsDirectory()
        {
            string startDir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(startDir))
            {
                string candidate = Path.Combine(startDir, "tools", "md2pdf");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                candidate = Path.Combine(startDir, "..", "tools", "md2pdf");
                if (Directory.Exists(Path.GetFullPath(candidate)))
                {
                    return Path.GetFullPath(candidate);
                }
                
                string parent = Path.GetDirectoryName(startDir);
                if (parent == startDir) break;
                startDir = parent;
            }
            return null;
        }
    }
}
