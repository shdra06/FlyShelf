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
                            try { proc.Kill(); } catch { }
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
