using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using FlyShelf.Classes;
using ICSharpCode.AvalonEdit.Highlighting;
using WinPdf = global::Windows.Data.Pdf;
using global::Windows.Storage;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using FlyShelf.Controls;
using WpfUi = Wpf.Ui.Controls;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        private FlyShelf.ViewModels.ClipboardItem _item;
        private Point _startPoint;
        private bool _isImageLoaded = false;
        private string _markdownRawContent = null;
        private global::Windows.Media.Ocr.OcrResult _ocrResult = null;
        private System.Collections.Generic.List<FlyShelf.Classes.OcrPreprocessor.MergedOcrWord> _mergedOcrWords = null;
        private string _mergedOcrText = null;
        private double _originalWidth = 0;
        private double _originalHeight = 0;
        private double _imageDpiX = 1.0;
        private double _imageDpiY = 1.0;
        // The pixel dimensions of the bitmap that was passed to the OCR engine.
        // May differ from _originalWidth/_originalHeight if the image was upscaled for better OCR.
        private double _ocrBitmapWidth = 0;
        private double _ocrBitmapHeight = 0;
        private bool _autoTriggerOcr = false;

        // ═══════════════════════════════════════════════════════════
        // DOODLE STATE
        // ═══════════════════════════════════════════════════════════
        private bool _isDoodleMode = false;
        private readonly System.Collections.Generic.Stack<Stroke> _doodleUndoStack = new();
        private readonly System.Collections.Generic.Stack<Stroke> _doodleRedoStack = new();
        private bool _hasUnsavedDoodle = false;
        private Border _activeDoodleColorBorder = null;

        // ═══════════════════════════════════════════════════════════
        // PDF STATE
        // ═══════════════════════════════════════════════════════════
        private bool _isPdfMode = false;
        private bool _isPdfEditorMode = false;
        private List<PageEntry> _pdfPageEntries = new();
        private Dictionary<string, BitmapImage> _pdfThumbnails = new();
        private Dictionary<int, string> _pdfModifiedPages = new(); // index -> temp image path
        private bool _isPdfModified = false;

        public QuickLookWindow(FlyShelf.ViewModels.ClipboardItem item, global::Windows.Media.Ocr.OcrResult preLoadedOcr = null, bool autoTriggerOcr = false)
        {
            InitializeComponent();
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            _item = item;
            _ocrResult = preLoadedOcr;
            _autoTriggerOcr = autoTriggerOcr;

            PreviewImage.Visibility = Visibility.Collapsed;
            if (ImageModeGrid != null) ImageModeGrid.Visibility = Visibility.Collapsed;
            if (WebPreview != null) WebPreview.Visibility = Visibility.Collapsed;
            TextPreviewScroll.Visibility = Visibility.Collapsed;
            DocumentPanel.Visibility = Visibility.Collapsed;
            if (PdfEditorGrid != null) PdfEditorGrid.Visibility = Visibility.Collapsed;

            PreviewImage.SizeChanged += (s, eArgs) =>
            {
                if (_ocrResult != null)
                {
                    RenderOcrOverlay();
                }
            };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
            await LoadContentAsync();

            // Auto-trigger OCR when opened from "Extract Text (OCR)" context menu.
            // This ensures the same code path as clicking the T button manually,
            // so bounding boxes are always perfectly aligned with the displayed image.
            if (_autoTriggerOcr && _isImageLoaded && OcrBtn != null && OcrBtn.IsEnabled)
            {
                OcrButton_Click(OcrBtn, new RoutedEventArgs());
            }
        }

        private void ApplyTheme()
        {
            // All visual properties now use DynamicResource bindings in XAML
            // that auto-adapt to any theme — no manual overrides needed.
        }

        private async System.Threading.Tasks.Task LoadContentAsync()
        {
            if (_item == null) return;

            // Markdown clipboard items may have RawContent but no FilePath — allow them through
            if (string.IsNullOrEmpty(_item.FilePath) && _item.Extension != "MARKDOWN" && _item.Extension != ".MD" && _item.ItemType != FlyShelf.ViewModels.ClipboardItemType.Code && _item.Extension != "JSON") return;

            LoadingProgress.Visibility = Visibility.Visible;

            string ext = Path.GetExtension(_item.FilePath ?? "").ToLower(CultureInfo.InvariantCulture);

            try
            {
                if (_item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Image || _item.ItemType == FlyShelf.ViewModels.ClipboardItemType.QRCode)
                {
                    PreviewImage.Visibility = Visibility.Visible;
                    if (ImageModeGrid != null) ImageModeGrid.Visibility = Visibility.Visible;
                    
                    var bitmap = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var _fi = new System.IO.FileInfo(_item.FilePath);
                            if (_fi.Length > 100_000_000)
                            {
                                System.Diagnostics.Debug.WriteLine($"[QUICKLOOK] Skipped — file too large ({_fi.Length} bytes): {_item.FilePath}");
                                return null;
                            }
                            byte[] imgBytes = File.ReadAllBytes(_item.FilePath);
                            BitmapImage bmp = new BitmapImage();
                            using (var imgStream = new System.IO.MemoryStream(imgBytes))
                            {
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.DecodePixelWidth = 4096; // Cap decode to prevent OOM on very large images
                                bmp.StreamSource = imgStream;
                                bmp.EndInit();
                            }
                            bmp.Freeze();
                            return bmp;
                        }
                        catch
                        {
                            return null;
                        }
                    });

                    if (bitmap != null)
                    {
                        PreviewImage.Source = bitmap;
                        _originalWidth = bitmap.PixelWidth;
                        _originalHeight = bitmap.PixelHeight;
                        double initDpiX = bitmap.DpiX > 0 ? bitmap.DpiX / 96.0 : 1.0;
                        double initDpiY = bitmap.DpiY > 0 ? bitmap.DpiY / 96.0 : 1.0;
                        _imageDpiX = initDpiX;
                        _imageDpiY = initDpiY;

                        double gridW = bitmap.PixelWidth / initDpiX;
                        double gridH = bitmap.PixelHeight / initDpiY;

                        if (ImageContainerGrid != null)
                        {
                            ImageContainerGrid.Width = gridW;
                            ImageContainerGrid.Height = gridH;
                        }
                        if (OcrOverlayCanvas != null)
                        {
                            OcrOverlayCanvas.Width = gridW;
                            OcrOverlayCanvas.Height = gridH;
                        }
                        
                        // Pre-scale intelligently based on original image aspect ratio and dpi to eliminate black spaces
                        double dpiX = initDpiX;
                        double dpiY = initDpiY;
                        double imgW = bitmap.PixelWidth / dpiX;
                        double imgH = bitmap.PixelHeight / dpiY;
                        double aspect = imgW / imgH;

                        double maxW = SystemParameters.WorkArea.Width * 0.7;
                        double maxH = SystemParameters.WorkArea.Height * 0.7 - 40; // Subtract header height

                        double targetW = imgW;
                        double targetH = imgH;

                        if (targetW > maxW)
                        {
                            targetW = maxW;
                            targetH = targetW / aspect;
                        }
                        if (targetH > maxH)
                        {
                            targetH = maxH;
                            targetW = targetH * aspect;
                        }

                        // Minimum size to keep controls visible
                        double minW = 320;
                        double minH = 240;
                        if (targetW < minW || targetH < minH)
                        {
                            if (aspect >= 1.0)
                            {
                                targetW = minW;
                                targetH = targetW / aspect;
                            }
                            else
                            {
                                targetH = minH;
                                targetW = targetH * aspect;
                            }
                        }

                        this.Width = targetW;
                        if (_item.ItemType == FlyShelf.ViewModels.ClipboardItemType.QRCode)
                        {
                            this.Height = targetH + 80; // Add header and QR content bar height back
                            if (QrContentBar != null) QrContentBar.Visibility = Visibility.Visible;
                            if (QrContentText != null) QrContentText.Text = _item.RawContent;
                        }
                        else
                        {
                            this.Height = targetH + 40; // Add header height back
                        }
                        
                        _isImageLoaded = true;
                        RotateBtn.Visibility = Visibility.Visible;
                        if (DoodleBtn != null) DoodleBtn.Visibility = Visibility.Visible;
                        if (OcrBtn != null) OcrBtn.Visibility = Visibility.Visible;

                        if (_ocrResult != null)
                        {
                            if (OcrOverlayCanvas != null) OcrOverlayCanvas.Visibility = Visibility.Visible;
                            if (CopyAllOcrBtn != null) CopyAllOcrBtn.Visibility = Visibility.Visible;
                            RenderOcrOverlay();
                        }
                    }
                }
                else if (ext == ".pdf")
                {
                    _isPdfMode = true;
                    WebPreview.Visibility = Visibility.Visible;
                    PdfManageBtn.Visibility = Visibility.Visible;
                    
                    try 
                    {
                        // Initialize WebView2 for PDF with modern features
                        string userDataFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlyShelf_PdfQL_" + Environment.ProcessId);
                        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                        await WebPreview.EnsureCoreWebView2Async(env);
                        
                        WebPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        WebPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                        WebPreview.CoreWebView2.Settings.IsZoomControlEnabled = true;
                        WebPreview.Source = new Uri(_item.FilePath);
                    } 
                    catch { } 
                    
                    this.Width = 800;
                    this.Height = SystemParameters.WorkArea.Height * 0.8;
                    _isImageLoaded = true;
                    if (DoodleBtn != null) DoodleBtn.Visibility = Visibility.Visible;
                    if (ZoomPanel != null) ZoomPanel.Visibility = Visibility.Visible;
                }
                else if (ext == ".html" || ext == ".htm" || ext == ".xml")
                {
                    // Fallback for non-PDF web content
                    WebPreview.Visibility = Visibility.Visible;
                    try { WebPreview.Source = new Uri(_item.FilePath); } catch { }
                    
                    this.Width = 600;
                    this.Height = SystemParameters.WorkArea.Height * 0.8;
                }
                else if (ext == ".md" || _item.Extension == "MARKDOWN" || _item.Extension == ".MD")
                {
                    // Render Markdown beautifully using WebView2 + MarkdownTemplate (same engine as PDF export)
                    try
                    {
                        string mdText = null;
                        if (!string.IsNullOrEmpty(_item.FilePath) && File.Exists(_item.FilePath))
                        {
                            mdText = await System.Threading.Tasks.Task.Run(() =>
                            {
                                try { return File.ReadAllText(_item.FilePath); }
                                catch { return null; }
                            });
                        }
                        if (string.IsNullOrEmpty(mdText) && !string.IsNullOrEmpty(_item.RawContent))
                        {
                            mdText = _item.RawContent;
                        }

                        if (!string.IsNullOrEmpty(mdText))
                        {
                            _markdownRawContent = mdText;
                            MarkdownWebView.Visibility = Visibility.Visible;
                            
                            // Initialize WebView2 with isolated user data folder
                            string userDataFolder = System.IO.Path.Combine(
                                System.IO.Path.GetTempPath(), 
                                "FlyShelf_QuickLook_" + Environment.ProcessId);
                            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                            await MarkdownWebView.EnsureCoreWebView2Async(env);
                            
                            // Enable text selection + right-click copy, disable dev tools
                            MarkdownWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                            MarkdownWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                            MarkdownWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
                            MarkdownWebView.CoreWebView2.Settings.IsPinchZoomEnabled = true;
                            
                            // Track zoom changes for the zoom label
                            MarkdownWebView.ZoomFactorChanged += MarkdownWebView_ZoomFactorChanged;
                            
                            string html = FlyShelf.Classes.MarkdownTemplate.GetHtml(mdText);
                            MarkdownWebView.NavigateToString(html);
                            
                            // Show markdown-specific buttons
                            MdToPdfBtn.Visibility = Visibility.Visible;
                            ZoomResetBtn.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            TextPreviewScroll.Visibility = Visibility.Visible;
                            TextPreview.Text = "[Empty Markdown]";
                        }
                    }
                    catch (Exception mdEx)
                    {
                        // Fallback to raw text if WebView2 rendering fails
                        System.Diagnostics.Debug.WriteLine($"[QUICKLOOK] Markdown WebView2 failed: {mdEx.Message}");
                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK", $"Markdown render error: {mdEx.Message}");
                        TextPreviewScroll.Visibility = Visibility.Visible;
                        MarkdownWebView.Visibility = Visibility.Collapsed;
                        TextPreview.Text = _item.RawContent ?? "[Failed to render Markdown]";
                    }

                    this.Width = 600;
                    this.Height = 700;
                    _isImageLoaded = true;
                }
                else if (ext == ".svg")
                {
                    // Render SVG using WebBrowser
                    try
                    {
                        string svgContent = await System.Threading.Tasks.Task.Run(() =>
                        {
                            try { return File.ReadAllText(_item.FilePath); }
                            catch { return null; }
                        });
                        if (!string.IsNullOrEmpty(svgContent))
                        {
                            // Sanitize SVG: strip <script> tags and inline event handlers
                            svgContent = System.Text.RegularExpressions.Regex.Replace(svgContent, @"<script[^>]*>[\s\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            svgContent = System.Text.RegularExpressions.Regex.Replace(svgContent, @"\son\w+=""[^""]*""", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            WebPreview.Visibility = Visibility.Visible;
                            string html = $"<!DOCTYPE html><html><body style='margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh;background:#1a1a2e'>{svgContent}</body></html>";
                            WebPreview.NavigateToString(html);
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                    this.Width = 500;
                    this.Height = 500;
                    _isImageLoaded = true;
                }
                else if (ext == ".cs" || ext == ".cpp" || ext == ".c" || ext == ".h" ||
                         ext == ".js" || ext == ".ts" || ext == ".jsx" || ext == ".tsx" ||
                         ext == ".py" || ext == ".java" || ext == ".json" ||
                         ext == ".xml" || ext == ".yaml" || ext == ".yml" ||
                         ext == ".sql" || ext == ".sh" || ext == ".bat" || ext == ".ps1" ||
                         ext == ".css" || ext == ".html" || ext == ".htm" ||
                         _item.Extension == "JSON" || _item.IsCodePreview)
                {
                    // Syntax-highlighted code preview via AvalonEdit
                    string codeText = null;
                    if (!string.IsNullOrEmpty(_item.FilePath) && File.Exists(_item.FilePath))
                    {
                        codeText = await System.Threading.Tasks.Task.Run(() =>
                        {
                            try { return File.ReadAllText(_item.FilePath); }
                            catch { return null; }
                        });
                    }
                    if (string.IsNullOrEmpty(codeText) && !string.IsNullOrEmpty(_item.RawContent))
                        codeText = _item.RawContent;

                    if (!string.IsNullOrEmpty(codeText))
                    {
                        // Auto-format JSON
                        if ((ext == ".json" || _item.Extension == "JSON") && 
                            FlyShelf.Classes.SmartContentDetector.IsValidJson(codeText))
                        {
                            codeText = FlyShelf.Classes.SmartContentDetector.PrettyPrintJson(codeText);
                        }

                        // Map extension to AvalonEdit highlighting name
                        string highlightName = (ext?.TrimStart('.') ?? _item.Extension ?? "").ToLower(CultureInfo.InvariantCulture) switch
                        {
                            "cs" => "C#",
                            "c#" => "C#",
                            "cpp" or "c" or "h" or "c++" => "C++",
                            "js" or "jsx" or "ts" or "tsx" or "javascript" => "JavaScript",
                            "json" => "JavaScript", // JSON highlights well with JS rules
                            "py" or "python" => "Python",
                            "java" => "Java",
                            "xml" or "xaml" or "svg" or "csproj" => "XML",
                            "html" or "htm" => "HTML",
                            "css" => "CSS",
                            "sql" => "SQL",
                            "bat" or "cmd" or "ps1" or "powershell" or "sh" or "bash" => "Python", // Approximate
                            _ => null
                        };

                        CodePreview.Text = codeText;
                        if (highlightName != null)
                        {
                            var highlighting = HighlightingManager.Instance.GetDefinition(highlightName);
                            if (highlighting != null)
                                CodePreview.SyntaxHighlighting = highlighting;
                        }
                        CodePreview.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        TextPreviewScroll.Visibility = Visibility.Visible;
                        TextPreview.Text = "[Empty file]";
                    }

                    this.Width = 650;
                    this.Height = 700;
                    _isImageLoaded = true;
                    if (TranslateBtn != null) TranslateBtn.Visibility = Visibility.Visible;
                }
                else if (ext == ".docx" || ext == ".txt" || ext == ".log")
                {
                    TextPreviewScroll.Visibility = Visibility.Visible;
                    
                    string textResult = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try 
                        {
                            if (ext == ".docx") 
                            {
                                using (var archive = System.IO.Compression.ZipFile.OpenRead(_item.FilePath))
                                {
                                    var entry = archive.GetEntry("word/document.xml");
                                    if (entry != null)
                                    {
                                        using (var stream = entry.Open())
                                        using (var reader = new System.IO.StreamReader(stream))
                                        {
                                            string xml = reader.ReadToEnd();
                                            string rawText = System.Text.RegularExpressions.Regex.Replace(xml, @"<[^>]+>", " ");
                                            return System.Text.RegularExpressions.Regex.Replace(rawText, @"\s+", " ").Trim();
                                        }
                                    }
                                }
                            }
                            else 
                            {
                                return File.ReadAllText(_item.FilePath);
                            }
                        } 
                        catch { } // Best-effort: failure is acceptable
                        return null;
                    });

                    if (textResult != null)
                    {
                        TextPreview.Text = textResult;
                    }
                    else
                    {
                        TextPreview.Text = "[FlyShelf Codec Error: Cannot extract raw string payload from this artifact natively]";
                    }

                    this.Width = 550;
                    this.Height = 650;
                    _isImageLoaded = true; // allow native dragging for textual representations

                    // Show translate button for text-type items
                    if (TranslateBtn != null) TranslateBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    // Default Document Fallback Mode
                    DocumentPanel.Visibility = Visibility.Visible;
                    DocTitle.Text = Path.GetFileName(_item.FilePath);
                    
                    long length = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            return new FileInfo(_item.FilePath).Length;
                        }
                        catch { return -1L; }
                    });

                    if (length >= 0)
                    {
                        DocSize.Text = $"{_item.ItemType} Document • {(length / 1024.0 / 1024.0):0.00} MB";
                    }
                    else
                    {
                        DocSize.Text = "Unknown Size";
                    }

                    this.Width = 400;
                    this.Height = 350;
                }
            }
            catch { } // Best-effort: failure is acceptable
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async System.Threading.Tasks.Task RenderPdfPageToImage(int pageIndex, string outputPath)
        {
            await System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(_item.FilePath);
                    var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                    using (var page = pdfDoc.GetPage((uint)pageIndex))
                    {
                        using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                        {
                            await page.RenderToStreamAsync(stream);
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                            var storageFile = await StorageFile.GetFileFromPathAsync(outputPath);
                            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                                await storageFile.OpenAsync(FileAccessMode.ReadWrite));
                            encoder.SetSoftwareBitmap(softwareBitmap);
                            await encoder.FlushAsync();
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PDF Page Render Error: {ex.Message}"); }
            });
        }

        // ═════════════════════════════════════════════════════════════
        // TRANSLATE
        // ═════════════════════════════════════════════════════════════

        private static readonly string[] _translateLanguages = new[]
        {
            "English", "Spanish", "French", "German", "Japanese",
            "Chinese", "Hindi", "Arabic", "Korean", "Portuguese"
        };

        private void TranslateButton_Click(object sender, RoutedEventArgs e)
        {
            // Build a context menu with language options
            var menu = new System.Windows.Controls.ContextMenu();
            foreach (var lang in _translateLanguages)
            {
                var menuItem = new System.Windows.Controls.MenuItem { Header = $"🌐 {lang}" };
                string targetLang = lang; // capture for closure
                menuItem.Click += async (s, ev) =>
                {
                    await TranslateTextAsync(targetLang);
                };
                menu.Items.Add(menuItem);
            }

            menu.PlacementTarget = TranslateBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private async System.Threading.Tasks.Task TranslateTextAsync(string targetLanguage)
        {
            // Get the current text content from the text preview
            string sourceText = TextPreview?.Text;
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                FlyShelf.Windows.ToastWindow.ShowToast("No text content to translate.");
                return;
            }

            // Check AI availability
            if (!FlyShelf.Classes.AiProviderService.Instance.IsAvailable)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Translate requires an AI API key");
                return;
            }

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                TranslateBtn.IsEnabled = false;
                HeaderTitle.Text = $"Translating to {targetLanguage}...";

                string translated = await FlyShelf.Classes.AiProviderService.Instance.TranslateAsync(sourceText, targetLanguage);

                if (!string.IsNullOrWhiteSpace(translated))
                {
                    TextPreview.Text = translated;
                    HeaderTitle.Text = $"Translated to {targetLanguage}";
                    FlyShelf.Windows.ToastWindow.ShowToast($"🌐 Translated to {targetLanguage}");
                    FlyShelf.Classes.Logger.LogAction("TRANSLATE", $"Translated {sourceText.Length} chars to {targetLanguage}");
                }
                else
                {
                    HeaderTitle.Text = "Quick Look";
                    FlyShelf.Windows.ToastWindow.ShowToast("Translation returned empty result.");
                }
            }
            catch (Exception ex)
            {
                HeaderTitle.Text = "Quick Look";
                FlyShelf.Classes.Logger.LogAction("TRANSLATE", $"Failed: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast($"Translation failed: {ex.Message}");
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                TranslateBtn.IsEnabled = true;
            }
        }

        private async void RotateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_item.FilePath) || !File.Exists(_item.FilePath)) return;
                
                LoadingProgress.Visibility = Visibility.Visible;
                RotateBtn.IsEnabled = false;

                string filePath = _item.FilePath;

                var fresh = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // Load the original file as bytes to avoid any file locking
                        var _fi = new System.IO.FileInfo(filePath);
                        if (_fi.Length > 100_000_000)
                        {
                            System.Diagnostics.Debug.WriteLine($"[QUICKLOOK] Skipped rotate — file too large ({_fi.Length} bytes): {filePath}");
                            return null;
                        }
                        byte[] fileBytes = File.ReadAllBytes(filePath);
                        BitmapImage original = new BitmapImage();
                        using (var ms = new System.IO.MemoryStream(fileBytes))
                        {
                            original.BeginInit();
                            original.CacheOption = BitmapCacheOption.OnLoad;
                            original.StreamSource = ms;
                            original.EndInit();
                        }
                        original.Freeze();
                        
                        // Create rotated bitmap
                        var rotated = new TransformedBitmap(original, new System.Windows.Media.RotateTransform(90));
                        rotated.Freeze();
                        
                        // Encode and save back
                        string ext = Path.GetExtension(filePath).ToLower(CultureInfo.InvariantCulture);
                        BitmapEncoder encoder;
                        if (ext == ".png") encoder = new PngBitmapEncoder();
                        else if (ext == ".bmp") encoder = new BmpBitmapEncoder();
                        else encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                        
                        encoder.Frames.Add(BitmapFrame.Create(rotated));
                        
                        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            encoder.Save(fs);
                        }
                        
                        // Reload fresh from bytes
                        var _fi2 = new System.IO.FileInfo(filePath);
                        if (_fi2.Length > 100_000_000)
                        {
                            System.Diagnostics.Debug.WriteLine($"[QUICKLOOK] Skipped reload — file too large ({_fi2.Length} bytes): {filePath}");
                            return null;
                        }
                        byte[] freshBytes = File.ReadAllBytes(filePath);
                        BitmapImage freshBmp = new BitmapImage();
                        using (var ms2 = new System.IO.MemoryStream(freshBytes))
                        {
                            freshBmp.BeginInit();
                            freshBmp.CacheOption = BitmapCacheOption.OnLoad;
                            freshBmp.StreamSource = ms2;
                            freshBmp.EndInit();
                        }
                        freshBmp.Freeze();
                        return freshBmp;
                    }
                    catch
                    {
                        return null;
                    }
                });

                if (fresh != null)
                {
                    // Clear the old OCR overlay since the image layout rotated 90 degrees!
                    _ocrResult = null;
                    if (OcrOverlayCanvas != null)
                    {
                        OcrOverlayCanvas.Children.Clear();
                        OcrOverlayCanvas.Visibility = Visibility.Collapsed;
                    }
                    if (CopyAllOcrBtn != null) CopyAllOcrBtn.Visibility = Visibility.Collapsed;

                    PreviewImage.Source = fresh;
                    FlyShelf.Classes.Logger.LogAction("ROTATE", "Rotated 90°: " + Path.GetFileName(_item.FilePath));
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Rotate failed: File could not be written or read");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("ROTATE", "Failed: " + ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast("Rotate failed: " + ex.Message);
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                RotateBtn.IsEnabled = true;
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = !this.Topmost;
            PinIcon.Symbol = this.Topmost
                ? Wpf.Ui.Controls.SymbolRegular.Pin24
                : Wpf.Ui.Controls.SymbolRegular.PinOff24;
            PinBtn.Foreground = this.Topmost
                ? (TryFindResource("WarningColor") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)))
                : (TryFindResource("ThemeTextMuted") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136)));
            PinBtn.ToolTip = this.Topmost ? "Pinned on top" : "Unpinned";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_item.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Could not launch the native visual previewer application: " + ex.Message); }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Don't initiate window drag when user is interacting with OCR text overlays
            if (IsOcrTextBoxSource(e.OriginalSource as DependencyObject)) return;

            // Don't initiate window drag when doodle mode is active (user is drawing)
            if (_isDoodleMode) return;

            if (e.OriginalSource is DependencyObject && !(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase))
            {
                _startPoint = e.GetPosition(null);

                // Allows the entire floating object to act as a 100% native draggable window!
                if (e.LeftButton == MouseButtonState.Pressed && e.ClickCount == 1)
                {
                    try { this.DragMove(); } catch { } // Best-effort: failure is acceptable
                }
            }
        }

        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Removed: "remove double tap to full screen from quick look/ preview"
            e.Handled = true;
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            // CRITICAL: Never initiate file drag-drop when the user is selecting OCR text.
            // Doing so causes WPF dispatcher re-entrancy crash:
            // "Dispatcher processing has been suspended, but messages are still being processed."
            if (IsOcrTextBoxSource(e.OriginalSource as DependencyObject)) return;

            if (e.LeftButton == MouseButtonState.Pressed && _isImageLoaded && !_isDoodleMode)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _startPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    // Convert the image payload into a massive Drag Source natively!
                    if (File.Exists(_item.FilePath))
                    {
                        var dataObject = new DataObject();
                        // Allows dragging directly into WhatsApp, Discord, Photoshop natively!
                        dataObject.SetData(DataFormats.FileDrop, new[] { _item.FilePath });
                        
                        DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether the given DependencyObject is (or is a child of) an OCR overlay TextBox.
        /// This prevents DragDrop.DoDragDrop from being called while the user is selecting text,
        /// which would cause a fatal WPF dispatcher re-entrancy crash.
        /// </summary>
        private bool IsOcrTextBoxSource(DependencyObject source)
        {
            if (source == null) return false;

            // Walk up the visual tree to see if we hit an OCR TextBox inside the overlay canvas
            DependencyObject current = source;
            while (current != null)
            {
                if (current is System.Windows.Controls.TextBox)
                {
                    // Verify this TextBox lives inside the OCR overlay canvas
                    DependencyObject parent = current;
                    while (parent != null)
                    {
                        if (parent == OcrOverlayCanvas) return true;
                        if (parent is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                        {
                            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
                        }
                        else
                        {
                            parent = LogicalTreeHelper.GetParent(parent);
                        }
                    }
                    return false;
                }
                
                if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return false;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Do nothing. Let the user keep it floating on their other monitor while they work!
        }

        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_selectedWordTexts.Count > 0)
                {
                    CopySelectedOcrWords();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (OcrOverlayCanvas.Visibility == Visibility.Visible)
                {
                    SelectAllOcrWords();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                // If doodle mode is active, exit doodle mode first
                if (_isDoodleMode)
                {
                    ExitDoodleMode();
                    e.Handled = true;
                    return;
                }
                this.Close();
                e.Handled = true;
            }
            // Doodle keyboard shortcuts
            else if (_isDoodleMode)
            {
                if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    DoodleUndo_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    DoodleRedo_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    DoodleSave_Click(null, null);
                    e.Handled = true;
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_isImageLoaded)
            {
                FlyShelf.Classes.SettingsManager.Current.QuickLookWidth = this.Width;
                FlyShelf.Classes.SettingsManager.Current.QuickLookHeight = this.Height;
                FlyShelf.Classes.SettingsManager.Save();
            }
            try
            {
                WebPreview.Dispose();
            }
            catch { } // Best-effort: failure is acceptable
            try
            {
                MarkdownWebView.Dispose();
            }
            catch { } // Best-effort: failure is acceptable
            base.OnClosed(e);
        }

        // ═══ Markdown Preview: Zoom & PDF Export ═══

        private void MarkdownWebView_ZoomFactorChanged(object sender, EventArgs e)
        {
            try
            {
                int pct = (int)Math.Round(MarkdownWebView.ZoomFactor * 100);
                ZoomLabel.Text = $"{pct}%";
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void MarkdownZoomReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MarkdownWebView.ZoomFactor = 1.0;
            }
            catch { } // Best-effort: failure is acceptable
        }

        private async void MarkdownToPdf_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_markdownRawContent)) return;

            try
            {
                MdToPdfBtn.IsEnabled = false;
                LoadingProgress.Visibility = Visibility.Visible;

                // Save PDF to the same directory as source file, or temp
                string sourceDir = !string.IsNullOrEmpty(_item?.FilePath) && Directory.Exists(Path.GetDirectoryName(_item.FilePath))
                    ? Path.GetDirectoryName(_item.FilePath)!
                    : Path.GetTempPath();
                string baseName = Path.GetFileNameWithoutExtension(_item?.FilePath ?? "document");
                string outputPdf = Path.Combine(sourceDir, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                bool success = await FlyShelf.Classes.WebView2Converter.ConvertMarkdownToPdfAsync(_markdownRawContent, outputPdf);

                LoadingProgress.Visibility = Visibility.Collapsed;
                MdToPdfBtn.IsEnabled = true;

                if (success && File.Exists(outputPdf))
                {
                    // Drop into clipboard via HandleDrop — same pattern as ConvertDocumentTask
                    var dataObj = new System.Windows.DataObject();
                    dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPdf });
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                    FlyShelf.Windows.ToastWindow.ShowToast($"Markdown → PDF exported! ✅ {Path.GetFileName(outputPdf)}");
                    mainWin?.ScrollClipboardToTop();
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("PDF export failed ❌");
                }
            }
            catch
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                MdToPdfBtn.IsEnabled = true;
            }
        }

        private async void OcrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null || string.IsNullOrEmpty(_item.FilePath) || !File.Exists(_item.FilePath)) return;

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                OcrBtn.IsEnabled = false;

                // Run OCR on background thread
                var ocrResultTuple = await System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        using (var stream = File.OpenRead(_item.FilePath))
                        {
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                            // CRITICAL: Convert to Bgra8/Premultiplied — the OCR engine requires this
                            // pixel format for reliable recognition. Without conversion, many images
                            // return empty or garbled results.
                            if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                                softwareBitmap.BitmapAlphaMode != global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
                            {
                                var originalBitmap = softwareBitmap;
                                softwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                    softwareBitmap,
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                                originalBitmap.Dispose();
                            }

                            // Store the actual OCR bitmap pixel dimensions for coordinate mapping.
                            uint ocrW = (uint)softwareBitmap.PixelWidth;
                            uint ocrH = (uint)softwareBitmap.PixelHeight;

                            // For small/medium images, upscale 3x for better OCR text detection.
                            // The OCR engine struggles with text smaller than ~12px.
                            if (Math.Max(ocrW, ocrH) < 2800)
                            {
                                global::Windows.Storage.Streams.InMemoryRandomAccessStream? inMemStream = null;
                                try
                                {
                                    uint newW = ocrW * 3;
                                    uint newH = ocrH * 3;
                                    // Cap at 4000px (OCR engine max)
                                    if (newW > 4000) { newW = 4000; newH = (uint)(ocrH * (4000.0 / ocrW)); }
                                    if (newH > 4000) { newH = 4000; newW = (uint)(ocrW * (4000.0 / ocrH)); }

                                    // Encode original → InMemoryStream with BitmapTransform scaling → Decode back
                                    inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                                    var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                        global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
                                    encoder.SetSoftwareBitmap(softwareBitmap);
                                    encoder.BitmapTransform.ScaledWidth = newW;
                                    encoder.BitmapTransform.ScaledHeight = newH;
                                    encoder.BitmapTransform.InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
                                    await encoder.FlushAsync();

                                    inMemStream.Seek(0);
                                    var dec2 = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inMemStream);
                                    var scaledBitmap = await dec2.GetSoftwareBitmapAsync(
                                        global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                        global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                                    var priorBitmap = softwareBitmap;
                                    softwareBitmap = scaledBitmap;
                                    priorBitmap.Dispose();
                                    ocrW = (uint)softwareBitmap.PixelWidth;
                                    ocrH = (uint)softwareBitmap.PixelHeight;
                                }
                                catch (Exception upscaleEx)
                                {
                                    FlyShelf.Classes.Logger.LogAction("OCR_UPSCALE", $"Upscale failed (using original): {upscaleEx.Message}");
                                    // Continue with original bitmap — upscale is best-effort
                                }
                                finally
                                {
                                    inMemStream?.Dispose();
                                }
                            }

                            // ── Multi-pass OCR with RESULT MERGING ──
                            // Runs OCR on all preprocessing variants and MERGES words from ALL results.
                            // This ensures text detected by ANY variant is included (e.g. header "61%"
                            // that only the Original or BradleyRoth variant can detect).
                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                            if (ocrEngine == null)
                            {
                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                            }

                            if (ocrEngine != null)
                            {
                                var variants = FlyShelf.Classes.OcrPreprocessor.CreateOcrVariants(softwareBitmap);
                                var allResults = new System.Collections.Generic.List<global::Windows.Media.Ocr.OcrResult>();

                                for (int v = 0; v < variants.Length; v++)
                                {
                                    try
                                    {
                                        var varResult = await ocrEngine.RecognizeAsync(variants[v].bitmap);
                                        if (varResult != null && varResult.Lines.Count > 0)
                                        {
                                            allResults.Add(varResult);
                                            int charCount = 0;
                                            foreach (var line in varResult.Lines)
                                                foreach (var word in line.Words)
                                                    charCount += word.Text.Length;
                                            FlyShelf.Classes.Logger.LogAction("OCR_MULTIPASS",
                                                $"QuickLook {variants[v].name}: {varResult.Lines.Count} lines, {charCount} chars");
                                        }
                                    }
                                    catch { } // Best-effort: failure is acceptable
                                    finally
                                    {
                                        variants[v].bitmap.Dispose();
                                    }
                                }

                                // Merge words from ALL results
                                var mergeResult = FlyShelf.Classes.OcrPreprocessor.MergeOcrResults(allResults);
                                return (mergeResult.words, mergeResult.mergedText, (double)ocrW, (double)ocrH);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK_OCR_FAIL", ex.Message);
                    }
                    return (null, null, 0.0, 0.0);
                });

                var mergedWords = ocrResultTuple.Item1;
                var mergedText = ocrResultTuple.Item2;
                if (mergedWords != null && mergedWords.Count > 0 && !string.IsNullOrWhiteSpace(mergedText))
                {
                    _mergedOcrWords = mergedWords;
                    _mergedOcrText = mergedText;
                    _ocrResult = null; // Using merged results instead
                    _ocrBitmapWidth = ocrResultTuple.Item3;
                    _ocrBitmapHeight = ocrResultTuple.Item4;
                    OcrOverlayCanvas.Visibility = Visibility.Visible;
                    CopyAllOcrBtn.Visibility = Visibility.Visible;
                    RenderOcrOverlay();
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"OCR Complete! {mergedWords.Count} words detected. Select text to copy.");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No text detected in image.");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("OCR Failed: " + ex.Message);
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                OcrBtn.IsEnabled = true;
            }
        }

        private void CopyAllOcrButton_Click(object sender, RoutedEventArgs e)
        {
            if ((_mergedOcrText == null || string.IsNullOrWhiteSpace(_mergedOcrText)) &&
                (_ocrResult == null || string.IsNullOrWhiteSpace(_ocrResult.Text))) return;

            string textToCopy = _mergedOcrText ?? _ocrResult?.Text ?? "";
            try
            {
                if (ClipboardHelper.SafeSetTextAllowCapture(textToCopy))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("All Image Text Copied to Clipboard! 📋");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed: " + ex.Message);
            }
        }

        private void CopyQrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null || string.IsNullOrWhiteSpace(_item.RawContent)) return;

            try
            {
                if (ClipboardHelper.SafeSetText(_item.RawContent))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("QR Code Text Copied! 📋");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Tracks which word overlays are currently "selected" (highlighted) for multi-word Ctrl+C copy.
        /// </summary>
        private readonly System.Collections.Generic.List<System.Windows.Controls.Border> _selectedWordBorders = new();
        private readonly System.Collections.Generic.List<string> _selectedWordTexts = new();

        // Drag-to-select state
        private bool _isDragSelecting = false;
        private bool _ocrCanvasEventsAttached = false;
        private Point _dragStartPoint;

        // Frozen brushes reused across render and drag-selection (initialized in RenderOcrOverlay)
        private System.Windows.Media.SolidColorBrush _ocrHoverBg;
        private System.Windows.Media.SolidColorBrush _ocrHoverBorder;
        private System.Windows.Media.SolidColorBrush _ocrSelectedBg;
        private System.Windows.Media.SolidColorBrush _ocrSelectedBorder;

        private void RenderOcrOverlay()
        {
            if (_mergedOcrWords == null && _ocrResult == null) return;
            if (_originalWidth == 0 || _originalHeight == 0) return;

            OcrOverlayCanvas.Children.Clear();
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();

            // Calculate size in logical Device-Independent Pixels (DIPs) to match WPF's layout engine.
            // Sizing the canvas and the container grid in DIPs ensures pixel-perfect mapping at any DPI.
            double containerW = _originalWidth / _imageDpiX;
            double containerH = _originalHeight / _imageDpiY;

            if (ImageContainerGrid != null)
            {
                ImageContainerGrid.Width = containerW;
                ImageContainerGrid.Height = containerH;
            }
            OcrOverlayCanvas.Width = containerW;
            OcrOverlayCanvas.Height = containerH;

            // Calculate the upscale scale factor. _ocrBitmapWidth can be larger if OCR upscaling was active.
            double scaleX = _ocrBitmapWidth > 0 ? (_ocrBitmapWidth / _originalWidth) : 1.0;
            double scaleY = _ocrBitmapHeight > 0 ? (_ocrBitmapHeight / _originalHeight) : 1.0;

            // Brushes reused across all words — store as fields for drag-selection reuse
            _ocrHoverBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0x60, 0xA5, 0xFA));
            _ocrHoverBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x35, 0x60, 0xA5, 0xFA));
            _ocrSelectedBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0x60, 0xA5, 0xFA));
            _ocrSelectedBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0x60, 0xA5, 0xFA));
            _ocrHoverBg.Freeze(); _ocrHoverBorder.Freeze(); _ocrSelectedBg.Freeze(); _ocrSelectedBorder.Freeze();
            var hoverBg = _ocrHoverBg;
            var hoverBorder = _ocrHoverBorder;
            var selectedBg = _ocrSelectedBg;
            var selectedBorder = _ocrSelectedBorder;
            var transparentBrush = System.Windows.Media.Brushes.Transparent;

            // Render words from merged results (preferred) or legacy OcrResult
            if (_mergedOcrWords != null && _mergedOcrWords.Count > 0)
            {
                foreach (var word in _mergedOcrWords)
                {
                    var rect = word.BoundingRect;
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    string wordText = word.Text;

                    double scaledLeft = rect.X / (scaleX * _imageDpiX);
                    double scaledTop = rect.Y / (scaleY * _imageDpiY);
                    double scaledWidth = rect.Width / (scaleX * _imageDpiX);
                    double scaledHeight = rect.Height / (scaleY * _imageDpiY);

                    if (scaledWidth <= 0 || scaledHeight <= 0) continue;

                    // Add horizontal/vertical padding for smoother selection
                    double hPad = Math.Max(3, scaledWidth * 0.12);
                    double vPad = Math.Max(1, scaledHeight * 0.08);

                    // Word highlight border — the interactive overlay element
                    var wordBorder = new System.Windows.Controls.Border
                    {
                        Width = scaledWidth + hPad * 2,
                        Height = scaledHeight + vPad * 2,
                        Background = transparentBrush,
                        BorderBrush = transparentBrush,
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(2),
                        Cursor = System.Windows.Input.Cursors.IBeam,
                        ToolTip = wordText,
                        Focusable = true,
                        Tag = wordText // store text in Tag for easy retrieval
                    };

                    // --- Click to select + start drag-to-select ---
                    wordBorder.MouseLeftButtonDown += (s, ev) =>
                    {
                        var border = s as System.Windows.Controls.Border;
                        if (border == null) return;

                        // If Ctrl is NOT held, deselect all other words first
                        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                        if (!ctrlHeld)
                        {
                            DeselectAllOcrWords();
                        }

                        // Select this word
                        SelectWordBorder(border);
                        border.Focus();

                        // Start drag-to-select: capture mouse on the canvas, store start
                        _isDragSelecting = true;
                        _dragStartPoint = ev.GetPosition(OcrOverlayCanvas);
                        OcrOverlayCanvas.CaptureMouse();
                        ev.Handled = true;
                    };

                    // --- Hover effects (only when not selected) ---
                    wordBorder.MouseEnter += (s, ev) =>
                    {
                        var border = s as System.Windows.Controls.Border;
                        if (border != null && !_selectedWordBorders.Contains(border))
                        {
                            border.Background = hoverBg;
                            border.BorderBrush = hoverBorder;
                        }
                    };
                    wordBorder.MouseLeave += (s, ev) =>
                    {
                        var border = s as System.Windows.Controls.Border;
                        if (border != null && !_selectedWordBorders.Contains(border))
                        {
                            border.Background = transparentBrush;
                            border.BorderBrush = transparentBrush;
                        }
                    };

                    // --- Right-click context menu ---
                    var menu = new System.Windows.Controls.ContextMenu();

                    var copyWordItem = new System.Windows.Controls.MenuItem { Header = "Copy Word" };
                    copyWordItem.Click += (s, ev) =>
                    {
                        try
                        {
                            if (ClipboardHelper.SafeSetText(wordText))
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast($"Copied: {wordText}");
                            }
                        }
                        catch { } // Best-effort: failure is acceptable
                    };

                    var copySelectedItem = new System.Windows.Controls.MenuItem { Header = "Copy Selected Words" };
                    copySelectedItem.Click += (s, ev) => { CopySelectedOcrWords(); };

                    var copyLineItem = new System.Windows.Controls.MenuItem { Header = "Copy Full Line" };
                    copyLineItem.Click += (s, ev) =>
                    {
                        try
                        {
                            // In merged mode, copy all selected words as the "line"
                            string lineToCopy = _selectedWordTexts.Count > 0 
                                ? string.Join(" ", _selectedWordTexts) 
                                : wordText;
                            if (ClipboardHelper.SafeSetText(lineToCopy))
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast("Copied full line");
                            }
                        }
                        catch { } // Best-effort: failure is acceptable
                    };

                    menu.Items.Add(copyWordItem);
                    menu.Items.Add(copySelectedItem);
                    menu.Items.Add(new System.Windows.Controls.Separator());
                    menu.Items.Add(copyLineItem);
                    wordBorder.ContextMenu = menu;

                    // Position on Canvas (offset by padding so the visible highlight centers on the word)
                    System.Windows.Controls.Canvas.SetLeft(wordBorder, scaledLeft - hPad);
                    System.Windows.Controls.Canvas.SetTop(wordBorder, scaledTop - vPad);
                    OcrOverlayCanvas.Children.Add(wordBorder);
                }
            }
            else if (_ocrResult != null)
            {
                // Legacy fallback: render from OcrResult (used when pre-loaded from ExtractText)
                foreach (var line in _ocrResult.Lines)
                {
                    if (line.Words == null || line.Words.Count == 0) continue;
                    string fullLineText = line.Text;

                    foreach (var word in line.Words)
                    {
                        var rect = word.BoundingRect;
                        if (rect.Width <= 0 || rect.Height <= 0) continue;

                        string wordText = word.Text;
                        double scaledLeft = rect.X / (scaleX * _imageDpiX);
                        double scaledTop = rect.Y / (scaleY * _imageDpiY);
                        double scaledWidth = rect.Width / (scaleX * _imageDpiX);
                        double scaledHeight = rect.Height / (scaleY * _imageDpiY);

                        if (scaledWidth <= 0 || scaledHeight <= 0) continue;
                        double hPad = Math.Max(3, scaledWidth * 0.12);
                        double vPad = Math.Max(1, scaledHeight * 0.08);

                        var wordBorder = new System.Windows.Controls.Border
                        {
                            Width = scaledWidth + hPad * 2,
                            Height = scaledHeight + vPad * 2,
                            Background = transparentBrush,
                            BorderBrush = transparentBrush,
                            BorderThickness = new Thickness(0),
                            CornerRadius = new CornerRadius(2),
                            Cursor = System.Windows.Input.Cursors.IBeam,
                            ToolTip = wordText,
                            Focusable = true,
                            Tag = wordText
                        };
                        wordBorder.MouseLeftButtonDown += (s, ev) =>
                        {
                            var border = s as System.Windows.Controls.Border;
                            if (border == null) return;
                            bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                            if (!ctrlHeld) DeselectAllOcrWords();
                            SelectWordBorder(border);
                            border.Focus();
                            _isDragSelecting = true;
                            _dragStartPoint = ev.GetPosition(OcrOverlayCanvas);
                            OcrOverlayCanvas.CaptureMouse();
                            ev.Handled = true;
                        };
                        wordBorder.MouseEnter += (s, ev) =>
                        {
                            var border = s as System.Windows.Controls.Border;
                            if (border != null && !_selectedWordBorders.Contains(border))
                            { border.Background = hoverBg; border.BorderBrush = hoverBorder; }
                        };
                        wordBorder.MouseLeave += (s, ev) =>
                        {
                            var border = s as System.Windows.Controls.Border;
                            if (border != null && !_selectedWordBorders.Contains(border))
                            { border.Background = transparentBrush; border.BorderBrush = transparentBrush; }
                        };
                        var menu = new System.Windows.Controls.ContextMenu();
                        var copyWordItem = new System.Windows.Controls.MenuItem { Header = "Copy Word" };
                        copyWordItem.Click += (s, ev) => { try { ClipboardHelper.SafeSetText(wordText); } catch { } /* Best-effort: failure is acceptable */ };
                        var copySelectedItem = new System.Windows.Controls.MenuItem { Header = "Copy Selected Words" };
                        copySelectedItem.Click += (s, ev) => { CopySelectedOcrWords(); };
                        var copyLineItem = new System.Windows.Controls.MenuItem { Header = "Copy Full Line" };
                        copyLineItem.Click += (s, ev) => { try { ClipboardHelper.SafeSetText(fullLineText); } catch { } /* Best-effort: failure is acceptable */ };
                        menu.Items.Add(copyWordItem);
                        menu.Items.Add(copySelectedItem);
                        menu.Items.Add(new System.Windows.Controls.Separator());
                        menu.Items.Add(copyLineItem);
                        wordBorder.ContextMenu = menu;
                        System.Windows.Controls.Canvas.SetLeft(wordBorder, scaledLeft - hPad);
                        System.Windows.Controls.Canvas.SetTop(wordBorder, scaledTop - vPad);
                        OcrOverlayCanvas.Children.Add(wordBorder);
                    }
                }
            }

            // Attach canvas-level mouse handlers for drag-to-select (only once)
            if (!_ocrCanvasEventsAttached)
            {
                _ocrCanvasEventsAttached = true;

                OcrOverlayCanvas.MouseMove += (s, ev) =>
                {
                    if (!_isDragSelecting || ev.LeftButton != MouseButtonState.Pressed) return;

                    // Rectangle-sweep selection: select all words whose bounds intersect
                    // the rectangle formed by drag start → current mouse position.
                    Point currentPt = ev.GetPosition(OcrOverlayCanvas);
                    Rect sweepRect = new Rect(_dragStartPoint, currentPt);

                    // Expand sweep vertically to be more forgiving (catch words on the same line)
                    double vExpand = 6;
                    sweepRect = new Rect(
                        sweepRect.Left, sweepRect.Top - vExpand,
                        sweepRect.Width, sweepRect.Height + vExpand * 2);

                    foreach (var child in OcrOverlayCanvas.Children)
                    {
                        if (child is System.Windows.Controls.Border border && border.Tag is string)
                        {
                            double bLeft = System.Windows.Controls.Canvas.GetLeft(border);
                            double bTop = System.Windows.Controls.Canvas.GetTop(border);
                            var bRect = new Rect(bLeft, bTop, border.Width, border.Height);

                            if (sweepRect.IntersectsWith(bRect))
                            {
                                SelectWordBorder(border);
                            }
                        }
                    }
                    ev.Handled = true;
                };

                OcrOverlayCanvas.MouseLeftButtonUp += (s, ev) =>
                {
                    if (_isDragSelecting)
                    {
                        _isDragSelecting = false;
                        OcrOverlayCanvas.ReleaseMouseCapture();
                        ev.Handled = true;
                    }
                };

                // Also allow starting drag from empty canvas space (between words)
                OcrOverlayCanvas.MouseLeftButtonDown += (s, ev) =>
                {
                    if (ev.OriginalSource == OcrOverlayCanvas)
                    {
                        DeselectAllOcrWords();
                        _isDragSelecting = true;
                        _dragStartPoint = ev.GetPosition(OcrOverlayCanvas);
                        OcrOverlayCanvas.CaptureMouse();
                        ev.Handled = true;
                    }
                };
            }
        }

        /// <summary>
        /// Copies all currently selected OCR words to the clipboard, joined by spaces.
        /// </summary>
        private void CopySelectedOcrWords()
        {
            if (_selectedWordTexts.Count == 0) return;
            try
            {
                string combined = string.Join(" ", _selectedWordTexts);
                FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Copying {_selectedWordTexts.Count} words: [{combined}]");
                
                if (ClipboardHelper.SafeSetTextAllowCapture(combined))
                {
                    // Verify clipboard was actually set
                    try
                    {
                        string verify = System.Windows.Clipboard.GetText();
                        FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Clipboard verified: [{verify}]");
                    }
                    catch { } // Best-effort: failure is acceptable
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"Copied {_selectedWordTexts.Count} word{(_selectedWordTexts.Count > 1 ? "s" : "")}");
                }
                else
                {
                    FlyShelf.Classes.Logger.LogAction("OCR_COPY", "SafeSetText returned FALSE — clipboard busy");
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Exception: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed — try again");
            }
        }

        /// <summary>
        /// Selects a single word border (adds to selection if not already selected).
        /// </summary>
        private void SelectWordBorder(System.Windows.Controls.Border border)
        {
            if (border == null || _selectedWordBorders.Contains(border)) return;
            border.Background = _ocrSelectedBg;
            border.BorderBrush = _ocrSelectedBorder;
            border.BorderThickness = new Thickness(2); // increased to 2 for crisp Viewbox scaling
            _selectedWordBorders.Add(border);
            _selectedWordTexts.Add(border.Tag as string ?? "");
        }

        /// <summary>
        /// Deselects all currently selected OCR word overlays.
        /// </summary>
        private void DeselectAllOcrWords()
        {
            var transparent = System.Windows.Media.Brushes.Transparent;
            foreach (var border in _selectedWordBorders)
            {
                border.Background = transparent;
                border.BorderBrush = transparent;
                border.BorderThickness = new Thickness(0);
            }
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();
        }

        /// <summary>
        /// Selects all OCR word overlays on the canvas.
        /// </summary>
        private void SelectAllOcrWords()
        {
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();

            foreach (var child in OcrOverlayCanvas.Children)
            {
                if (child is System.Windows.Controls.Border border && border.Tag is string text)
                {
                    border.Background = _ocrSelectedBg;
                    border.BorderBrush = _ocrSelectedBorder;
                    _selectedWordBorders.Add(border);
                    _selectedWordTexts.Add(text);
                }
            }

            if (_selectedWordBorders.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Selected all {_selectedWordBorders.Count} words • Ctrl+C to copy");
            }
        }

        // ═════════════════════════════════════════════════════════════
        // DOODLE / DRAWING MODE
        // ═════════════════════════════════════════════════════════════

        private async void DoodleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDoodleMode)
            {
                ExitDoodleMode();
            }
            else
            {
                if (_isPdfMode)
                {
                    // For PDF, we enter doodle mode by rendering the first page 
                    // (or the one they are looking at) to the ImageModeGrid
                    await RenderPdfPageToImage(0); // Default to first page for now
                    WebPreview.Visibility = Visibility.Collapsed;
                    PdfEditorGrid.Visibility = Visibility.Collapsed;
                    ImageModeGrid.Visibility = Visibility.Visible;
                }
                EnterDoodleMode();
            }
        }

        private async System.Threading.Tasks.Task RenderPdfPageToImage(int pageIndex)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_item.FilePath);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                if (pageIndex >= pdfDoc.PageCount) return;

                using (var page = pdfDoc.GetPage((uint)pageIndex))
                using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(stream);
                    
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream.AsStream();
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    PreviewImage.Source = bitmap;
                    _isImageLoaded = true;
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"PDF Render Error: {ex.Message}");
            }
        }

        private void EnterDoodleMode()
        {
            _isDoodleMode = true;

            // Size InkCanvas to match image container
            if (ImageContainerGrid != null)
            {
                DoodleCanvas.Width = ImageContainerGrid.Width;
                DoodleCanvas.Height = ImageContainerGrid.Height;
            }

            // Set default drawing attributes
            var da = new System.Windows.Ink.DrawingAttributes
            {
                Color = Colors.White,
                Width = 3,
                Height = 3,
                FitToCurve = true,
                StylusTip = System.Windows.Ink.StylusTip.Ellipse,
                IsHighlighter = false
            };
            DoodleCanvas.DefaultDrawingAttributes = da;

            // Show doodle UI
            DoodleCanvas.Visibility = Visibility.Visible;
            DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
            DoodleToolbar.Visibility = Visibility.Visible;
            DoodleUndoBtn.Visibility = Visibility.Visible;
            DoodleRedoBtn.Visibility = Visibility.Visible;

            // Update doodle button appearance to show it's active
            DoodleBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // green when active
            DoodleBtn.ToolTip = "Exit Doodle Mode";

            // Hide OCR overlay to prevent interaction conflicts
            if (OcrOverlayCanvas.Visibility == Visibility.Visible)
            {
                OcrOverlayCanvas.Visibility = Visibility.Collapsed;
            }

            // Set default color highlight
            _activeDoodleColorBorder = DoodleColorWhite;
            UpdateDoodleColorHighlight();

            // Populate color palette grid (7 columns × 5 rows = 35 colors)
            PopulateDoodlePalette();

            // Sync slider
            DoodleSizeSlider.Value = da.Width;
            DoodleSizeLabel.Text = ((int)da.Width).ToString(CultureInfo.InvariantCulture);

            UpdateDoodleButtonStates();

            FlyShelf.Classes.Logger.LogAction("DOODLE", "Entered doodle mode");
        }

        private void ExitDoodleMode()
        {
            _isDoodleMode = false;

            // Hide doodle UI
            DoodleCanvas.Visibility = Visibility.Collapsed;
            DoodleToolbar.Visibility = Visibility.Collapsed;
            DoodleUndoBtn.Visibility = Visibility.Collapsed;
            DoodleRedoBtn.Visibility = Visibility.Collapsed;
            DoodleSaveBtn.Visibility = Visibility.Collapsed;

            // Reset doodle button appearance
            DoodleBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)); // original purple
            DoodleBtn.ToolTip = "Draw / Annotate";

            // Restore OCR overlay if we had results
            if (_mergedOcrWords != null || _ocrResult != null)
            {
                OcrOverlayCanvas.Visibility = Visibility.Visible;
            }

            FlyShelf.Classes.Logger.LogAction("DOODLE", "Exited doodle mode");
        }

        private void DoodleCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            // Push to undo stack and clear redo (new stroke breaks redo chain)
            _doodleUndoStack.Push(e.Stroke);
            _doodleRedoStack.Clear();
            _hasUnsavedDoodle = true;
            UpdateDoodleButtonStates();
        }

        private void DoodleCanvas_StrokeErased(object sender, RoutedEventArgs e)
        {
            // When strokes are erased, we can't easily push them to undo,
            // but we mark as unsaved and clear redo
            _doodleRedoStack.Clear();
            _hasUnsavedDoodle = true;
            UpdateDoodleButtonStates();
        }

        private void DoodleUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_doodleUndoStack.Count == 0) return;

            var stroke = _doodleUndoStack.Pop();
            if (DoodleCanvas.Strokes.Remove(stroke))
            {
                _doodleRedoStack.Push(stroke);
            }
            _hasUnsavedDoodle = DoodleCanvas.Strokes.Count > 0;
            UpdateDoodleButtonStates();
        }

        private void DoodleRedo_Click(object sender, RoutedEventArgs e)
        {
            if (_doodleRedoStack.Count == 0) return;

            var stroke = _doodleRedoStack.Pop();
            DoodleCanvas.Strokes.Add(stroke);
            _doodleUndoStack.Push(stroke);
            _hasUnsavedDoodle = true;
            UpdateDoodleButtonStates();
        }

        private void UpdateDoodleButtonStates()
        {
            DoodleUndoBtn.IsEnabled = _doodleUndoStack.Count > 0;
            DoodleRedoBtn.IsEnabled = _doodleRedoStack.Count > 0;
            DoodleSaveBtn.Visibility = _hasUnsavedDoodle ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void DoodleSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isPdfMode)
            {
                try
                {
                    LoadingProgress.Visibility = Visibility.Visible;
                    // Flatten doodle to image
                    var rtb = new RenderTargetBitmap((int)ImageContainerGrid.Width, (int)ImageContainerGrid.Height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(ImageContainerGrid);
                    
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    
                    string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FlyShelf_PdfDoodle_{Guid.NewGuid()}.png");
                    using (var fs = System.IO.File.OpenWrite(tempPath))
                    {
                        encoder.Save(fs);
                    }
                    
                    // We assume doodle was on page 0 for now (the one loaded in RenderPdfPageToImage)
                    // In a full impl, we'd track WHICH page was being doodled.
                    _pdfModifiedPages[0] = tempPath;
                    _isPdfModified = true;
                    
                    FlyShelf.Windows.ToastWindow.ShowToast("🎨 Annotation applied to page");
                    ExitDoodleMode();
                }
                catch (Exception ex)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Doodle Save Error: {ex.Message}");
                }
                finally
                {
                    LoadingProgress.Visibility = Visibility.Collapsed;
                }
                return;
            }
            if (!_isImageLoaded || string.IsNullOrEmpty(_item?.FilePath)) return;
            if (DoodleCanvas.Strokes.Count == 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("No strokes to save");
                return;
            }

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                DoodleSaveBtn.IsEnabled = false;

                string filePath = _item.FilePath;
                string ext = Path.GetExtension(filePath).ToLower(CultureInfo.InvariantCulture);

                // Capture strokes on UI thread before going to background
                var strokesCopy = new StrokeCollection(DoodleCanvas.Strokes);
                double canvasW = DoodleCanvas.Width;
                double canvasH = DoodleCanvas.Height;

                // Get the source bitmap for compositing
                var sourceImage = PreviewImage.Source as BitmapSource;
                if (sourceImage == null)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Cannot save: no image loaded");
                    return;
                }

                // Render strokes onto the image at full resolution
                int pixelW = sourceImage.PixelWidth;
                int pixelH = sourceImage.PixelHeight;

                // Create a DrawingVisual that composites the original image + strokes
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    // Draw the original image
                    dc.DrawImage(sourceImage, new Rect(0, 0, pixelW, pixelH));

                    // Scale factor from canvas DIPs to image pixels
                    double scaleX = pixelW / canvasW;
                    double scaleY = pixelH / canvasH;

                    // Draw each stroke scaled to pixel coordinates
                    foreach (var stroke in strokesCopy)
                    {
                        // Create a scaled copy of the stroke
                        var points = stroke.StylusPoints;
                        var scaledPoints = new StylusPointCollection();
                        foreach (var pt in points)
                        {
                            scaledPoints.Add(new StylusPoint(pt.X * scaleX, pt.Y * scaleY, pt.PressureFactor));
                        }

                        var scaledAttrs = stroke.DrawingAttributes.Clone();
                        scaledAttrs.Width *= scaleX;
                        scaledAttrs.Height *= scaleY;

                        var scaledStroke = new Stroke(scaledPoints, scaledAttrs);
                        scaledStroke.Draw(dc);
                    }
                }

                var rtb = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                // Encode and save on background thread
                await System.Threading.Tasks.Task.Run(() =>
                {
                    BitmapEncoder encoder;
                    if (ext == ".png") encoder = new PngBitmapEncoder();
                    else if (ext == ".bmp") encoder = new BmpBitmapEncoder();
                    else encoder = new JpegBitmapEncoder { QualityLevel = 95 };

                    encoder.Frames.Add(BitmapFrame.Create(rtb));

                    using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                    {
                        encoder.Save(fs);
                    }
                });

                // Reload the saved image to show the baked result
                var freshBmp = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var _fi = new System.IO.FileInfo(filePath!);
                        if (_fi.Length > 100_000_000)
                        {
                            System.Diagnostics.Debug.WriteLine($"[QUICKLOOK] Skipped reload — file too large ({_fi.Length} bytes): {filePath}");
                            return null;
                        }
                        byte[] bytes = File.ReadAllBytes(filePath);
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(bytes))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        return bmp;
                    }
                    catch { return null; }
                });

                if (freshBmp != null)
                {
                    PreviewImage.Source = freshBmp;
                }

                // Clear strokes (they're now baked into the image)
                DoodleCanvas.Strokes.Clear();
                _doodleUndoStack.Clear();
                _doodleRedoStack.Clear();
                _hasUnsavedDoodle = false;
                UpdateDoodleButtonStates();

                FlyShelf.Windows.ToastWindow.ShowToast("🎨 Annotated image saved!");
                FlyShelf.Classes.Logger.LogAction("DOODLE", $"Saved annotated image: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("DOODLE", $"Save failed: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast("Save failed: " + ex.Message);
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                DoodleSaveBtn.IsEnabled = true;
            }
        }

        private void DoodleColorPick_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border colorBorder && colorBorder.Tag is string hexColor)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hexColor);
                    DoodleCanvas.DefaultDrawingAttributes.Color = color;

                    // Update highlight
                    _activeDoodleColorBorder = colorBorder;
                    UpdateDoodleColorHighlight();

                    // Reset palette button to rainbow gradient (preset selected, not custom)
                    var rainbow = new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0),
                        new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.25),
                        new GradientStop(Color.FromRgb(0x10, 0xB9, 0x81), 0.5),
                        new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 0.75),
                        new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 1),
                    }, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
                    rainbow.Freeze();
                    DoodlePaletteBtn.Background = rainbow;

                    // Switch back to ink mode if in eraser mode
                    if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
                    {
                        DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                        DoodleEraserBtn.Background = Brushes.Transparent;
                        DoodleEraserLabel.Text = "Eraser";
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }
            e.Handled = true;
        }

        private void UpdateDoodleColorHighlight()
        {
            var accentBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xA7, 0x8B, 0xFA));
            accentBrush.Freeze();
            var transparentBrush = Brushes.Transparent;

            // Reset all color borders
            foreach (var cb in new[] { DoodleColorWhite, DoodleColorRed, DoodleColorYellow, DoodleColorGreen, DoodleColorBlue })
            {
                cb.BorderThickness = new Thickness(1);
                cb.BorderBrush = transparentBrush;
            }

            // Highlight active
            if (_activeDoodleColorBorder != null)
            {
                _activeDoodleColorBorder.BorderThickness = new Thickness(2);
                _activeDoodleColorBorder.BorderBrush = accentBrush;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // COLOR PALETTE — Full color grid with custom picker
        // ═══════════════════════════════════════════════════════════════

        private static readonly string[] _paletteColors = new[]
        {
            // Row 1: Pure + Warm tones
            "#FFFFFF", "#C0C0C0", "#808080", "#404040", "#000000", "#FFF1F2", "#FEF3C7",
            // Row 2: Reds
            "#FCA5A5", "#F87171", "#EF4444", "#DC2626", "#B91C1C", "#991B1B", "#7F1D1D",
            // Row 3: Oranges + Yellows
            "#FDBA74", "#FB923C", "#F59E0B", "#EAB308", "#CA8A04", "#A16207", "#854D0E",
            // Row 4: Greens + Teals
            "#86EFAC", "#4ADE80", "#22C55E", "#10B981", "#059669", "#047857", "#065F46",
            // Row 5: Blues + Purples
            "#93C5FD", "#60A5FA", "#3B82F6", "#6366F1", "#8B5CF6", "#A855F7", "#EC4899",
        };

        private void PopulateDoodlePalette()
        {
            if (DoodlePaletteGrid.Children.Count > 0) return; // Already populated

            foreach (string hex in _paletteColors)
            {
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(1.5),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = hex,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = hex == "#FFFFFF"
                        ? new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xA3, 0xB8))
                        : Brushes.Transparent,
                    ToolTip = hex,
                };

                // Hover effect
                swatch.MouseEnter += (s, _) =>
                {
                    if (s is Border b) b.RenderTransform = new System.Windows.Media.ScaleTransform(1.15, 1.15, 11, 11);
                };
                swatch.MouseLeave += (s, _) =>
                {
                    if (s is Border b) b.RenderTransform = null;
                };

                swatch.MouseLeftButtonDown += DoodlePaletteSwatch_Click;
                DoodlePaletteGrid.Children.Add(swatch);
            }
        }

        private void DoodlePalette_Click(object sender, MouseButtonEventArgs e)
        {
            DoodlePalettePopup.IsOpen = !DoodlePalettePopup.IsOpen;
            e.Handled = true;
        }

        private void DoodlePaletteSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border swatch && swatch.Tag is string hex)
            {
                ApplyDoodleColor(hex);
                DoodlePalettePopup.IsOpen = false;
            }
            e.Handled = true;
        }

        private void DoodleCustomColor_Click(object sender, MouseButtonEventArgs e)
        {
            DoodlePalettePopup.IsOpen = false;

            // WPF-native hex color input dialog
            var currentColor = DoodleCanvas.DefaultDrawingAttributes.Color;
            string currentHex = $"#{currentColor.R:X2}{currentColor.G:X2}{currentColor.B:X2}";

            var inputWin = new Window
            {
                Title = "Custom Color",
                Width = 320,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)),
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            var label = new TextBlock
            {
                Text = "Enter hex color (e.g. #FF5733):",
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(label);

            var inputRow = new StackPanel { Orientation = Orientation.Horizontal };
            var hexInput = new TextBox
            {
                Text = currentHex,
                Width = 160,
                FontSize = 14,
                FontFamily = new FontFamily("Consolas"),
                Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA7, 0x8B, 0xFA)),
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var previewSwatch = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 0, 0, 0),
                Background = new SolidColorBrush(currentColor),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            };
            hexInput.TextChanged += (_, _) =>
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(hexInput.Text.Trim());
                    previewSwatch.Background = new SolidColorBrush(c);
                }
                catch { }
            };
            inputRow.Children.Add(hexInput);
            inputRow.Children.Add(previewSwatch);
            panel.Children.Add(inputRow);

            string result = null;
            var okBtn = new System.Windows.Controls.Button
            {
                Content = "Apply",
                Width = 80,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(0, 6, 0, 6),
                FontSize = 12,
            };
            okBtn.Click += (_, _) => { result = hexInput.Text.Trim(); inputWin.Close(); };
            panel.Children.Add(okBtn);

            inputWin.Content = panel;
            hexInput.SelectAll();
            hexInput.Focus();
            inputWin.ShowDialog();

            if (!string.IsNullOrEmpty(result))
            {
                try
                {
                    // Validate it parses
                    ColorConverter.ConvertFromString(result);
                    ApplyDoodleColor(result);
                }
                catch
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Invalid color — use format like #FF5733");
                }
            }
            e.Handled = true;
        }

        private void ApplyDoodleColor(string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                DoodleCanvas.DefaultDrawingAttributes.Color = color;

                // Clear preset highlight (custom color selected)
                _activeDoodleColorBorder = null;
                UpdateDoodleColorHighlight();

                // Update palette button to show the chosen color
                DoodlePaletteBtn.Background = new SolidColorBrush(color);

                // Switch back to ink mode if in eraser mode
                if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
                {
                    DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    DoodleEraserBtn.Background = Brushes.Transparent;
                    DoodleEraserLabel.Text = "Eraser";
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void DoodleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DoodleCanvas == null) return;
            int size = (int)e.NewValue;
            DoodleCanvas.DefaultDrawingAttributes.Width = size;
            DoodleCanvas.DefaultDrawingAttributes.Height = size;
            if (DoodleSizeLabel != null) DoodleSizeLabel.Text = size.ToString(CultureInfo.InvariantCulture);
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double scale = e.NewValue;
            if (_isPdfMode && !_isPdfEditorMode && WebPreview != null && WebPreview.CoreWebView2 != null)
            {
                WebPreview.ZoomFactor = scale;
            }
            else if (_isPdfEditorMode && PdfThumbnailPanel != null)
            {
                foreach (UIElement child in PdfThumbnailPanel.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        fe.Width = 130 * scale;
                        fe.Height = 170 * scale;
                    }
                }
            }
        }

        private void DoodleEraser_Click(object sender, MouseButtonEventArgs e)
        {
            if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
            {
                // Switch back to ink
                DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                DoodleEraserBtn.Background = Brushes.Transparent;
                DoodleEraserLabel.Text = "Eraser";
            }
            else
            {
                // Switch to eraser
                DoodleCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
                DoodleEraserBtn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xEF, 0x44, 0x44));
                DoodleEraserLabel.Text = "Drawing";
            }
            e.Handled = true;
        }

        private void DoodleClearAll_Click(object sender, MouseButtonEventArgs e)
        {
            if (DoodleCanvas.Strokes.Count == 0) return;

            // Push all current strokes to undo before clearing
            foreach (var stroke in DoodleCanvas.Strokes)
            {
                _doodleUndoStack.Push(stroke);
            }
            DoodleCanvas.Strokes.Clear();
            _doodleRedoStack.Clear();
            _hasUnsavedDoodle = false;
            UpdateDoodleButtonStates();

            FlyShelf.Windows.ToastWindow.ShowToast("All strokes cleared");
            e.Handled = true;
        }
        // ═══════════════════════════════════════════════════════════
        // PDF MANAGEMENT LOGIC
        // ═══════════════════════════════════════════════════════════

        private async void PdfManage_Click(object sender, RoutedEventArgs e)
        {
            if (!_isPdfMode) return;

            if (!_isPdfEditorMode)
            {
                _isPdfEditorMode = true;
                PdfAddBtn.Visibility = Visibility.Visible;
                PdfSaveBtn.Visibility = Visibility.Visible;
                PdfEditorGrid.Visibility = Visibility.Visible;
                WebPreview.Visibility = Visibility.Collapsed;
                PdfManageBtn.Appearance = WpfUi.ControlAppearance.Primary;
                PdfManageBtn.ToolTip = "Back to Browser View";

                if (_pdfPageEntries.Count == 0)
                {
                    await LoadPdfPagesAsync(_item.FilePath, true);
                }
                else
                {
                    RebuildPdfGrid();
                }
            }
            else
            {
                _isPdfEditorMode = false;
                WebPreview.Visibility = Visibility.Visible;
                PdfEditorGrid.Visibility = Visibility.Collapsed;
                PdfAddBtn.Visibility = Visibility.Collapsed;
                PdfSaveBtn.Visibility = Visibility.Collapsed;
                PdfManageBtn.Appearance = WpfUi.ControlAppearance.Secondary;
                PdfManageBtn.ToolTip = "Manage Pages (Reorder / Add)";
            }
        }

        private async System.Threading.Tasks.Task LoadPdfPagesAsync(string path, bool isInitial = false)
        {
            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                var file = await StorageFile.GetFileFromPathAsync(path);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                
                string fileName = System.IO.Path.GetFileName(path);

                for (uint i = 0; i < pdfDoc.PageCount; i++)
                {
                    var entry = new PageEntry
                    {
                        OriginalPage = (int)i + 1,
                        SourceFile = path,
                        SourceLabel = isInitial ? "" : fileName,
                        IsExternal = !isInitial
                    };
                    _pdfPageEntries.Add(entry);

                    // Load thumbnail
                    using (var page = pdfDoc.GetPage(i))
                    using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        var options = new WinPdf.PdfPageRenderOptions
                        {
                            DestinationWidth = 200,
                            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                        };
                        await page.RenderToStreamAsync(stream, options);
                        
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream.AsStream();
                        bitmap.EndInit();
                        bitmap.Freeze();

                        _pdfThumbnails[$"{path}:{i+1}"] = bitmap;
                    }
                }

                RebuildPdfGrid();
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"PDF Load Error: {ex.Message}");
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void RebuildPdfGrid()
        {
            PdfThumbnailPanel.Children.Clear();
            for (int i = 0; i < _pdfPageEntries.Count; i++)
            {
                var entry = _pdfPageEntries[i];
                var tile = new PdfPageTile
                {
                    PageIndex = i,
                    SourceFile = entry.SourceFile
                };

                string key = $"{entry.SourceFile}:{entry.OriginalPage}";
                if (_pdfThumbnails.TryGetValue(key, out var bmp))
                {
                    tile.SetThumbnail(bmp);
                }

                tile.SetPageInfo(i + 1, entry.SourceLabel, entry.RotationDegrees);
                
                tile.DeleteRequested += (s, idx) => {
                    _pdfPageEntries.RemoveAt(idx);
                    _isPdfModified = true;
                    RebuildPdfGrid();
                };

                tile.RotateRequested += (s, idx) => {
                    _pdfPageEntries[idx].RotationDegrees = (tile.Rotation);
                    _isPdfModified = true;
                };

                // Simple drag-and-drop support
                tile.MouseMove += (s, e) => {
                    if (e.LeftButton == MouseButtonState.Pressed && tile.ActionsOverlay.Visibility != Visibility.Visible)
                    {
                        DragDrop.DoDragDrop(tile, tile, DragDropEffects.Move);
                    }
                };

                tile.Drop += (s, e) => {
                    if (e.Data.GetData(typeof(PdfPageTile)) is PdfPageTile sourceTile)
                    {
                        int oldIndex = sourceTile.PageIndex;
                        int newIndex = tile.PageIndex;
                        if (oldIndex != newIndex)
                        {
                            var item = _pdfPageEntries[oldIndex];
                            _pdfPageEntries.RemoveAt(oldIndex);
                            _pdfPageEntries.Insert(newIndex, item);
                            _isPdfModified = true;
                            RebuildPdfGrid();
                        }
                    }
                };
                tile.AllowDrop = true;

                PdfThumbnailPanel.Children.Add(tile);
            }
        }

        private async void PdfAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PDF Files|*.pdf",
                Title = "Add Pages from PDF",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string file in dlg.FileNames)
                {
                    await LoadPdfPagesAsync(file, false);
                }
                _isPdfModified = true;
            }
        }

        private void PdfSave_Click(object sender, RoutedEventArgs e)
        {
            if (PdfSaveBtn.ContextMenu != null)
            {
                PdfSaveBtn.ContextMenu.PlacementTarget = PdfSaveBtn;
                PdfSaveBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                PdfSaveBtn.ContextMenu.IsOpen = true;
            }
        }

        private async void PdfSaveOverwrite_Click(object sender, RoutedEventArgs e)
        {
            await SavePdfChangesAsync(_item.FilePath);
        }

        private async void PdfSaveAs_Click(object sender, RoutedEventArgs e)
        {
            // Save directly to same directory as source — no file picker
            string sourceDir = Path.GetDirectoryName(_item.FilePath) ?? Path.GetTempPath();
            string baseName = Path.GetFileNameWithoutExtension(_item.FilePath) + $"_Edited_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string outputPath = Path.Combine(sourceDir, baseName);

            await SavePdfChangesAsync(outputPath);

            if (File.Exists(outputPath))
            {
                // Drop into clipboard via HandleDrop
                var dataObj = new System.Windows.DataObject();
                dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPath });
                var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                FlyShelf.Windows.ToastWindow.ShowToast($"PDF saved as copy ✅ {Path.GetFileName(outputPath)}");
                mainWin?.ScrollClipboardToTop();
            }
        }

        private async System.Threading.Tasks.Task SavePdfChangesAsync(string targetPath)
        {
            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                FlyShelf.Windows.ToastWindow.ShowToast("💾 Saving PDF changes...");

                await System.Threading.Tasks.Task.Run(() =>
                {
                    using (var outDoc = new PdfDocument())
                    {
                        var sourceDocs = new Dictionary<string, PdfDocument>();

                        try
                        {
                            foreach (var entry in _pdfPageEntries)
                            {
                                if (_pdfModifiedPages.TryGetValue(_pdfPageEntries.IndexOf(entry), out var modImagePath))
                                {
                                    // Use modified image page
                                    string pagePdf = ConversionUtils.ConvertImageToPdf(modImagePath);
                                    using (var tempDoc = PdfReader.Open(pagePdf, PdfDocumentOpenMode.Import))
                                    {
                                        outDoc.AddPage(tempDoc.Pages[0]);
                                    }
                                }
                                else
                                {
                                    if (!sourceDocs.TryGetValue(entry.SourceFile, out var srcDoc))
                                    {
                                        srcDoc = PdfReader.Open(entry.SourceFile, PdfDocumentOpenMode.Import);
                                        sourceDocs[entry.SourceFile] = srcDoc;
                                    }

                                    var page = outDoc.AddPage(srcDoc.Pages[entry.OriginalPage - 1]);
                                    if (entry.RotationDegrees != 0)
                                    {
                                        page.Rotate = (page.Rotate + entry.RotationDegrees) % 360;
                                    }
                                }
                            }

                            bool isOverwrite = string.Equals(targetPath, _item.FilePath, StringComparison.OrdinalIgnoreCase);
                            string finalPath = targetPath;
                            if (isOverwrite)
                            {
                                finalPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");
                            }

                            outDoc.Save(finalPath);

                            if (isOverwrite)
                            {
                                System.IO.File.Copy(finalPath, targetPath, true);
                                System.IO.File.Delete(finalPath);
                            }
                        }
                        finally
                        {
                            foreach (var doc in sourceDocs.Values) doc.Dispose();
                        }
                    }
                });

                FlyShelf.Windows.ToastWindow.ShowToast("✅ PDF saved successfully!");
                _isPdfModified = false;
                
                bool isOverwrite = string.Equals(targetPath, _item.FilePath, StringComparison.OrdinalIgnoreCase);
                if (!isOverwrite)
                {
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                    if (vm != null)
                    {
                        var newItem = new FlyShelf.ViewModels.ClipboardItem(targetPath);
                        vm.DroppedItems.Insert(0, newItem);
                        FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(newItem);
                        mainWin?.ScrollClipboardToTop();
                    }
                }

                WebPreview.Source = new Uri(targetPath);
                PdfManage_Click(null, null);
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Save Failed: {ex.Message}");
                FlyShelf.Classes.Logger.LogAction("PDF_SAVE_ERR", ex.ToString());
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
