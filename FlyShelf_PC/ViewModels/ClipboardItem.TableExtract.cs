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



                    // ═══ PHASE 1: Windows Native OCR + Smart Grid Detection ═══



                    try



                    {



                        using (var stream = File.OpenRead(FilePath))



                        {



                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());



                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();



                            



                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(



                                new global::Windows.Globalization.Language("en-US"));



                            



                            if (ocrEngine == null)



                            {



                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();



                            }



                            



                            if (ocrEngine == null)



                            {



                                Classes.Logger.LogAction("TABLE_OCR", "No OCR engine available � install English language pack");



                            }



                            



                            if (ocrEngine != null)



                            {



                                var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);



                                



                                if (ocrResult != null && ocrResult.Lines.Count >= 2)



                                {



                                    // Collect all words with their bounding boxes



                                    var allWords = new List<(string Text, double X, double Y, double W, double H, double Right)>();



                                    foreach (var line in ocrResult.Lines)



                                    {



                                        foreach (var word in line.Words)



                                        {



                                            var rect = word.BoundingRect;



                                            allWords.Add((word.Text, rect.X, rect.Y, rect.Width, rect.Height, rect.X + rect.Width));



                                        }



                                    }



                                    



                                    Classes.Logger.LogAction("TABLE_OCR", $"Found {allWords.Count} words in {ocrResult.Lines.Count} lines");



                                    if (allWords.Count >= 4)



                                    {



                                        // ── STEP 1: Group words into rows by Y-coordinate ──



                                        var sorted = allWords.OrderBy(w => w.Y).ToList();



                                        // Use median height for outlier-resistant row detection
                                        var heights = sorted.Select(w => w.H).OrderBy(h => h).ToList();
                                        double medianHeight = heights[heights.Count / 2];



                                        double rowThreshold = medianHeight * 0.7;



                                        



                                        var rows = new List<List<(string Text, double X, double W, double Right)>>();



                                        var currentRow = new List<(string Text, double X, double W, double Right)>();



                                        double lastY = sorted[0].Y;



                                        



                                        foreach (var word in sorted)



                                        {



                                            if (Math.Abs(word.Y - lastY) > rowThreshold && currentRow.Count > 0)



                                            {



                                                rows.Add(currentRow.OrderBy(w => w.X).ToList());



                                                currentRow = new List<(string Text, double X, double W, double Right)>();



                                            }



                                            currentRow.Add((word.Text, word.X, word.W, word.Right));



                                            lastY = word.Y;



                                        }



                                        if (currentRow.Count > 0)



                                            rows.Add(currentRow.OrderBy(w => w.X).ToList());



                                        



                                        if (rows.Count >= 2)



                                        {



                                            // ── STEP 2: Detect column separators via gap clustering ──



                                            double avgW = allWords.Average(w => w.W);



                                            double minGap = avgW * 1.2;



                                            



                                            var allGaps = new List<(double Center, double Size)>();



                                            foreach (var row in rows)



                                                for (int gi = 0; gi < row.Count - 1; gi++)



                                                {



                                                    double gap = row[gi + 1].X - row[gi].Right;



                                                    if (gap > minGap)



                                                        allGaps.Add(((row[gi].Right + row[gi + 1].X) / 2.0, gap));



                                                }



                                            



                                            var separators = new List<double>();



                                            double clusterDist = avgW * 2.0;



                                            foreach (var g in allGaps.OrderBy(g => g.Center))



                                            {



                                                bool merged = false;



                                                for (int si = 0; si < separators.Count; si++)



                                                    if (Math.Abs(g.Center - separators[si]) < clusterDist)



                                                    { separators[si] = (separators[si] + g.Center) / 2.0; merged = true; break; }



                                                if (!merged) separators.Add(g.Center);



                                            }



                                            



                                            separators = separators



                                                .Where(s => allGaps.Count(g => Math.Abs(g.Center - s) < clusterDist) >= Math.Max(2, rows.Count * 0.4))



                                                .OrderBy(s => s).ToList();



                                            int numCols = separators.Count + 1;



                                            



                                            if (numCols >= 2)



                                            {



                                                var jsonDict = new Dictionary<string, object>();



                                                for (int ri = 0; ri < rows.Count; ri++)



                                                {



                                                    var buckets = new string[numCols];



                                                    for (int c = 0; c < numCols; c++) buckets[c] = "";



                                                    foreach (var word in rows[ri])



                                                    {



                                                        double wc = word.X + word.W / 2.0;



                                                        int col = 0;



                                                        for (int si = 0; si < separators.Count; si++)



                                                        { if (wc > separators[si]) col = si + 1; else break; }



                                                        if (col >= numCols) col = numCols - 1;



                                                        buckets[col] += (buckets[col].Length > 0 ? " " : "") + word.Text;



                                                    }



                                                    for (int ci = 0; ci < numCols; ci++)



                                                        jsonDict[$"({ri},{ci})"] = new { text = buckets[ci].Trim(), conf = 0.90 };



                                                }



                                                finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);
                                                extractionMethod = "OCR";



                                                Classes.Logger.LogAction("TABLE_EXTRACT", $"OCR: {rows.Count}x{numCols} table ({separators.Count} separators)");



                                            }



                                        }



                                    }



                                }



                            }



                        }



                    }



                    catch (Exception ocrEx)



                    {



                        Classes.Logger.LogAction("TABLE_OCR_FAIL", ocrEx.Message);



                    }



                    // ═══ PHASE 2: Gemini AI Fallback (if OCR failed or detected no table) ═══



                    if (string.IsNullOrWhiteSpace(finalJsonPayload) || !finalJsonPayload.StartsWith("{"))



                    {



                        string apiKey = FlyShelf.Classes.SettingsManager.Current.GeminiApiKey;



                        if (string.IsNullOrWhiteSpace(apiKey))



                        {



                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                                FlyShelf.Windows.ToastWindow.ShowToast("OCR couldn't detect table structure. Set Gemini API Key in Settings for AI fallback.")



                            );



                            return;



                        }



                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                            FlyShelf.Windows.ToastWindow.ShowToast("OCR inconclusive. Using Gemini AI for table extraction...")



                        );



                        



                        finalJsonPayload = await FlyShelf.Classes.GeminiEngine.ExtractFormattedTableFromImageAsync(FilePath, apiKey);
                        extractionMethod = "Gemini";



                        Classes.Logger.LogAction("TABLE_EXTRACT", "Gemini AI extracted table successfully");



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





    }
}
