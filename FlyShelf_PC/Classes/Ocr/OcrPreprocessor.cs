// ---------------------------------------------------------------
// OcrPreprocessor — Multi-Pass Image Enhancement for OCR Accuracy
//
// Generates multiple preprocessing variants and lets the OCR engine
// pick the best result. This handles ALL image types:
//   - Light backgrounds with colored highlights (tables, spreadsheets)
//   - Dark-themed screenshots (Task Manager, IDE, terminals)
//   - Low-contrast images (faded text, gray backgrounds)
//
// Pipeline variants:
//   1. Enhanced — highlight neutralization + contrast stretch (light images)
//   2. Inverted + Enhanced — for dark theme screenshots
//   3. Otsu Binarized — global threshold, maximum text separation
//   4. Bradley-Roth Adaptive — local threshold, handles varying backgrounds
//   5. Inverted Only — simple inversion without highlight neutralization
//
// Used by: ClipboardItem.Ocr.cs, QuickLookWindow.xaml.cs
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Multi-pass image preprocessing for dramatically improved Windows.Media.Ocr accuracy.
    /// Creates multiple preprocessing variants, each optimized for different image types.
    /// The caller runs OCR on each variant and picks the result with the most detected text.
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
        /// Creates multiple preprocessing variants for multi-pass OCR.
        /// Running OCR on each variant and picking the best result gives dramatically
        /// better accuracy across ALL image types — light backgrounds, dark themes,
        /// colored highlights, and low-contrast images.
        ///
        /// Returns array of { bitmap, variantName } tuples.
        /// Caller must dispose all returned bitmaps.
        /// </summary>
        public static unsafe (SoftwareBitmap bitmap, string name)[] CreateOcrVariants(SoftwareBitmap input)
        {
            var variants = new List<(SoftwareBitmap bitmap, string name)>();

            // Adaptive: for large images (>4 megapixels), use only the most effective
            // 4 variants to reduce peak memory usage from ~256MB to ~128MB.
            bool isLargeImage = (long)input.PixelWidth * input.PixelHeight > 4_000_000;

            // Variant 1: Enhanced (highlight neutralization + contrast stretch)
            // Best for: light backgrounds with colored row highlights (tables, spreadsheets)
            try
            {
                variants.Add((EnhanceForOcr(input), "Enhanced"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"Enhanced variant failed: {ex.Message}");
            }

            // Variant 2: Inverted + Enhanced
            // Best for: dark theme screenshots (Task Manager, IDE, terminals, dark mode apps)
            // Inverting makes dark bg → light, light text → dark, then enhancement cleans it up
            try
            {
                var inverted = InvertColors(input);
                var invertedEnhanced = EnhanceForOcr(inverted);
                inverted.Dispose();
                variants.Add((invertedEnhanced, "Inverted+Enhanced"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"Inverted variant failed: {ex.Message}");
            }

            // Variant 3: Otsu binarization (global adaptive threshold)
            // Best for: images with clear bimodal histogram (text vs uniform background)
            try
            {
                variants.Add((BinarizeOtsu(input), "OtsuBinarized"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"Otsu variant failed: {ex.Message}");
            }

            // Variant 4: Bradley-Roth local adaptive binarization
            // Best for: images with VARYING backgrounds (e.g. Task Manager header cells
            // have different shade than data rows). Computes a different threshold for
            // each local neighborhood, so it handles multi-zone contrast perfectly.
            try
            {
                variants.Add((BinarizeBradleyRoth(input), "BradleyRoth"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"BradleyRoth variant failed: {ex.Message}");
            }

            // Variant 5: Simple inversion without highlight neutralization
            if (!isLargeImage)
            try
            {
                variants.Add((InvertColors(input), "InvertedOnly"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"InvertedOnly variant failed: {ex.Message}");
            }

            // Variant 6: Original (no preprocessing)
            try
            {
                var original = SoftwareBitmap.Convert(input,
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                variants.Add((original, "Original"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"Original variant failed: {ex.Message}");
            }

            // Variant 7: Inverted + Bold (morphological text thickening)
            // Thin text strokes (especially '%' character with tiny circles and diagonal)
            // are too few pixels for OCR to recognize. Bolding doubles the stroke width.
            if (!isLargeImage)
            try
            {
                variants.Add((InvertAndBolden(input), "InvertedBold"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"InvertedBold variant failed: {ex.Message}");
            }

            // Variant 8: Grayscale with aggressive contrast stretch
            // Maximum dynamic range — 0th/100th percentile stretch
            if (!isLargeImage)
            try
            {
                variants.Add((GrayscaleMaxContrast(input), "GrayscaleStretch"));
            }
            catch (Exception ex)
            {
                Logger.LogAction("OCR_VARIANT", $"GrayscaleStretch variant failed: {ex.Message}");
            }

            // Fallback: if all preprocessing failed, use a format-converted copy
            if (variants.Count == 0)
            {
                try
                {
                    var copy = SoftwareBitmap.Convert(input,
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    variants.Add((copy, "Original"));
                }
                catch { } // Best-effort: failure is acceptable
            }

            return variants.ToArray();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  OCR RESULT MERGING — union of words from ALL variants
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A merged OCR word with its bounding rectangle and text.
        /// Used to build a union of words detected across multiple preprocessing variants.
        /// </summary>
        public struct MergedOcrWord
        {
            public global::Windows.Foundation.Rect BoundingRect;
            public string Text;
        }

        /// <summary>
        /// Merges OCR results from multiple preprocessing variants into a single word list.
        /// 
        /// Instead of picking the single "best" result (which may miss text that
        /// only one variant can detect), this combines ALL words from ALL results.
        /// Overlapping words (IoM > 0.3) are de-duplicated, keeping the longer text.
        /// 
        /// Returns a sorted word list and reconstructed text with lines grouped by Y-proximity.
        /// </summary>
        public static (List<MergedOcrWord> words, string mergedText) MergeOcrResults(
            List<global::Windows.Media.Ocr.OcrResult> allResults)
        {
            if (allResults == null || allResults.Count == 0)
                return (new List<MergedOcrWord>(), "");

            var merged = new List<MergedOcrWord>();

            foreach (var result in allResults)
            {
                if (result == null) continue;
                foreach (var line in result.Lines)
                {
                    if (line.Words == null || line.Words.Count == 0) continue;

                    foreach (var word in line.Words)
                    {
                        var newWord = new MergedOcrWord
                        {
                            BoundingRect = word.BoundingRect,
                            Text = word.Text
                        };

                        // Check overlap with existing merged words
                        bool isDuplicate = false;
                        for (int i = 0; i < merged.Count; i++)
                        {
                            if (BoundingBoxOverlap(merged[i].BoundingRect, newWord.BoundingRect) > 0.3)
                            {
                                isDuplicate = true;
                                // Keep the longer text (more chars = more info)
                                if (newWord.Text.Length > merged[i].Text.Length)
                                    merged[i] = newWord;
                                break;
                            }
                        }

                        if (!isDuplicate)
                            merged.Add(newWord);
                    }
                }
            }

            // Sort by center-Y (top→bottom) then X (left→right) for stable line detection
            merged.Sort((a, b) =>
            {
                double aCenterY = a.BoundingRect.Y + a.BoundingRect.Height / 2.0;
                double bCenterY = b.BoundingRect.Y + b.BoundingRect.Height / 2.0;
                int yCompare = aCenterY.CompareTo(bCenterY);
                return yCompare != 0 ? yCompare : a.BoundingRect.X.CompareTo(b.BoundingRect.X);
            });

            // Reconstruct lines by grouping words with similar center-Y positions
            // Use median height × 0.6 as the line-break threshold for robustness
            double lineThreshold;
            if (merged.Count > 0)
            {
                var heights = merged.Select(w => w.BoundingRect.Height).OrderBy(h => h).ToList();
                double medianH = heights[heights.Count / 2];
                lineThreshold = medianH * 0.6;
            }
            else
            {
                lineThreshold = 20;
            }

            var lines = new List<string>();
            var currentLineWords = new List<MergedOcrWord>();
            double currentLineCenterY = -1000;

            foreach (var word in merged)
            {
                double wordCenterY = word.BoundingRect.Y + word.BoundingRect.Height / 2.0;

                if (Math.Abs(wordCenterY - currentLineCenterY) > lineThreshold)
                {
                    // Flush the current line — sort words left-to-right by X before joining
                    if (currentLineWords.Count > 0)
                    {
                        currentLineWords.Sort((a, b) => a.BoundingRect.X.CompareTo(b.BoundingRect.X));
                        lines.Add(string.Join(" ", currentLineWords.Select(w => w.Text)));
                    }
                    currentLineWords.Clear();
                    currentLineCenterY = wordCenterY;
                }
                currentLineWords.Add(word);
            }
            if (currentLineWords.Count > 0)
            {
                currentLineWords.Sort((a, b) => a.BoundingRect.X.CompareTo(b.BoundingRect.X));
                lines.Add(string.Join(" ", currentLineWords.Select(w => w.Text)));
            }

            string mergedText = string.Join("\n", lines);
            Logger.LogAction("OCR_MERGE", $"Merged {merged.Count} words into {lines.Count} lines from {allResults.Count} variants");

            return (merged, mergedText);
        }

        /// <summary>
        /// Intersection-over-Minimum-Area overlap ratio (0.0 to 1.0).
        /// </summary>
        private static double BoundingBoxOverlap(global::Windows.Foundation.Rect a, global::Windows.Foundation.Rect b)
        {
            double x1 = Math.Max(a.X, b.X);
            double y1 = Math.Max(a.Y, b.Y);
            double x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            double y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            if (x2 <= x1 || y2 <= y1) return 0.0;

            double intersectArea = (x2 - x1) * (y2 - y1);
            double minArea = Math.Min(a.Width * a.Height, b.Width * b.Height);
            return minArea > 0 ? intersectArea / minArea : 0.0;
        }

        /// <summary>
        /// Smart single-variant enhancement for background/auto OCR where speed matters.
        /// Detects dark vs light images and applies the appropriate preprocessing.
        /// </summary>
        public static unsafe SoftwareBitmap SmartEnhance(SoftwareBitmap input)
        {
            float avgLum = AnalyzeAverageLuminance(input);
            Logger.LogAction("OCR_SMART", $"Average luminance: {avgLum:F0} → {(avgLum < 110 ? "dark theme" : "light theme")} detected");

            if (avgLum < 110) // Dark theme image
            {
                // Invert first → becomes light background with dark text → then enhance
                var inverted = InvertColors(input);
                var enhanced = EnhanceForOcr(inverted);
                inverted.Dispose();
                return enhanced;
            }
            else
            {
                // Light theme: standard enhancement
                return EnhanceForOcr(input);
            }
        }

        /// <summary>
        /// Enhances a SoftwareBitmap for OCR: neutralizes colored highlights + stretches contrast.
        /// Best for light-background images with colored row highlights.
        /// </summary>
        public static unsafe SoftwareBitmap EnhanceForOcr(SoftwareBitmap input)
        {
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
                            data[idx + 0] = 255;
                            data[idx + 1] = 255;
                            data[idx + 2] = 255;
                        }
                    }
                }

                // ═══ PASS 2: Histogram contrast stretch ═══
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

                int range = Math.Max(pHigh - pLow, 30);

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

            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }

        /// <summary>
        /// Inverts colors then morphologically BOLDENS (thickens) text strokes.
        /// 
        /// After inversion, dark background becomes light and white text becomes dark.
        /// Then a 3x3 minimum filter (applied twice) expands dark pixels outward,
        /// effectively doubling the stroke width of all text. This makes thin characters
        /// like '%' (with tiny circles and diagonal) much more detectable by OCR.
        ///
        /// The minimum filter works because text is dark (0) on light (255) background:
        /// taking the minimum of a 3x3 neighborhood spreads dark pixels by 1px in all directions.
        /// </summary>
        public static unsafe SoftwareBitmap InvertAndBolden(SoftwareBitmap input)
        {
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

                // Step 1: Convert to inverted grayscale in-place
                // White text on dark bg → dark text on light bg
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int gray = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        byte invGray = (byte)(255 - Math.Clamp(gray, 0, 255));
                        data[idx + 0] = invGray;
                        data[idx + 1] = invGray;
                        data[idx + 2] = invGray;
                        data[idx + 3] = 255;
                    }
                }

                // Step 2: Morphological dilation of dark text (3x3 min filter)
                // Applied TWICE for 2px expansion in each direction
                for (int pass = 0; pass < 2; pass++)
                {
                    // Read current grayscale values into array
                    byte[] current = new byte[width * height];
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = layout.StartIndex + layout.Stride * y;
                        for (int x = 0; x < width; x++)
                            current[y * width + x] = data[rowOffset + 4 * x]; // B channel = gray
                    }

                    // Apply 3x3 minimum filter
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = layout.StartIndex + layout.Stride * y;
                        for (int x = 0; x < width; x++)
                        {
                            byte minVal = 255;
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int ny = y + dy;
                                if (ny < 0 || ny >= height) continue;
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx;
                                    if (nx < 0 || nx >= width) continue;
                                    byte v = current[ny * width + nx];
                                    if (v < minVal) minVal = v;
                                }
                            }
                            int idx = rowOffset + 4 * x;
                            data[idx + 0] = minVal;
                            data[idx + 1] = minVal;
                            data[idx + 2] = minVal;
                        }
                    }
                }
            }

            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }

        /// <summary>
        /// Converts to grayscale with aggressive full-range contrast stretch.
        /// Uses 0th/100th percentile (actual min/max) instead of 2nd/98th,
        /// giving maximum dynamic range. Also inverts if dark-themed.
        /// </summary>
        public static unsafe SoftwareBitmap GrayscaleMaxContrast(SoftwareBitmap input)
        {
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

                // Pass 1: Find actual min/max luminance
                int minLum = 255, maxLum = 0;
                long totalLum = 0;
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int lum = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        if (lum < minLum) minLum = lum;
                        if (lum > maxLum) maxLum = lum;
                        totalLum += lum;
                    }
                }

                int range = Math.Max(maxLum - minLum, 1);
                bool isDark = (totalLum / (width * height)) < 110;

                // Pass 2: Stretch to full 0-255 range, convert to grayscale, auto-invert if dark
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int lum = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        int stretched = (int)((lum - minLum) * 255.0 / range);
                        byte val = (byte)Math.Clamp(stretched, 0, 255);
                        if (isDark) val = (byte)(255 - val); // Invert for dark themes
                        data[idx + 0] = val;
                        data[idx + 1] = val;
                        data[idx + 2] = val;
                        data[idx + 3] = 255;
                    }
                }
            }

            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }

        /// <summary>
        /// Inverts all pixel colors (R,G,B → 255-R, 255-G, 255-B).
        /// </summary>
        public static unsafe SoftwareBitmap InvertColors(SoftwareBitmap input)
        {
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

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        data[idx + 0] = (byte)(255 - data[idx + 0]); // B
                        data[idx + 1] = (byte)(255 - data[idx + 1]); // G
                        data[idx + 2] = (byte)(255 - data[idx + 2]); // R
                        // Alpha stays unchanged
                    }
                }
            }

            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }

        /// <summary>
        /// Otsu's adaptive binarization — automatically finds the optimal threshold
        /// to separate text from background. Works on both dark and light images.
        /// After binarization, ensures the result is dark-text-on-light-background
        /// (inverts if necessary) since OCR engines prefer this orientation.
        /// </summary>
        public static unsafe SoftwareBitmap BinarizeOtsu(SoftwareBitmap input)
        {
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

                // Step 1: Build grayscale histogram
                int[] histogram = new int[256];
                int totalPixels = width * height;

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int gray = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        histogram[Math.Clamp(gray, 0, 255)]++;
                    }
                }

                // Step 2: Find Otsu's optimal threshold
                // Maximizes between-class variance: σ²_B = w0 * w1 * (μ0 - μ1)²
                int bestThreshold = 128;
                double bestVariance = 0;

                double totalSum = 0;
                for (int i = 0; i < 256; i++) totalSum += i * histogram[i];

                double sumBg = 0;
                int weightBg = 0;

                for (int t = 0; t < 256; t++)
                {
                    weightBg += histogram[t];
                    if (weightBg == 0) continue;

                    int weightFg = totalPixels - weightBg;
                    if (weightFg == 0) break;

                    sumBg += t * histogram[t];
                    double meanBg = sumBg / weightBg;
                    double meanFg = (totalSum - sumBg) / weightFg;

                    double variance = (double)weightBg * weightFg * (meanBg - meanFg) * (meanBg - meanFg);

                    if (variance > bestVariance)
                    {
                        bestVariance = variance;
                        bestThreshold = t;
                    }
                }

                Logger.LogAction("OCR_BINARIZE", $"Otsu threshold: {bestThreshold}");

                // Step 3: Binarize and count black/white pixels
                int blackCount = 0;
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int gray = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);

                        byte val = (byte)(gray < bestThreshold ? 0 : 255);
                        data[idx + 0] = val;
                        data[idx + 1] = val;
                        data[idx + 2] = val;
                        data[idx + 3] = 255;

                        if (val == 0) blackCount++;
                    }
                }

                // Step 4: Ensure dark-on-light orientation
                // If majority of pixels are black, the image was dark-themed.
                // Invert so OCR sees dark text on light background.
                if (blackCount > totalPixels / 2)
                {
                    Logger.LogAction("OCR_BINARIZE", "Majority black — inverting for dark-on-light text");
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = layout.StartIndex + layout.Stride * y;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + 4 * x;
                            data[idx + 0] = (byte)(255 - data[idx + 0]);
                            data[idx + 1] = (byte)(255 - data[idx + 1]);
                            data[idx + 2] = (byte)(255 - data[idx + 2]);
                        }
                    }
                }
            }

            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }

        /// <summary>
        /// Bradley-Roth local adaptive binarization using integral images.
        /// Unlike global Otsu, this computes a DIFFERENT threshold for each pixel
        /// based on its local neighborhood. This is critical for images with
        /// varying backgrounds — e.g. Task Manager where header cells (61%, 0%)
        /// have a different background shade than the data rows below.
        ///
        /// Algorithm: For each pixel, compute the mean of an S×S window around it.
        /// If the pixel is more than t% below the local mean, it's foreground (black).
        /// Otherwise it's background (white).
        ///
        /// After binarization, auto-inverts if the image is majority-black (dark theme).
        /// </summary>
        public static unsafe SoftwareBitmap BinarizeBradleyRoth(SoftwareBitmap input)
        {
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

                // Step 1: Compute grayscale values and integral image
                int[,] gray = new int[width, height];
                long[,] integral = new long[width, height];

                for (int y = 0; y < height; y++)
                {
                    long rowSum = 0;
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;
                        int val = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        gray[x, y] = val;
                        rowSum += val;
                        integral[x, y] = (y == 0) ? rowSum : (integral[x, y - 1] + rowSum);
                    }
                }

                // Step 2: Bradley-Roth adaptive thresholding
                int S = width / 8;  // Window size = 1/8th of image width
                if (S < 4) S = 4;
                double t = 0.15;    // 15% below local mean = foreground

                int blackCount = 0;
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowOffset + 4 * x;

                        // Local window bounds (clamped to image edges)
                        int x1 = Math.Max(0, x - S / 2);
                        int x2 = Math.Min(width - 1, x + S / 2);
                        int y1 = Math.Max(0, y - S / 2);
                        int y2 = Math.Min(height - 1, y + S / 2);

                        int count = (x2 - x1 + 1) * (y2 - y1 + 1);

                        // Sum from integral image
                        long sum = integral[x2, y2];
                        if (x1 > 0) sum -= integral[x1 - 1, y2];
                        if (y1 > 0) sum -= integral[x2, y1 - 1];
                        if (x1 > 0 && y1 > 0) sum += integral[x1 - 1, y1 - 1];

                        double localAvg = (double)sum / count;

                        // Binarize: if pixel is significantly below local average → foreground
                        byte resultVal = (gray[x, y] < localAvg * (1.0 - t)) ? (byte)0 : (byte)255;

                        data[idx + 0] = resultVal;
                        data[idx + 1] = resultVal;
                        data[idx + 2] = resultVal;
                        data[idx + 3] = 255;

                        if (resultVal == 0) blackCount++;
                    }
                }

                // Step 3: Auto-invert for dark-themed images
                int totalPixels = width * height;
                if (blackCount > totalPixels / 2)
                {
                    Logger.LogAction("OCR_BRADLEY", "Majority black — inverting for dark-on-light text");
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = layout.StartIndex + layout.Stride * y;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + 4 * x;
                            data[idx + 0] = (byte)(255 - data[idx + 0]);
                            data[idx + 1] = (byte)(255 - data[idx + 1]);
                            data[idx + 2] = (byte)(255 - data[idx + 2]);
                        }
                    }
                }
            }

            var result = SoftwareBitmap.Convert(working, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            working.Dispose();
            return result;
        }

        /// <summary>
        /// Analyzes the average luminance of an image to detect dark vs light themes.
        /// Returns 0-255 where &lt; 110 typically indicates a dark-themed screenshot.
        /// </summary>
        private static unsafe float AnalyzeAverageLuminance(SoftwareBitmap input)
        {
            SoftwareBitmap working;
            if (input.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                working = SoftwareBitmap.Convert(input, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            }
            else
            {
                working = SoftwareBitmap.Copy(input);
            }

            long totalLum = 0;
            int width = working.PixelWidth;
            int height = working.PixelHeight;

            // Sample every 4th pixel for speed (16x faster than full scan)
            int sampleCount = 0;

            using (var buffer = working.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                byteAccess.GetBuffer(out byte* data, out uint capacity);
                var layout = buffer.GetPlaneDescription(0);

                for (int y = 0; y < height; y += 4)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x += 4)
                    {
                        int idx = rowOffset + 4 * x;
                        int lum = (int)(0.299 * data[idx + 2] + 0.587 * data[idx + 1] + 0.114 * data[idx + 0]);
                        totalLum += lum;
                        sampleCount++;
                    }
                }
            }

            working.Dispose();
            return sampleCount > 0 ? (float)totalLum / sampleCount : 128f;
        }
    }
}
