// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Image.cs — Image viewing: rotation with file save,
// and translate functionality for text-based content.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
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
    }
}
