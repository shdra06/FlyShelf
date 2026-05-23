// ═══════════════════════════════════════════════════════════════



// ClipboardItem — Actions & Conversions



// Execute, Sandbox, Terminal, Document/Image conversion, OCR, QR



// Split from ClipboardItem.cs for modularity (<500 lines)



// ═══════════════════════════════════════════════════════════════



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



        public void OpenSandbox()



        {



            try



            {



                if (ItemType != ClipboardItemType.Code) return;



                



                // Do not block execution if FilePath is populated and RawContent is explicitly empty 



                if (string.IsNullOrEmpty(RawContent) && string.IsNullOrEmpty(FilePath)) return;



                string sandboxDir;



                string fullPath;



                // [PATH REMEMBRANCE]: Validate if the copied sequence is a physical HDD File natively!



                if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))



                {



                    sandboxDir = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();



                    fullPath = FilePath;



                }



                else



                {



                    // Fallback to anonymous Temp Storage explicitly for Text Blocks dragged natively from Non-Path Apps 



                    sandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox", Guid.NewGuid().ToString().Substring(0, 6));



                    Directory.CreateDirectory(sandboxDir);



                    



                    string filename = string.IsNullOrEmpty(FileName) ? "snippet.txt" : FileName;



                    fullPath = Path.Combine(sandboxDir, filename);



                    



                    File.WriteAllText(fullPath, RawContent);



                }



                var startInfo = new ProcessStartInfo



                {



                    FileName = "cmd.exe",



                    Arguments = $"/C code \"{sandboxDir}\" \"{fullPath}\"",



                    UseShellExecute = false,



                    CreateNoWindow = true



                };



                FlyShelf.Classes.Logger.LogAction("SANDBOX EXECUTION", $"Launching VS Code payload. Target: {fullPath}");



                Process.Start(startInfo);



            }



            catch (Exception ex)



            {



                FlyShelf.Classes.Logger.LogAction("DEBUG", $"Sandbox Launch Failed: {ex.Message}");



            }



        }



        public void RunInTerminal()



        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Terminal execution is not available in the Store version.");
            return;
#else



            try



            {



                if (string.IsNullOrEmpty(RawContent) && string.IsNullOrEmpty(FilePath)) return;



                bool isPhysicalScript = !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);



                System.Windows.MessageBoxResult result = System.Windows.MessageBoxResult.Yes;



                if (!isPhysicalScript)



                {



                    result = System.Windows.MessageBox.Show(



                        "You are about to execute raw clipboard text directly in your native Command Prompt.\n\n" +



                        "Are you absolutely sure you want to run this command? Malicious scripts can heavily damage your operating system:\n\n" +



                        (RawContent?.Length > 200 ? RawContent.Substring(0, 200) + "..." : RawContent),



                        "Security Warning: Terminal Hook Execution",



                        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);



                }



                if (result == System.Windows.MessageBoxResult.Yes)



                {



                    var startInfo = new ProcessStartInfo



                    {



                        FileName = "cmd.exe",



                        UseShellExecute = true,



                        CreateNoWindow = false



                    };



                    // [PATH REMEMBRANCE]: If it's a physical file, simply open configuring CMD exactly in its native folder directory!



                    if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))



                    {



                        startInfo.WorkingDirectory = Path.GetDirectoryName(FilePath) ?? "";



                        



                        // Dynamically Bootstrap the Engine based on Extension!



                        if (Extension == ".JS")



                            startInfo.Arguments = $"/k node \"{FileName}\"";



                        else if (Extension == ".PY")



                            startInfo.Arguments = $"/k python \"{FileName}\"";



                        else if (Extension == ".BAT" || Extension == ".CMD")



                            startInfo.Arguments = $"/c \"{FileName}\"";



                    }



                    else



                    {



                        // Fallback Behavior: Execute text blocks natively



                        startInfo.Arguments = $"/k {RawContent}";



                    }



                    FlyShelf.Classes.Logger.LogAction("TERMINAL EXECUTION", $"Spawned native command prompt. Args: {startInfo.Arguments} | WorkingDir: {startInfo.WorkingDirectory}");



                    Process.Start(startInfo);



                }



            }



            catch (Exception ex)



            {



                FlyShelf.Classes.Logger.LogAction("DEBUG", $"Terminal Hook Failed: {ex.Message}");



            }
