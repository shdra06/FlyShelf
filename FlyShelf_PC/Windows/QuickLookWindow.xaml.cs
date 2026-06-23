using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        private FlyShelf.ViewModels.ClipboardItem _item;
        private Point _startPoint;
        private bool _isImageLoaded = false;
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

        public QuickLookWindow(FlyShelf.ViewModels.ClipboardItem item, global::Windows.Media.Ocr.OcrResult preLoadedOcr = null, bool autoTriggerOcr = false)
        {
            InitializeComponent();
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            _item = item;
            _ocrResult = preLoadedOcr;
            _autoTriggerOcr = autoTriggerOcr;

            PreviewImage.Visibility = Visibility.Collapsed;
            if (ImageModeGrid != null) ImageModeGrid.Visibility = Visibility.Collapsed;
            WebPreview.Visibility = Visibility.Collapsed;
            TextPreviewScroll.Visibility = Visibility.Collapsed;
            DocumentPanel.Visibility = Visibility.Collapsed;

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
            if (_item == null || string.IsNullOrEmpty(_item.FilePath)) return;

            LoadingProgress.Visibility = Visibility.Visible;

            string ext = Path.GetExtension(_item.FilePath ?? "").ToLower();

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
                else if (ext == ".pdf" || ext == ".html" || ext == ".htm" || ext == ".xml")
                {
                    WebPreview.Visibility = Visibility.Visible;
                    try { WebPreview.Navigate(new Uri(_item.FilePath)); } catch { }
                    
                    this.Width = 600;
                    this.Height = SystemParameters.WorkArea.Height * 0.8;
                    _isImageLoaded = true; // allow dragging natively
                }
                else if (ext == ".docx" || ext == ".txt" || ext == ".log" || ext == ".md" || ext == ".cs" || ext == ".cpp" || ext == ".js" || ext == ".json")
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
                        catch { }
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
                        DocSize.Text = $"{_item.ItemType.ToString()} Document • {(length / 1024.0 / 1024.0):0.00} MB";
                    }
                    else
                    {
                        DocSize.Text = "Unknown Size";
                    }

                    this.Width = 400;
                    this.Height = 350;
                }
            }
            catch { }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
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
                        string ext = Path.GetExtension(filePath).ToLower();
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
                    try { this.DragMove(); } catch { }
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
                        dataObject.SetData(DataFormats.FileDrop, new string[] { _item.FilePath });
                        
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
            catch { }
            base.OnClosed(e);
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
                                    catch { }
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
                if (ClipboardHelper.SafeSetText(textToCopy))
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
                        catch { }
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
                        catch { }
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
                        copyWordItem.Click += (s, ev) => { try { ClipboardHelper.SafeSetText(wordText); } catch { } };
                        var copySelectedItem = new System.Windows.Controls.MenuItem { Header = "Copy Selected Words" };
                        copySelectedItem.Click += (s, ev) => { CopySelectedOcrWords(); };
                        var copyLineItem = new System.Windows.Controls.MenuItem { Header = "Copy Full Line" };
                        copyLineItem.Click += (s, ev) => { try { ClipboardHelper.SafeSetText(fullLineText); } catch { } };
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
                
                if (ClipboardHelper.SafeSetText(combined))
                {
                    // Verify clipboard was actually set
                    try
                    {
                        string verify = System.Windows.Clipboard.GetText();
                        FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Clipboard verified: [{verify}]");
                    }
                    catch { }
                    
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

        private void DoodleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDoodleMode)
            {
                ExitDoodleMode();
            }
            else
            {
                EnterDoodleMode();
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

            // Sync slider
            DoodleSizeSlider.Value = da.Width;
            DoodleSizeLabel.Text = ((int)da.Width).ToString();

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
            if (DoodleCanvas.Strokes.Contains(stroke))
            {
                DoodleCanvas.Strokes.Remove(stroke);
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
            if (!_hasUnsavedDoodle || _item == null || string.IsNullOrEmpty(_item.FilePath)) return;
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
                string ext = Path.GetExtension(filePath).ToLower();

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

                    // Switch back to ink mode if in eraser mode
                    if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
                    {
                        DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                        DoodleEraserBtn.Background = Brushes.Transparent;
                        DoodleEraserLabel.Text = "Eraser";
                    }
                }
                catch { }
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

        private void DoodleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DoodleCanvas == null) return;
            int size = (int)e.NewValue;
            DoodleCanvas.DefaultDrawingAttributes.Width = size;
            DoodleCanvas.DefaultDrawingAttributes.Height = size;
            if (DoodleSizeLabel != null) DoodleSizeLabel.Text = size.ToString();
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
    }
}
