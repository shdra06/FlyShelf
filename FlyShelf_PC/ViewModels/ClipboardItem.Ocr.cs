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

        public async void ExtractText()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;

            if (!FlyShelf.Classes.LicenseManager.CanExtractOcr())
            {
                FlyShelf.Classes.UpgradePrompt.ShowOcrLimit();
                return;
            }

            try
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Scanning Native Hardware OCR...");

                await Task.Run(async () => 
                {
                    using (var stream = File.OpenRead(FilePath))
                    {
                        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                        // ── Pixel format conversion ──
                        // The OCR engine requires Bgra8/Premultiplied for reliable recognition.
                        // Without this, many images return empty or garbled results.
                        if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                            softwareBitmap.BitmapAlphaMode != global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
                        {
                            softwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                softwareBitmap,
                                global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                        }

                        // ── 2x upscale for small/medium images ──
                        // Windows.Media.Ocr struggles with text below ~20px height.
                        // Most screenshots at 96-144 DPI have text around 12-16px.
                        // 2x upscaling pushes it well above the recognition threshold.
                        uint imgW = (uint)softwareBitmap.PixelWidth;
                        uint imgH = (uint)softwareBitmap.PixelHeight;

                        if (Math.Max(imgW, imgH) < 2800)
                        {
                            try
                            {
                                uint newW = imgW * 3;
                                uint newH = imgH * 3;
                                // Cap at 3800px to stay within OCR engine's ~4000px limit
                                if (newW > 3800) { newW = 3800; newH = (uint)(imgH * (3800.0 / imgW)); }
                                if (newH > 3800) { newH = 3800; newW = (uint)(imgW * (3800.0 / imgH)); }

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
                                softwareBitmap = await dec2.GetSoftwareBitmapAsync(
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                                FlyShelf.Classes.Logger.LogAction("OCR", $"Upscaled {imgW}x{imgH} → {newW}x{newH} for better recognition");
                            }
                            catch (Exception upscaleEx)
                            {
                                FlyShelf.Classes.Logger.LogAction("OCR_UPSCALE", $"Upscale failed (using original): {upscaleEx.Message}");
                            }
                        }
                        else if (Math.Max(imgW, imgH) > 3800)
                        {
                            // Downscale very large images to stay within OCR engine limits
                            try
                            {
                                double scale = 3800.0 / Math.Max(imgW, imgH);
                                uint newW = (uint)(imgW * scale);
                                uint newH = (uint)(imgH * scale);

                                var inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                                var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                    global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
                                encoder.SetSoftwareBitmap(softwareBitmap);
                                encoder.BitmapTransform.ScaledWidth = newW;
                                encoder.BitmapTransform.ScaledHeight = newH;
                                encoder.BitmapTransform.InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Linear;
                                await encoder.FlushAsync();

                                inMemStream.Seek(0);
                                var dec2 = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inMemStream);
                                softwareBitmap = await dec2.GetSoftwareBitmapAsync(
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                                FlyShelf.Classes.Logger.LogAction("OCR", $"Downscaled {imgW}x{imgH} → {newW}x{newH} (OCR engine limit)");
                            }
                            catch (Exception downscaleEx)
                            {
                                FlyShelf.Classes.Logger.LogAction("OCR_DOWNSCALE", $"Downscale failed (using original): {downscaleEx.Message}");
                            }
                        }

                        // ── Multi-pass OCR for maximum accuracy ──
                        // Creates 3 preprocessing variants (Enhanced, Inverted+Enhanced, Otsu Binarized)
                        // and runs OCR on each. Picks the result with the most detected text.
                        // This handles all image types: light backgrounds, dark themes, low contrast.
                        var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                        if (ocrEngine == null)
                        {
                            ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                        }

                        if (ocrEngine != null)
                        {
                            var variants = FlyShelf.Classes.OcrPreprocessor.CreateOcrVariants(softwareBitmap);
                            global::Windows.Media.Ocr.OcrResult bestResult = null;
                            int bestScore = 0;
                            string bestVariantName = "";

                            for (int v = 0; v < variants.Length; v++)
                            {
                                try
                                {
                                    var varResult = await ocrEngine.RecognizeAsync(variants[v].bitmap);
                                    if (varResult != null)
                                    {
                                        int score = 0;
                                        foreach (var line in varResult.Lines)
                                            foreach (var word in line.Words)
                                                score += word.Text.Length;

                                        FlyShelf.Classes.Logger.LogAction("OCR_MULTIPASS",
                                            $"{variants[v].name}: {varResult.Lines.Count} lines, {score} chars");

                                        if (score > bestScore)
                                        {
                                            bestScore = score;
                                            bestResult = varResult;
                                            bestVariantName = variants[v].name;
                                        }
                                    }
                                }
                                catch { }
                                finally
                                {
                                    variants[v].bitmap.Dispose();
                                }
                            }

                            if (bestResult != null && !string.IsNullOrWhiteSpace(bestResult.Text))
                            {
                                FlyShelf.Classes.Logger.LogAction("OCR_MULTIPASS",
                                    $"Winner: {bestVariantName} (score={bestScore})");

                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
                                {
                                    FlyShelf.Classes.ClipboardHelper.SafeSetText(bestResult.Text, suppressEcho: true, echoDelayMs: 500);
                                    FlyShelf.Windows.ToastWindow.ShowToast("OCR Text Copied to Clipboard! 📋");
                                    FlyShelf.Classes.LicenseManager.RecordOcrExtraction();

                                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                                    if (mainWin != null)
                                    {
                                        mainWin.ShowQuickLookForItem(this, bestResult);
                                    }
                                });
                            }
                            else
                            {
                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
                                    FlyShelf.Windows.ToastWindow.ShowToast("No Text Detected in Image.")
                                );
                            }
                        }
                        else
                        {
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
                                FlyShelf.Windows.ToastWindow.ShowToast("Native OCR engine failed to load.")
                            );
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
                    FlyShelf.Windows.ToastWindow.ShowToast($"OCR Engine Missing/Failed")
                );
            }
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

                        // ── Pixel format conversion ──
                        if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                            softwareBitmap.BitmapAlphaMode != global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
                        {
                            softwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                softwareBitmap,
                                global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
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
                                softwareBitmap = await dec2.GetSoftwareBitmapAsync(
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
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

                                var inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                                var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                    global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
                                encoder.SetSoftwareBitmap(softwareBitmap);
                                encoder.BitmapTransform.ScaledWidth = newW;
                                encoder.BitmapTransform.ScaledHeight = newH;
                                encoder.BitmapTransform.InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Linear;
                                await encoder.FlushAsync();

                                inMemStream.Seek(0);
                                var dec2 = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inMemStream);
                                softwareBitmap = await dec2.GetSoftwareBitmapAsync(
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                            }
                            catch (Exception downscaleEx)
                            {
                                FlyShelf.Classes.Logger.LogAction("AUTO_OCR_DOWNSCALE", $"Downscale failed (using original): {downscaleEx.Message}");
                            }
                        }

                        // ── Smart contrast enhancement (auto dark/light detection) ──
                        try
                        {
                            var enhanced = FlyShelf.Classes.OcrPreprocessor.SmartEnhance(softwareBitmap);
                            softwareBitmap = enhanced;
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

                            if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                            {
                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (this.ItemType != ClipboardItemType.QRCode)
                                    {
                                        this.RawContent = result.Text;
                                        FlyShelf.Classes.Logger.LogAction("AUTO_OCR", $"Successfully extracted {result.Text.Length} chars of text.");
                                    }
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    FlyShelf.Classes.Logger.LogAction("AUTO_OCR_FAIL", $"Failed to run background OCR: {ex.Message}");
                }
            });
        }
    }
}
