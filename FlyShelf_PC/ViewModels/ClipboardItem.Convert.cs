// ---------------------------------------------------------------
// ClipboardItem — Document & Image Conversion
// ConvertDocumentTask, ConvertImageToPdf
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




    }
}
