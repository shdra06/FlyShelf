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
        private static readonly Classes.OnnxTableDetector _tableDetector = new();
        private static readonly Classes.OnnxTextRecognizer _textRecognizer = new();

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

                FlyShelf.Windows.ToastWindow.ShowToast("Extracting Table... ⏳");

                await System.Threading.Tasks.Task.Run(async () =>
                {
                    bool success = false;
                    try
                    {
                        _tableDetector.Initialize();
                        _textRecognizer.Initialize();
                        success = await ExtractTableOnnxAsync(FilePath);
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("ONNX_LOAD_FAIL", $"ONNX setup failed, falling back to legacy: {ex.Message}");
                    }

                    if (!success)
                    {
                        Classes.Logger.LogAction("TABLE_FALLBACK", "Running Legacy Windows OCR Table Extractor...");
                        await ExtractTableLegacyAsync();
                    }
                });
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("TABLE_EXTRACT_FAIL", ex.Message);
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast($"Table Extraction Failed: {ex.Message}")
                );
            }
        }

        private async System.Threading.Tasks.Task<bool> ExtractTableOnnxAsync(string imgPath)
        {
            try
            {
                Classes.Logger.LogAction("ONNX_TABLE", "Running ONNX Table Extraction...");

                // 1. Detect structures and cluster cells into rows and columns
                var gridStructure = await _tableDetector.DetectTableStructureAsync(imgPath);
                if (gridStructure == null || gridStructure.Rows == 0 || gridStructure.Cols == 0)
                {
                    Classes.Logger.LogAction("ONNX_TABLE", "No table grid structure detected by ONNX model.");
                    return false;
                }

                // If it is a 6-feature model (Table Boundary Detector), run hybrid cropped extraction
                if (gridStructure.ModelFeatures == 6)
                {
                    Classes.Logger.LogAction("ONNX_TABLE", "6-feature table detector model detected. Running hybrid cropped extraction.");
                    var tableBox = gridStructure.Cells[0, 0];
                    if (tableBox != null && tableBox.Width > 0 && tableBox.Height > 0)
                    {
                        Classes.Logger.LogAction("ONNX_TABLE", $"Running hybrid cropped extraction: X={tableBox.X}, Y={tableBox.Y}, W={tableBox.Width}, H={tableBox.Height}");
                        await ExtractTableLegacyAsync(tableBox);
                        return true;
                    }
                    else
                    {
                        Classes.Logger.LogAction("ONNX_TABLE", "No valid table boundary box found in 6-feature output.");
                        return false;
                    }
                }

                int numRows = gridStructure.Rows;
                int numCols = gridStructure.Cols;
                var gridData = new string[numRows, numCols];

                // 2. Perform OCR on each cell individually
                for (int r = 0; r < numRows; r++)
                {
                    for (int c = 0; c < numCols; c++)
                    {
                        var box = gridStructure.Cells[r, c];
                        if (box.Width > 0 && box.Height > 0)
                        {
                            gridData[r, c] = await _textRecognizer.OCRCellAsync(imgPath, box);
                        }
                        else
                        {
                            gridData[r, c] = "";
                        }
                    }
                }

                // 3. Post-Processing Corrections (Numeric & Date Columns)
                // Re-use your existing Step 5 correction rules to fix numbers and dates
                for (int c = 0; c < numCols; c++)
                {
                    int totalNonEmpty = 0;
                    int numericCount = 0;
                    int dateCount = 0;

                    for (int r = 0; r < numRows; r++)
                    {
                        string text = gridData[r, c].Trim();
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
                        if (numChars + oSubstitutions > letterChars) numericCount++;

                        int slashOrDash = text.Count(ch => ch == '/' || ch == '-');
                        if (slashOrDash >= 2 && text.Any(char.IsDigit)) dateCount++;
                    }

                    if (totalNonEmpty > 0)
                    {
                        bool isNumeric = (double)numericCount / totalNonEmpty >= 0.4;
                        bool isDate = (double)dateCount / totalNonEmpty >= 0.4;

                        if (isNumeric)
                        {
                            for (int r = 0; r < numRows; r++)
                            {
                                if (r == 0 && totalNonEmpty > 1 && !gridData[r, c].Any(char.IsDigit)) continue;

                                string text = gridData[r, c];
                                if (string.IsNullOrEmpty(text)) continue;

                                var corrected = new System.Text.StringBuilder();
                                for (int i = 0; i < text.Length; i++)
                                {
                                    char ch = text[i];
                                    if (ch == 'o' || ch == 'O')
                                        corrected.Append('0');
                                    else if (ch == 'l' || ch == 'I' || ch == 'i' || ch == '|')
                                        corrected.Append('1');
                                    else if (ch == 's' || ch == 'S')
                                        corrected.Append('5');
                                    else if (ch == 'g')
                                        corrected.Append('9');
                                    else if (ch == 'z' || ch == 'Z')
                                        corrected.Append('2');
                                    else if (ch == 'b')
                                        corrected.Append('6');
                                    else if (ch == 'm' || ch == 'M')
                                        corrected.Append("01");
                                    else
                                        corrected.Append(ch);
                                }

                                gridData[r, c] = corrected.ToString()
                                    .Replace(" ,", ",").Replace(" .", ".")
                                    .Replace(", ", ",").Replace(". ", ".");
                            }
                        }
                        else if (isDate)
                        {
                            for (int r = 0; r < numRows; r++)
                            {
                                if (r == 0 && totalNonEmpty > 1 && !gridData[r, c].Any(char.IsDigit)) continue;

                                string text = gridData[r, c];
                                if (string.IsNullOrEmpty(text)) continue;

                                gridData[r, c] = text
                                    .Replace(" /", "/").Replace("/ ", "/")
                                    .Replace(" -", "-").Replace("- ", "-");
                            }
                        }
                    }
                }

                // 3.5 Character Count Validation
                float minX = float.MaxValue;
                float minY = float.MaxValue;
                float maxX = float.MinValue;
                float maxY = float.MinValue;

                for (int r = 0; r < numRows; r++)
                {
                    for (int c = 0; c < numCols; c++)
                    {
                        var box = gridStructure.Cells[r, c];
                        if (box.Width > 0 && box.Height > 0)
                        {
                            if (box.X < minX) minX = box.X;
                            if (box.Y < minY) minY = box.Y;
                            if (box.Right > maxX) maxX = box.Right;
                            if (box.Bottom > maxY) maxY = box.Bottom;
                        }
                    }
                }

                if (minX < maxX && minY < maxY)
                {
                    try
                    {
                        var tableUnionBox = new Classes.DetectedBox
                        {
                            X = minX,
                            Y = minY,
                            Width = maxX - minX,
                            Height = maxY - minY
                        };

                        int onnxCharCount = 0;
                        for (int r = 0; r < numRows; r++)
                            for (int c = 0; c < numCols; c++)
                                onnxCharCount += GetAlphaNumericCharCount(gridData[r, c]);

                        int rawOcrCharCount = await GetRawOcrCharCountAsync(imgPath, tableUnionBox);
                        int diff = rawOcrCharCount - onnxCharCount;
                        Classes.Logger.LogAction("ONNX_TABLE", $"ONNX Validation: Raw OCR={rawOcrCharCount}, ONNX Grid={onnxCharCount}, Diff={diff}");

                        if (diff > 50)
                        {
                            Classes.Logger.LogAction("ONNX_TABLE", $"ONNX Cell OCR missed {diff} characters. Falling back to Legacy pipeline.");
                            return false;
                        }
                    }
                    catch (Exception valEx)
                    {
                        Classes.Logger.LogAction("ONNX_TABLE_WARN", $"ONNX validation failed: {valEx.Message}");
                    }
                }

                // 4. Serialize to JSON Payload
                var jsonDict = new Dictionary<string, object>();
                for (int r = 0; r < numRows; r++)
                {
                    for (int c = 0; c < numCols; c++)
                    {
                        jsonDict[$"({r},{c})"] = new { text = gridData[r, c].Trim(), conf = 0.95 };
                    }
                }

                string finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);

                // 5. Open Table Editor Window on Main thread
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        FlyShelf.Classes.LicenseManager.RecordTableExtraction();
                        var editor = new FlyShelf.Windows.TableEditorWindow(finalJsonPayload, imgPath, "ONNX_Pipeline");
                        editor.Show();
                    }
                    catch (Exception uiEx)
                    {
                        Classes.Logger.LogAction("TABLE_UI_FAIL", $"Failed to open Table Editor: {uiEx.Message}");
                        FlyShelf.Windows.ToastWindow.ShowToast($"Failed to open Table Editor: {uiEx.Message}");
                    }
                });

                Classes.Logger.LogAction("ONNX_TABLE", $"ONNX Table Extract Succeeded: {numRows}x{numCols} grid");
                return true;
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("ONNX_TABLE_FAIL", $"Error in ONNX pipeline: {ex.Message}");
                return false;
            }
        }

        private async System.Threading.Tasks.Task ExtractTableLegacyAsync(Classes.DetectedBox tableBox = null)
        {
            string finalJsonPayload = string.Empty;
            string extractionMethod = string.Empty;
            try
            {
                uint originalWidth = 0;
                uint originalHeight = 0;
                global::Windows.Graphics.Imaging.SoftwareBitmap raw1x = null;

                using (var stream = File.OpenRead(FilePath))
                {
                    var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                    
                    // Premium Safeguard: Downscale huge images to a max of 1600px to avoid OutOfMemory / LOH fragmentation
                    originalWidth = decoder.OrientedPixelWidth;
                    originalHeight = decoder.OrientedPixelHeight;
                    uint maxDimension = 2000;

                    Classes.DetectedBox originalTableBox = null;
                    if (tableBox != null)
                    {
                        originalTableBox = new Classes.DetectedBox
                        {
                            X = tableBox.X,
                            Y = tableBox.Y,
                            Width = tableBox.Width,
                            Height = tableBox.Height
                        };

                        // Expand top of table boundary box by 20% of the table's height to capture the header
                        float headerExpansion = tableBox.Height * 0.20f;
                        float originalY = tableBox.Y;
                        tableBox.Y = Math.Max(0f, tableBox.Y - headerExpansion);
                        tableBox.Height += (originalY - tableBox.Y);

                        // Pad left/right margins by 40px to prevent row labels and columns (like Volume) from being clipped
                        float originalX = tableBox.X;
                        tableBox.X = Math.Max(0f, tableBox.X - 40f);
                        float right = Math.Min((float)originalWidth, originalX + tableBox.Width + 40f);
                        tableBox.Width = right - tableBox.X;

                        try
                        {
                            // Load the horizontal band at 1x scale
                            uint bandY = (uint)Math.Max(0, Math.Min(originalHeight - 1, tableBox.Y));
                            uint bandH = (uint)Math.Max(1, Math.Min(originalHeight - bandY, tableBox.Height));
                            
                            var bandTransform = new global::Windows.Graphics.Imaging.BitmapTransform
                            {
                                Bounds = new global::Windows.Graphics.Imaging.BitmapBounds
                                {
                                    X = 0,
                                    Y = bandY,
                                    Width = originalWidth,
                                    Height = bandH
                                }
                            };
                            
                            using (var bandBitmap = await decoder.GetSoftwareBitmapAsync(
                                decoder.BitmapPixelFormat,
                                decoder.BitmapAlphaMode,
                                bandTransform,
                                global::Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                                global::Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb))
                            {
                                using (var binarizedBand = PreprocessImage(bandBitmap))
                                {
                                    var (expandedLeft, expandedRight) = FindHorizontalGridSpan(binarizedBand, tableBox.X, tableBox.X + tableBox.Width);
                                    
                                    Classes.Logger.LogAction("TABLE_EXTRACT", $"ONNX Box X-span: {tableBox.X} to {tableBox.X + tableBox.Width}. Expanded X-span from horizontal band lines: {expandedLeft} to {expandedRight}");
                                    
                                    tableBox.X = (float)expandedLeft;
                                    tableBox.Width = (float)(expandedRight - expandedLeft);

                                    originalTableBox.X = (float)expandedLeft;
                                    originalTableBox.Width = (float)(expandedRight - expandedLeft);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("TABLE_EXTRACT_WARN", $"Horizontal band line analysis failed: {ex.Message}");
                        }
                    }

                    double scale = 1.0;
                    if (tableBox == null && (originalWidth > maxDimension || originalHeight > maxDimension))
                    {
                        scale = (double)maxDimension / Math.Max(originalWidth, originalHeight);
                    }

                    var transform = new global::Windows.Graphics.Imaging.BitmapTransform
                    {
                        InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
                    };

                    if (tableBox != null)
                    {
                        uint cropX = (uint)Math.Max(0, Math.Min(originalWidth - 1, tableBox.X));
                        uint cropY = (uint)Math.Max(0, Math.Min(originalHeight - 1, tableBox.Y));
                        uint cropW = (uint)Math.Max(1, Math.Min(originalWidth - cropX, tableBox.Width));
                        uint cropH = (uint)Math.Max(1, Math.Min(originalHeight - cropY, tableBox.Height));

                        transform.Bounds = new global::Windows.Graphics.Imaging.BitmapBounds
                        {
                            X = cropX,
                            Y = cropY,
                            Width = cropW,
                            Height = cropH
                        };
                        transform.ScaledWidth = originalWidth;
                        transform.ScaledHeight = originalHeight;
                        Classes.Logger.LogAction("TABLE_EXTRACT", $"Cropping image to table bounds at 1x scale: X={transform.Bounds.X}, Y={transform.Bounds.Y}, W={transform.Bounds.Width}, H={transform.Bounds.Height}");
                    }
                    else
                    {
                        transform.ScaledWidth = (uint)(originalWidth * scale);
                        transform.ScaledHeight = (uint)(originalHeight * scale);
                        Classes.Logger.LogAction("TABLE_EXTRACT", $"Scaled image from {originalWidth}x{originalHeight} to {transform.ScaledWidth}x{transform.ScaledHeight} for optimal OCR performance.");
                    }

                    raw1x = await decoder.GetSoftwareBitmapAsync(
                        decoder.BitmapPixelFormat,
                        decoder.BitmapAlphaMode,
                        transform,
                        global::Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                        global::Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
                    );
                }

                List<int> cols1x;
                List<int> rows1x;
                global::Windows.Graphics.Imaging.SoftwareBitmap rawProcessed;

                int targetScale = (tableBox != null) ? 2 : 1;
                int padding = 40;

                if (tableBox != null)
                {
                    // ── 2x bilinear upscaled pipeline for cropped table ──
                    global::Windows.Graphics.Imaging.SoftwareBitmap raw2x = ScaleBilinear(raw1x, 0, 0, raw1x.PixelWidth, raw1x.PixelHeight, 2);
                    global::Windows.Graphics.Imaging.SoftwareBitmap binarized2x = PreprocessImage(raw2x);
                    var (cols2x, rows2x) = DetectGridLines(binarized2x);
                    binarized2x.Dispose();

                    // Preserve headers - highlight neutralization start Y (bottom of header row Y=rows2x[2])
                    int startY = rows2x.Count > 2 ? rows2x[2] : (rows2x.Count > 1 ? rows2x[1] : 0);
                    NeutralizeHighlightColumns(raw2x, startY);

                    // Erasure with safe Y bounds and narrower width
                    EraseDetectedGridLinesPrecise2x(raw2x, cols2x, rows2x);

                    // Map 2x grid lines back to 1x for the unified layout pipeline
                    cols1x = cols2x.Select(c => c / 2).ToList();
                    rows1x = rows2x.Select(r => r / 2).ToList();
                    rawProcessed = raw2x;
                }
                else
                {
                    // ── Legacy 1x pipeline ──
                    global::Windows.Graphics.Imaging.SoftwareBitmap binarizedBitmap = PreprocessImage(raw1x);
                    var (detectedCols, detectedRows) = DetectGridLines(binarizedBitmap);
                    cols1x = detectedCols;
                    rows1x = detectedRows;

                    EraseGridLines1xPrecise(raw1x, binarizedBitmap);
                    binarizedBitmap.Dispose();
                    rawProcessed = raw1x;
                }

                // ── Scale and Pad processed raw crop for OCR ──
                global::Windows.Graphics.Imaging.SoftwareBitmap ocrBitmap = CropScaleAndPadSoftwareBitmap(
                    rawProcessed,
                    0, 0,
                    rawProcessed.PixelWidth, rawProcessed.PixelHeight,
                    1, // scale = 1 since rawProcessed is already at the target scale (2x or 1x)
                    padding);

                // Save the processed/cleaned image to disk for Quick Look text overlay
                await SaveSoftwareBitmapToFileAsync(rawProcessed, FilePath);

                if (rawProcessed != raw1x)
                {
                    rawProcessed.Dispose();
                }
                raw1x.Dispose();

                    var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                        new global::Windows.Globalization.Language("en-US"));

                    if (ocrEngine == null)
                        ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();

                    if (ocrEngine == null)
                    {
                        Classes.Logger.LogAction("TABLE_OCR", "No OCR engine available - install English language pack");
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Table Extraction requires Windows English OCR Pack. Please enable it in Settings.")
                        );
                        ocrBitmap.Dispose();
                        return;
                    }

                    // ── STEP 1d: Run Windows OCR on the scaled & padded raw crop ──
                    var ocrResult = await ocrEngine.RecognizeAsync(ocrBitmap);
                    ocrBitmap.Dispose();

                    if (ocrResult == null || ocrResult.Lines.Count == 0)
                    {
                        Classes.Logger.LogAction("TABLE_OCR", "No OCR text detected in image.");
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("No text detected in this image. Try a clearer screenshot.")
                        );
                        return;
                    }

                    // Extract all words in OCR space
                    var rawWords = new List<ExtractedWord>();
                    foreach (var line in ocrResult.Lines)
                    {
                        foreach (var word in line.Words)
                        {
                            var rect = word.BoundingRect;
                            rawWords.Add(new ExtractedWord
                            {
                                Text = word.Text,
                                X = rect.X,
                                Y = rect.Y,
                                W = rect.Width,
                                H = rect.Height
                            });
                        }
                    }

                    // Map words to 1x crop space for border analysis
                    var words1x = rawWords.Select(w => new ExtractedWord
                    {
                        Text = w.Text,
                        X = (w.X - padding) / targetScale,
                        Y = (w.Y - padding) / targetScale,
                        W = w.W / targetScale,
                        H = w.H / targetScale
                    }).ToList();

                    // Estimate borders and boundaries
                    double avgColW = 50.0;
                    if (cols1x.Count >= 2) avgColW = (cols1x[cols1x.Count - 1] - cols1x[0]) / (double)(cols1x.Count - 1);
                    double avgRowH = 25.0;
                    if (rows1x.Count >= 2) avgRowH = (rows1x[rows1x.Count - 1] - rows1x[0]) / (double)(rows1x.Count - 1);

                    double tableTop = rows1x.Count > 0 ? rows1x[0] : 0;
                    if (rows1x.Count > 0 && words1x.Any(w => w.CenterY < rows1x[0] && w.CenterY >= rows1x[0] - avgRowH * 1.2))
                    {
                        tableTop = Math.Max(0.0, rows1x[0] - avgRowH);
                    }
                    double tableBottom = rows1x.Count > 0 ? rows1x[rows1x.Count - 1] : 0;
                    if (rows1x.Count > 0 && words1x.Any(w => w.CenterY > rows1x[rows1x.Count - 1] && w.CenterY <= rows1x[rows1x.Count - 1] + avgRowH * 1.2))
                    {
                        tableBottom = Math.Min(originalHeight, rows1x[rows1x.Count - 1] + avgRowH);
                    }

                    double tableLeft = cols1x.Count > 0 ? cols1x[0] : 0;
                    if (cols1x.Count > 0 && words1x.Any(w => w.CenterX < cols1x[0] && w.CenterX >= cols1x[0] - avgColW * 1.2))
                    {
                        tableLeft = Math.Max(0.0, cols1x[0] - avgColW);
                    }
                    double tableRight = cols1x.Count > 0 ? cols1x[cols1x.Count - 1] : originalWidth;
                    if (cols1x.Count > 0 && words1x.Any(w => w.CenterX > cols1x[cols1x.Count - 1] && w.CenterX <= cols1x[cols1x.Count - 1] + avgColW * 1.2))
                    {
                        tableRight = Math.Min(originalWidth, cols1x[cols1x.Count - 1] + avgColW);
                    }

                    // Filter detected grid lines to within the boundaries
                    var finalCols1x = cols1x.Where(c => c >= tableLeft - 5 && c <= tableRight + 5).ToList();
                    var finalRows1x = rows1x.Where(r => r >= tableTop - 5 && r <= tableBottom + 5).ToList();

                    // Smart Boundary Insertion
                    if (finalCols1x.Count == 0 || finalCols1x[0] - tableLeft > 15) finalCols1x.Insert(0, (int)tableLeft);
                    if (tableRight - finalCols1x[finalCols1x.Count - 1] > 15) finalCols1x.Add((int)tableRight);
                    if (finalRows1x.Count == 0 || finalRows1x[0] - tableTop > 15) finalRows1x.Insert(0, (int)tableTop);
                    if (tableBottom - finalRows1x[finalRows1x.Count - 1] > 15) finalRows1x.Add((int)tableBottom);

                    // Scale grid lines to OCR space
                    var cols = finalCols1x.Select(c => c * targetScale + padding).ToList();
                    var rows = finalRows1x.Select(r => r * targetScale + padding).ToList();

                    // Filter words in OCR space to ignore title and outer noise
                    double ocrTableLeft = tableLeft * targetScale + padding;
                    double ocrTableRight = tableRight * targetScale + padding;
                    double ocrTableTop = tableTop * targetScale + padding;
                    double ocrTableBottom = tableBottom * targetScale + padding;

                    var allWords = rawWords.Where(w => 
                        w.CenterX >= ocrTableLeft - 5 && 
                        w.CenterX <= ocrTableRight + 5 && 
                        w.CenterY >= ocrTableTop - 5 && 
                        w.CenterY <= ocrTableBottom + 5
                    ).ToList();

                    Classes.Logger.LogAction("TABLE_OCR", $"Found {allWords.Count} words inside table boundaries (discarded {rawWords.Count - allWords.Count} outside)");

                    if (allWords.Count < 3)
                    {
                        Classes.Logger.LogAction("TABLE_OCR", "Too few words inside table boundaries.");
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Too few words detected inside the table area.")
                        );
                        return;
                    }

                    // ── STEP 2: Cluster Rows & Cells for layout estimation ──
                    var layoutRows = new List<ExtractedRow>();
                    var sortedWords = allWords.OrderBy(w => w.CenterY).ToList();

                    foreach (var word in sortedWords)
                    {
                        ExtractedRow? targetRow = null;
                        double maxOverlap = 0;

                        foreach (var row in layoutRows)
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
                            layoutRows.Add(newRow);
                        }
                    }

                    layoutRows = layoutRows.OrderBy(r => r.CenterY).ToList();

                    // Filter out title/caption rows at the top of the table
                    while (layoutRows.Count > 0)
                    {
                        var firstRow = layoutRows[0];
                        var rowWords = firstRow.Words.OrderBy(w => w.X).ToList();
                        if (rowWords.Count == 0)
                        {
                            layoutRows.RemoveAt(0);
                            continue;
                        }

                        var tempCells = new List<ExtractedCell>();
                        var currentCellWords = new List<ExtractedWord> { rowWords[0] };
                        var rowHeights = rowWords.Select(w => w.H).OrderBy(h => h).ToList();
                        double rowMedianHeight = rowHeights.Count > 0 ? rowHeights[rowHeights.Count / 2] : 15.0;
                        double spaceWidth = rowMedianHeight * 1.3;

                        for (int i = 1; i < rowWords.Count; i++)
                        {
                            var prevWord = rowWords[i - 1];
                            var currWord = rowWords[i];
                            double gap = currWord.X - prevWord.Right;
                            if (gap < spaceWidth)
                            {
                                currentCellWords.Add(currWord);
                            }
                            else
                            {
                                tempCells.Add(CreateCellFromWords(currentCellWords));
                                currentCellWords = new List<ExtractedWord> { currWord };
                            }
                        }
                        if (currentCellWords.Count > 0)
                        {
                            tempCells.Add(CreateCellFromWords(currentCellWords));
                        }

                        if (tempCells.Count == 1)
                        {
                            double rowSpan = tempCells[0].Right - tempCells[0].Left;
                            double totalSpan = allWords.Max(w => w.Right) - allWords.Min(w => w.X);
                            if (rowSpan > totalSpan * 0.4)
                            {
                                Classes.Logger.LogAction("TABLE_EXTRACT", $"Ignoring title/caption row: '{tempCells[0].Text}'");
                                var titleWords = firstRow.Words;
                                allWords = allWords.Where(w => !titleWords.Contains(w)).ToList();
                                layoutRows.RemoveAt(0);
                                continue;
                            }
                        }
                        break;
                    }

                    if (layoutRows.Count < 2)
                    {
                        Classes.Logger.LogAction("TABLE_OCR", "Only 1 row detected in layout after title filtering - not a table.");
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Only 1 row detected — image doesn't appear to contain a table.")
                        );
                        return;
                    }

                    // Update sortedWords to exclude title words
                    sortedWords = allWords.OrderBy(w => w.CenterY).ToList();

                    var sortedHeights = sortedWords.Select(w => w.H).OrderBy(h => h).ToList();
                    double globalMedianHeight = sortedHeights.Count > 0 ? sortedHeights[sortedHeights.Count / 2] : 15.0;
                    double maxSpaceWidth = globalMedianHeight * 1.3;

                    foreach (var row in layoutRows)
                    {
                        var rowSortedWords = row.Words.OrderBy(w => w.X).ToList();
                        if (rowSortedWords.Count == 0) continue;

                        var currentCellWords = new List<ExtractedWord> { rowSortedWords[0] };
                        
                        for (int i = 1; i < rowSortedWords.Count; i++)
                        {
                            var prevWord = rowSortedWords[i - 1];
                            var currWord = rowSortedWords[i];
                            
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

                    int maxCells = layoutRows.Max(r => r.Cells.Count);

                    int gridCols = cols.Count - 1;
                    int gridRows = rows.Count - 1;
                    Classes.Logger.LogAction("TABLE_EXTRACT", $"Grid lines detected: {gridCols} cols, {gridRows} rows. Layout estimated columns: {maxCells}");
                    int rawCharCount = allWords.Sum(w => GetAlphaNumericCharCount(w.Text));

                    // Check if the detected grid has enough columns/rows and matches the text layout structure.
                    bool useGridParser = (cols.Count >= 3 && rows.Count >= 3) && (gridCols >= maxCells - 1);
                    bool gridSuccess = false;

                    if (useGridParser)
                    {
                        Classes.Logger.LogAction("TABLE_EXTRACT", "Grid matches text layout. Running Single-Run Grid Parser...");

                        var gridMatrix = new string[gridRows, gridCols];
                        for (int r = 0; r < gridRows; r++)
                            for (int c = 0; c < gridCols; c++)
                                gridMatrix[r, c] = "";

                        foreach (var word in sortedWords)
                        {
                            double cx = word.CenterX;
                            double cy = word.CenterY;

                            int colIdx = -1;
                            for (int c = 0; c < gridCols; c++)
                            {
                                if (cx >= cols[c] && cx < cols[c + 1])
                                {
                                    colIdx = c;
                                    break;
                                }
                            }

                            int rowIdx = -1;
                            for (int r = 0; r < gridRows; r++)
                            {
                                if (cy >= rows[r] && cy < rows[r + 1])
                                {
                                    rowIdx = r;
                                    break;
                                }
                            }

                            if (colIdx != -1 && rowIdx != -1)
                            {
                                if (gridMatrix[rowIdx, colIdx].Length > 0)
                                    gridMatrix[rowIdx, colIdx] += " " + word.Text;
                                else
                                    gridMatrix[rowIdx, colIdx] = word.Text;
                            }
                        }

                        // Apply type-specific post-processing corrections to gridMatrix
                        for (int c = 0; c < gridCols; c++)
                        {
                            int totalNonEmpty = 0;
                            int numericCount = 0;
                            int dateCount = 0;

                            for (int r = 0; r < gridRows; r++)
                            {
                                string text = gridMatrix[r, c].Trim();
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
                                    numericCount++;

                                int slashOrDash = text.Count(ch => ch == '/' || ch == '-');
                                if (slashOrDash >= 2 && text.Any(char.IsDigit))
                                    dateCount++;
                            }

                            if (totalNonEmpty > 0)
                            {
                                bool isNumeric = (double)numericCount / totalNonEmpty >= 0.4;
                                bool isDate = (double)dateCount / totalNonEmpty >= 0.4;

                                if (isNumeric)
                                {
                                    Classes.Logger.LogAction("TABLE_EXTRACT", $"Cell-Grid Column {c} identified as NUMERIC. Applying corrections.");
                                    for (int r = 0; r < gridRows; r++)
                                    {
                                        if (r == 0 && totalNonEmpty > 1 && !gridMatrix[r, c].Any(char.IsDigit))
                                            continue; // Skip header

                                        string text = gridMatrix[r, c];
                                        if (string.IsNullOrEmpty(text)) continue;

                                        int digits = text.Count(char.IsDigit);
                                        int letters = text.Count(char.IsLetter);
                                        if (digits == 0 && letters > 2)
                                            continue;

                                         var corrected = new System.Text.StringBuilder();
                                         for (int i = 0; i < text.Length; i++)
                                         {
                                             char ch = text[i];
                                             if (ch == 'o' || ch == 'O')
                                                 corrected.Append('0');
                                             else if (ch == 'l' || ch == 'I' || ch == 'i' || ch == '|')
                                                 corrected.Append('1');
                                             else if (ch == 's' || ch == 'S')
                                                 corrected.Append('5');
                                             else if (ch == 'g')
                                                 corrected.Append('9');
                                             else if (ch == 'z' || ch == 'Z')
                                                 corrected.Append('2');
                                             else if (ch == 'b')
                                                 corrected.Append('6');
                                             else if (ch == 'm' || ch == 'M')
                                                 corrected.Append("01");
                                             else
                                                 corrected.Append(ch);
                                         }
                                        
                                        string cleaned = corrected.ToString()
                                            .Replace(" ,", ",")
                                            .Replace(" .", ".")
                                            .Replace(", ", ",")
                                            .Replace(". ", ".");

                                        gridMatrix[r, c] = cleaned;
                                    }
                                }
                                else if (isDate)
                                {
                                    Classes.Logger.LogAction("TABLE_EXTRACT", $"Cell-Grid Column {c} identified as DATE. Applying corrections.");
                                    for (int r = 0; r < gridRows; r++)
                                    {
                                        if (r == 0 && totalNonEmpty > 1 && !gridMatrix[r, c].Any(char.IsDigit))
                                            continue; // Skip header

                                        string text = gridMatrix[r, c];
                                        if (string.IsNullOrEmpty(text)) continue;

                                        string cleaned = text
                                            .Replace(" /", "/")
                                            .Replace("/ ", "/")
                                            .Replace(" -", "-")
                                            .Replace("- ", "-");
                                        gridMatrix[r, c] = cleaned;
                                    }
                                }
                            }
                        }

                        // Validate character count in gridMatrix vs raw OCR
                        int gridCharCount = 0;
                        for (int r = 0; r < gridRows; r++)
                            for (int c = 0; c < gridCols; c++)
                                gridCharCount += GetAlphaNumericCharCount(gridMatrix[r, c]);

                        int diff = rawCharCount - gridCharCount;
                        Classes.Logger.LogAction("TABLE_EXTRACT", $"Character check: Raw OCR={rawCharCount}, Grid={gridCharCount}, Diff={diff}");

                        if (diff <= 50)
                        {
                            // Serialize Grid to Final JSON Payload
                            var jsonDict = new Dictionary<string, object>();
                            for (int ri = 0; ri < gridRows; ri++)
                            {
                                for (int ci = 0; ci < gridCols; ci++)
                                {
                                    jsonDict[$"({ri},{ci})"] = new { text = gridMatrix[ri, ci].Trim(), conf = 0.95 };
                                }
                            }

                            finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);
                            extractionMethod = "FlyShelf_CellGrid";
                            gridSuccess = true;
                        }
                        else
                        {
                            Classes.Logger.LogAction("TABLE_EXTRACT", $"Grid parser dropped {diff} characters (too much text lost). Forcing fallback to Layout parser!");
                        }
                    }

                    if (!gridSuccess)
                    {
                        Classes.Logger.LogAction("TABLE_EXTRACT", "Grid lines incomplete or mismatching layout. Falling back to Layout-based OCR Parser...");

                        // Collect all visual fragments across the table
                        var allFragments = layoutRows.SelectMany(r => r.Cells).OrderBy(f => f.Top).ThenBy(f => f.Left).ToList();

                        // Step 1: Merge fragments vertically into logical cells
                        var logicalCells = new List<ExtractedCell>();

                        foreach (var f in allFragments)
                        {
                            ExtractedCell? targetCell = null;
                            
                            // Find an existing logical cell that is directly above this fragment and overlaps horizontally
                            foreach (var c in logicalCells)
                            {
                                double horizontalOverlap = Math.Max(0, Math.Min(f.Right, c.Right) - Math.Max(f.Left, c.Left));
                                double minWidth = Math.Min(f.Right - f.Left, c.Right - c.Left);

                                if (minWidth > 0 && (horizontalOverlap / minWidth) >= 0.4) // 40% horizontal overlap
                                {
                                    double verticalGap = f.Top - c.Bottom;
                                    // Allow a small negative gap (overlap) and a gap up to 1.5 * globalMedianHeight
                                    if (verticalGap >= -5 && verticalGap <= globalMedianHeight * 1.5)
                                    {
                                        targetCell = c;
                                        break;
                                    }
                                }
                            }

                            if (targetCell != null)
                            {
                                // Append fragment text with a newline
                                targetCell.Text += "\n" + f.Text;
                                targetCell.Left = Math.Min(targetCell.Left, f.Left);
                                targetCell.Right = Math.Max(targetCell.Right, f.Right);
                                targetCell.Top = Math.Min(targetCell.Top, f.Top);
                                targetCell.Bottom = Math.Max(targetCell.Bottom, f.Bottom);
                            }
                            else
                            {
                                // Create a new logical cell
                                logicalCells.Add(new ExtractedCell
                                {
                                    Text = f.Text,
                                    Left = f.Left,
                                    Right = f.Right,
                                    Top = f.Top,
                                    Bottom = f.Bottom
                                });
                            }
                        }

                        // Step 2: Group logical cells into logical rows based on vertical overlap
                        var logicalRows = new List<ExtractedRow>();
                        var sortedLogicalCells = logicalCells.OrderBy(c => c.Top).ThenBy(c => c.Left).ToList();

                        foreach (var cell in sortedLogicalCells)
                        {
                            ExtractedRow? targetRow = null;
                            double maxOverlap = 0;

                            foreach (var row in logicalRows)
                            {
                                double overlap = Math.Max(0, Math.Min(cell.Bottom, row.MaxBottom) - Math.Max(cell.Top, row.MinY));
                                double minHeight = Math.Min(cell.Bottom - cell.Top, row.MaxBottom - row.MinY);

                                if (minHeight > 0)
                                {
                                    double overlapRatio = overlap / minHeight;
                                    if (overlapRatio >= 0.4) // 40% vertical overlap
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
                                targetRow.Cells.Add(cell);
                                targetRow.MinY = Math.Min(targetRow.MinY, cell.Top);
                                targetRow.MaxBottom = Math.Max(targetRow.MaxBottom, cell.Bottom);
                            }
                            else
                            {
                                var newRow = new ExtractedRow
                                {
                                    MinY = cell.Top,
                                    MaxBottom = cell.Bottom
                                };
                                newRow.Cells.Add(cell);
                                logicalRows.Add(newRow);
                            }
                        }

                        // Sort the logical rows top-to-bottom
                        logicalRows = logicalRows.OrderBy(r => r.MinY).ToList();

                        // For each row, sort its cells left-to-right
                        foreach (var row in logicalRows)
                        {
                            row.Cells = row.Cells.OrderBy(c => c.Left).ToList();
                        }

                        // Step 3: Determine Column Separators based on the row with max cells
                        maxCells = logicalRows.Count > 0 ? logicalRows.Max(r => r.Cells.Count) : 0;
                        var separators = new List<double>();
                        int numCols = 1;

                        if (maxCells >= 2)
                        {
                            var referenceRows = logicalRows.Where(r => r.Cells.Count == maxCells).ToList();
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

                        if (numCols < 2 && logicalRows.Count >= 2)
                        {
                            double minTextX = allFragments.Min(f => f.Left);
                            double maxTextX = allFragments.Max(f => f.Right);
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

                        // Step 4: Map logical cells to the grid matrix
                        var grid = new string[logicalRows.Count, numCols];
                        for (int r = 0; r < logicalRows.Count; r++)
                        {
                            for (int c = 0; c < numCols; c++)
                            {
                                grid[r, c] = "";
                            }
                        }

                        for (int ri = 0; ri < logicalRows.Count; ri++)
                        {
                            var row = logicalRows[ri];
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
                                    grid[ri, col] += "\n" + cell.Text;
                                }
                                else
                                {
                                    grid[ri, col] = cell.Text;
                                }
                            }
                        }

                        for (int c = 0; c < numCols; c++)
                        {
                            int totalNonEmpty = 0;
                            int numericCount = 0;
                            int dateCount = 0;

                            for (int r = 0; r < logicalRows.Count; r++)
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
                                    numericCount++;

                                int slashOrDash = text.Count(ch => ch == '/' || ch == '-');
                                if (slashOrDash >= 2 && text.Any(char.IsDigit))
                                    dateCount++;
                            }

                            if (totalNonEmpty > 0)
                            {
                                bool isNumeric = (double)numericCount / totalNonEmpty >= 0.4;
                                bool isDate = (double)dateCount / totalNonEmpty >= 0.4;

                                if (isNumeric)
                                {
                                    Classes.Logger.LogAction("TABLE_EXTRACT", $"Column {c} identified as NUMERIC. Applying corrections.");
                                    for (int r = 0; r < logicalRows.Count; r++)
                                    {
                                        if (r == 0 && totalNonEmpty > 1 && !grid[r, c].Any(char.IsDigit))
                                            continue; // Skip header

                                        string text = grid[r, c];
                                        if (string.IsNullOrEmpty(text)) continue;

                                         var corrected = new System.Text.StringBuilder();
                                         for (int i = 0; i < text.Length; i++)
                                         {
                                             char ch = text[i];
                                             if (ch == 'o' || ch == 'O')
                                                 corrected.Append('0');
                                             else if (ch == 'l' || ch == 'I' || ch == 'i' || ch == '|')
                                                 corrected.Append('1');
                                             else if (ch == 's' || ch == 'S')
                                                 corrected.Append('5');
                                             else if (ch == 'g')
                                                 corrected.Append('9');
                                             else if (ch == 'z' || ch == 'Z')
                                                 corrected.Append('2');
                                             else if (ch == 'b')
                                                 corrected.Append('6');
                                             else if (ch == 'm' || ch == 'M')
                                                 corrected.Append("01");
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
                                    for (int r = 0; r < logicalRows.Count; r++)
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

                        var jsonDict = new Dictionary<string, object>();
                        for (int ri = 0; ri < logicalRows.Count; ri++)
                        {
                            for (int ci = 0; ci < numCols; ci++)
                            {
                                jsonDict[$"({ri},{ci})"] = new { text = grid[ri, ci].Trim(), conf = 0.92 };
                            }
                        }

                        finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);
                        extractionMethod = tableBox != null ? "ONNX_Cropped" : "FlyShelf";
                        Classes.Logger.LogAction("TABLE_EXTRACT", $"Smart Clustered OCR: {logicalRows.Count}x{numCols} table ({separators.Count} separators)");
                    }
            }
            catch (Exception ocrEx)
            {
                Classes.Logger.LogAction("TABLE_OCR_FAIL", ocrEx.Message);
            }

            if (!string.IsNullOrWhiteSpace(finalJsonPayload) && finalJsonPayload.StartsWith("{"))
            {
                string imgPath = FilePath;
                string method = extractionMethod;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        FlyShelf.Classes.LicenseManager.RecordTableExtraction();
                        var editor = new FlyShelf.Windows.TableEditorWindow(finalJsonPayload, imgPath, method);
                        editor.Show();
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
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    FlyShelf.Windows.ToastWindow.ShowToast("No table structure detected in this image.")
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

        private static unsafe (List<int> cols, List<int> rows) DetectGridLines(global::Windows.Graphics.Imaging.SoftwareBitmap binaryBitmap)
        {
            int width = binaryBitmap.PixelWidth;
            int height = binaryBitmap.PixelHeight;

            bool[,] isBlack = new bool[width, height];
            using (var buffer = binaryBitmap.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
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
                        int pxIdx = rowOffset + 4 * x;
                        isBlack[x, y] = data[pxIdx + 0] < 128;
                    }
                }
            }

            bool[] isVertLinePx = new bool[width];
            int vertThreshold = (int)(height * 0.40); // 40% of height

            for (int x = 0; x < width; x++)
            {
                int maxRun = 0;
                int currentRun = 0;
                int gapCount = 0;
                for (int y = 0; y < height; y++)
                {
                    if (isBlack[x, y])
                    {
                        currentRun += gapCount + 1;
                        gapCount = 0;
                    }
                    else
                    {
                        if (currentRun > 0)
                        {
                            gapCount++;
                            if (gapCount > 4) // Max 4 consecutive white pixels allowed as a gap
                            {
                                if (currentRun > maxRun) maxRun = currentRun;
                                currentRun = 0;
                                gapCount = 0;
                            }
                        }
                    }
                }
                if (currentRun > maxRun) maxRun = currentRun;

                if (maxRun >= vertThreshold)
                {
                    isVertLinePx[x] = true;
                }
            }

            var vertLines = new List<int>();
            int startX = -1;
            for (int x = 0; x < width; x++)
            {
                if (isVertLinePx[x])
                {
                    if (startX == -1) startX = x;
                }
                else
                {
                    if (startX != -1)
                    {
                        int lineWidth = x - startX;
                        if (lineWidth <= 10) // Discard thick bands (e.g. text columns, button backgrounds)
                        {
                            int center = (startX + x - 1) / 2;
                            vertLines.Add(center);
                        }
                        startX = -1;
                    }
                }
            }
            if (startX != -1)
            {
                int lineWidth = width - startX;
                if (lineWidth <= 10)
                {
                    vertLines.Add((startX + width - 1) / 2);
                }
            }

            bool[] isHorizLinePx = new bool[height];
            int horizThreshold = (int)(width * 0.40); // 40% of width

            for (int y = 0; y < height; y++)
            {
                int maxRun = 0;
                int currentRun = 0;
                int gapCount = 0;
                for (int x = 0; x < width; x++)
                {
                    if (isBlack[x, y])
                    {
                        currentRun += gapCount + 1;
                        gapCount = 0;
                    }
                    else
                    {
                        if (currentRun > 0)
                        {
                            gapCount++;
                            if (gapCount > 4) // Max 4 consecutive white pixels allowed as a gap
                            {
                                if (currentRun > maxRun) maxRun = currentRun;
                                currentRun = 0;
                                gapCount = 0;
                            }
                        }
                    }
                }
                if (currentRun > maxRun) maxRun = currentRun;

                if (maxRun >= horizThreshold)
                {
                    isHorizLinePx[y] = true;
                }
            }

            var horizLines = new List<int>();
            int startY = -1;
            for (int y = 0; y < height; y++)
            {
                if (isHorizLinePx[y])
                {
                    if (startY == -1) startY = y;
                }
                else
                {
                    if (startY != -1)
                    {
                        int lineHeight = y - startY;
                        if (lineHeight <= 10) // Discard thick bands (e.g. headers, box backgrounds)
                        {
                            int center = (startY + y - 1) / 2;
                            horizLines.Add(center);
                        }
                        startY = -1;
                    }
                }
            }
            if (startY != -1)
            {
                int lineHeight = height - startY;
                if (lineHeight <= 10)
                {
                    horizLines.Add((startY + height - 1) / 2);
                }
            }

            return (vertLines, horizLines);
        }

        private static unsafe void EraseGridLines(global::Windows.Graphics.Imaging.SoftwareBitmap bitmap)
        {
            using (var buffer = bitmap.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.ReadWrite))
            using (var reference = buffer.CreateReference())
            {
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                byteAccess.GetBuffer(out byte* data, out uint capacity);
                var layout = buffer.GetPlaneDescription(0);

                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;

                bool[,] isBlack = new bool[width, height];
                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int pxIdx = rowOffset + 4 * x;
                        isBlack[x, y] = data[pxIdx + 0] < 128;
                    }
                }

                bool[,] toRemove = new bool[width, height];
                int horizThreshold = 35;
                int vertThreshold = 30;

                for (int y = 0; y < height; y++)
                {
                    int runLength = 0;
                    for (int x = 0; x < width; x++)
                    {
                        if (isBlack[x, y])
                        {
                            runLength++;
                        }
                        else
                        {
                            if (runLength >= horizThreshold)
                            {
                                for (int k = x - runLength; k < x; k++)
                                {
                                    toRemove[k, y] = true;
                                }
                            }
                            runLength = 0;
                        }
                    }
                    if (runLength >= horizThreshold)
                    {
                        for (int k = width - runLength; k < width; k++)
                        {
                            toRemove[k, y] = true;
                        }
                    }
                }

                for (int x = 0; x < width; x++)
                {
                    int runLength = 0;
                    for (int y = 0; y < height; y++)
                    {
                        if (isBlack[x, y])
                        {
                            runLength++;
                        }
                        else
                        {
                            if (runLength >= vertThreshold)
                            {
                                for (int k = y - runLength; k < y; k++)
                                {
                                    toRemove[x, k] = true;
                                }
                            }
                            runLength = 0;
                        }
                    }
                    if (runLength >= vertThreshold)
                    {
                        for (int k = height - runLength; k < height; k++)
                        {
                            toRemove[x, k] = true;
                        }
                    }
                }

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        if (toRemove[x, y])
                        {
                            int pxIdx = rowOffset + 4 * x;
                            data[pxIdx + 0] = 255;
                            data[pxIdx + 1] = 255;
                            data[pxIdx + 2] = 255;
                            data[pxIdx + 3] = 255;
                        }
                    }
                }
            }
        }

        private static unsafe global::Windows.Graphics.Imaging.SoftwareBitmap CropAndScaleSoftwareBitmap(
            global::Windows.Graphics.Imaging.SoftwareBitmap src,
            int cropX, int cropY, int cropW, int cropH,
            int scale)
        {
            int dstW = cropW * scale;
            int dstH = cropH * scale;

            var dst = new global::Windows.Graphics.Imaging.SoftwareBitmap(
                global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                dstW, dstH,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight);

            using (var srcBuffer = src.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
            using (var srcRef = srcBuffer.CreateReference())
            using (var dstBuffer = dst.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Write))
            using (var dstRef = dstBuffer.CreateReference())
            {
                var srcAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(srcRef);
                srcAccess.GetBuffer(out byte* srcData, out uint srcCap);
                var srcLayout = srcBuffer.GetPlaneDescription(0);

                var dstAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(dstRef);
                dstAccess.GetBuffer(out byte* dstData, out uint dstCap);
                var dstLayout = dstBuffer.GetPlaneDescription(0);

                int srcW = src.PixelWidth;
                int srcH = src.PixelHeight;

                for (int y = 0; y < dstH; y++)
                {
                    int localY = y / scale;
                    int srcY = cropY + localY;
                    srcY = Math.Max(0, Math.Min(srcH - 1, srcY));

                    byte* srcRow = srcData + srcLayout.StartIndex + srcLayout.Stride * srcY;
                    byte* dstRow = dstData + dstLayout.StartIndex + dstLayout.Stride * y;

                    for (int x = 0; x < dstW; x++)
                    {
                        int localX = x / scale;
                        int srcX = cropX + localX;
                        srcX = Math.Max(0, Math.Min(srcW - 1, srcX));

                        byte* srcPixel = srcRow + 4 * srcX;
                        byte* dstPixel = dstRow + 4 * x;

                        dstPixel[0] = srcPixel[0];
                        dstPixel[1] = srcPixel[1];
                        dstPixel[2] = srcPixel[2];
                        dstPixel[3] = srcPixel[3];
                    }
                }
            }
            return dst;
        }

        private static int GetAlphaNumericCharCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetterOrDigit(text[i]))
                    count++;
            }
            return count;
        }

        private async System.Threading.Tasks.Task<int> GetRawOcrCharCountAsync(string imgPath, Classes.DetectedBox box)
        {
            try
            {
                using (var stream = File.OpenRead(imgPath))
                {
                    var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                    uint originalWidth = decoder.OrientedPixelWidth;
                    uint originalHeight = decoder.OrientedPixelHeight;

                    var transform = new global::Windows.Graphics.Imaging.BitmapTransform();
                    uint cropX = (uint)Math.Max(0, Math.Min(originalWidth - 1, box.X));
                    uint cropY = (uint)Math.Max(0, Math.Min(originalHeight - 1, box.Y));
                    uint cropW = (uint)Math.Max(1, Math.Min(originalWidth - cropX, box.Width));
                    uint cropH = (uint)Math.Max(1, Math.Min(originalHeight - cropY, box.Height));

                    transform.Bounds = new global::Windows.Graphics.Imaging.BitmapBounds
                    {
                        X = cropX,
                        Y = cropY,
                        Width = cropW,
                        Height = cropH
                    };

                    using (var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        decoder.BitmapPixelFormat,
                        decoder.BitmapAlphaMode,
                        transform,
                        global::Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                        global::Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb))
                    {
                        var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                            new global::Windows.Globalization.Language("en-US"));
                        if (ocrEngine == null) ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();

                        if (ocrEngine != null)
                        {
                            var result = await ocrEngine.RecognizeAsync(softwareBitmap);
                            if (result != null)
                            {
                                int count = 0;
                                foreach (var line in result.Lines)
                                {
                                    foreach (var word in line.Words)
                                    {
                                        count += GetAlphaNumericCharCount(word.Text);
                                    }
                                }
                                return count;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("OCR_COUNT_FAIL", ex.Message);
            }
            return 0;
        }

        private static unsafe (double minX, double maxX) FindHorizontalGridSpan(global::Windows.Graphics.Imaging.SoftwareBitmap binaryBitmap, double initialLeft, double initialRight)
        {
            int width = binaryBitmap.PixelWidth;
            int height = binaryBitmap.PixelHeight;

            bool[,] isBlack = new bool[width, height];
            using (var buffer = binaryBitmap.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
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
                        int pxIdx = rowOffset + 4 * x;
                        isBlack[x, y] = data[pxIdx + 0] < 128;
                    }
                }
            }

            double minX = initialLeft;
            double maxX = initialRight;
            double initialWidth = initialRight - initialLeft;
            double minLineLength = Math.Max(50.0, initialWidth * 0.25); // at least 25% of initial table width, or 50px

            for (int y = 0; y < height; y++)
            {
                int startRun = -1;
                int gapCount = 0;
                for (int x = 0; x < width; x++)
                {
                    if (isBlack[x, y])
                    {
                        if (startRun == -1)
                        {
                            startRun = x;
                        }
                        gapCount = 0;
                    }
                    else
                    {
                        if (startRun != -1)
                        {
                            gapCount++;
                            if (gapCount > 40) // Allow up to 40 pixels gap in a grid line to bridge selection highlight transitions
                            {
                                int endRun = x - gapCount;
                                int runLength = endRun - startRun + 1;
                                if (runLength >= minLineLength)
                                {
                                    bool overlaps = (startRun <= initialRight && endRun >= initialLeft);
                                    if (overlaps)
                                    {
                                        if (startRun < minX) minX = startRun;
                                        if (endRun > maxX) maxX = endRun;
                                    }
                                }
                                startRun = -1;
                                gapCount = 0;
                            }
                        }
                    }
                }
                if (startRun != -1)
                {
                    int endRun = width - 1;
                    int runLength = endRun - startRun + 1;
                    if (runLength >= minLineLength)
                    {
                        bool overlaps = (startRun <= initialRight && endRun >= initialLeft);
                        if (overlaps)
                        {
                            if (startRun < minX) minX = startRun;
                            if (endRun > maxX) maxX = endRun;
                        }
                    }
                }
            }

            return (minX, maxX);
        }

        private static int GetVerticalRunLength(bool[,] isDark, int x, int y, int height)
        {
            if (!isDark[x, y]) return 0;
            int top = y;
            while (top > 0 && isDark[x, top - 1]) top--;
            int bottom = y;
            while (bottom < height - 1 && isDark[x, bottom + 1]) bottom++;
            return bottom - top + 1;
        }

        private static int GetHorizontalRunLength(bool[,] isDark, int x, int y, int width)
        {
            if (!isDark[x, y]) return 0;
            int left = x;
            while (left > 0 && isDark[left - 1, y]) left--;
            int right = x;
            while (right < width - 1 && isDark[right + 1, y]) right++;
            return right - left + 1;
        }

        private static unsafe void EraseGridLines1xPrecise(global::Windows.Graphics.Imaging.SoftwareBitmap raw, global::Windows.Graphics.Imaging.SoftwareBitmap binarized)
        {
            int width = binarized.PixelWidth;
            int height = binarized.PixelHeight;

            bool[,] isDark = new bool[width, height];
            using (var binBuffer = binarized.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
            using (var binRef = binBuffer.CreateReference())
            {
                var binAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(binRef);
                binAccess.GetBuffer(out byte* binData, out uint binCap);
                var binLayout = binBuffer.GetPlaneDescription(0);

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = binLayout.StartIndex + binLayout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        int pxIdx = rowOffset + 4 * x;
                        isDark[x, y] = binData[pxIdx + 0] < 128; // Adaptive binarized is grayscale (B=G=R)
                    }
                }
            }

            bool[,] isPartOfHorizLine = new bool[width, height];
            bool[,] isPartOfVertLine = new bool[width, height];

            int horizThreshold = 50;
            int vertThreshold = 50;

            // Horizontal pass
            for (int y = 0; y < height; y++)
            {
                int runLength = 0;
                for (int x = 0; x < width; x++)
                {
                    if (isDark[x, y])
                    {
                        runLength++;
                    }
                    else
                    {
                        if (runLength >= horizThreshold)
                        {
                            for (int k = x - runLength; k < x; k++)
                            {
                                isPartOfHorizLine[k, y] = true;
                            }
                        }
                        runLength = 0;
                    }
                }
                if (runLength >= horizThreshold)
                {
                    for (int k = width - runLength; k < width; k++)
                    {
                        isPartOfHorizLine[k, y] = true;
                    }
                }
            }

            // Vertical pass
            for (int x = 0; x < width; x++)
            {
                int runLength = 0;
                for (int y = 0; y < height; y++)
                {
                    if (isDark[x, y])
                    {
                        runLength++;
                    }
                    else
                    {
                        if (runLength >= vertThreshold)
                        {
                            for (int k = y - runLength; k < y; k++)
                            {
                                isPartOfVertLine[x, k] = true;
                            }
                        }
                        runLength = 0;
                    }
                }
                if (runLength >= vertThreshold)
                {
                    for (int k = height - runLength; k < height; k++)
                    {
                        isPartOfVertLine[x, k] = true;
                    }
                }
            }

            bool[,] toRemove = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (isPartOfHorizLine[x, y])
                    {
                        // Check if it's a character crossing
                        int vRun = GetVerticalRunLength(isDark, x, y, height);
                        if (vRun >= 4 && vRun < 35)
                        {
                            // Preserve it!
                            continue;
                        }
                        toRemove[x, y] = true;
                    }
                    if (isPartOfVertLine[x, y])
                    {
                        // Check if it's a character crossing
                        int hRun = GetHorizontalRunLength(isDark, x, y, width);
                        if (hRun >= 4 && hRun < 35)
                        {
                            // Preserve it!
                            continue;
                        }
                        toRemove[x, y] = true;
                    }
                }
            }

            // Apply mask to raw image by painting those pixels white
            using (var rawBuffer = raw.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.ReadWrite))
            using (var rawRef = rawBuffer.CreateReference())
            {
                var rawAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(rawRef);
                rawAccess.GetBuffer(out byte* rawData, out uint rawCap);
                var rawLayout = rawBuffer.GetPlaneDescription(0);

                for (int y = 0; y < height; y++)
                {
                    int rowOffset = rawLayout.StartIndex + rawLayout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        if (toRemove[x, y])
                        {
                            int pxIdx = rowOffset + 4 * x;
                            rawData[pxIdx + 0] = 255; // B
                            rawData[pxIdx + 1] = 255; // G
                            rawData[pxIdx + 2] = 255; // R
                            rawData[pxIdx + 3] = 255; // A
                        }
                    }
                }
            }
        }

        private static unsafe global::Windows.Graphics.Imaging.SoftwareBitmap CropScaleAndPadSoftwareBitmap(
            global::Windows.Graphics.Imaging.SoftwareBitmap src,
            int cropX, int cropY, int cropW, int cropH,
            int scale, int paddingPx)
        {
            int dstW = cropW * scale + paddingPx * 2;
            int dstH = cropH * scale + paddingPx * 2;

            var dst = new global::Windows.Graphics.Imaging.SoftwareBitmap(
                global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                dstW, dstH,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight);

            using (var srcBuffer = src.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
            using (var srcRef = srcBuffer.CreateReference())
            using (var dstBuffer = dst.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Write))
            using (var dstRef = dstBuffer.CreateReference())
            {
                var srcAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(srcRef);
                srcAccess.GetBuffer(out byte* srcData, out uint srcCap);
                var srcLayout = srcBuffer.GetPlaneDescription(0);

                var dstAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(dstRef);
                dstAccess.GetBuffer(out byte* dstData, out uint dstCap);
                var dstLayout = dstBuffer.GetPlaneDescription(0);

                int srcW = src.PixelWidth;
                int srcH = src.PixelHeight;

                // Fill background with white
                for (int y = 0; y < dstH; y++)
                {
                    byte* dstRow = dstData + dstLayout.StartIndex + dstLayout.Stride * y;
                    for (int x = 0; x < dstW; x++)
                    {
                        byte* dstPixel = dstRow + 4 * x;
                        dstPixel[0] = 255;
                        dstPixel[1] = 255;
                        dstPixel[2] = 255;
                        dstPixel[3] = 255;
                    }
                }

                // Copy scaled source inside padding
                for (int y = 0; y < cropH * scale; y++)
                {
                    int localY = y / scale;
                    int srcY = cropY + localY;
                    srcY = Math.Max(0, Math.Min(srcH - 1, srcY));

                    byte* srcRow = srcData + srcLayout.StartIndex + srcLayout.Stride * srcY;
                    byte* dstRow = dstData + dstLayout.StartIndex + dstLayout.Stride * (y + paddingPx);

                    for (int x = 0; x < cropW * scale; x++)
                    {
                        int localX = x / scale;
                        int srcX = cropX + localX;
                        srcX = Math.Max(0, Math.Min(srcW - 1, srcX));

                        byte* srcPixel = srcRow + 4 * srcX;
                        byte* dstPixel = dstRow + 4 * (x + paddingPx);

                        dstPixel[0] = srcPixel[0];
                        dstPixel[1] = srcPixel[1];
                        dstPixel[2] = srcPixel[2];
                        dstPixel[3] = srcPixel[3];
                    }
                }
            }
            return dst;
        }

        private static unsafe global::Windows.Graphics.Imaging.SoftwareBitmap ScaleBilinear(global::Windows.Graphics.Imaging.SoftwareBitmap src, int cropX, int cropY, int cropW, int cropH, int scale)
        {
            int dstW = cropW * scale;
            int dstH = cropH * scale;
            var dst = new global::Windows.Graphics.Imaging.SoftwareBitmap(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, dstW, dstH, global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight);

            using (var srcBuffer = src.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
            using (var srcRef = srcBuffer.CreateReference())
            using (var dstBuffer = dst.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Write))
            using (var dstRef = dstBuffer.CreateReference())
            {
                var srcAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(srcRef);
                srcAccess.GetBuffer(out byte* srcData, out uint srcCap);
                var srcLayout = srcBuffer.GetPlaneDescription(0);

                var dstAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(dstRef);
                dstAccess.GetBuffer(out byte* dstData, out uint dstCap);
                var dstLayout = dstBuffer.GetPlaneDescription(0);

                int srcW = src.PixelWidth;
                int srcH = src.PixelHeight;

                for (int y = 0; y < dstH; y++)
                {
                    float srcY = cropY + (float)y / scale;
                    int y0 = (int)Math.Floor(srcY);
                    int y1 = Math.Min(srcH - 1, y0 + 1);
                    float dy = srcY - y0;

                    byte* srcRow0 = srcData + srcLayout.StartIndex + srcLayout.Stride * y0;
                    byte* srcRow1 = srcData + srcLayout.StartIndex + srcLayout.Stride * y1;
                    byte* dstRow = dstData + dstLayout.StartIndex + dstLayout.Stride * y;

                    for (int x = 0; x < dstW; x++)
                    {
                        float srcX = cropX + (float)x / scale;
                        int x0 = (int)Math.Floor(srcX);
                        int x1 = Math.Min(srcW - 1, x0 + 1);
                        float dx = srcX - x0;

                        byte* p00 = srcRow0 + 4 * x0;
                        byte* p10 = srcRow0 + 4 * x1;
                        byte* p01 = srcRow1 + 4 * x0;
                        byte* p11 = srcRow1 + 4 * x1;

                        byte* dstPixel = dstRow + 4 * x;

                        for (int c = 0; c < 4; c++)
                        {
                            float val = (1 - dx) * (1 - dy) * p00[c] +
                                        dx * (1 - dy) * p10[c] +
                                        (1 - dx) * dy * p01[c] +
                                        dx * dy * p11[c];

                            dstPixel[c] = (byte)Math.Clamp(val, 0, 255);
                        }
                    }
                }
            }
            return dst;
        }

        private static unsafe void NeutralizeHighlightColumns(global::Windows.Graphics.Imaging.SoftwareBitmap bitmap, int startY)
        {
            using (global::Windows.Graphics.Imaging.BitmapBuffer buffer = bitmap.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.ReadWrite))
            using (global::Windows.Foundation.IMemoryBufferReference reference = buffer.CreateReference())
            {
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                byteAccess.GetBuffer(out byte* data, out uint capacity);
                global::Windows.Graphics.Imaging.BitmapPlaneDescription layout = buffer.GetPlaneDescription(0);

                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;

                int highlightLeft = -1;
                int highlightRight = -1;

                for (int x = 0; x < width; x++)
                {
                    int blueCount = 0;
                    for (int y = startY; y < height; y++)
                    {
                        int rowOffset = layout.StartIndex + layout.Stride * y;
                        int pxIdx = rowOffset + 4 * x;
                        byte b = data[pxIdx + 0];
                        byte g = data[pxIdx + 1];
                        byte r = data[pxIdx + 2];

                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));

                        if (max - min > 15 && min > 120 && b > r + 15)
                        {
                            blueCount++;
                        }
                    }
                    if (blueCount >= 15)
                    {
                        if (highlightLeft == -1) highlightLeft = x;
                        highlightRight = x;
                    }
                }

                if (highlightLeft == -1 || highlightRight == -1)
                {
                    Classes.Logger.LogAction("TABLE_EXTRACT", "No highlight detected for neutralization.");
                    return;
                }
                Classes.Logger.LogAction("TABLE_EXTRACT", $"Highlight Columns Range: [{highlightLeft}, {highlightRight}]");

                int margin = 5;
                int leftBoundary = Math.Max(0, highlightLeft - margin);
                int rightBoundary = Math.Min(width - 1, highlightRight + margin);

                for (int y = startY; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = leftBoundary; x <= rightBoundary; x++)
                    {
                        int pxIdx = rowOffset + 4 * x;
                        byte b = data[pxIdx + 0];
                        byte g = data[pxIdx + 1];
                        byte r = data[pxIdx + 2];

                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));

                        int gray = (int)(0.299 * r + 0.587 * g + 0.114 * b);

                        if (max - min > 12 && min > 110 && b > r + 12 && gray > 180)
                        {
                            data[pxIdx + 0] = 255;
                            data[pxIdx + 1] = 255;
                            data[pxIdx + 2] = 255;
                        }
                    }
                }
            }
        }

        private static unsafe void EraseDetectedGridLinesPrecise2x(
            global::Windows.Graphics.Imaging.SoftwareBitmap raw2x,
            List<int> cols2x,
            List<int> rows2x)
        {
            int width = raw2x.PixelWidth;
            int height = raw2x.PixelHeight;

            int vertStartY = rows2x.Count > 2 ? rows2x[2] : (rows2x.Count > 1 ? rows2x[1] : 0);
            int horizStartY = rows2x.Count > 3 ? rows2x[3] : (rows2x.Count > 2 ? rows2x[2] : 0);

            bool[,] isDark = new bool[width, height];
            using (var buffer = raw2x.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.Read))
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
                        int pxIdx = rowOffset + 4 * x;
                        byte b = data[pxIdx + 0];
                        byte g = data[pxIdx + 1];
                        byte r = data[pxIdx + 2];
                        
                        int gray = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                        isDark[x, y] = gray < 130;
                    }
                }
            }

            bool[,] toRemove = new bool[width, height];

            // Mark vertical lines for removal, preserving horizontal character crossings, only for Y >= vertStartY
            foreach (var col in cols2x)
            {
                int startX = Math.Max(0, col - 2);
                int endX = Math.Min(width - 1, col + 2);

                for (int y = vertStartY; y < height; y++)
                {
                    for (int x = startX; x <= endX; x++)
                    {
                        if (isDark[x, y])
                        {
                            int hRun = GetHorizontalRunLength(isDark, x, y, width);
                            int vRun = GetVerticalRunLength(isDark, x, y, height);
                            if (hRun >= 4 && hRun < 70 && vRun < 35)
                            {
                                continue;
                            }
                        }
                        toRemove[x, y] = true;
                    }
                }
            }

            // Mark horizontal lines for removal, preserving vertical character crossings, only for row >= horizStartY
            foreach (var row in rows2x)
            {
                if (row < horizStartY) continue;

                int startY_row = Math.Max(0, row - 2);
                int endY_row = Math.Min(height - 1, row + 2);

                for (int y = startY_row; y <= endY_row; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (isDark[x, y])
                        {
                            int hRun = GetHorizontalRunLength(isDark, x, y, width);
                            int vRun = GetVerticalRunLength(isDark, x, y, height);
                            if (vRun >= 4 && vRun < 70 && hRun < 35)
                            {
                                continue;
                            }
                        }
                        toRemove[x, y] = true;
                    }
                }
            }

            using (var buffer = raw2x.LockBuffer(global::Windows.Graphics.Imaging.BitmapBufferAccessMode.ReadWrite))
            using (var reference = buffer.CreateReference())
            {
                var byteAccess = WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference);
                byteAccess.GetBuffer(out byte* data, out uint capacity);
                var layout = buffer.GetPlaneDescription(0);

                for (int y = vertStartY; y < height; y++)
                {
                    int rowOffset = layout.StartIndex + layout.Stride * y;
                    for (int x = 0; x < width; x++)
                    {
                        if (toRemove[x, y])
                        {
                            int pxIdx = rowOffset + 4 * x;
                            data[pxIdx + 0] = 255;
                            data[pxIdx + 1] = 255;
                            data[pxIdx + 2] = 255;
                            data[pxIdx + 3] = 255;
                        }
                    }
                }
            }
        }

        private static async System.Threading.Tasks.Task SaveSoftwareBitmapToFileAsync(global::Windows.Graphics.Imaging.SoftwareBitmap softwareBitmap, string filePath)
        {
            try
            {
                global::Windows.Graphics.Imaging.SoftwareBitmap bitmapToSave = softwareBitmap;
                bool needsDispose = false;

                // PNG and JPEG encoders support Bgra8/Rgba8.
                if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 &&
                    softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8)
                {
                    bitmapToSave = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                        softwareBitmap,
                        global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight);
                    needsDispose = true;
                }

                string ext = System.IO.Path.GetExtension(filePath).ToLower();
                Guid encoderId = global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId;
                if (ext == ".jpg" || ext == ".jpeg")
                {
                    encoderId = global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId;
                }
                else if (ext == ".bmp")
                {
                    encoderId = global::Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId;
                }

                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var randomAccessStream = fs.AsRandomAccessStream();
                    var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(encoderId, randomAccessStream);
                    encoder.SetSoftwareBitmap(bitmapToSave);
                    await encoder.FlushAsync();
                }

                if (needsDispose)
                {
                    bitmapToSave.Dispose();
                }

                Classes.Logger.LogAction("TABLE_EXTRACT", $"Successfully saved modified SoftwareBitmap to {filePath}");
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("TABLE_EXTRACT_WARN", $"Failed to save SoftwareBitmap to {filePath}: {ex.Message}");
            }
        }
    }
}
