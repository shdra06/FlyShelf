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
                    RotateBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
                    OcrBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
                    CopyAllOcrBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6));
                    CloseBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
                    PinBtn.Foreground = this.Topmost 
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
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
                var ocrResult = await System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        using (var stream = File.OpenRead(_item.FilePath))
                        {
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                            if (ocrEngine == null)
                            {
                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                            }

                            if (ocrEngine != null)
                            {
                                return await ocrEngine.RecognizeAsync(softwareBitmap);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK_OCR_FAIL", ex.Message);
                    }
                    return null;
                });

                if (ocrResult != null && !string.IsNullOrWhiteSpace(ocrResult.Text))
                {
                    _ocrResult = ocrResult;
                    OcrOverlayCanvas.Visibility = Visibility.Visible;
                    CopyAllOcrBtn.Visibility = Visibility.Visible;
                    RenderOcrOverlay();
                    
                    FlyShelf.Windows.ToastWindow.ShowToast("OCR Text Detection Complete! Select text to copy.");
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

            var source = image.Source;
            double srcWidth = source.Width;
            double srcHeight = source.Height;

            double scaleX = image.ActualWidth / srcWidth;
            double scaleY = image.ActualHeight / srcHeight;

            double scale = Math.Min(scaleX, scaleY);

            double displayWidth = srcWidth * scale;
            double displayHeight = srcHeight * scale;

            double left = (image.ActualWidth - displayWidth) / 2.0;
            double top = (image.ActualHeight - displayHeight) / 2.0;

            return new Rect(left, top, displayWidth, displayHeight);
        }

        private void RenderOcrOverlay()
        {
            if (_ocrResult == null || _originalWidth == 0 || _originalHeight == 0) return;

            OcrOverlayCanvas.Children.Clear();

            Rect renderRect = GetImageRenderRect(PreviewImage);
            if (renderRect.Width == 0 || renderRect.Height == 0) return;

            foreach (var line in _ocrResult.Lines)
            {
                if (line.Words == null || line.Words.Count == 0) continue;

                // Calculate the bounding box of the line by unioning all its words
                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;

                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                    if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                }

                double lineW = maxX - minX;
                double lineH = maxY - minY;

                // Map to actual displayed coordinates
                double scaledLeft = renderRect.Left + (minX / _originalWidth) * renderRect.Width;
                double scaledTop = renderRect.Top + (minY / _originalHeight) * renderRect.Height;
                double scaledWidth = (lineW / _originalWidth) * renderRect.Width;
                double scaledHeight = (lineH / _originalHeight) * renderRect.Height;

                // Ensure non-zero size
                if (scaledWidth <= 0 || scaledHeight <= 0) continue;

                // Create overlay grid container
                var overlayGrid = new System.Windows.Controls.Grid
                {
                    Width = scaledWidth,
                    Height = scaledHeight
                };

                // Semi-transparent highlight border that shows on hover
                var highlightBorder = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x60, 0xA5, 0xFA)), // subtle transparent blue
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x3A, 0x60, 0xA5, 0xFA)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Visibility = Visibility.Collapsed
                };

                // Selectable text box
                var textBox = new System.Windows.Controls.TextBox
                {
                    Text = line.Text,
                    IsReadOnly = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = System.Windows.Media.Brushes.Transparent, // Invisible text so image shines through
                    SelectionBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0x60, 0xA5, 0xFA)),
                    CaretBrush = System.Windows.Media.Brushes.Transparent,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    FontSize = Math.Max(8, scaledHeight * 0.75), // Font size matching scaled height
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.IBeam,
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden
                };

                // Hover interactions
                overlayGrid.MouseEnter += (s, e) => { highlightBorder.Visibility = Visibility.Visible; };
                overlayGrid.MouseLeave += (s, e) => { highlightBorder.Visibility = Visibility.Collapsed; };

                // Right click copy context menu
                var menu = new System.Windows.Controls.ContextMenu();
                var copySelect = new System.Windows.Controls.MenuItem { Header = "Copy Selection" };
                copySelect.Click += (s, e) => 
                {
                    if (!string.IsNullOrEmpty(textBox.SelectedText))
                    {
                        try { System.Windows.Clipboard.SetText(textBox.SelectedText); } catch { }
                    }
                };
                var copyAll = new System.Windows.Controls.MenuItem { Header = "Copy Full Line" };
                copyAll.Click += (s, e) => 
                {
                    try { System.Windows.Clipboard.SetText(line.Text); } catch { }
                };

                menu.Items.Add(copySelect);
                menu.Items.Add(copyAll);
                textBox.ContextMenu = menu;

                overlayGrid.Children.Add(highlightBorder);
                overlayGrid.Children.Add(textBox);

                // Position on Canvas
                System.Windows.Controls.Canvas.SetLeft(overlayGrid, scaledLeft);
                System.Windows.Controls.Canvas.SetTop(overlayGrid, scaledTop);
                OcrOverlayCanvas.Children.Add(overlayGrid);
            }
        }
    }
}
