// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.xaml.cs — Main partial class file.
// Contains: constructor, shared fields, LoadContentAsync, and common
// UI helpers (drag, resize, pin, close, keyboard shortcuts).
//
// Partial class files:
//   • QuickLookWindow.Ocr.cs      — OCR detection, overlay, selection
//   • QuickLookWindow.Doodle.cs   — Drawing/annotation mode
//   • QuickLookWindow.Pdf.cs      — PDF page editor & save
//   • QuickLookWindow.Markdown.cs — Markdown zoom & PDF export
//   • QuickLookWindow.Image.cs    — Image rotation & translate
// ═══════════════════════════════════════════════════════════════════════

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
        private bool _webPreviewInitialized = false;
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
        private EventHandler<object> _zoomHandler;

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
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            _item = item;
            _ocrResult = preLoadedOcr;
            _autoTriggerOcr = autoTriggerOcr;

            // Free PDF thumbnail BitmapImages (unmanaged memory) when window closes
            Closed += (s, ev) => { _pdfThumbnails.Clear(); FlyShelf.Classes.SmoothScrollFeature.Detach(this); };

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
            await SafeAsyncHandler.RunAsync(async () =>
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
            });
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
            // Text items with file:/// URIs should also be allowed for smart resolution
            if (string.IsNullOrEmpty(_item.FilePath) && _item.Extension != "MARKDOWN" && _item.Extension != ".MD" && _item.ItemType != FlyShelf.ViewModels.ClipboardItemType.Code && _item.Extension != "JSON")
            {
                // ═══ SMART FILE URI RESOLUTION ═══
                // If this is a text item containing a file:/// URI, try to resolve it
                // so old clipboard entries (captured before the URI fix) still work in Quick Look
                string rawText = _item.RawContent?.Trim();
                if (!string.IsNullOrEmpty(rawText) && rawText.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string resolvedPath = new Uri(rawText).LocalPath;
                        // Fix %3A-encoded colons: "/c:/path" → "c:\path"
                        if (resolvedPath.Length >= 3 && resolvedPath[0] == '/' && char.IsLetter(resolvedPath[1]) && resolvedPath[2] == ':')
                            resolvedPath = resolvedPath.Substring(1);
                        resolvedPath = resolvedPath.Replace('/', '\\');
                        
                        if (File.Exists(resolvedPath))
                        {
                            // Upgrade: treat this as a file item for Quick Look purposes
                            _item.FilePath = resolvedPath;
                            FlyShelf.Classes.Logger.LogAction("QUICKLOOK", $"Resolved file:// URI to: {resolvedPath}");
                        }
                    }
                    catch { }
                }

                // If still no file path after resolution, show raw text content or return
                if (string.IsNullOrEmpty(_item.FilePath))
                {
                    if (!string.IsNullOrEmpty(_item.RawContent))
                    {
                        // Show text content in the text preview
                        TextPreviewScroll.Visibility = Visibility.Visible;
                        TextPreview.Text = _item.RawContent;
                        this.Width = 550;
                        this.Height = 500;
                        LoadingProgress.Visibility = Visibility.Collapsed;
                    }
                    return;
                }
            }

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

                        // Minimum size to keep controls visible — absolute floor
                        // For very thin images (e.g., 1920×100 banners), the aspect-ratio
                        // scaling can produce tiny windows. Enforce a usable floor.
                        double minW = 400;
                        double minH = 300;
                        if (targetW < minW || targetH < minH)
                        {
                            if (aspect >= 1.0)
                            {
                                targetW = Math.Max(targetW, minW);
                                targetH = Math.Max(targetW / aspect, minH);
                            }
                            else
                            {
                                targetH = Math.Max(targetH, minH);
                                targetW = Math.Max(targetH * aspect, minW);
                            }
                            // Re-cap to work area after floor enforcement
                            if (targetW > maxW) targetW = maxW;
                            if (targetH > maxH) targetH = maxH;
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

                        // Center on screen after dynamic sizing
                        CenterOnScreen();
                        
                        _isImageLoaded = true;
                        RotateBtn.Visibility = Visibility.Visible;
                        if (CopyImageBtn != null) CopyImageBtn.Visibility = Visibility.Visible;
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
                        _webPreviewInitialized = true;
                        
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

                            // ═══ Generate HTML using the proven MarkdownTemplate engine ═══
                            string html = !string.IsNullOrEmpty(_item.FilePath)
                                ? FlyShelf.Classes.MarkdownTemplate.GetHtml(mdText, _item.FilePath)
                                : FlyShelf.Classes.MarkdownTemplate.GetHtml(mdText);
                            
                            WebPreview.Visibility = Visibility.Visible;
                            
                            string userDataFolder = System.IO.Path.Combine(
                                System.IO.Path.GetTempPath(), 
                                "FlyShelf_MdQL_" + Environment.ProcessId);
                            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                            await WebPreview.EnsureCoreWebView2Async(env);
                            _webPreviewInitialized = true;
                            
                            WebPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                            WebPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                            WebPreview.CoreWebView2.Settings.IsZoomControlEnabled = true;
                            WebPreview.CoreWebView2.Settings.IsScriptEnabled = true;
                            WebPreview.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E);
                            
                            // Track zoom changes for the zoom label
                            _zoomHandler = (s, a) => MarkdownWebView_ZoomFactorChanged(s, EventArgs.Empty);
                            WebPreview.ZoomFactorChanged += _zoomHandler;
                            
                            // ═══ FIX: Use NavigateToString instead of file:// URI ═══
                            // file:// URIs in WebView2 can block inline <script> execution
                            // due to security policies. NavigateToString bypasses this entirely
                            // and is the same approach that works for SVG rendering.
                            WebPreview.NavigateToString(html);
                            
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
                        WebPreview.Visibility = Visibility.Collapsed;
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
                            if (_webPreviewInitialized) WebPreview.NavigateToString(html);
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
                // Center on screen after all dynamic sizing — ensures no content type
                // spawns half off-screen regardless of the size computed above
                CenterOnScreen();
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
            if (PinLabel != null) PinLabel.Text = this.Topmost ? "Pinned" : "Pin";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Centers the QuickLook window on the current screen work area.
        /// Called after dynamic sizing so the window doesn't spawn half off-screen.
        /// </summary>
        private void CenterOnScreen()
        {
            var workArea = SystemParameters.WorkArea;
            double newLeft = (workArea.Width - this.Width) / 2 + workArea.Left;
            double newTop = (workArea.Height - this.Height) / 2 + workArea.Top;

            // Clamp to screen bounds
            if (newLeft < workArea.Left) newLeft = workArea.Left;
            if (newTop < workArea.Top) newTop = workArea.Top;
            if (newLeft + this.Width > workArea.Right) newLeft = workArea.Right - this.Width;
            if (newTop + this.Height > workArea.Bottom) newTop = workArea.Bottom - this.Height;

            this.Left = newLeft;
            this.Top = newTop;
        }

        /// <summary>
        /// Copies the current preview image to the system clipboard.
        /// </summary>
        private void CopyImageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (PreviewImage?.Source is BitmapSource bitmapSource)
                {
                    Clipboard.SetImage(bitmapSource);
                    // Brief visual feedback — change button text
                    if (CopyImageBtn.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is System.Windows.Controls.TextBlock tb)
                    {
                        string original = tb.Text;
                        tb.Text = "Copied!";
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                        timer.Tick += (s, args) => { timer.Stop(); tb.Text = original; };
                        timer.Start();
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogCrash("QuickLook_CopyImage", ex);
            }
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
                        
                        MainWindow._isInternalDragSource = true;
                        try 
                        { 
                            DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy); 
                        }
                        catch (Exception ex) 
                        { 
                            Classes.Logger.LogAction("QUICKLOOK_DRAG", $"DoDragDrop failed: {ex.Message}"); 
                        }
                        finally
                        {
                            MainWindow._isInternalDragSource = false;
                        }
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
            // Unsubscribe ZoomFactorChanged to prevent leak
            try { if (_zoomHandler != null && WebPreview?.CoreWebView2 != null) WebPreview.ZoomFactorChanged -= _zoomHandler; } catch { }
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
            // Cleanup WebView2 user data folders
            try {
                string pdfDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FlyShelf_PdfQL_{Environment.ProcessId}");
                if (System.IO.Directory.Exists(pdfDir))
                    _ = System.Threading.Tasks.Task.Run(() => { try { System.IO.Directory.Delete(pdfDir, true); } catch {} });
                string mdDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FlyShelf_MdQL_{Environment.ProcessId}");
                if (System.IO.Directory.Exists(mdDir))
                    _ = System.Threading.Tasks.Task.Run(() => { try { System.IO.Directory.Delete(mdDir, true); } catch {} });
            } catch {}
            base.OnClosed(e);
        }
    }
}
