// ---------------------------------------------------------------
// ClipboardItem — OCR Text Extraction
// ExtractText, ScanForOcrTextAsync
// Split from ClipboardItem.Actions.cs for modularity
//
// v2.2.1: Added 2x bilinear upscale + Bgra8 conversion + dimension
//         capping for dramatically improved OCR accuracy on screenshots.
//         Previously fed raw bitmaps directly to Windows.Media.Ocr which
//         missed small text (<20px). Now matches QuickLookWindow quality.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FlyShelf.ViewModels
{
    public partial class ClipboardItem
    {

        public async Task ExtractText()
        {
            try
            {
                if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;

                if (!FlyShelf.Classes.LicenseManager.CanExtractOcr())
                {
                    FlyShelf.Classes.UpgradePrompt.ShowOcrLimit();
                    return;
                }

                var method = FlyShelf.Classes.SettingsManager.Current.DefaultAiMethod ?? "auto";

                // "local" → always use local engine, skip popup
                if (method == "local")
                {
                    ExtractTextLocal();
                    return;
                }

                // "api" → always use API (if key exists)
                if (method == "api" && FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey)
                {
                    await ExtractTextWithAI();
                    return;
                }

                // "auto" (default) → API if key exists, else popup
                if (FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey)
                {
                    await ExtractTextWithAI();
                    return;
                }

                // No API key → show choice popup
                bool? useAI = await ShowAiOrLocalChoiceAsync("OCR Text Extraction");
                if (useAI == null) return; // Cancelled
                if (useAI == true)
                {
                    await ExtractTextWithAI();
                    return;
                }

                // Local fallback
                ExtractTextLocal();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OCR] ExtractText error: {ex.Message}");
            }
        }

        private void ExtractTextLocal()
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                if (mainWin != null)
                {
                    mainWin.ShowQuickLookForItem(this, preLoadedOcr: null, autoTriggerOcr: true);
                }
            });
        }

        private async Task ExtractTextWithAI()
        {
            try
            {
                FlyShelf.Windows.ToastWindow.ShowToast("AI OCR in progress...");

                if (string.IsNullOrEmpty(FilePath)) return;
                byte[] imageBytes = await Task.Run(() =>
                {
                    var fi = new System.IO.FileInfo(FilePath!);
                    if (fi.Length > 100_000_000)
                        throw new InvalidOperationException($"File too large for OCR ({fi.Length} bytes): {FilePath}");
                    return File.ReadAllBytes(FilePath);
                });
                string ext = Path.GetExtension(FilePath).ToLowerInvariant();
                string mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/png"
                };

                string result = await FlyShelf.Classes.AiProviderService.Instance.GenerateWithImageAsync(
                    "Extract ALL text from this image. Return only the extracted text, preserving the original formatting and line breaks. Do not add any commentary.",
                    imageBytes, mimeType);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        try { FlyShelf.Classes.ClipboardHelper.SafeSetText(result); } catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("OCR", $"Clipboard write failed: {ex.Message}"); }
                        FlyShelf.Windows.ToastWindow.ShowToast("AI OCR text copied to clipboard!");
                    });
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("AI OCR returned empty result");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"AI OCR failed: {ex.Message}");
                Classes.Logger.LogAction("AI_OCR", $"Failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows a popup with two choices: "Use API Key" or "Use Local (Weak)".
        /// Returns true for AI, false for local, null for cancelled.
        /// </summary>
        private Task<bool?> ShowAiOrLocalChoiceAsync(string featureName)
        {
            var tcs = new TaskCompletionSource<bool?>();

            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                var dialog = new System.Windows.Window
                {
                    Title = featureName,
                    Width = 380,
                    Height = 200,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    WindowStyle = System.Windows.WindowStyle.ToolWindow,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E2E"))
                };

                var panel = new System.Windows.Controls.StackPanel
                {
                    Margin = new System.Windows.Thickness(20),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };

                var title = new System.Windows.Controls.TextBlock
                {
                    Text = $"No API key configured for {featureName}",
                    FontSize = 14,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.White,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new System.Windows.Thickness(0, 0, 0, 16)
                };

                var btnPanel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };

                var aiBtn = new System.Windows.Controls.Button
                {
                    Content = "Set Up API Key",
                    Padding = new System.Windows.Thickness(16, 8, 16, 8),
                    Margin = new System.Windows.Thickness(0, 0, 10, 0),
                    FontSize = 13,
                    Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7C3AED")),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new System.Windows.Thickness(0)
                };

                var localBtn = new System.Windows.Controls.Button
                {
                    Content = "Use Local (Weak)",
                    Padding = new System.Windows.Thickness(16, 8, 16, 8),
                    FontSize = 13,
                    Background = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#374151")),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new System.Windows.Thickness(0)
                };

                aiBtn.Click += (s, e) =>
                {
                    dialog.Close();
                    bool hasKey = FlyShelf.Classes.AiProviderService.Instance.EnsureApiKeyOrPrompt(null);
                    tcs.TrySetResult(hasKey ? true : null);
                };

                localBtn.Click += (s, e) =>
                {
                    dialog.Close();
                    tcs.TrySetResult(false);
                };

                dialog.Closed += (s, e) =>
                {
                    tcs.TrySetResult(null);
                };

                btnPanel.Children.Add(aiBtn);
                btnPanel.Children.Add(localBtn);
                panel.Children.Add(title);
                panel.Children.Add(btnPanel);
                dialog.Content = panel;
                dialog.ShowDialog();
            });

            return tcs.Task;
        }



        public void ScanForOcrTextAsync(string path)
        {
            if (!FlyShelf.Classes.LicenseManager.CanExtractOcr()) return;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                    using (var stream = File.OpenRead(path))
                    {
                        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                        try
                        {
                            // ── Pixel format conversion ──
                        if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                            softwareBitmap.BitmapAlphaMode != global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
                        {
                            var original = softwareBitmap;
                            softwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                original,
                                global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                            original.Dispose();
                        }

                        // ── 2x upscale for small/medium images ──
                        uint imgW = (uint)softwareBitmap.PixelWidth;
                        uint imgH = (uint)softwareBitmap.PixelHeight;

                        if (Math.Max(imgW, imgH) < 2800)
                        {
                            try
                            {
                                uint newW = imgW * 3;
                                uint newH = imgH * 3;
                                if (newW > 3800) { newW = 3800; newH = (uint)(imgH * (3800.0 / imgW)); }
                                if (newH > 3800) { newH = 3800; newW = (uint)(imgW * (3800.0 / imgH)); }

                                using var inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                                var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                    global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
                                encoder.SetSoftwareBitmap(softwareBitmap);
                                encoder.BitmapTransform.ScaledWidth = newW;
                                encoder.BitmapTransform.ScaledHeight = newH;
                                encoder.BitmapTransform.InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
                                await encoder.FlushAsync();

                                inMemStream.Seek(0);
                                var dec2 = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inMemStream);
                                var upscaledBitmap = await dec2.GetSoftwareBitmapAsync(
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                                softwareBitmap.Dispose();
                                softwareBitmap = upscaledBitmap;
                            }
                            catch (Exception upscaleEx)
                            {
                                FlyShelf.Classes.Logger.LogAction("AUTO_OCR_UPSCALE", $"Upscale failed (using original): {upscaleEx.Message}");
                            }
                        }
                        else if (Math.Max(imgW, imgH) > 3800)
                        {
                            try
                            {
                                double scale = 3800.0 / Math.Max(imgW, imgH);
                                uint newW = (uint)(imgW * scale);
                                uint newH = (uint)(imgH * scale);

                                using var inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                                var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                    global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
                                encoder.SetSoftwareBitmap(softwareBitmap);
                                encoder.BitmapTransform.ScaledWidth = newW;
                                encoder.BitmapTransform.ScaledHeight = newH;
                                encoder.BitmapTransform.InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Linear;
                                await encoder.FlushAsync();

                                inMemStream.Seek(0);
                                var dec2 = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inMemStream);
                                var downscaledBitmap = await dec2.GetSoftwareBitmapAsync(
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                                softwareBitmap.Dispose();
                                softwareBitmap = downscaledBitmap;
                            }
                            catch (Exception downscaleEx)
                            {
                                FlyShelf.Classes.Logger.LogAction("AUTO_OCR_DOWNSCALE", $"Downscale failed (using original): {downscaleEx.Message}");
                            }
                        }

                        // ── Try Modern OCR Engine first (background scan) ──
                        var modernAutoResult = await FlyShelf.Classes.ModernOcrEngine.RecognizeAsync(softwareBitmap);
                        if (modernAutoResult != null)
                        {
                            var (autoText, _) = modernAutoResult.Value;
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            {
                                if (this.ItemType != ClipboardItemType.QRCode)
                                {
                                    this.RawContent = autoText;
                                    FlyShelf.Classes.Logger.LogAction("AUTO_OCR", $"AI TextRecognizer extracted {autoText.Length} chars ✓");
                                }
                            });
                            return; // Modern engine succeeded — skip legacy pipeline
                        }

                        // ── Smart contrast enhancement (auto dark/light detection) ──
                        try
                        {
                            var enhanced = FlyShelf.Classes.OcrPreprocessor.SmartEnhance(softwareBitmap);
                            var preEnhance = softwareBitmap;
                            softwareBitmap = enhanced;
                            if (!ReferenceEquals(preEnhance, enhanced)) preEnhance.Dispose();
                        }
                        catch (Exception enhanceEx)
                        {
                            FlyShelf.Classes.Logger.LogAction("OCR_ENHANCE", $"Auto-OCR enhancement failed (using original): {enhanceEx.Message}");
                        }

                        // ── Run OCR ──
                        var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                        if (ocrEngine == null)
                        {
                            ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                        }

                        if (ocrEngine != null)
                        {
                            var result = await ocrEngine.RecognizeAsync(softwareBitmap);

                            if (result != null && result.Lines.Count > 0)
                            {
                                // Build text line-by-line to preserve paragraph structure
                                var lineTexts = new System.Collections.Generic.List<string>();
                                foreach (var line in result.Lines)
                                    lineTexts.Add(line.Text);
                                string ocrText = string.Join("\n", lineTexts);

                                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                {
                                    if (this.ItemType != ClipboardItemType.QRCode)
                                    {
                                        this.RawContent = ocrText;
                                        FlyShelf.Classes.Logger.LogAction("AUTO_OCR", $"Successfully extracted {ocrText.Length} chars, {lineTexts.Count} lines.");
                                    }
                                });
                            }
                        }
                        } // close try
                        finally
                        {
                            softwareBitmap?.Dispose();
                        }
                    } // close using
                } // close outer try
                catch (Exception ex)
                {
                    FlyShelf.Classes.Logger.LogAction("AUTO_OCR_FAIL", $"Failed to run background OCR: {ex.Message}");
                }
            });
        }
    }
}
