using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        private FlyShelf.ViewModels.ClipboardItem _item;
        private Point _startPoint;
        private bool _isImageLoaded = false;
        private global::Windows.Media.Ocr.OcrResult _ocrResult = null;
        private double _originalWidth = 0;
        private double _originalHeight = 0;
        // The pixel dimensions of the bitmap that was passed to the OCR engine.
        // May differ from _originalWidth/_originalHeight if the image was upscaled for better OCR.
        private double _ocrBitmapWidth = 0;
        private double _ocrBitmapHeight = 0;

        public QuickLookWindow(FlyShelf.ViewModels.ClipboardItem item, global::Windows.Media.Ocr.OcrResult preLoadedOcr = null)
        {
            InitializeComponent();
            _item = item;
            _ocrResult = preLoadedOcr;

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
        }

        private void ApplyTheme()
        {
            try
            {
                bool isLight = FlyShelf.Classes.SettingsManager.Current.ColorScheme == 1;

                // Toggle DWM Immersive Dark Mode attribute on QuickLook so native shadow borders adapt to light/dark
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int darkValue = isLight ? 0 : 1;
                        FlyShelf.Classes.NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref darkValue, sizeof(int));

                        int cn = FlyShelf.Classes.NativeMethods.DWMWA_COLOR_DARK_GRAY;
                        FlyShelf.Classes.NativeMethods.DwmSetWindowAttribute(hwnd, FlyShelf.Classes.NativeMethods.DWMWA_BORDER_COLOR, ref cn, sizeof(int));
                    }
                }
                catch { }

                if (isLight)
                {
                    OuterBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE5, 0xF5, 0xF6, 0xF8));
                    OuterBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x00, 0x00));
                    HeaderGrid.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE5, 0xE5, 0xE6, 0xE8));
                    HeaderTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
                    TextPreviewScroll.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF6, 0xF8));
                    TextPreview.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x11, 0x11));
                    DocTitle.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x11, 0x11));
                    DocSize.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
                    RotateBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x37, 0x7C, 0xF6)); // Blue
                    OcrBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x96, 0x6C)); // Green
                    CopyAllOcrBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x3A, 0xED)); // Purple
                    CloseBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)); // Red
                    PinBtn.Foreground = this.Topmost 
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 119, 6))  // Amber
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80)); // Gray
                    HelperText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0x11, 0x11, 0x11));
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task LoadContentAsync()
        {
            if (_item == null || string.IsNullOrEmpty(_item.FilePath)) return;

            LoadingProgress.Visibility = Visibility.Visible;

            string ext = Path.GetExtension(_item.FilePath ?? "").ToLower();

            try
            {
                if (_item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Image)
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
                        
                        // Pre-scale intelligently based on original image aspect ratio and dpi to eliminate black spaces
                        double dpiX = bitmap.DpiX > 0 ? bitmap.DpiX / 96.0 : 1.0;
                        double dpiY = bitmap.DpiY > 0 ? bitmap.DpiY / 96.0 : 1.0;
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
                        this.Height = targetH + 40; // Add header height back
                        
                        _isImageLoaded = true;
                        RotateBtn.Visibility = Visibility.Visible;
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
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11))  // #F59E0B
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136)); // #888
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

            if (e.LeftButton == MouseButtonState.Pressed && _isImageLoaded)
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
                        parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
                    }
                    return false;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Do nothing. Let the user keep it floating on their other monitor while they work!
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
                                softwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                    softwareBitmap,
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                            }

                            // Store the actual OCR bitmap pixel dimensions for coordinate mapping.
                            uint ocrW = (uint)softwareBitmap.PixelWidth;
                            uint ocrH = (uint)softwareBitmap.PixelHeight;

                            // For small images, upscale 2x for better OCR text detection.
                            // The OCR engine struggles with text smaller than ~12px.
                            if (ocrW < 1500 && ocrH < 1500)
                            {
                                try
                                {
                                    uint newW = ocrW * 2;
                                    uint newH = ocrH * 2;
                                    // Cap at 4000px (OCR engine max)
                                    if (newW > 4000) { newW = 4000; newH = (uint)(ocrH * (4000.0 / ocrW)); }
                                    if (newH > 4000) { newH = 4000; newW = (uint)(ocrW * (4000.0 / ocrH)); }

                                    // Encode original → InMemoryStream with BitmapTransform scaling → Decode back
                                    var inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
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

                                    softwareBitmap = scaledBitmap;
                                    ocrW = (uint)softwareBitmap.PixelWidth;
                                    ocrH = (uint)softwareBitmap.PixelHeight;
                                }
                                catch (Exception upscaleEx)
                                {
                                    FlyShelf.Classes.Logger.LogAction("OCR_UPSCALE", $"Upscale failed (using original): {upscaleEx.Message}");
                                    // Continue with original bitmap — upscale is best-effort
                                }
                            }

                            // Try user profile languages first (more likely to match), then en-US fallback
                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                            if (ocrEngine == null)
                            {
                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                            }

                            if (ocrEngine != null)
                            {
                                var result = await ocrEngine.RecognizeAsync(softwareBitmap);
                                return (result, (double)ocrW, (double)ocrH);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK_OCR_FAIL", ex.Message);
                    }
                    return (null, 0.0, 0.0);
                });

                var ocrResult = ocrResultTuple.result;
                if (ocrResult != null && !string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    _ocrResult = ocrResult;
                    _ocrBitmapWidth = ocrResultTuple.Item2;
                    _ocrBitmapHeight = ocrResultTuple.Item3;
                    OcrOverlayCanvas.Visibility = Visibility.Visible;
                    CopyAllOcrBtn.Visibility = Visibility.Visible;
                    RenderOcrOverlay();
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"OCR Complete! {ocrResult.Lines.Count} lines detected. Select text to copy.");
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
            if (_ocrResult == null || string.IsNullOrWhiteSpace(_ocrResult.Text)) return;

            try
            {
                FlyShelf.MainWindow.SetWritingClipboard(true);
                System.Windows.Clipboard.SetText(_ocrResult.Text);
                
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    FlyShelf.MainWindow.SetWritingClipboard(false);
                });

                FlyShelf.Windows.ToastWindow.ShowToast("All Image Text Copied to Clipboard! 📋");
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed: " + ex.Message);
            }
        }

        private Rect GetImageRenderRect(System.Windows.Controls.Image image)
        {
            if (image == null || image.Source == null || image.ActualWidth == 0 || image.ActualHeight == 0)
                return new Rect();

            // CRITICAL: Use PixelWidth/PixelHeight for coordinate mapping,
            // since OCR bounding rects are in pixel coordinates.
            // source.Width/Height are in DIPs and cause misalignment on non-96 DPI images.
            double srcWidth, srcHeight;
            if (image.Source is BitmapSource bmpSrc)
            {
                double dpiScaleX = bmpSrc.DpiX > 0 ? bmpSrc.DpiX / 96.0 : 1.0;
                double dpiScaleY = bmpSrc.DpiY > 0 ? bmpSrc.DpiY / 96.0 : 1.0;
                srcWidth = bmpSrc.PixelWidth / dpiScaleX;
                srcHeight = bmpSrc.PixelHeight / dpiScaleY;
            }
            else
            {
                srcWidth = image.Source.Width;
                srcHeight = image.Source.Height;
            }

            double scaleX = image.ActualWidth / srcWidth;
            double scaleY = image.ActualHeight / srcHeight;

            double scale = Math.Min(scaleX, scaleY);

            double displayWidth = srcWidth * scale;
            double displayHeight = srcHeight * scale;

            double left = (image.ActualWidth - displayWidth) / 2.0;
            double top = (image.ActualHeight - displayHeight) / 2.0;

            return new Rect(left, top, displayWidth, displayHeight);
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
            if (_ocrResult == null || _originalWidth == 0 || _originalHeight == 0) return;

            OcrOverlayCanvas.Children.Clear();
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();

            Rect renderRect = GetImageRenderRect(PreviewImage);
            if (renderRect.Width == 0 || renderRect.Height == 0) return;

            // Use the OCR bitmap dimensions for coordinate mapping.
            // These may differ from _originalWidth/_originalHeight if upscaling was applied.
            double ocrW = _ocrBitmapWidth > 0 ? _ocrBitmapWidth : _originalWidth;
            double ocrH = _ocrBitmapHeight > 0 ? _ocrBitmapHeight : _originalHeight;

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

            foreach (var line in _ocrResult.Lines)
            {
                if (line.Words == null || line.Words.Count == 0) continue;

                string fullLineText = line.Text;

                foreach (var word in line.Words)
                {
                    var rect = word.BoundingRect;
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    string wordText = word.Text;

                    // Map word bounding rect to displayed image coordinates
                    // OCR coords are in the OCR bitmap's pixel space (ocrW x ocrH)
                    double scaledLeft = renderRect.Left + (rect.X / ocrW) * renderRect.Width;
                    double scaledTop = renderRect.Top + (rect.Y / ocrH) * renderRect.Height;
                    double scaledWidth = (rect.Width / ocrW) * renderRect.Width;
                    double scaledHeight = (rect.Height / ocrH) * renderRect.Height;

                    if (scaledWidth <= 0 || scaledHeight <= 0) continue;

                    // Add horizontal padding to fill gaps between words (makes drag selection smoother)
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

                    // --- Ctrl+C keyboard handler ---
                    wordBorder.KeyDown += (s, ev) =>
                    {
                        if (ev.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                        {
                            CopySelectedOcrWords();
                            ev.Handled = true;
                        }
                        // Ctrl+A to select all words
                        if (ev.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                        {
                            SelectAllOcrWords();
                            ev.Handled = true;
                        }
                    };

                    // --- Right-click context menu ---
                    var menu = new System.Windows.Controls.ContextMenu();

                    var copyWordItem = new System.Windows.Controls.MenuItem { Header = "Copy Word" };
                    copyWordItem.Click += (s, ev) =>
                    {
                        try
                        {
                            FlyShelf.MainWindow.SetWritingClipboard(true);
                            System.Windows.Clipboard.SetText(wordText);
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(500);
                                FlyShelf.MainWindow.SetWritingClipboard(false);
                            });
                            FlyShelf.Windows.ToastWindow.ShowToast($"Copied: {wordText}");
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
                            FlyShelf.MainWindow.SetWritingClipboard(true);
                            System.Windows.Clipboard.SetText(fullLineText);
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(500);
                                FlyShelf.MainWindow.SetWritingClipboard(false);
                            });
                            FlyShelf.Windows.ToastWindow.ShowToast("Copied full line");
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
                FlyShelf.MainWindow.SetWritingClipboard(true);
                System.Windows.Clipboard.SetText(combined);
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    FlyShelf.MainWindow.SetWritingClipboard(false);
                });
                FlyShelf.Windows.ToastWindow.ShowToast($"Copied {_selectedWordTexts.Count} word{(_selectedWordTexts.Count > 1 ? "s" : "")}");
            }
            catch { }
        }

        /// <summary>
        /// Selects a single word border (adds to selection if not already selected).
        /// </summary>
        private void SelectWordBorder(System.Windows.Controls.Border border)
        {
            if (border == null || _selectedWordBorders.Contains(border)) return;
            border.Background = _ocrSelectedBg;
            border.BorderBrush = _ocrSelectedBorder;
            border.BorderThickness = new Thickness(1);
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
    }
}
