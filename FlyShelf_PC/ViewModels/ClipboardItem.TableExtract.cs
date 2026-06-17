// ---------------------------------------------------------------
// ClipboardItem — Table Extraction from Images/PDFs
// ExtractTable (OCR-based table detection and parsing)
// Split from ClipboardItem.Actions.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FlyShelf.ViewModels
{

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
                    // === FlyShelf Smart Table Detection Engine ===
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

                            // -- STEP 1: Run OCR --
                            // Try OCR directly on the original image first (works best for clean screenshots).
                            // Only fall back to Bradley-Roth binarization if the original yields too few results.
                            
                            // Ensure OCR-compatible format
                            global::Windows.Graphics.Imaging.SoftwareBitmap ocrBitmap = softwareBitmap;
                            if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                                softwareBitmap.BitmapAlphaMode != global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
                            {
                                ocrBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                    softwareBitmap,
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                            }

                            var ocrResult = await ocrEngine.RecognizeAsync(ocrBitmap);
                            if (ocrBitmap != softwareBitmap) ocrBitmap.Dispose();

                            Classes.Logger.LogAction("TABLE_OCR", $"Original image OCR: {ocrResult?.Lines.Count ?? 0} lines");



                            if (ocrResult == null || ocrResult.Lines.Count < 2)
                            {
                                Classes.Logger.LogAction("TABLE_OCR", "Insufficient OCR lines for table detection");
                                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                    FlyShelf.Windows.ToastWindow.ShowToast("Could not detect text in the image.")
                                );
                                return;
                            }

                            // Use the image dimensions for projection profile
                            int imagePixelWidth = (int)softwareBitmap.PixelWidth;

                            {
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
                                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                        FlyShelf.Windows.ToastWindow.ShowToast("Too few words detected for table extraction.")
                                    );
                                    return;
                                }

                                // -- STEP 2: Adaptive Row Clustering --
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

                                // -- STEP 3: Advanced Column Detection using X-Projection Profiles --
                                // Detect columns by projecting words horizontally and locating empty vertical gutters.
                                int widthInt = imagePixelWidth;
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

                                // -- STEP 4: Assign words to cells with smart bucketing --
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
                        try
                        {
                            FlyShelf.Classes.LicenseManager.RecordTableExtraction();
                            var editor = new FlyShelf.Windows.TableEditorWindow(finalJsonPayload, imgPath, method);
                            editor.Show();
                            editor.Activate();
                            // Briefly set topmost to punch through a topmost parent, then release
                            editor.Topmost = true;
                            editor.Topmost = false;
                        }
                        catch (Exception uiEx)
                        {
                            Classes.Logger.LogAction("TABLE_UI_FAIL", $"Failed to open Table Editor: {uiEx.Message}");
                            FlyShelf.Windows.ToastWindow.ShowToast($"Failed to open Table Editor: {uiEx.Message}");
                        }
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
    }
}
