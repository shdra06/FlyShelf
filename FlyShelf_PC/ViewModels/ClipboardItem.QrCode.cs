// ---------------------------------------------------------------
// ClipboardItem — QR Code, Google Search & PDF-to-Word
// ScanForQRCodeAsync, GoogleSearch, ConvertPdfToWordTask, ManualScanQRCode
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
