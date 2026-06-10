// ---------------------------------------------------------------
// OcrPreprocessor — Image Enhancement for OCR Accuracy
//
// Addresses the core weakness in the Windows.Media.Ocr pipeline:
// colored row highlights (blue, yellow, green) drastically reduce
// text/background contrast, causing character misrecognition
// (0↔O, 1↔l, commas dropped, slashes missed).
//
// Pipeline:
//   1. Highlight Neutralization — replaces light-colored pixels with white
//   2. Contrast Stretch — histogram normalization for max text separation
//
// Used by: ClipboardItem.Ocr.cs, QuickLookWindow.xaml.cs
// ---------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Image preprocessing utilities for improving Windows.Media.Ocr accuracy.
    /// Neutralizes colored row highlights, enhances contrast, and normalizes
    /// pixel values before feeding to the OCR engine.
    /// </summary>
    public static class OcrPreprocessor
    {
        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }

        /// <summary>
        /// Enhances a SoftwareBitmap for improved OCR recognition accuracy.
        ///
        /// Steps:
        /// 1. Neutralize colored highlights — removes blue/yellow/green row backgrounds
        ///    that reduce text contrast and cause character confusion
        /// 2. Contrast stretch — remaps pixel intensity using 2nd/98th percentile histogram
        ///    normalization for maximum text/background separation
        ///
        /// Output has identical dimensions to input. Only pixel values change.
        /// Returns a NEW SoftwareBitmap in Bgra8/Premultiplied format (OCR-ready).
        /// Caller is responsible for disposing both input and output bitmaps.
        /// </summary>
        public static unsafe SoftwareBitmap EnhanceForOcr(SoftwareBitmap input)
        {
            // Convert to Bgra8/Straight for direct pixel manipulation
            SoftwareBitmap working;
            if (input.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                input.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
            {
                working = SoftwareBitmap.Convert(input, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            }
            else
            {
                working = SoftwareBitmap.Copy(input);
            }

            int width = working.PixelWidth;
            int height = working.PixelHeight;

            using (var buffer = working.LockBuffer(BitmapBufferAccessMode.ReadWrite))
            using (var reference = buffer.CreateReference())
            {
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                byteAccess.GetBuffer(out byte* data, out uint capacity);
                var layout = buffer.GetPlaneDescription(0);

                // ═══ PASS 1: Neutralize colored highlights ═══
                //
                // Colored row highlights (blue, yellow, green, etc.) confuse the OCR engine
                // by reducing luminance contrast between text and background. A pixel is
                // classified as a "highlight background" if it has:
                //
                //   - High luminance (> 140): it's a bright/light pixel (not text)
                //   - Noticeable saturation (> 20): it has a color tint (not neutral gray/white)
                //
                // Examples:
                //   Blue highlight   (200,220,240) → lum≈218, sat=40  → ✅ neutralized
                //   Yellow highlight (255,255,200) → lum≈249, sat=55  → ✅ neutralized
                //   White background (255,255,255) → lum=255, sat=0   → ❌ preserved (no tint)
                //   Black text       (30,30,30)    → lum=30,  sat=0   → ❌ preserved (dark)
                //   Dark blue text   (20,30,80)    → lum≈33,  sat=60  → ❌ preserved (dark)
                //
                int neutralizedCount = 0;
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        byte b = data[idx + 0];
                        byte g = data[idx + 1];
                        byte r = data[idx + 2];

                        int maxC = Math.Max(r, Math.Max(g, b));
                        int minC = Math.Min(r, Math.Min(g, b));
                        int saturation = maxC - minC;
                        int luminance = (int)(0.299 * r + 0.587 * g + 0.114 * b);

                        if (luminance > 140 && saturation > 20)
                        {
                            data[idx + 0] = 255; // B → white
                            data[idx + 1] = 255; // G → white
                            data[idx + 2] = 255; // R → white
                            neutralizedCount++;
                        }
                    }
                }

                Logger.LogAction("OCR_ENHANCE", $"Pass 1: Neutralized {neutralizedCount} highlight pixels ({(100.0 * neutralizedCount / (width * height)):F1}% of image)");

                // ═══ PASS 2: Histogram-based contrast stretch ═══
                //
                // After removing colored highlights, text pixels might still have weak contrast.
                // Build a luminance histogram, find the 2nd and 98th percentile values,
                // and remap all channels proportionally to maximize dynamic range.
                //
                // This turns faded/gray text into crisp black-on-white, which is exactly
                // what Windows.Media.Ocr needs for reliable character recognition.
                //
                int[] histogram = new int[256];
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int lum = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        histogram[Math.Clamp(lum, 0, 255)]++;
                    }
                }

                int totalPixels = width * height;
                int p2Target = (int)(totalPixels * 0.02);
                int p98Target = (int)(totalPixels * 0.98);

                int pLow = 0, pHigh = 255;
                int cumulative = 0;
                for (int i = 0; i < 256; i++)
                {
                    cumulative += histogram[i];
                    if (cumulative >= p2Target && pLow == 0) pLow = i;
                    if (cumulative >= p98Target) { pHigh = i; break; }
                }

                // Ensure a minimum range to prevent division by zero or extreme amplification
                int range = Math.Max(pHigh - pLow, 30);
                Logger.LogAction("OCR_ENHANCE", $"Pass 2: Contrast stretch [{pLow}..{pHigh}] → [0..255] (range={range})");

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int val = data[idx + ch];
                            int stretched = (int)((val - pLow) * 255.0 / range);
                            data[idx + ch] = (byte)Math.Clamp(stretched, 0, 255);
                        }
                    }
                }
            }

            // Convert back to Premultiplied for OCR engine compatibility
            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }
    }
}
