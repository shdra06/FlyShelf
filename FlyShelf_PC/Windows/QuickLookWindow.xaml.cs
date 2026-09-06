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
        private readonly System.Threading.CancellationTokenSource _cts = new System.Threading.CancellationTokenSource();

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
        private bool _isDocxMode = false;
        private bool _isCodeEditMode = false;

        // ═══════════════════════════════════════════════════════════
        // ASPECT-RATIO RESIZE CONSTRAINT (image mode)
        // ═══════════════════════════════════════════════════════════
        private bool _isImageAspectLocked = false;
        private double _imageAspectRatio = 1.0; // width / height
        private System.Windows.Interop.HwndSource _hwndSource;

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
            ConfigureCodeEditor();
        }

        private void ConfigureCodeEditor()
        {
            if (CodePreview == null) return;
            bool isLight = SettingsManager.Current?.ColorScheme == 1;

            // Performance optimizations: disable expensive regex parsing and drag drop
            CodePreview.Options.EnableHyperlinks = false;
            CodePreview.Options.EnableEmailHyperlinks = false;
            CodePreview.Options.EnableTextDragDrop = false;
            CodePreview.Options.ShowBoxForControlCharacters = false;
            CodePreview.Options.HighlightCurrentLine = true;
            CodePreview.Options.ConvertTabsToSpaces = true;
            CodePreview.Options.IndentationSize = 4;
            CodePreview.Options.AllowScrollBelowDocument = false;
            CodePreview.Options.EnableVirtualSpace = false;

            // Sharp typography & ClearType
            TextOptions.SetTextFormattingMode(CodePreview, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(CodePreview, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(CodePreview, TextHintingMode.Fixed);
            RenderOptions.SetClearTypeHint(CodePreview, ClearTypeHint.Enabled);

            if (isLight)
            {
                var bg = new SolidColorBrush(Color.FromRgb(248, 249, 250)); bg.Freeze();
                var fg = new SolidColorBrush(Color.FromRgb(36, 41, 47)); fg.Freeze();
                var ln = new SolidColorBrush(Color.FromRgb(140, 149, 159)); ln.Freeze();
                CodePreview.Background = bg;
                CodePreview.Foreground = fg;
                CodePreview.LineNumbersForeground = ln;
                if (TextPreviewScroll != null) TextPreviewScroll.Background = bg;
                if (TextPreview != null) TextPreview.Foreground = fg;

                var lightSel = new SolidColorBrush(Color.FromArgb(80, 9, 105, 218)); lightSel.Freeze();
                CodePreview.TextArea.SelectionBrush = lightSel;
                CodePreview.TextArea.SelectionForeground = null;
                CodePreview.TextArea.SelectionBorder = null;

                var lightLine = new SolidColorBrush(Color.FromArgb(16, 0, 0, 0)); lightLine.Freeze();
                CodePreview.TextArea.TextView.CurrentLineBackground = lightLine;
                CodePreview.TextArea.TextView.CurrentLineBorder = null;
            }
            else
            {
                var bg = new SolidColorBrush(Color.FromRgb(24, 24, 37)); bg.Freeze(); // #181825 Catppuccin Mantle
                var fg = new SolidColorBrush(Color.FromRgb(205, 214, 244)); fg.Freeze(); // #CDD6F4
                var ln = new SolidColorBrush(Color.FromRgb(92, 99, 112)); ln.Freeze(); // #5C6370
                CodePreview.Background = bg;
                CodePreview.Foreground = fg;
                CodePreview.LineNumbersForeground = ln;
                if (TextPreviewScroll != null) TextPreviewScroll.Background = bg;
                if (TextPreview != null) TextPreview.Foreground = fg;

                var darkSel = new SolidColorBrush(Color.FromArgb(90, 97, 175, 239)); darkSel.Freeze(); // #5A61AFEF
                CodePreview.TextArea.SelectionBrush = darkSel;
                CodePreview.TextArea.SelectionForeground = null;
                CodePreview.TextArea.SelectionBorder = null;

                var darkLine = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)); darkLine.Freeze(); // #10FFFFFF
                CodePreview.TextArea.TextView.CurrentLineBackground = darkLine;
                CodePreview.TextArea.TextView.CurrentLineBorder = null;
            }
        }

        private static void ApplyModernSyntaxTheme(IHighlightingDefinition definition)
        {
            if (definition == null) return;
            bool isLight = SettingsManager.Current?.ColorScheme == 1;

            if (!isLight)
            {
                // High-contrast, eye-friendly modern dark palette (OneDark / Catppuccin inspired)
                var commentBrush = new SimpleHighlightingBrush(Color.FromRgb(127, 132, 142));      // #7F848E Slate Muted
                var stringBrush = new SimpleHighlightingBrush(Color.FromRgb(152, 195, 121));       // #98C379 Sage Green
                var keywordBrush = new SimpleHighlightingBrush(Color.FromRgb(97, 175, 239));       // #61AFEF Sky Blue
                var controlBrush = new SimpleHighlightingBrush(Color.FromRgb(198, 120, 221));      // #C678DD Lilac
                var typeBrush = new SimpleHighlightingBrush(Color.FromRgb(229, 192, 123));         // #E5C07B Warm Gold
                var numberBrush = new SimpleHighlightingBrush(Color.FromRgb(209, 154, 102));       // #D19A66 Amber Orange
                var propertyBrush = new SimpleHighlightingBrush(Color.FromRgb(224, 108, 117));     // #E06C75 Soft Coral
                var functionBrush = new SimpleHighlightingBrush(Color.FromRgb(97, 175, 239));      // #61AFEF Blue
                var punctuationBrush = new SimpleHighlightingBrush(Color.FromRgb(171, 178, 191));  // #ABB2BF Light Slate
                var preprocessorBrush = new SimpleHighlightingBrush(Color.FromRgb(229, 192, 123)); // #E5C07B Gold
                var regexBrush = new SimpleHighlightingBrush(Color.FromRgb(86, 182, 194));         // #56B6C2 Teal

                var visited = new System.Collections.Generic.HashSet<HighlightingColor>();

                void TransformColor(HighlightingColor color)
                {
                    if (color == null || !visited.Add(color)) return;
                    string name = color.Name ?? "";

                    if (name.Contains("Comment", StringComparison.OrdinalIgnoreCase) || name.Contains("DocComment", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = commentBrush;
                        color.FontStyle = FontStyles.Italic;
                    }
                    else if (name.Contains("String", StringComparison.OrdinalIgnoreCase) || name.Contains("Char", StringComparison.OrdinalIgnoreCase) ||
                            (name.Contains("Value", StringComparison.OrdinalIgnoreCase) && (name.Contains("Attribute", StringComparison.OrdinalIgnoreCase) || name.Contains("Css", StringComparison.OrdinalIgnoreCase) || definition.Name == "CSS")))
                    {
                        color.Foreground = stringBrush;
                    }
                    else if (name.Contains("Control", StringComparison.OrdinalIgnoreCase) || name.Contains("Statement", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = controlBrush;
                        color.FontWeight = FontWeights.SemiBold;
                    }
                    else if (name.Contains("Keyword", StringComparison.OrdinalIgnoreCase) || name.Equals("Keywords", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = keywordBrush;
                        color.FontWeight = FontWeights.SemiBold;
                    }
                    else if (name.Contains("Type", StringComparison.OrdinalIgnoreCase) || name.Contains("Class", StringComparison.OrdinalIgnoreCase) || name.Contains("Struct", StringComparison.OrdinalIgnoreCase) || name.Contains("Interface", StringComparison.OrdinalIgnoreCase) || name.Contains("ClassSelector", StringComparison.OrdinalIgnoreCase) || name.Contains("IdSelector", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = typeBrush;
                    }
                    else if (name.Contains("Number", StringComparison.OrdinalIgnoreCase) || name.Contains("Digits", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = numberBrush;
                    }
                    else if (name.Contains("Property", StringComparison.OrdinalIgnoreCase) || name.Contains("Tag", StringComparison.OrdinalIgnoreCase) || name.Contains("Attribute", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = propertyBrush;
                    }
                    else if (name.Contains("Selector", StringComparison.OrdinalIgnoreCase) || name.Contains("Target", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = controlBrush;
                    }
                    else if (name.Contains("Method", StringComparison.OrdinalIgnoreCase) || name.Contains("Function", StringComparison.OrdinalIgnoreCase) || name.Contains("Call", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = functionBrush;
                    }
                    else if (name.Contains("Punctuation", StringComparison.OrdinalIgnoreCase) || name.Contains("Delimiter", StringComparison.OrdinalIgnoreCase) || name.Contains("Bracket", StringComparison.OrdinalIgnoreCase) || name.Contains("Colon", StringComparison.OrdinalIgnoreCase) || name.Contains("CurlyBrackets", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = punctuationBrush;
                    }
                    else if (name.Contains("PreProcessor", StringComparison.OrdinalIgnoreCase) || name.Contains("Macro", StringComparison.OrdinalIgnoreCase) || name.Contains("Directive", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = preprocessorBrush;
                    }
                    else if (name.Contains("Regex", StringComparison.OrdinalIgnoreCase))
                    {
                        color.Foreground = regexBrush;
                    }
                    else if (color.Foreground != null)
                    {
                        var fgColor = color.Foreground.GetColor(null);
                        if (fgColor.HasValue)
                        {
                            var c = fgColor.Value;
                            // Remap dark/harsh AvalonEdit default light colors
                            if (c.R == 0 && c.G == 0 && c.B > 100) // Pure dark blue
                                color.Foreground = keywordBrush;
                            else if (c.R > 160 && c.G == 0 && c.B == 0) // Harsh pure red
                                color.Foreground = propertyBrush;
                            else if (c.R == 0 && c.G > 80 && c.B == 0) // Dark green
                                color.Foreground = stringBrush;
                            else if (c.R == 0 && c.G == 0 && c.B == 0) // Pure black
                                color.Foreground = punctuationBrush;
                            else
                            {
                                // Luminance boost if text is too dim
                                double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                                if (lum < 0.45)
                                {
                                    byte r = (byte)Math.Min(255, c.R + (255 - c.R) * 0.55);
                                    byte g = (byte)Math.Min(255, c.G + (255 - c.G) * 0.55);
                                    byte b = (byte)Math.Min(255, c.B + (255 - c.B) * 0.55);
                                    color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(r, g, b));
                                }
                            }
                        }
                    }
                }

                void ProcessRuleSet(HighlightingRuleSet ruleSet)
                {
                    if (ruleSet == null) return;
                    foreach (var rule in ruleSet.Rules)
                    {
                        if (rule?.Color != null) TransformColor(rule.Color);
                    }
                    foreach (var span in ruleSet.Spans)
                    {
                        if (span == null) continue;
                        if (span.SpanColor != null) TransformColor(span.SpanColor);
                        if (span.StartColor != null) TransformColor(span.StartColor);
                        if (span.EndColor != null) TransformColor(span.EndColor);
                        if (span.RuleSet != null) ProcessRuleSet(span.RuleSet);
                    }
                }

                ProcessRuleSet(definition.MainRuleSet);

                // Also check named colors directly
                string[] standardNames = { "Comment", "DocComment", "String", "Char", "Keywords", "ControlKeywords", "StatementKeywords", "ValueKeywords", "TypeKeywords", "Types", "Classes", "Structs", "Interfaces", "Number", "Digits", "Property", "Tag", "Attribute", "AttributeName", "AttributeValue", "Selector", "ClassSelector", "IdSelector", "Method", "Function", "MethodCall", "FunctionCall", "Punctuation", "Delimiter", "Bracket", "Colon", "CurlyBrackets", "PreProcessor", "Macro", "Directive", "Regex", "Heading", "List", "Link", "Code" };
                foreach (var sName in standardNames)
                {
                    try
                    {
                        var col = definition.GetNamedColor(sName);
                        if (col != null) TransformColor(col);
                    }
                    catch { }
                }
            }
        }

        private async System.Threading.Tasks.Task LoadContentAsync()
        {
            if (_item == null) return;

            // Markdown clipboard items may have RawContent but no FilePath — allow them through
            // Text items with file paths, file:/// URIs, or markdown content should also be allowed for smart resolution
            if (string.IsNullOrEmpty(_item.FilePath) && !_item.IsMarkdownPreview && _item.Extension != "MARKDOWN" && _item.Extension != ".MD" && _item.Extension != "MD" && _item.ItemType != FlyShelf.ViewModels.ClipboardItemType.Code && _item.Extension != "JSON")
            {
                // ═══ SMART FILE PATH / URI RESOLUTION ═══
                string rawText = _item.RawContent?.Trim();
                if (!string.IsNullOrEmpty(rawText))
                {
                    string candidatePath = rawText;
                    if (candidatePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            candidatePath = new Uri(candidatePath).LocalPath;
                            // Fix %3A-encoded colons: "/c:/path" → "c:\path"
                            if (candidatePath.Length >= 3 && candidatePath[0] == '/' && char.IsLetter(candidatePath[1]) && candidatePath[2] == ':')
                                candidatePath = candidatePath.Substring(1);
                            candidatePath = candidatePath.Replace('/', '\\');
                        }
                        catch { }
                    }
                    else if (candidatePath.Length >= 2 && ((candidatePath[0] == '"' && candidatePath[^1] == '"') || (candidatePath[0] == '\'' && candidatePath[^1] == '\'')))
                    {
                        candidatePath = candidatePath.Substring(1, candidatePath.Length - 2).Trim();
                    }

                    if (!string.IsNullOrEmpty(candidatePath) && !candidatePath.Contains('\n') && File.Exists(candidatePath))
                    {
                        _item.FilePath = candidatePath;
                        string fileExt = Path.GetExtension(candidatePath).ToLowerInvariant();
                        if (fileExt == ".md")
                        {
                            _item.Extension = "MARKDOWN";
                            try { _item.RawContent = File.ReadAllText(candidatePath); } catch { }
                        }
                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK", $"Resolved path to: {candidatePath}");
                    }
                }

                // If still no file path after resolution, check for markdown text or show raw text content
                if (string.IsNullOrEmpty(_item.FilePath))
                {
                    if (!string.IsNullOrEmpty(_item.RawContent))
                    {
                        if (FlyShelf.Classes.MarkdownDetector.IsMarkdown(_item.RawContent))
                        {
                            _item.Extension = "MARKDOWN";
                        }
                        else
                        {
                            // Show text content in the text preview
                            TextPreviewScroll.Visibility = Visibility.Visible;
                            TextPreview.Text = _item.RawContent;
                            this.Width = 550;
                            this.Height = 500;
                            LoadingProgress.Visibility = Visibility.Collapsed;
                            if (CodeEditBtn != null) CodeEditBtn.Visibility = Visibility.Visible;
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }

            LoadingProgress.Visibility = Visibility.Visible;

            string ext = Path.GetExtension(_item.FilePath ?? "").ToLower(CultureInfo.InvariantCulture);

            try
            {
                // Default: opaque themed background for non-image content (text, code, PDF, etc.)
                // The image branch below will override this to transparent.
                var themeBg = TryFindResource("ThemeWindowFallback") as System.Windows.Media.Brush
                              ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25));
                OuterBorder.Background = themeBg;
                OuterBorder.BorderBrush = TryFindResource("ThemeOverlayBorder") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
                OuterBorder.BorderThickness = new Thickness(1);
                // Push content below the overlay header for non-image modes
                if (ContentGrid != null) ContentGrid.Margin = new Thickness(0, 40, 0, 0);
                // Make header fully opaque for non-image modes
                HeaderGrid.Background = TryFindResource("ThemeOverlayBg") as System.Windows.Media.Brush
                                        ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25));

                if (_item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Image || _item.ItemType == FlyShelf.ViewModels.ClipboardItemType.QRCode)
                {
                    // Transparent window — image defines the window shape
                    // CRITICAL: Reset window.Background that was overridden by ApplyWindowBackdropAndBackground
                    this.Background = System.Windows.Media.Brushes.Transparent;
                    OuterBorder.Background = System.Windows.Media.Brushes.Transparent;
                    OuterBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
                    OuterBorder.BorderThickness = new Thickness(0);
                    // Image fills entire window — header overlays on top
                    if (ContentGrid != null) ContentGrid.Margin = new Thickness(0);
                    HeaderGrid.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0xCC, 0x18, 0x18, 0x25)); // Semi-transparent

                    PreviewImage.Visibility = Visibility.Visible;
                    if (ImageModeGrid != null) ImageModeGrid.Visibility = Visibility.Visible;
                    
                    var bitmap = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(_item.FilePath) && File.Exists(_item.FilePath))
                            {
                                return FlyShelf.Classes.ImageThumbnailManager.LoadThumbnail(_item.FilePath, 4096);
                            }
                            else if (!string.IsNullOrEmpty(_item.RawContent) && FlyShelf.Classes.ImageThumbnailManager.IsSvgMarkup(_item.RawContent))
                            {
                                return FlyShelf.Classes.ImageThumbnailManager.RenderSvgFromMarkup(_item.RawContent, 2048, 2048);
                            }
                            return _item.Icon;
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

                        // Minimum size — keep controls usable but much smaller
                        // for transparent image mode (no visible background to hide gaps)
                        double minW = 200;
                        double minH = 120;
                        if (targetW < minW || targetH < minH)
                        {
                            if (aspect >= 1.0)
                            {
                                targetW = Math.Max(targetW, minW);
                                targetH = targetW / aspect;
                            }
                            else
                            {
                                targetH = Math.Max(targetH, minH);
                                targetW = targetH * aspect;
                            }
                            // Re-cap to work area after floor enforcement
                            if (targetW > maxW) targetW = maxW;
                            if (targetH > maxH) targetH = maxH;
                        }

                        this.Width = targetW;
                        if (_item.ItemType == FlyShelf.ViewModels.ClipboardItemType.QRCode)
                        {
                            this.Height = targetH + 40; // QR content bar at bottom
                            if (QrContentBar != null) QrContentBar.Visibility = Visibility.Visible;
                            if (QrContentText != null) QrContentText.Text = _item.RawContent;
                        }
                        else
                        {
                            this.Height = targetH; // Header overlays image, no extra space needed
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

                        // ═══ Enable aspect-ratio-locked resize ═══
                        // This prevents black gaps from appearing during resize by
                        // constraining the window to match the image's aspect ratio.
                        _imageAspectRatio = aspect;
                        _isImageAspectLocked = true;
                        InstallAspectRatioHook();
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
                    if (PdfToImagesBtn != null) PdfToImagesBtn.Visibility = Visibility.Visible;
                    if (PdfCompressBtn != null) PdfCompressBtn.Visibility = Visibility.Visible;
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
                else if (ext == ".md" || _item.IsMarkdownPreview || _item.Extension == "MARKDOWN" || _item.Extension == ".MD" || _item.Extension == "MD")
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
                            
                            if (!_webPreviewInitialized)
                            {
                                string userDataFolder = System.IO.Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                                    "FlyShelf", "WebView2_QuickLook");
                                if (!Directory.Exists(userDataFolder)) Directory.CreateDirectory(userDataFolder);
                                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                                await WebPreview.EnsureCoreWebView2Async(env);
                                _webPreviewInitialized = true;
                            }
                            
                            WebPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                            WebPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                            WebPreview.CoreWebView2.Settings.IsZoomControlEnabled = true;
                            WebPreview.CoreWebView2.Settings.IsScriptEnabled = true;
                            WebPreview.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E);
                            
                            // Track zoom changes for the zoom label
                            if (_zoomHandler == null)
                            {
                                _zoomHandler = (s, a) => MarkdownWebView_ZoomFactorChanged(s, EventArgs.Empty);
                                WebPreview.ZoomFactorChanged += _zoomHandler;
                            }
                            
                            // Listen for render completion / errors
                            WebPreview.CoreWebView2.WebMessageReceived += (s, a) =>
                            {
                                try
                                {
                                    string msg = a.TryGetWebMessageAsString();
                                    if (msg == "RENDER_COMPLETE")
                                    {
                                        Dispatcher.Invoke(() => { LoadingProgress.Visibility = Visibility.Collapsed; });
                                    }
                                    else if (msg != null && msg.StartsWith("RENDER_ERROR:", StringComparison.Ordinal))
                                    {
                                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK_MD_ERR", msg);
                                        Dispatcher.Invoke(() => { LoadingProgress.Visibility = Visibility.Collapsed; });
                                    }
                                }
                                catch { }
                            };

                            WebPreview.NavigationCompleted += (s, a) =>
                            {
                                Dispatcher.Invoke(() => { LoadingProgress.Visibility = Visibility.Collapsed; });
                            };

                            // ═══ Map the markdown file's directory to a virtual host for image loading ═══
                            // WebView2's NavigateToString() treats the page as about:blank, blocking all file:// requests.
                            // Virtual host mapping creates an allowed HTTP host that serves local files.
                            if (!string.IsNullOrEmpty(_item.FilePath))
                            {
                                try
                                {
                                    string mdDir = System.IO.Path.GetDirectoryName(_item.FilePath);
                                    if (!string.IsNullOrEmpty(mdDir) && Directory.Exists(mdDir))
                                    {
                                        WebPreview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                                            "md-assets.flyshelf", mdDir,
                                            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                                    }
                                }
                                catch (Exception vhEx)
                                {
                                    FlyShelf.Classes.Logger.LogAction("QUICKLOOK", $"Virtual host mapping failed: {vhEx.Message}");
                                }
                            }

                            WebPreview.NavigateToString(html);
                            
                            // Show markdown-specific buttons
                            if (CopyMdBtn != null) CopyMdBtn.Visibility = Visibility.Visible;
                            if (CopyHtmlBtn != null) CopyHtmlBtn.Visibility = Visibility.Visible;
                            if (MdEditBtn != null) MdEditBtn.Visibility = Visibility.Visible;
                            MdToPdfBtn.Visibility = Visibility.Visible;
                            ZoomResetBtn.Visibility = Visibility.Visible;
                            LoadingProgress.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            TextPreviewScroll.Visibility = Visibility.Visible;
                            TextPreview.Text = "[Empty Markdown]";
                            LoadingProgress.Visibility = Visibility.Collapsed;
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
                        LoadingProgress.Visibility = Visibility.Collapsed;
                    }

                    double screenW = SystemParameters.WorkArea.Width;
                    double screenH = SystemParameters.WorkArea.Height;
                    this.Width = Math.Min(960, Math.Max(600, screenW * 0.85));
                    this.Height = Math.Min(820, Math.Max(650, screenH * 0.90));
                    CenterOnScreen();
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
                            {
                                ApplyModernSyntaxTheme(highlighting);
                                CodePreview.SyntaxHighlighting = highlighting;
                            }
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
                    if (CodeEditBtn != null) CodeEditBtn.Visibility = Visibility.Visible;
                }
                else if (ext is ".docx" or ".doc" or ".rtf" or ".odt")
                {
                    await LoadWordDocumentAsync();
                }
                else if (ext == ".txt" || ext == ".log")
                {
                    TextPreviewScroll.Visibility = Visibility.Visible;
                    
                    string textResult = await System.Threading.Tasks.Task.Run(() =>
                    {
                        try 
                        {
                            return File.ReadAllText(_item.FilePath);
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

                    // Show edit button for text-type items
                    if (CodeEditBtn != null) CodeEditBtn.Visibility = Visibility.Visible;
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

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                if (MaximizeIcon != null) MaximizeIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Maximize24;
                if (MaximizeBtn != null) MaximizeBtn.ToolTip = "Maximize";
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                if (MaximizeIcon != null) MaximizeIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.SquareMultiple24;
                if (MaximizeBtn != null) MaximizeBtn.ToolTip = "Restore";
            }
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

        // ═══════════════════════════════════════════════════════════
        // RESPONSIVE HEADER — Compact mode for narrow windows
        // When the window is too narrow, button text labels collapse
        // leaving only icons for a clean, usable toolbar.
        // ═══════════════════════════════════════════════════════════
        private bool _isHeaderCompact = false;

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateHeaderCompactMode();
        }

        private void UpdateHeaderCompactMode()
        {
            const double compactThreshold = 480;
            bool shouldBeCompact = this.ActualWidth < compactThreshold;

            if (shouldBeCompact == _isHeaderCompact) return; // No change needed
            _isHeaderCompact = shouldBeCompact;

            // Hide/show title text
            if (HeaderTitle != null)
                HeaderTitle.Visibility = shouldBeCompact ? Visibility.Collapsed : Visibility.Visible;

            // Iterate header buttons and collapse/show their label TextBlocks
            if (HeaderButtonsPanel == null) return;
            foreach (var child in HeaderButtonsPanel.Children)
            {
                if (child is Wpf.Ui.Controls.Button btn)
                {
                    // Buttons with StackPanel content containing icon + label
                    if (btn.Content is StackPanel sp)
                    {
                        foreach (var item in sp.Children)
                        {
                            // Label TextBlocks have FontSize 10.5 — icon TextBlocks use larger sizes
                            if (item is TextBlock tb && tb.FontSize <= 11.0)
                            {
                                tb.Visibility = shouldBeCompact ? Visibility.Collapsed : Visibility.Visible;
                            }
                        }
                    }
                }
            }
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
            if (e.OriginalSource is DependencyObject dep && !IsOcrTextBoxSource(dep) && !_isDoodleMode)
            {
                ToggleMaximize();
                e.Handled = true;
            }
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
            // Markdown / Code / Text / DOCX / Doodle keyboard shortcuts
            if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (CodeEditBtn != null && CodeEditBtn.Visibility == Visibility.Visible)
                {
                    CodeEditToggle_Click(null, null);
                    e.Handled = true;
                }
                else if (!string.IsNullOrEmpty(_markdownRawContent) || (MdEditBtn != null && MdEditBtn.Visibility == Visibility.Visible))
                {
                    MarkdownEditToggle_Click(null, null);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_isDoodleMode)
                {
                    DoodleSave_Click(null, null);
                    e.Handled = true;
                }
                else if (_isDocxMode)
                {
                    DocxSave_Click(null, null);
                    e.Handled = true;
                }
                else if (_isCodeEditMode || (CodeSaveBtn != null && CodeSaveBtn.Visibility == Visibility.Visible))
                {
                    CodeSave_Click(null, null);
                    e.Handled = true;
                }
                else if (_isMarkdownEditMode || !string.IsNullOrEmpty(_markdownRawContent))
                {
                    MarkdownSave_Click(null, null);
                    e.Handled = true;
                }
            }
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
            }
        }

        private void CodeEditToggle_Click(object sender, RoutedEventArgs e)
        {
            _isCodeEditMode = !_isCodeEditMode;
            if (_isCodeEditMode)
            {
                if (CodePreview != null && CodePreview.Visibility == Visibility.Visible)
                {
                    CodePreview.IsReadOnly = false;
                    CodePreview.Focus();
                }
                if (TextPreview != null && TextPreviewScroll.Visibility == Visibility.Visible)
                {
                    TextPreview.IsReadOnly = false;
                    TextPreview.Focus();
                }

                if (CodeEditLabel != null) CodeEditLabel.Text = "Lock";
                if (CodeEditIcon != null) CodeEditIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.LockClosed24;
                if (CodeSaveBtn != null) CodeSaveBtn.Visibility = Visibility.Visible;

                FlyShelf.Windows.ToastWindow.ShowToast("Code editor enabled (Ctrl+S to save, Ctrl+E to lock)");
            }
            else
            {
                if (CodePreview != null) CodePreview.IsReadOnly = true;
                if (TextPreview != null) TextPreview.IsReadOnly = true;

                if (CodeEditLabel != null) CodeEditLabel.Text = "Edit";
                if (CodeEditIcon != null) CodeEditIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24;
                if (CodeSaveBtn != null) CodeSaveBtn.Visibility = Visibility.Collapsed;
            }
        }

        private async void CodeSave_Click(object sender, RoutedEventArgs e)
        {
            string updatedContent = CodePreview != null && CodePreview.Visibility == Visibility.Visible
                ? CodePreview.Text
                : (TextPreview?.Text ?? "");

            if (string.IsNullOrEmpty(updatedContent) && _item == null) return;

            try
            {
                if (CodeSaveBtn != null) CodeSaveBtn.IsEnabled = false;

                if (_item != null)
                {
                    _item.RawContent = updatedContent;
                    if (_item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Text)
                    {
                        _item.FileName = updatedContent.Length > 100 ? updatedContent.Substring(0, 100) : updatedContent;
                    }
                }

                if (!string.IsNullOrEmpty(_item?.FilePath) && File.Exists(_item.FilePath))
                {
                    await File.WriteAllTextAsync(_item.FilePath, updatedContent, System.Text.Encoding.UTF8);
                    FlyShelf.Windows.ToastWindow.ShowToast($"Saved! 💾 {Path.GetFileName(_item.FilePath)}");
                }
                else
                {
                    Classes.ClipboardHelper.SafeSetText(updatedContent);
                    FlyShelf.Windows.ToastWindow.ShowToast("Code updated & copied to clipboard! 💾");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Failed to save: {ex.Message} ❌");
            }
            finally
            {
                if (CodeSaveBtn != null) CodeSaveBtn.IsEnabled = true;
            }
        }

        public string EffectivePdfPath => _currentDocxPdfPath ?? _item?.FilePath ?? "";

        private void DocxSave_Click(object sender, RoutedEventArgs e)
        {
            DocxExportPdf_Click(sender, e);
        }

        // ═══════════════════════════════════════════════════════════
        // WM_SIZING HOOK — Aspect-ratio-locked resize for image mode
        // Intercepts the Windows resize message BEFORE the window is
        // redrawn, so the user never sees any black gap / dead space.
        // ═══════════════════════════════════════════════════════════
        private const int WM_SIZING = 0x0214;
        private const int WMSZ_LEFT = 1;
        private const int WMSZ_RIGHT = 2;
        private const int WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4;
        private const int WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6;
        private const int WMSZ_BOTTOMLEFT = 7;
        private const int WMSZ_BOTTOMRIGHT = 8;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void InstallAspectRatioHook()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                if (helper.Handle == IntPtr.Zero) return;
                _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
                _hwndSource?.AddHook(AspectRatioWndProc);
            }
            catch { } // Best-effort
        }

        private IntPtr AspectRatioWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SIZING && _isImageAspectLocked && _imageAspectRatio > 0)
            {
                // Get the current DPI scale factor for accurate pixel-to-DIP conversion
                double dpiScale = 1.0;
                try
                {
                    var source = PresentationSource.FromVisual(this);
                    if (source?.CompositionTarget != null)
                        dpiScale = source.CompositionTarget.TransformToDevice.M11;
                }
                catch { }

                var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
                int edge = wParam.ToInt32();

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                // Calculate desired dimensions maintaining aspect ratio
                // The aspect ratio is width/height of the image content
                double aspect = _imageAspectRatio;

                switch (edge)
                {
                    case WMSZ_LEFT:
                    case WMSZ_RIGHT:
                        // Width changed — adjust height
                        int newH = (int)(width / aspect);
                        rect.Bottom = rect.Top + newH;
                        break;

                    case WMSZ_TOP:
                    case WMSZ_BOTTOM:
                        // Height changed — adjust width
                        int newW = (int)(height * aspect);
                        rect.Right = rect.Left + newW;
                        break;

                    case WMSZ_TOPLEFT:
                        // Dragging top-left — anchor bottom-right
                        if ((double)width / height > aspect)
                        {
                            rect.Top = rect.Bottom - (int)(width / aspect);
                        }
                        else
                        {
                            rect.Left = rect.Right - (int)(height * aspect);
                        }
                        break;

                    case WMSZ_TOPRIGHT:
                        // Dragging top-right — anchor bottom-left
                        if ((double)width / height > aspect)
                        {
                            rect.Top = rect.Bottom - (int)(width / aspect);
                        }
                        else
                        {
                            rect.Right = rect.Left + (int)(height * aspect);
                        }
                        break;

                    case WMSZ_BOTTOMLEFT:
                        // Dragging bottom-left — anchor top-right
                        if ((double)width / height > aspect)
                        {
                            rect.Bottom = rect.Top + (int)(width / aspect);
                        }
                        else
                        {
                            rect.Left = rect.Right - (int)(height * aspect);
                        }
                        break;

                    case WMSZ_BOTTOMRIGHT:
                        // Dragging bottom-right — anchor top-left
                        if ((double)width / height > aspect)
                        {
                            rect.Bottom = rect.Top + (int)(width / aspect);
                        }
                        else
                        {
                            rect.Right = rect.Left + (int)(height * aspect);
                        }
                        break;
                }

                System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();

            // Remove WM_SIZING aspect-ratio hook
            try { _hwndSource?.RemoveHook(AspectRatioWndProc); } catch { }

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
                if (WebPreview != null)
                {
                    try { WebPreview.NavigationStarting -= null; } catch { }
                    try { WebPreview.NavigationCompleted -= null; } catch { }
                    WebPreview.Source = new Uri("about:blank");
                    WebPreview.Dispose();
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("QUICKLOOK_CLOSE", $"Error disposing WebPreview: {ex.Message}");
            }
            // Cleanup WebView2 temp user data folders asynchronously
            try {
                var tempDirs = System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "FlyShelf_QL_*")
                    .Concat(System.IO.Directory.GetDirectories(System.IO.Path.GetTempPath(), "FlyShelf_PdfQL_*"));
                foreach (var dir in tempDirs)
                {
                    _ = System.Threading.Tasks.Task.Run(() => { try { System.IO.Directory.Delete(dir, true); } catch {} });
                }
            } catch {}
            base.OnClosed(e);
        }
    }
}
