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

                var method = FlyShelf.Classes.SettingsManager.Current.DefaultAiMethod ?? "auto";

                // "local" → always use local engine, skip popup
                if (method == "local")
                {
                    ExtractTableLocal();
                    return;
                }

                // "api" → always use API (if key exists)
                if (method == "api" && FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey)
                {
                    await ExtractTableWithAI();
                    return;
                }

                // "auto" (default) → API if key exists, else popup
                if (FlyShelf.Classes.AiProviderService.Instance.HasCloudApiKey)
                {
                    await ExtractTableWithAI();
                    return;
                }

                // No API key → show choice popup
                bool? useAI = await ShowAiOrLocalChoiceAsync("Table Extraction");
                if (useAI == null) return; // Cancelled
                if (useAI == true)
                {
                    await ExtractTableWithAI();
                    return;
                }

                // Local fallback — run existing local extraction
                ExtractTableLocal();
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("TABLE", $"ExtractTable error: {ex.Message}");
            }
        }

        private async void ExtractTableLocal()
        {
            try
            {
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
                                var dispatcherA = System.Windows.Application.Current?.Dispatcher;
                                if (dispatcherA != null)
                                    await dispatcherA.InvokeAsync(() =>
                                        FlyShelf.Windows.ToastWindow.ShowToast("Could not detect text in the image.")
                                    );
                                return;
                            }

                            // Use the image dimensions for projection profile
                            int imagePixelWidth = (int)softwareBitmap.PixelWidth;

                            {
                                // Collect all words with their bounding boxes
                                var allWords = new List<(string Text, double X, double Y, double W, double H, double Right, double Bottom, double CenterX, double CenterY, int LineIndex)>();
                                int lineIdx = 0;
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
                                            rect.Y + rect.Height / 2.0,
                                            lineIdx
                                        ));
                                    }
                                    lineIdx++;
                                }

                                Classes.Logger.LogAction("TABLE_OCR", $"Found {allWords.Count} words in {ocrResult.Lines.Count} lines");

                                if (allWords.Count < 3)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Too few words for table structure");
                                    var dispatcherB = System.Windows.Application.Current?.Dispatcher;
                                    if (dispatcherB != null)
                                        await dispatcherB.InvokeAsync(() =>
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
                                var rows = new List<List<(string Text, double X, double W, double Right, double CenterX, int LineIndex)>>();
                                var currentRow = new List<(string Text, double X, double W, double Right, double CenterX, int LineIndex)>();
                                double lastY = sorted[0].Y;

                                foreach (var word in sorted)
                                {
                                    if (Math.Abs(word.Y - lastY) > rowThreshold && currentRow.Count > 0)
                                    {
                                        rows.Add(currentRow.OrderBy(w => w.X).ToList());
                                        currentRow = new List<(string Text, double X, double W, double Right, double CenterX, int LineIndex)>();
                                    }
                                    currentRow.Add((word.Text, word.X, word.W, word.Right, word.CenterX, word.LineIndex));
                                    lastY = word.Y;
                                }
                                if (currentRow.Count > 0)
                                    rows.Add(currentRow.OrderBy(w => w.X).ToList());

                                if (rows.Count < 2)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Only 1 row detected - not a table");
                                    return;
                                }

                                // -- STEP 3: Smart Column Detection via Gap Analysis --
                                // Instead of projection profiles that confuse inter-word spaces with
                                // column gutters, we merge same-line words into text segments, then
                                // statistically classify inter-segment gaps and use multi-row voting.

                                // 3a: Build text segments per row by merging same-line consecutive words
                                var allGaps = new List<(double Center, double Width, int RowIndex)>();

                                for (int ri = 0; ri < rows.Count; ri++)
                                {
                                    var row = rows[ri];
                                    if (row.Count < 2) continue;

                                    // Merge consecutive words from same OCR line with small gaps
                                    var segments = new List<(double Left, double Right)>();
                                    double segLeft = row[0].X;
                                    double segRight = row[0].Right;
                                    int segLine = row[0].LineIndex;

                                    for (int wi = 1; wi < row.Count; wi++)
                                    {
                                        double gapToNext = row[wi].X - segRight;
                                        bool sameLine = row[wi].LineIndex == segLine;

                                        // Merge if same OCR line and gap is small (< 1.5x median char height)
                                        if (sameLine && gapToNext < medianHeight * 1.5 && gapToNext >= 0)
                                        {
                                            segRight = Math.Max(segRight, row[wi].Right);
                                        }
                                        else
                                        {
                                            segments.Add((segLeft, segRight));
                                            segLeft = row[wi].X;
                                            segRight = row[wi].Right;
                                            segLine = row[wi].LineIndex;
                                        }
                                    }
                                    segments.Add((segLeft, segRight));

                                    // Compute inter-segment gaps
                                    for (int si = 1; si < segments.Count; si++)
                                    {
                                        double gapWidth = segments[si].Left - segments[si - 1].Right;
                                        if (gapWidth > 0)
                                        {
                                            double gapCenter = (segments[si - 1].Right + segments[si].Left) / 2.0;
                                            allGaps.Add((gapCenter, gapWidth, ri));
                                        }
                                    }
                                }

                                // 3b: Statistical gap classification using Otsu's method on gap widths
                                var separators = new List<double>();

                                if (allGaps.Count > 0)
                                {
                                    var sortedGapWidths = allGaps.Select(g => g.Width).OrderBy(w => w).ToList();
                                    double medianGap = sortedGapWidths[sortedGapWidths.Count / 2];

                                    // Find bimodal threshold separating word-gaps from column-gaps
                                    double bestGapThreshold = medianGap * 2.0;
                                    double bestGapVariance = double.MinValue;
                                    var distinctWidths = sortedGapWidths.Distinct().OrderBy(w => w).ToList();

                                    foreach (var t in distinctWidths)
                                    {
                                        var below = sortedGapWidths.Where(g => g <= t).ToList();
                                        var above = sortedGapWidths.Where(g => g > t).ToList();
                                        if (below.Count == 0 || above.Count == 0) continue;

                                        double w0 = (double)below.Count / sortedGapWidths.Count;
                                        double w1 = (double)above.Count / sortedGapWidths.Count;
                                        double m0 = below.Average();
                                        double m1 = above.Average();
                                        double interClassVariance = w0 * w1 * (m0 - m1) * (m0 - m1);

                                        if (interClassVariance > bestGapVariance)
                                        {
                                            bestGapVariance = interClassVariance;
                                            bestGapThreshold = t;
                                        }
                                    }

                                    // Enforce minimum: column gaps must be notably wider than word spacing
                                    double minThreshold = Math.Max(medianGap * 1.5, medianHeight * 0.5);
                                    bestGapThreshold = Math.Max(bestGapThreshold, minThreshold);

                                    Classes.Logger.LogAction("TABLE_OCR", $"Gap analysis: {allGaps.Count} gaps, median={medianGap:F1}, threshold={bestGapThreshold:F1}");

                                    // 3c: Filter gaps above threshold and cluster by X position
                                    var candidateGaps = allGaps.Where(g => g.Width > bestGapThreshold).ToList();

                                    // Cluster candidate gaps by X position (tolerance = medianHeight)
                                    double clusterTolerance = medianHeight;
                                    var clusters = new List<List<(double Center, double Width, int RowIndex)>>();

                                    foreach (var gap in candidateGaps.OrderBy(g => g.Center))
                                    {
                                        bool added = false;
                                        foreach (var cluster in clusters)
                                        {
                                            double clusterCenter = cluster.Average(g => g.Center);
                                            if (Math.Abs(gap.Center - clusterCenter) <= clusterTolerance)
                                            {
                                                cluster.Add(gap);
                                                added = true;
                                                break;
                                            }
                                        }
                                        if (!added)
                                        {
                                            clusters.Add(new List<(double Center, double Width, int RowIndex)> { gap });
                                        }
                                    }

                                    // 3d: Confirm separators via multi-row consistency voting
                                    // A separator must appear in at least 40% of rows (allows for merged cells)
                                    int minRowCount = Math.Max(2, (int)(rows.Count * 0.4));

                                    foreach (var cluster in clusters)
                                    {
                                        int distinctRows = cluster.Select(g => g.RowIndex).Distinct().Count();
                                        if (distinctRows >= minRowCount)
                                        {
                                            double separatorX = cluster.Average(g => g.Center);
                                            separators.Add(separatorX);
                                        }
                                    }

                                    separators = separators.OrderBy(s => s).ToList();
                                }

                                int numCols = separators.Count + 1;

                                // Fallback: if only 1 column found, use header-row word positions as anchors
                                if (numCols < 2 && rows.Count >= 2)
                                {
                                    var headerRow = rows[0];
                                    if (headerRow.Count >= 2)
                                    {
                                        // Headers are typically short words that align with column boundaries
                                        for (int wi = 1; wi < headerRow.Count; wi++)
                                        {
                                            double gapWidth = headerRow[wi].X - headerRow[wi - 1].Right;
                                            if (gapWidth > medianHeight * 0.5)
                                            {
                                                double sepX = (headerRow[wi - 1].Right + headerRow[wi].X) / 2.0;
                                                separators.Add(sepX);
                                            }
                                        }
                                        separators = separators.OrderBy(s => s).ToList();
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
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
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
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast($"Table Extraction Failed: {ex.Message}")
                );
            }
        }

        private async Task ExtractTableWithAI()
        {
            try
            {
                FlyShelf.Windows.ToastWindow.ShowToast("🧠 AI Table Extraction... ⏳");

                byte[] imageBytes = await Task.Run(() => File.ReadAllBytes(FilePath));
                string ext = Path.GetExtension(FilePath).ToLower();
                string mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/png"
                };

                string result = await FlyShelf.Classes.AiProviderService.Instance.GenerateWithImageAsync(
                    "Extract the table from this image. Return ONLY a valid JSON array of arrays where the first array is headers and subsequent arrays are rows. Example: [[\"Name\",\"Age\"],[\"Alice\",\"30\"]]. If there are multiple tables, extract the largest one. Do not add any commentary or markdown formatting.",
                    imageBytes, mimeType, maxTokens: 8192);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    // Clean up the response — remove markdown code fences if present
                    result = result.Trim();
                    if (result.StartsWith("```"))
                    {
                        int firstNewline = result.IndexOf('\n');
                        if (firstNewline > 0) result = result.Substring(firstNewline + 1);
                        if (result.EndsWith("```")) result = result.Substring(0, result.Length - 3).Trim();
                    }

                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        try
                        {
                            // Try to open in TableEditorWindow
                            var tableWindow = new FlyShelf.Windows.TableEditorWindow(result);
                            tableWindow.Show();
                        }
                        catch
                        {
                            // Fallback: copy to clipboard
                            try { System.Windows.Clipboard.SetText(result); } catch { }
                            FlyShelf.Windows.ToastWindow.ShowToast("✅ AI table data copied to clipboard!");
                        }
                    });
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠ AI returned empty result");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"❌ AI Table Extract failed: {ex.Message}");
                Classes.Logger.LogAction("AI_TABLE", $"Failed: {ex.Message}");
            }
        }
    }
}
