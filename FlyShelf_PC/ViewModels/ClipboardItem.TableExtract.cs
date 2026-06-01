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
                Classes.Logger.LogAction("TABLE_DEBUG", $"ExtractTable called. FilePath: '{FilePath}', IsImagePreview: {IsImagePreview}, CanExtractTable: {FlyShelf.Classes.LicenseManager.CanExtractTable()}, FileExists: {(string.IsNullOrEmpty(FilePath) ? "false" : System.IO.File.Exists(FilePath).ToString())}");

                if (!FlyShelf.Classes.LicenseManager.CanExtractTable())
                {
                    FlyShelf.Classes.UpgradePrompt.ShowTableExtractLimit();
                    return;
                }

                if (!IsImagePreview || string.IsNullOrEmpty(FilePath))
                {
                    Classes.Logger.LogAction("TABLE_DEBUG", $"Returned early due to guards. IsImagePreview: {IsImagePreview}, FilePathIsEmpty: {string.IsNullOrEmpty(FilePath)}");
                    return;
                }

                FlyShelf.Windows.ToastWindow.ShowToast("Extracting Table from Image... ⏳");

                string finalJsonPayload = string.Empty;
                string extractionMethod = string.Empty;

                await System.Threading.Tasks.Task.Run(async () =>
                {
                    Classes.Logger.LogAction("TABLE_DEBUG", "Background processing thread started.");
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

                                // ── STEP 2: Clustered Row and Cell Detection ──
                                // Cluster words into rows using robust vertical overlap clustering
                                var extractedRows = new List<ExtractedRow>();
                                
                                // Sort words by Y center first to process top-to-bottom
                                var wordsList = allWords.Select(w => new ExtractedWord
                                {
                                    Text = w.Text,
                                    X = w.X,
                                    Y = w.Y,
                                    W = w.W,
                                    H = w.H
                                }).OrderBy(w => w.CenterY).ToList();

                                foreach (var word in wordsList)
                                {
                                    // Find if this word overlaps vertically with an existing row
                                    ExtractedRow? targetRow = null;
                                    double maxOverlap = 0;

                                    foreach (var row in extractedRows)
                                    {
                                        double overlap = Math.Max(0, Math.Min(word.Bottom, row.MaxBottom) - Math.Max(word.Y, row.MinY));
                                        double minH = Math.Min(word.H, row.MaxBottom - row.MinY);
                                        
                                        if (minH > 0)
                                        {
                                            double overlapRatio = overlap / minH;
                                            if (overlapRatio >= 0.4) // 40% vertical overlap threshold
                                            {
                                                if (overlap > maxOverlap)
                                                {
                                                    maxOverlap = overlap;
                                                    targetRow = row;
                                                }
                                            }
                                        }
                                    }

                                    if (targetRow != null)
                                    {
                                        targetRow.Words.Add(word);
                                        targetRow.MinY = Math.Min(targetRow.MinY, word.Y);
                                        targetRow.MaxBottom = Math.Max(targetRow.MaxBottom, word.Bottom);
                                    }
                                    else
                                    {
                                        var newRow = new ExtractedRow();
                                        newRow.Words.Add(word);
                                        newRow.MinY = word.Y;
                                        newRow.MaxBottom = word.Bottom;
                                        extractedRows.Add(newRow);
                                    }
                                }

                                // Sort the clustered rows by their vertical CenterY
                                extractedRows = extractedRows.OrderBy(r => r.CenterY).ToList();

                                if (extractedRows.Count < 2)
                                {
                                    Classes.Logger.LogAction("TABLE_OCR", "Only 1 row detected - not a table");
                                    return;
                                }

                                // Calculate robust median height to determine space threshold
                                var sortedHeights = wordsList.Select(w => w.H).OrderBy(h => h).ToList();
                                double globalMedianHeight = sortedHeights.Count > 0 ? sortedHeights[sortedHeights.Count / 2] : 15.0;
                                double maxSpaceWidth = globalMedianHeight * 0.9; // Dynamic space threshold for cell merging

                                foreach (var row in extractedRows)
                                {
                                    var sortedWords = row.Words.OrderBy(w => w.X).ToList();
                                    if (sortedWords.Count == 0) continue;

                                    var currentCellWords = new List<ExtractedWord> { sortedWords[0] };
                                    
                                    for (int i = 1; i < sortedWords.Count; i++)
                                    {
                                        var prevWord = sortedWords[i - 1];
                                        var currWord = sortedWords[i];
                                        
                                        double gap = currWord.X - prevWord.Right;
                                        
                                        if (gap < maxSpaceWidth)
                                        {
                                            currentCellWords.Add(currWord);
                                        }
                                        else
                                        {
                                            row.Cells.Add(CreateCellFromWords(currentCellWords));
                                            currentCellWords = new List<ExtractedWord> { currWord };
                                        }
                                    }
                                    if (currentCellWords.Count > 0)
                                    {
                                        row.Cells.Add(CreateCellFromWords(currentCellWords));
                                    }
                                }

                                // ── STEP 3: Advanced Column Separator Estimation ──
                                int maxCells = extractedRows.Max(r => r.Cells.Count);
                                var separators = new List<double>();
                                int numCols = 1;

                                if (maxCells >= 2)
                                {
                                    // Collect all separators from reference rows (rows that contain exactly maxCells)
                                    var referenceRows = extractedRows.Where(r => r.Cells.Count == maxCells).ToList();
                                    
                                    int numSeps = maxCells - 1;
                                    var sepLists = new List<double>[numSeps];
                                    for (int i = 0; i < numSeps; i++) sepLists[i] = new List<double>();

                                    foreach (var row in referenceRows)
                                    {
                                        for (int i = 0; i < numSeps; i++)
                                        {
                                            double sep = (row.Cells[i].Right + row.Cells[i + 1].Left) / 2.0;
                                            sepLists[i].Add(sep);
                                        }
                                    }

                                    // Average the separator positions to get global separators
                                    for (int i = 0; i < numSeps; i++)
                                    {
                                        if (sepLists[i].Count > 0)
                                        {
                                            separators.Add(sepLists[i].Average());
                                        }
                                    }
                                    separators = separators.OrderBy(s => s).ToList();
                                    numCols = separators.Count + 1;
                                }

                                // Fallback: if columns are 1, segment width-wise or by max cells
                                if (numCols < 2 && extractedRows.Count >= 2)
                                {
                                    double minTextX = wordsList.Min(w => w.X);
                                    double maxTextX = wordsList.Max(w => w.Right);
                                    double span = maxTextX - minTextX;
                                    int fallbackCols = Math.Max(2, maxCells);

                                    double colWidth = span / fallbackCols;
                                    separators.Clear();
                                    for (int i = 1; i < fallbackCols; i++)
                                    {
                                        separators.Add(minTextX + i * colWidth);
                                    }
                                    numCols = separators.Count + 1;
                                }

                                // ── STEP 4: Build Grid Matrix and Assign Cells ──
                                var grid = new string[extractedRows.Count, numCols];
                                for (int r = 0; r < extractedRows.Count; r++)
                                {
                                    for (int c = 0; c < numCols; c++)
                                    {
                                        grid[r, c] = "";
                                    }
                                }

                                for (int ri = 0; ri < extractedRows.Count; ri++)
                                {
                                    var row = extractedRows[ri];
                                    foreach (var cell in row.Cells)
                                    {
                                        double cx = (cell.Left + cell.Right) / 2.0;
                                        int col = 0;
                                        for (int si = 0; si < separators.Count; si++)
                                        {
                                            if (cx > separators[si]) col = si + 1;
                                            else break;
                                        }
                                        if (col >= numCols) col = numCols - 1;

                                        if (grid[ri, col].Length > 0)
                                        {
                                            grid[ri, col] += " " + cell.Text;
                                        }
                                        else
                                        {
                                            grid[ri, col] = cell.Text;
                                        }
                                    }
                                }

                                // ── STEP 5: Post-Processing Alphanumeric & Numeric Type Corrections ──
                                for (int c = 0; c < numCols; c++)
                                {
                                    int totalNonEmpty = 0;
                                    int numericCount = 0;
                                    int dateCount = 0;

                                    for (int r = 0; r < extractedRows.Count; r++)
                                    {
                                        string text = grid[r, c].Trim();
                                        if (string.IsNullOrEmpty(text)) continue;

                                        totalNonEmpty++;

                                        int numChars = 0;
                                        int letterChars = 0;
                                        foreach (char ch in text)
                                        {
                                            if (char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '$' || ch == '%' || char.IsWhiteSpace(ch))
                                                numChars++;
                                            else if (char.IsLetter(ch))
                                                letterChars++;
                                        }
                                        
                                        int oSubstitutions = text.Count(ch => ch == 'o' || ch == 'O');
                                        if (numChars + oSubstitutions > letterChars)
                                        {
                                            numericCount++;
                                        }

                                        int slashOrDash = text.Count(ch => ch == '/' || ch == '-');
                                        if (slashOrDash >= 2 && text.Any(char.IsDigit))
                                        {
                                            dateCount++;
                                        }
                                    }

                                    if (totalNonEmpty > 0)
                                    {
                                        bool isNumeric = (double)numericCount / totalNonEmpty >= 0.4;
                                        bool isDate = (double)dateCount / totalNonEmpty >= 0.4;

                                        if (isNumeric)
                                        {
                                            Classes.Logger.LogAction("TABLE_EXTRACT", $"Column {c} identified as NUMERIC. Applying corrections.");
                                            for (int r = 0; r < extractedRows.Count; r++)
                                            {
                                                if (r == 0 && totalNonEmpty > 1 && !grid[r, c].Any(char.IsDigit))
                                                    continue; // Skip header

                                                string text = grid[r, c];
                                                if (string.IsNullOrEmpty(text)) continue;

                                                var corrected = new System.Text.StringBuilder();
                                                foreach (char ch in text)
                                                {
                                                    if (ch == 'o' || ch == 'O')
                                                        corrected.Append('0');
                                                    else
                                                        corrected.Append(ch);
                                                }
                                                
                                                string cleaned = corrected.ToString()
                                                    .Replace(" ,", ",")
                                                    .Replace(" .", ".")
                                                    .Replace(", ", ",")
                                                    .Replace(". ", ".");

                                                grid[r, c] = cleaned;
                                            }
                                        }
                                        else if (isDate)
                                        {
                                            Classes.Logger.LogAction("TABLE_EXTRACT", $"Column {c} identified as DATE. Applying corrections.");
                                            for (int r = 0; r < extractedRows.Count; r++)
                                            {
                                                if (r == 0 && totalNonEmpty > 1 && !grid[r, c].Any(char.IsDigit))
                                                    continue; // Skip header

                                                string text = grid[r, c];
                                                if (string.IsNullOrEmpty(text)) continue;

                                                string cleaned = text
                                                    .Replace(" /", "/")
                                                    .Replace("/ ", "/")
                                                    .Replace(" -", "-")
                                                    .Replace("- ", "-");
                                                grid[r, c] = cleaned;
                                            }
                                        }
                                    }
                                }

                                // ── STEP 6: Serialize Grid to Final JSON Payload ──
                                var jsonDict = new Dictionary<string, object>();
                                for (int ri = 0; ri < extractedRows.Count; ri++)
                                {
                                    for (int ci = 0; ci < numCols; ci++)
                                    {
                                        jsonDict[$"({ri},{ci})"] = new { text = grid[ri, ci].Trim(), conf = 0.92 };
                                    }
                                }

                                finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);
                                extractionMethod = "FlyShelf";

                                Classes.Logger.LogAction("TABLE_EXTRACT", $"Smart Clustered OCR: {extractedRows.Count}x{numCols} table ({separators.Count} separators)");
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
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                byteAccess.GetBuffer(out byte* dataInBytes, out uint capacity);
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

        private class ExtractedWord
        {
            public string Text { get; set; } = "";
            public double X { get; set; }
            public double Y { get; set; }
            public double W { get; set; }
            public double H { get; set; }
            public double Right => X + W;
            public double Bottom => Y + H;
            public double CenterX => X + W / 2.0;
            public double CenterY => Y + H / 2.0;
        }

        private class ExtractedCell
        {
            public string Text { get; set; } = "";
            public double Left { get; set; }
            public double Right { get; set; }
            public double Top { get; set; }
            public double Bottom { get; set; }
        }

        private class ExtractedRow
        {
            public double MinY { get; set; } = double.MaxValue;
            public double MaxBottom { get; set; } = double.MinValue;
            public List<ExtractedWord> Words { get; set; } = new();
            public List<ExtractedCell> Cells { get; set; } = new();
            public double CenterY => (MinY + MaxBottom) / 2.0;
        }

        private static ExtractedCell CreateCellFromWords(List<ExtractedWord> words)
        {
            double left = words.Min(w => w.X);
            double right = words.Max(w => w.Right);
            double top = words.Min(w => w.Y);
            double bottom = words.Max(w => w.Bottom);
            string text = string.Join(" ", words.Select(w => w.Text));
            return new ExtractedCell
            {
                Text = text,
                Left = left,
                Right = right,
                Top = top,
                Bottom = bottom
            };
        }
    }
}
