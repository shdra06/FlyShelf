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

        /// <summary>Batch converts multiple DOC/DOCX files to PDF. Reuses a single Word instance for maximum speed and zero lag.</summary>
        public static async Task<string[]> ConvertDocsToPdfsAsync(string[] docPaths)
        {
#if MSIX_STORE
            await Task.CompletedTask;
            return Array.Empty<string>();
#else
            if (docPaths == null || docPaths.Length == 0) return Array.Empty<string>();

            return await Task.Run(() =>
            {
                object word = null;
                var convertedPaths = new System.Collections.Generic.List<string>();
                
                try
                {
                    Type wordType = Type.GetTypeFromProgID("Word.Application");
                    if (wordType == null) throw new Exception("Microsoft Word not found. Please install Word to enable DOCX to PDF conversion.");
                    
                    word = Activator.CreateInstance(wordType);
                    dynamic dynamicWord = word;
                    dynamicWord.Visible = false;
                    dynamicWord.DisplayAlerts = 0; // wdAlertsNone

                    string outputDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads", "FlyShelf", "Converted");
                    Directory.CreateDirectory(outputDir);

                    foreach (string docPath in docPaths)
                    {
                        if (!File.Exists(docPath)) continue;

                        try
                        {
                            string pdfPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(docPath) + "_" + Guid.NewGuid().ToString()[..4] + ".pdf");
                            
                            // wdOpenFormatAuto = 0, wdFormatPDF = 17
                            dynamic doc = dynamicWord.Documents.Open(docPath, false, true); // FileName, ConfirmConversions, ReadOnly
                            doc.SaveAs2(pdfPath, 17); // FileFormat: wdFormatPDF
                            doc.Close(false); // wdDoNotSaveChanges = 0
                            
                            if (File.Exists(pdfPath)) convertedPaths.Add(pdfPath);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("DOC2PDF_BATCH_ERR", $"Failed to convert {Path.GetFileName(docPath)}: {ex.Message}");
                        }
                    }

                    dynamicWord.Quit();
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
                }

                return convertedPaths.ToArray();
            });
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
                return await WebView2Converter.ConvertMarkdownToPdfAsync(mdContent, outputPdf);
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
                            await Task.Run(() => npmProc.WaitForExit(45000));
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
                        bool exited = proc.WaitForExit(30000); // 30s timeout
                        if (!exited)
                        {
                            try { proc.Kill(); } catch { } // Best-effort: failure is acceptable
                            Logger.LogAction("MD2PDF_NODE", "Node process timed out.");
                            return false;
                        }
                        if (proc.ExitCode != 0)
                        {
                            string err = proc.StandardError.ReadToEnd();
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
