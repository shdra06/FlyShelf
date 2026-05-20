using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO;

namespace AdvanceClip.Windows
{
    public partial class QuickLookWindow : Window
    {
        private AdvanceClip.ViewModels.ClipboardItem _item;
        private Point _startPoint;
        private bool _isImageLoaded = false;

        public QuickLookWindow(AdvanceClip.ViewModels.ClipboardItem item)
        {
            InitializeComponent();
            _item = item;

            PreviewImage.Visibility = Visibility.Collapsed;
            WebPreview.Visibility = Visibility.Collapsed;
            TextPreviewScroll.Visibility = Visibility.Collapsed;
            DocumentPanel.Visibility = Visibility.Collapsed;
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
                bool isLight = AdvanceClip.Classes.SettingsManager.Current.ColorScheme == 1;

                // Toggle DWM Immersive Dark Mode attribute on QuickLook so native shadow borders adapt to light/dark
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        int darkValue = isLight ? 0 : 1;
                        AdvanceClip.Classes.NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref darkValue, sizeof(int));
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
                if (_item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Image)
                {
                    PreviewImage.Visibility = Visibility.Visible;
                    
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
                        TextPreview.Text = "[AdvanceClip Codec Error: Cannot extract raw string payload from this artifact natively]";
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
                    PreviewImage.Source = fresh;
                    AdvanceClip.Classes.Logger.LogAction("ROTATE", "Rotated 90°: " + Path.GetFileName(_item.FilePath));
                }
                else
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast("Rotate failed: File could not be written or read");
                }
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("ROTATE", "Failed: " + ex.Message);
                AdvanceClip.Windows.ToastWindow.ShowToast("Rotate failed: " + ex.Message);
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
                AdvanceClip.Classes.SettingsManager.Current.QuickLookWidth = this.Width;
                AdvanceClip.Classes.SettingsManager.Current.QuickLookHeight = this.Height;
                AdvanceClip.Classes.SettingsManager.Save();
            }
            try
            {
                WebPreview.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }
    }
}