#endif



        }



        public void OpenInBrowser()



        {



            try



            {



                if (IsUrlPreview && !string.IsNullOrEmpty(RawContent))



                {



                    Process.Start(new ProcessStartInfo { FileName = RawContent, UseShellExecute = true });



                }



            }



            catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("DEBUG", $"Browser Hook Failed: {ex.Message}"); }



        }



        public void RunAdminTerminal()



        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Elevated terminal is not available in the Store version.");
            return;
#else



            try



            {



                if (string.IsNullOrEmpty(FilePath)) return;



                var startInfo = new ProcessStartInfo



                {



                    FileName = Extension == ".PS1" ? "powershell.exe" : "cmd.exe",



                    Arguments = Extension == ".PS1" ? $"-NoExit -ExecutionPolicy RemoteSigned -File \"{FilePath}\"" : $"/k \"{FilePath}\"",



                    UseShellExecute = true,



                    Verb = "runas" // Forces UAC Admin Elevation intelligently!



                };



                Process.Start(startInfo);



            }



            catch (Exception ex)



            {



                System.Windows.MessageBox.Show($"Failed to launch elevated terminal: {ex.Message}", "FlyShelf OS Hook Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);



            }
#endif



        }



        public void CompileAndRunNative()



        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Code compilation is not available in the Store version.");
            return;
#else



            try



            {



                if (string.IsNullOrEmpty(FilePath) && string.IsNullOrEmpty(RawContent)) return;



                



                string sourceFile = FilePath;



                string exeDir = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();



                string exeName = Path.Combine(exeDir, Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(FilePath) ? "FlyShelfTempCompile" : FilePath) + ".exe");



                if (string.IsNullOrEmpty(FilePath))



                {



                    sourceFile = Path.Combine(Path.GetTempPath(), "FlyShelfRuntime_" + Guid.NewGuid().ToString().Substring(0, 4) + ".cpp");



                    File.WriteAllText(sourceFile, RawContent);



                    exeName = Path.Combine(Path.GetTempPath(), "FlyShelfRuntime.exe");



                }



                



                var startInfo = new ProcessStartInfo



                {



                    FileName = "cmd.exe",



                    Arguments = $"/k title FlyShelf C/C++ Compiler && echo [FlyShelf Engine] Executing g++ on payload... && g++ \"{sourceFile}\" -o \"{exeName}\" && echo ----------------------------------------- && \"{exeName}\"",



                    UseShellExecute = true,



                    CreateNoWindow = false



                };



                Process.Start(startInfo);



            }



            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Hardware Compiler Error"); }
