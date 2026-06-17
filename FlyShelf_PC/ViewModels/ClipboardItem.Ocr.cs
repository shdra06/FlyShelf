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
            try
            {
                if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;

                if (!FlyShelf.Classes.LicenseManager.CanExtractOcr())
                {
                    FlyShelf.Classes.UpgradePrompt.ShowOcrLimit();
                    return;
                }

                // Open QuickLook and auto-trigger its OCR (the T button).
                // This ensures bounding boxes are perfectly aligned with the displayed image,
                // unlike running a separate OCR pipeline with different upscaling/coordinates.
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    if (mainWin != null)
                    {
                        mainWin.ShowQuickLookForItem(this, preLoadedOcr: null, autoTriggerOcr: true);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OCR] ExtractText error: {ex.Message}");
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

                        // ── Try Modern OCR Engine first (background scan) ──
                        var modernAutoResult = await FlyShelf.Classes.ModernOcrEngine.RecognizeAsync(softwareBitmap);
                        if (modernAutoResult != null)
                        {
                            var (autoText, _) = modernAutoResult.Value;
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
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

                            if (result != null && result.Lines.Count > 0)
                            {
                                // Build text line-by-line to preserve paragraph structure
                                var lineTexts = new System.Collections.Generic.List<string>();
                                foreach (var line in result.Lines)
                                    lineTexts.Add(line.Text);
                                string ocrText = string.Join("\n", lineTexts);

                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (this.ItemType != ClipboardItemType.QRCode)
                                    {
                                        this.RawContent = ocrText;
                                        FlyShelf.Classes.Logger.LogAction("AUTO_OCR", $"Successfully extracted {ocrText.Length} chars, {lineTexts.Count} lines.");
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
