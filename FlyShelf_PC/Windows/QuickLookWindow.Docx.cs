// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Docx.cs — High-fidelity Word document preview (.docx, .doc, .rtf, .odt)
// Converts documents asynchronously to cached PDF and renders in WebView2.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using FlyShelf.Classes;
using FlyShelf.Classes.Utils;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        private string? _currentDocxPdfPath;

        /// <summary>
        /// Loads and renders a Word / rich document (.docx, .doc, .rtf, .odt)
        /// with high visual fidelity by converting to a cached PDF and displaying in WebView2.
        /// </summary>
        private async Task LoadWordDocumentAsync()
        {
            if (string.IsNullOrEmpty(_item?.FilePath) || !File.Exists(_item.FilePath))
            {
                DocumentPanel.Visibility = Visibility.Visible;
                DocTitle.Text = _item?.FileName ?? "Document Not Found";
                return;
            }

            _isDocxMode = true;

            // Comfortable viewing dimensions matching typical standard page aspect ratios
            this.Width = 850;
            this.Height = Math.Min(960, SystemParameters.WorkArea.Height * 0.85);
            _isImageLoaded = true;

            // Show Word document toolbar actions
            if (DocxOpenNativeBtn != null) DocxOpenNativeBtn.Visibility = Visibility.Visible;
            if (DocxExportPdfBtn != null) DocxExportPdfBtn.Visibility = Visibility.Visible;
            if (DocxCopyTextBtn != null) DocxCopyTextBtn.Visibility = Visibility.Visible;
            if (ZoomPanel != null) ZoomPanel.Visibility = Visibility.Visible;
            if (DoodleBtn != null) DoodleBtn.Visibility = Visibility.Visible;

            string filePath = _item.FilePath;
            string cacheDir = Path.Combine(Path.GetTempPath(), "FlyShelf_DocPreviews");

            try
            {
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                // Compute deterministic cache key from path, last write time, and file size
                var fi = new FileInfo(filePath);
                string hashInput = $"{filePath}_{fi.LastWriteTimeUtc.Ticks}_{fi.Length}";
                string hashStr;
                using (var md5 = MD5.Create())
                {
                    byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
                    hashStr = Convert.ToHexString(hashBytes);
                }

                string safeBaseName = Path.GetFileNameWithoutExtension(filePath);
                foreach (char c in Path.GetInvalidFileNameChars()) safeBaseName = safeBaseName.Replace(c, '_');
                string cachedPdfPath = Path.Combine(cacheDir, $"{safeBaseName}_{hashStr}.pdf");

                // 1. FAST PATH: Instant display if cached PDF exists and is valid
                if (File.Exists(cachedPdfPath) && new FileInfo(cachedPdfPath).Length > 0)
                {
                    _currentDocxPdfPath = cachedPdfPath;
                    await DisplayDocumentPdfAsync(cachedPdfPath);
                    CleanOldDocPreviewsAsync(cacheDir);
                    return;
                }

                // 2. SLOW PATH: Convert document in background while keeping UI fully responsive
                LoadingProgress.Visibility = Visibility.Visible;

                string? convertedPdf = await Task.Run(async () =>
                {
                    try
                    {
                        return await ConversionUtils.ConvertDocToPdfAsync(filePath, cachedPdfPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("DOCX_CONVERT_ERR", $"Conversion failed for {Path.GetFileName(filePath)}: {ex.Message}");
                        return null;
                    }
                });

                if (!this.IsLoaded) return; // Window was closed during async conversion

                if (!string.IsNullOrEmpty(convertedPdf) && File.Exists(convertedPdf) && new FileInfo(convertedPdf).Length > 0)
                {
                    _currentDocxPdfPath = convertedPdf;
                    await DisplayDocumentPdfAsync(convertedPdf);
                }
                else
                {
                    // Graceful fallback: clean text extraction without font corruptions
                    await DisplayDocumentTextFallbackAsync();
                }

                CleanOldDocPreviewsAsync(cacheDir);
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX_LOAD_ERR", $"Word document load error: {ex.Message}");
                await DisplayDocumentTextFallbackAsync();
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Renders the converted PDF into the WebView2 viewer.
        /// </summary>
        private async Task DisplayDocumentPdfAsync(string pdfPath)
        {
            try
            {
                bool initialized = await EnsureWebViewInitializedAsync();
                if (initialized && WebPreview != null)
                {
                    WebPreview.Visibility = Visibility.Visible;
                    WebPreview.Source = new Uri(pdfPath);
                }
                else
                {
                    await DisplayDocumentTextFallbackAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX_WEBVIEW_ERR", $"WebView2 display failed: {ex.Message}");
                await DisplayDocumentTextFallbackAsync();
            }
        }

        /// <summary>
        /// Graceful fallback that extracts clean text and displays it using standard system typography.
        /// </summary>
        private async Task DisplayDocumentTextFallbackAsync()
        {
            try
            {
                string? text = await Task.Run(() =>
                {
                    string ext = Path.GetExtension(_item.FilePath).ToLowerInvariant();
                    if (ext == ".docx")
                    {
                        return DocxToPdfConverter.ExtractTextFallback(_item.FilePath);
                    }
                    return null;
                });

                if (!string.IsNullOrWhiteSpace(text))
                {
                    TextPreviewScroll.Visibility = Visibility.Visible;
                    TextPreview.IsReadOnly = true;
                    TextPreview.Text = text;
                    TextPreview.FontFamily = new System.Windows.Media.FontFamily("Calibri, Segoe UI, sans-serif");
                    TextPreview.FontSize = 14;
                }
                else
                {
                    DocumentPanel.Visibility = Visibility.Visible;
                    DocTitle.Text = Path.GetFileName(_item.FilePath);
                }
            }
            catch
            {
                DocumentPanel.Visibility = Visibility.Visible;
                DocTitle.Text = Path.GetFileName(_item.FilePath);
            }
        }

        /// <summary>
        /// Ensures CoreWebView2 is initialized with an isolated, dedicated process-specific data folder.
        /// </summary>
        private async Task<bool> EnsureWebViewInitializedAsync()
        {
            if (_webPreviewInitialized && WebPreview?.CoreWebView2 != null) return true;
            try
            {
                string userDataFolder = Path.Combine(Path.GetTempPath(), "FlyShelf_PdfQL_" + Environment.ProcessId);
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebPreview.EnsureCoreWebView2Async(env);
                _webPreviewInitialized = true;

                WebPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                WebPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                WebPreview.CoreWebView2.Settings.IsZoomControlEnabled = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("WEBVIEW_INIT_ERR", $"Failed to initialize WebView2: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Opens the original document file in Microsoft Word or the default system viewer.
        /// </summary>
        private void DocxOpenNative_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_item?.FilePath) && File.Exists(_item.FilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_item.FilePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Could not open document: {ex.Message} ❌");
            }
        }

        /// <summary>
        /// Allows the user to export and save the converted PDF anywhere on their system.
        /// </summary>
        private void DocxExportPdf_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentDocxPdfPath) || !File.Exists(_currentDocxPdfPath))
                {
                    ToastWindow.ShowToast("Document preview is still preparing... ⏳");
                    return;
                }

                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Document as PDF",
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = Path.GetFileNameWithoutExtension(_item.FilePath) + ".pdf",
                    InitialDirectory = Path.GetDirectoryName(_item.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (sfd.ShowDialog(this) == true)
                {
                    File.Copy(_currentDocxPdfPath, sfd.FileName, true);
                    ToastWindow.ShowToast($"PDF exported! 📄 {Path.GetFileName(sfd.FileName)}");
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to export PDF: {ex.Message} ❌");
            }
        }

        /// <summary>
        /// Extracts and copies clean document text to the clipboard.
        /// </summary>
        private async void DocxCopyText_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_item?.FilePath) || !File.Exists(_item.FilePath)) return;

                string? text = await Task.Run(() =>
                {
                    string ext = Path.GetExtension(_item.FilePath).ToLowerInvariant();
                    if (ext == ".docx")
                    {
                        return DocxToPdfConverter.ExtractTextFallback(_item.FilePath);
                    }
                    else if (ext == ".txt" || ext == ".rtf")
                    {
                        return File.ReadAllText(_item.FilePath);
                    }
                    return null;
                });

                if (!string.IsNullOrWhiteSpace(text))
                {
                    ClipboardHelper.SafeSetText(text);
                    ToastWindow.ShowToast("Document text copied to clipboard! 📋");
                }
                else
                {
                    ToastWindow.ShowToast("No extractable text found in document.");
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to copy text: {ex.Message} ❌");
            }
        }

        /// <summary>
        /// Background maintenance: deletes cached previews older than 7 days to prevent disk bloat.
        /// </summary>
        private static void CleanOldDocPreviewsAsync(string cacheDir)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(cacheDir)) return;
                    var dir = new DirectoryInfo(cacheDir);
                    var cutoff = DateTime.UtcNow.AddDays(-7);
                    foreach (var file in dir.GetFiles("*.pdf"))
                    {
                        try
                        {
                            if (file.LastAccessTimeUtc < cutoff) file.Delete();
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }
    }
}
