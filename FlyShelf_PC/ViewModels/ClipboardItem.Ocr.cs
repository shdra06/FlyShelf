// ---------------------------------------------------------------
// ClipboardItem — OCR Text Extraction
// ExtractText, ScanForOcrTextAsync
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

                                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;

                                    if (mainWin != null)

                                    {

                                        mainWin.ShowQuickLookForItem(this, result);

                                    }

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




    }
}
