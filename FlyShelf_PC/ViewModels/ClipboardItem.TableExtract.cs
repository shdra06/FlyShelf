// ---------------------------------------------------------------
// ClipboardItem — Table Extraction from Images/PDFs
// ExtractTable (OCR-based table detection and parsing)
// Split from ClipboardItem.Actions.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FlyShelf.ViewModels
{
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    public partial class ClipboardItem
    {
        public async void ExtractTable()
        {
            try
            {
                if (!FlyShelf.Classes.LicenseManager.CanExtractTable())
                {
                    FlyShelf.Classes.UpgradePrompt.ShowTableExtractLimit();
                    return;
                }

                if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;

                FlyShelf.Windows.ToastWindow.ShowToast("Extracting Table from Image... ⏳");

                string finalJsonPayload = string.Empty;
                string extractionMethod = string.Empty;

                await System.Threading.Tasks.Task.Run(async () =>
                {
                    // ═══ FlyShelf Smart Table Detection Engine ═══
                    try
                    {
                        using (var stream = File.OpenRead(FilePath))
                        {
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                                new global::Windows.Globalization.Language("en-US"));

                            if (ocrEngine == null)
                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();

                            if (ocrEngine == null)
                            {
                                Classes.Logger.LogAction("TABLE_OCR", "No OCR engine available - install English language pack");
                                return;
                            }

                            // ── STEP 1: Preprocess Image using Bradley-Roth Local Adaptive Binarization ──
                            // This converts any low-contrast gray text on dark background into crisp black text on white background
                            using (var preprocessed = PreprocessImage(softwareBitmap))
                            {
                                var ocrResult = await ocrEngine.RecognizeAsync(preprocessed);

                                if (ocrResult == null || ocrResult.Lines.Count < 2)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Insufficient OCR lines for table detection");
                                    return;
                                }

                                // Collect all words with their bounding boxes
                                var allWords = new List<(string Text, double X, double Y, double W, double H, double Right, double Bottom, double CenterX, double CenterY)>();
                                foreach (var line in ocrResult.Lines)
                                {
                                    foreach (var word in line.Words)
                                    {
                                        var rect = word.BoundingRect;
                                        allWords.Add((
                                            word.Text,
                                            rect.X,
                                            rect.Y,
                                            rect.Width,
                                            rect.Height,
                                            rect.X + rect.Width,
                                            rect.Y + rect.Height,
                                            rect.X + rect.Width / 2.0,
                                            rect.Y + rect.Height / 2.0
                                        ));
                                    }
                                }

                                Classes.Logger.LogAction("TABLE_OCR", $"Found {allWords.Count} words in {ocrResult.Lines.Count} lines");

                                if (allWords.Count < 3)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Too few words for table structure");
                                    return;
                                }

                                // ── STEP 2: Adaptive Row Clustering ──
                                // Sort by Y coordinate and use adaptive threshold based on
                                // statistical analysis of vertical gaps between words
                                var sorted = allWords.OrderBy(w => w.Y).ToList();

                                // Calculate robust median height using middle 80% of values
                                var heights = sorted.Select(w => w.H).OrderBy(h => h).ToList();
                                int trimCount = Math.Max(1, heights.Count / 10);
                                var trimmedHeights = heights.Skip(trimCount).Take(heights.Count - 2 * trimCount).ToList();
                                double medianHeight = trimmedHeights.Count > 0
                                    ? trimmedHeights[trimmedHeights.Count / 2]
                                    : heights[heights.Count / 2];

                                // Adaptive threshold: use 60% of median height for tight tables,
                                // but also consider the actual gap distribution
                                var yGaps = new List<double>();
                                for (int i = 1; i < sorted.Count; i++)
                                {
                                    double gap = sorted[i].Y - sorted[i - 1].Y;
                                    if (gap > 0) yGaps.Add(gap);
                                }

                                double rowThreshold;
                                if (yGaps.Count > 2)
                                {
                                    yGaps.Sort();
                                    // Find the natural break point between intra-row and inter-row gaps
                                    // using Otsu's method (bimodal threshold)
                                    double bestThreshold = medianHeight * 0.6;
                                    double bestVariance = double.MinValue;
                                    var distinctGaps = yGaps.Distinct().OrderBy(g => g).ToList();

                                    foreach (var t in distinctGaps)
                                    {
                                        var below = yGaps.Where(g => g <= t).ToList();
                                        var above = yGaps.Where(g => g > t).ToList();
                                        if (below.Count == 0 || above.Count == 0) continue;

                                        double w0 = (double)below.Count / yGaps.Count;
                                        double w1 = (double)above.Count / yGaps.Count;
                                        double m0 = below.Average();
                                        double m1 = above.Average();
                                        double variance = w0 * w1 * (m0 - m1) * (m0 - m1);

                                        if (variance > bestVariance)
                                        {
                                            bestVariance = variance;
                                            bestThreshold = t;
                                        }
                                    }
                                    rowThreshold = bestThreshold;
                                }
                                else
                                {
                                    rowThreshold = medianHeight * 0.6;
                                }

                                // Cluster words into rows
                                var rows = new List<List<(string Text, double X, double W, double Right, double CenterX)>>();
                                var currentRow = new List<(string Text, double X, double W, double Right, double CenterX)>();
                                double lastY = sorted[0].Y;

                                foreach (var word in sorted)
                                {
                                    if (Math.Abs(word.Y - lastY) > rowThreshold && currentRow.Count > 0)
                                    {
                                        rows.Add(currentRow.OrderBy(w => w.X).ToList());
                                        currentRow = new List<(string Text, double X, double W, double Right, double CenterX)>();
                                    }
                                    currentRow.Add((word.Text, word.X, word.W, word.Right, word.CenterX));
                                    lastY = word.Y;
                                }
                                if (currentRow.Count > 0)
                                    rows.Add(currentRow.OrderBy(w => w.X).ToList());

                                if (rows.Count < 2)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Only 1 row detected - not a table");
                                    return;
                                }

                                // ── STEP 3: Advanced Column Detection using X-Projection Profiles ──
                                // Detect columns by projecting words horizontally and locating empty vertical gutters.
                                int widthInt = preprocessed.PixelWidth;
                                int[] xProfile = new int[widthInt];

                                // Exclude spanned/merged headers or text blocks (width > 30% of total image width)
                                foreach (var word in allWords)
                                {
                                    if (word.W > widthInt * 0.3)
                                        continue;

                                    int left = Math.Clamp((int)word.X, 0, widthInt - 1);
                                    int right = Math.Clamp((int)word.Right, 0, widthInt - 1);

                                    for (int i = left; i <= right; i++)
                                    {
                                        xProfile[i]++;
                                    }
                                }

                                // Locate continuous empty vertical gutters (valleys where xProfile is 0)
                                var gutters = new List<(int Start, int End)>();
                                bool inGutter = false;
                                int gutterStart = 0;

                                for (int i = 0; i < widthInt; i++)
                                {
                                    bool isGutter = xProfile[i] == 0;
                                    if (isGutter)
                                    {
                                        if (!inGutter)
                                        {
                                            gutterStart = i;
                                            inGutter = true;
                                        }
                                    }
                                    else
                                    {
                                        if (inGutter)
                                        {
                                            gutters.Add((gutterStart, i - 1));
                                            inGutter = false;
                                        }
                                    }
                                }
                                if (inGutter)
                                {
                                    gutters.Add((gutterStart, widthInt - 1));
                                }

                                // Filter and extract column separators
                                double minTextX = allWords.Min(w => w.X);
                                double maxTextX = allWords.Max(w => w.Right);

                                var separators = new List<double>();
                                foreach (var gutter in gutters)
                                {
                                    double center = (gutter.Start + gutter.End) / 2.0;
                                    int gutterWidth = gutter.End - gutter.Start + 1;

                                    // Ignore gutters that are too narrow (less than 5 pixels)
                                    if (gutterWidth < 5)
                                        continue;

                                    // Separator must be strictly between active text boundaries
                                    if (center > minTextX && center < maxTextX)
                                    {
                                        separators.Add(center);
                                    }
                                }
                                separators = separators.OrderBy(s => s).ToList();

                                int numCols = separators.Count + 1;

                                // Fallback: if projection profile found only 1 column, segment horizontally based on max words
                                if (numCols < 2 && rows.Count >= 2)
                                {
                                    int maxRowWords = rows.Max(r => r.Count);
                                    if (maxRowWords >= 2)
                                    {
                                        double span = maxTextX - minTextX;
                                        double colWidth = span / maxRowWords;
                                        separators.Clear();
                                        for (int i = 1; i < maxRowWords; i++)
                                        {
                                            separators.Add(minTextX + i * colWidth);
                                        }
                                        numCols = separators.Count + 1;
                                    }
                                }

                                if (numCols < 2)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Could not detect column structure");
                                    return;
                                }

                                // ── STEP 4: Assign words to cells with smart bucketing ──
                                var jsonDict = new Dictionary<string, object>();

                                for (int ri = 0; ri < rows.Count; ri++)
                                {
                                    var buckets = new string[numCols];
                                    for (int c = 0; c < numCols; c++) buckets[c] = "";

                                    foreach (var word in rows[ri])
                                    {
                                        // Use word center for more accurate column assignment
                                        double wc = word.CenterX;
                                        int col = 0;
                                        for (int si = 0; si < separators.Count; si++)
                                        {
                                            if (wc > separators[si]) col = si + 1;
                                            else break;
                                        }
                                        if (col >= numCols) col = numCols - 1;

                                        buckets[col] += (buckets[col].Length > 0 ? " " : "") + word.Text;
                                    }

                                    for (int ci = 0; ci < numCols; ci++)
                                        jsonDict[$"({ri},{ci})"] = new { text = buckets[ci].Trim(), conf = 0.92 };
                                }

                                finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);
                                extractionMethod = "FlyShelf";

                                Classes.Logger.LogAction("TABLE_EXTRACT", $"Smart OCR: {rows.Count}x{numCols} table ({separators.Count} separators)");
                            }
                        }
                    }
                    catch (Exception ocrEx)
                    {
                        Classes.Logger.LogAction("TABLE_OCR_FAIL", ocrEx.Message);
                    }
                });

                if (!string.IsNullOrWhiteSpace(finalJsonPayload) && finalJsonPayload.StartsWith("{"))
                {
                    string imgPath = FilePath;
                    string method = extractionMethod;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        FlyShelf.Classes.LicenseManager.RecordTableExtraction();
                        var editor = new FlyShelf.Windows.TableEditorWindow(finalJsonPayload, imgPath, method);
                        editor.Show();
                    });
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No table structure detected in this image.");
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("TABLE_EXTRACT_FAIL", ex.Message);
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast($"Table Extraction Failed: {ex.Message}")
                );
            }
        }

        /// <summary>
        /// Preprocesses a SoftwareBitmap by applying Bradley-Roth Local Adaptive Thresholding.
        /// This creates a binarized high-contrast version of the image (black text on white background).
        /// </summary>
        private static unsafe global::Windows.Graphics.Imaging.SoftwareBitmap PreprocessImage(global::Windows.Graphics.Imaging.SoftwareBitmap input)
        {
            global::Windows.Graphics.Imaging.SoftwareBitmap bgra8Bitmap;
            if (input.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                input.BitmapAlphaMode == global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
            {
                bgra8Bitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(input, 
                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, 
                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight);
            }
            else
            {
                bgra8Bitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Copy(input);
            }

            int width = bgra8Bitmap.PixelWidth;
            int height = bgra8Bitmap.PixelHeight;

            int[,] gray = new int[width, height];
            long[,] integral = new long[width, height];

            using (global::Windows.Graphics.Imaging.BitmapBuffer buffer = bgra8Bitmap.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.ReadWrite))
            using (global::Windows.Foundation.IMemoryBufferReference reference = buffer.CreateReference())
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* dataInBytes, out uint capacity);
                global::Windows.Graphics.Imaging.BitmapPlaneDescription bufferLayout = buffer.GetPlaneDescription(0);

                // 1. Populate grayscale and integral image
                for (int y = 0; y < height; y++)
                {
                    long rowSum = 0;
                    int rowOffset = bufferLayout.StartIndex + bufferLayout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int pixelIndex = rowOffset + 4 * x;
                        byte b = dataInBytes[pixelIndex + 0];
                        byte g = dataInBytes[pixelIndex + 1];
                        byte r = dataInBytes[pixelIndex + 2];

                        int val = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                        gray[x, y] = val;

                        rowSum += val;
                        if (y == 0)
                        {
                            integral[x, y] = rowSum;
                        }
                        else
                        {
                            integral[x, y] = integral[x, y - 1] + rowSum;
                        }
                    }
                }

                // 2. Perform Bradley-Roth Adaptive Thresholding
                int S = width / 8;
                if (S < 4) S = 4;
                double t = 0.15; // 15% threshold difference

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = bufferLayout.StartIndex + bufferLayout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int pixelIndex = rowOffset + 4 * x;

                        int x1 = Math.Max(0, x - S / 2);
                        int x2 = Math.Min(width - 1, x + S / 2);
                        int y1 = Math.Max(0, y - S / 2);
                        int y2 = Math.Min(height - 1, y + S / 2);

                        int count = (x2 - x1 + 1) * (y2 - y1 + 1);

                        long sum = integral[x2, y2];
                        if (x1 > 0) sum -= integral[x1 - 1, y2];
                        if (y1 > 0) sum -= integral[x2, y1 - 1];
                        if (x1 > 0 && y1 > 0) sum += integral[x1 - 1, y1 - 1];

                        double localAvg = (double)sum / count;

                        // Binarize
                        byte resultValue = (gray[x, y] < localAvg * (1.0 - t)) ? (byte)0 : (byte)255;

                        dataInBytes[pixelIndex + 0] = resultValue; // B
                        dataInBytes[pixelIndex + 1] = resultValue; // G
                        dataInBytes[pixelIndex + 2] = resultValue; // R
                        dataInBytes[pixelIndex + 3] = 255;         // A
                    }
                }
            }

            return bgra8Bitmap;
        }
    }
}