#endif



        }



        public void ConvertDocumentTask()



        {



            System.Threading.Tasks.Task.Run(() =>



            {



                try



                {



                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;



                    



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                        FlyShelf.Windows.ToastWindow.ShowToast("Synthesizing Document Format natively... ♻️")



                    );



                    string targetPdf = Path.Combine(Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(), Path.GetFileNameWithoutExtension(FilePath) + "_Converted.pdf");



                    



                    // Native COM Script for high-fidelity conversion without python dependencies



                    string script = $"$word = New-Object -ComObject Word.Application; $word.Visible = $false; $doc = $word.Documents.Open('{FilePath}'); $doc.SaveAs([ref]'{targetPdf}', [ref]17); $doc.Close(); $word.Quit();";



                    



                    var startInfo = new ProcessStartInfo



                    {



                        FileName = "powershell.exe",



                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy RemoteSigned -Command \"{script}\"",



                        UseShellExecute = false,



                        CreateNoWindow = true



                    };



                    



                    using (var process = Process.Start(startInfo))



                    {



                        if (process != null)



                        {



                            process.WaitForExit(60000); // 60s timeout — prevent stuck Word COM from hanging forever
                            if (!process.HasExited)
                            {
                                try { process.Kill(); } catch { }
                                FlyShelf.Classes.Logger.LogAction("CONVERT_DOC", "Killed stuck Word process after 60s timeout");
                            }



                            if (File.Exists(targetPdf))



                            {



                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                                {



                                    // Drop the synthesized PDF back locally



                                    var dataObj = new System.Windows.DataObject();



                                    dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { targetPdf });



                                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;



                                    (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);



                                    



                                    FlyShelf.Windows.ToastWindow.ShowToast("Format Synthesized Successfully ✅");



                                });



                            }



                            else



                            {



                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                                    FlyShelf.Windows.ToastWindow.ShowToast("Synthesis Failed: Could not output file ❌")



                                );



                            }



                        }



                    }



                }



                catch (Exception ex)



                {



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                        FlyShelf.Windows.ToastWindow.ShowToast($"Synthesis Exception: {ex.Message} ❌")



                    );



                }



            });



        }



        /// <summary>



        /// Convert an image to a single-page PDF (A4 size). No external dependencies.



        /// Uses raw PDF specification writing with embedded JPEG stream.



        /// </summary>



        public void ConvertImageToPdf()



        {



            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;



            System.Threading.Tasks.Task.Run(() =>



            {



                try



                {



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                        FlyShelf.Windows.ToastWindow.ShowToast("Converting Image to PDF... 📄")



                    );



                    string outputPdf = Path.Combine(



                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),



                        Path.GetFileNameWithoutExtension(FilePath) + ".pdf");



                    // Load image to get dimensions



                    byte[] jpegBytes;



                    int imgWidth, imgHeight;



                    using (var bmp = new System.Drawing.Bitmap(FilePath))



                    {



                        imgWidth = bmp.Width;



                        imgHeight = bmp.Height;



                        // Convert to JPEG for PDF embedding



                        using (var ms = new MemoryStream())



                        {



                            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);



                            jpegBytes = ms.ToArray();



                        }



                    }



                    // A4 page size in points (72 dpi): 595.28 x 841.89



                    double pageW = 595.28, pageH = 841.89;



                    double margin = 36; // 0.5 inch margin



                    double usableW = pageW - 2 * margin;



                    double usableH = pageH - 2 * margin;



                    // Scale image to fit page while maintaining aspect ratio



                    double scale = Math.Min(usableW / imgWidth, usableH / imgHeight);



                    double drawW = imgWidth * scale;



                    double drawH = imgHeight * scale;



                    double drawX = margin + (usableW - drawW) / 2;



                    double drawY = margin + (usableH - drawH) / 2;



                    // Write a minimal valid PDF



                    using (var fs = new FileStream(outputPdf, FileMode.Create))



                    using (var writer = new StreamWriter(fs, System.Text.Encoding.ASCII))



                    {



                        var offsets = new List<long>();



                        writer.Write("%PDF-1.4\n");



                        writer.Flush();



                        offsets.Add(fs.Position);



                        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");



                        writer.Flush();



                        offsets.Add(fs.Position);



                        writer.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");



                        writer.Flush();



                        offsets.Add(fs.Position);



                        writer.Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW:F2} {pageH:F2}] /Contents 4 0 R /Resources << /XObject << /Img1 5 0 R >> >> >>\nendobj\n");



                        writer.Flush();



                        string contentStream = $"q\n{drawW:F2} 0 0 {drawH:F2} {drawX:F2} {drawY:F2} cm\n/Img1 Do\nQ\n";



                        offsets.Add(fs.Position);



                        writer.Write($"4 0 obj\n<< /Length {contentStream.Length} >>\nstream\n{contentStream}endstream\nendobj\n");



                        writer.Flush();



                        offsets.Add(fs.Position);



                        writer.Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgWidth} /Height {imgHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegBytes.Length} >>\nstream\n");



                        writer.Flush();



                        fs.Write(jpegBytes, 0, jpegBytes.Length);



                        writer.Write("\nendstream\nendobj\n");



                        writer.Flush();



                        long xrefOffset = fs.Position;



                        writer.Write($"xref\n0 {offsets.Count + 1}\n");



                        writer.Write("0000000000 65535 f \n");



                        foreach (var off in offsets)



                            writer.Write($"{off:D10} 00000 n \n");



                        writer.Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");



                        writer.Flush();



                    }



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                    {



                        var dataObj = new System.Windows.DataObject();



                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPdf });



                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;



                        (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);



                        FlyShelf.Windows.ToastWindow.ShowToast($"Image → PDF converted! ✅ {Path.GetFileName(outputPdf)}");



                    });



                }



                catch (Exception ex)



                {



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                        FlyShelf.Windows.ToastWindow.ShowToast($"Image→PDF failed: {ex.Message} ❌")



                    );



                }



            });



        }



                public async void ExtractText()

        {

            if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;



            try

            {

                FlyShelf.Windows.ToastWindow.ShowToast("Scanning Native Hardware OCR...");



                await Task.Run(async () => 

                {

                    using (var stream = File.OpenRead(FilePath))

                    {

                        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                        

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

                                    try

                                    {

                                        FlyShelf.MainWindow.SetWritingClipboard(true);

                                        System.Windows.Clipboard.SetText(result.Text);

                                    }

                                    catch { }

                                    _ = System.Threading.Tasks.Task.Run(async () =>

                                    {

                                        await System.Threading.Tasks.Task.Delay(500);

                                        FlyShelf.MainWindow.SetWritingClipboard(false);

                                    });

                                    FlyShelf.Windows.ToastWindow.ShowToast("OCR Text Copied to Clipboard! 📋");

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

            System.Threading.Tasks.Task.Run(async () =>

            {

                try

                {

                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;



                    using (var stream = File.OpenRead(path))

                    {

                        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();



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



        public async void ExtractTable()



        {



            try



            {



                if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;



                FlyShelf.Windows.ToastWindow.ShowToast("Extracting Table from Image... ⏳");



                string finalJsonPayload = string.Empty;



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



                                        double avgHeight = sorted.Average(w => w.H);



                                        double rowThreshold = avgHeight * 0.7;



                                        



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



                                                .Where(s => allGaps.Count(g => Math.Abs(g.Center - s) < clusterDist) >= Math.Max(2, rows.Count * 0.3))



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



                        Classes.Logger.LogAction("TABLE_EXTRACT", "Gemini AI extracted table successfully");



                    }



                });



                if (!string.IsNullOrWhiteSpace(finalJsonPayload) && finalJsonPayload.StartsWith("{"))



                {



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 



                    {



                        var editor = new FlyShelf.Windows.TableEditorWindow(finalJsonPayload);



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



        public void ScanForQRCodeAsync(string path)

        {

            System.Threading.Tasks.Task.Run(() => {

                try {

                    using (var bmp = new System.Drawing.Bitmap(path))

                    {

                        var reader = new ZXing.Windows.Compatibility.BarcodeReader();

                        reader.Options.TryHarder = true;

                        reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.DATA_MATRIX };

                        var result = reader.Decode(bmp);

                        if (result != null && !string.IsNullOrWhiteSpace(result.Text)) {

                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {

                                // Update item type and content and automatically copy to clipboard!

                                this.ItemType = ClipboardItemType.QRCode;

                                this.RawContent = result.Text;

                                this.EvaluateSmartActions();

                                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("ItemType"));

                                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("RawContent"));

                                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("IsImagePreview"));



                                // Suppress toast and auto-copy for FlyShelf's own pairing QR codes

                                if (result.Text.Contains("\"app\":\"FlyShelf\""))

                                {

                                    Classes.Logger.LogAction("QR_SCAN", "Detected FlyShelf pairing QR  suppressed toast");

                                    return;

                                }



                                try

                                {

                                    FlyShelf.MainWindow.SetWritingClipboard(true);

                                    System.Windows.Clipboard.SetText(result.Text);

                                }

                                catch { }

                                _ = System.Threading.Tasks.Task.Run(async () =>

                                {

                                    await System.Threading.Tasks.Task.Delay(500);

                                    FlyShelf.MainWindow.SetWritingClipboard(false);

                                });



                                FlyShelf.Windows.ToastWindow.ShowToast("QR Code Extracted & Copied! 📋");

                            });

                        }

                    }

                } catch (Exception ex) { Classes.Logger.LogAction("QR_SCAN", $"Scan failed: {ex.Message}"); }

            });

        }



        public void GoogleSearch()



        {



            try



            {



                if (string.IsNullOrEmpty(RawContent)) return;



                string query = Uri.EscapeDataString(RawContent);



                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo



                {



                    FileName = $"https://www.google.com/search?q={query}",



                    UseShellExecute = true



                });



            }



            catch (Exception ex)



            {



                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                    FlyShelf.Windows.ToastWindow.ShowToast($"Search Error: {ex.Message}")



                );



            }



        }



        public void ConvertPdfToWordTask()



        {



            System.Threading.Tasks.Task.Run(() =>



            {



                try



                {



                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                        FlyShelf.Windows.ToastWindow.ShowToast("📄 Converting PDF to Word...")



                    );



                    string outputPath = Path.Combine(



                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),



                        Path.GetFileNameWithoutExtension(FilePath) + "_Converted.docx");



                    // Use Word COM to open PDF and save as DOCX (Word 2013+ supports this natively)



                    string script = $@"



$word = New-Object -ComObject Word.Application



$word.Visible = $false



$doc = $word.Documents.Open('{FilePath.Replace("'", "''")}')



$doc.SaveAs([ref]'{outputPath.Replace("'", "''")}', [ref]16)



$doc.Close()



$word.Quit();



";



                    var psi = new System.Diagnostics.ProcessStartInfo



                    {



                        FileName = "powershell.exe",



                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy RemoteSigned -Command \"{script}\"",



                        CreateNoWindow = true,



                        UseShellExecute = false



                    };



                    using (var process = Process.Start(psi))



                    {



                        process?.WaitForExit(60000);
                        if (process != null && !process.HasExited)
                        {
                            try { process.Kill(); } catch { }
                            FlyShelf.Classes.Logger.LogAction("PDF2WORD", "Killed stuck conversion process after 60s timeout");
                        }



                    }



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                    {



                        if (File.Exists(outputPath))



                        {



                            var dataObj = new System.Windows.DataObject();



                            dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPath });



                            var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;



                            var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;



                            vm?.HandleDrop(dataObj, true);



                            // Open containing folder with the file selected



                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\"");



                            FlyShelf.Windows.ToastWindow.ShowToast($"✅ Converted: {Path.GetFileName(outputPath)}");



                        }



                        else



                        {



                            FlyShelf.Windows.ToastWindow.ShowToast("❌ Conversion failed — Microsoft Word required");



                        }



                    });



                }



                catch (Exception ex)



                {



                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>



                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ PDF to Word error: {ex.Message}")



                    );



                }



            });



        }

        public void ManualScanQRCode()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath) || !System.IO.File.Exists(FilePath))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No image file found to scan");
                    return;
                }

                using (var bmp = new System.Drawing.Bitmap(FilePath))
                {
                    var reader = new ZXing.Windows.Compatibility.BarcodeReader();
                    reader.Options.TryHarder = true;
                    reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.DATA_MATRIX };
                    var result = reader.Decode(bmp);
                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                            this.ItemType = ClipboardItemType.QRCode;
                            this.RawContent = result.Text;
                            this.EvaluateSmartActions();
                            this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("ItemType"));
                            this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("RawContent"));
                            this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("IsImagePreview"));

                            try
                            {
                                FlyShelf.MainWindow.SetWritingClipboard(true);
                                System.Windows.Clipboard.SetText(result.Text);
                            }
                            catch { }
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(500);
                                FlyShelf.MainWindow.SetWritingClipboard(false);
                            });

                            FlyShelf.Windows.ToastWindow.ShowToast("QR Code Extracted & Copied! 📋");
                        });
                    }
                    else
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("No QR Code detected in image 🔍");
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("QR_MANUAL_SCAN_FAIL", ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast("Error scanning QR Code");
            }
        }

    }

}

