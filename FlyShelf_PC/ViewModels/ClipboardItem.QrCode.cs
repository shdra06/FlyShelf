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
            if (!FlyShelf.Classes.LicenseManager.CanScanQr()) return;

            System.Threading.Tasks.Task.Run(() => {

                try {

                    using (var bmp = new System.Drawing.Bitmap(path))

                    {

                        var reader = new ZXing.Windows.Compatibility.BarcodeReader();

                        reader.Options.TryHarder = true;

                        reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.DATA_MATRIX };

                        var result = reader.Decode(bmp);

                        if (result != null && !string.IsNullOrWhiteSpace(result.Text)) {

                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => {

                                // Update item type and content and automatically copy to clipboard!

                                this.ItemType = ClipboardItemType.QRCode;

                                this.RawContent = result.Text;

                                this.EvaluateSmartActions();

                                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ItemType)));

                                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RawContent)));

                                this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsImagePreview)));



                                // Suppress toast and auto-copy for FlyShelf's own pairing QR codes

                                if (result.Text.Contains("\"app\":\"FlyShelf\"", StringComparison.Ordinal))

                                {

                                    Classes.Logger.LogAction("QR_SCAN", "Detected FlyShelf pairing QR  suppressed toast");

                                    return;

                                }



                                FlyShelf.Classes.ClipboardHelper.SafeSetText(result.Text, suppressEcho: true, echoDelayMs: 500);



                                FlyShelf.Windows.ToastWindow.ShowToast("QR Code Extracted & Copied!");
                                FlyShelf.Classes.LicenseManager.RecordQrScan();

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



                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>



                    FlyShelf.Windows.ToastWindow.ShowToast($"Search Error: {ex.Message}")



                );



            }



        }



        public void ConvertPdfToWordTask()
        {
#if MSIX_STORE
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                FlyShelf.Windows.ToastWindow.ShowToast("PDF to Word conversion is not available in the Store version."));
            return;
#else
            if (!FlyShelf.Classes.LicenseManager.CanConvertDoc())
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    FlyShelf.Classes.UpgradePrompt.ShowDocConvertLimit());
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;

                    string outputPath = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + $"_Converted_{DateTime.Now:yyyyMMdd_HHmmss}.docx");

                    bool converted = false;

                    // ═══════════════════════════════════════════════════════
                    // STRATEGY 1: Word COM — fully silent, best quality
                    // Word converts PDF→DOCX natively with all dialogs
                    // suppressed. No user interaction needed.
                    // ═══════════════════════════════════════════════════════
                    if (Type.GetTypeFromProgID("Word.Application") != null)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Converting PDF to Word (via Word)...")
                        );

                        // PowerShell script with auto-dialog dismissal for Word's PDF conversion prompt
                        string script = $@"
# ═══ Auto-dismiss Word's PDF conversion dialog ═══
# Word shows a modal dialog for PDF→DOCX that can't be suppressed via COM settings.
# We spawn a background job that polls for the dialog and clicks OK automatically.
$dialogJob = Start-Job -ScriptBlock {{
    Add-Type @'
    using System;
    using System.Runtime.InteropServices;
    public class WinApi {{
        [DllImport(""user32.dll"", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport(""user32.dll"")]
        public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
        [DllImport(""user32.dll"")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport(""user32.dll"", CharSet = CharSet.Auto)]
        public static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);
        public const uint BM_CLICK = 0x00F5;
    }}
'@
    for ($i = 0; $i -lt 60; $i++) {{
        Start-Sleep -Milliseconds 500
        # Look for Word's dialog by title 'Microsoft Word'
        $hwnd = [WinApi]::FindWindow('#32770', 'Microsoft Word')
        if ($hwnd -ne [IntPtr]::Zero) {{
            # Find the OK button (Button class, text 'OK' or '&OK')
            $okBtn = [WinApi]::FindWindowEx($hwnd, [IntPtr]::Zero, 'Button', 'OK')
            if ($okBtn -eq [IntPtr]::Zero) {{
                $okBtn = [WinApi]::FindWindowEx($hwnd, [IntPtr]::Zero, 'Button', '&OK')
            }}
            if ($okBtn -ne [IntPtr]::Zero) {{
                [WinApi]::SetForegroundWindow($hwnd)
                [WinApi]::SendMessage($okBtn, [WinApi]::BM_CLICK, 0, 0)
                break
            }}
        }}
    }}
}}

$word = New-Object -ComObject Word.Application
$word.Visible = $false
$word.DisplayAlerts = 0
$word.AutomationSecurity = 3
$word.Options.DoNotPromptForConvert = $true
$word.Options.ConfirmConversions = $false
try {{
    # ConfirmConversions=$false (2nd param) suppresses most dialogs
    # but Word's PDF conversion dialog is special — the background job handles it
    $doc = $word.Documents.Open('{FilePath.Replace("'", "''")}', $false, $false, $false)
    $doc.SaveAs([ref]'{outputPath.Replace("'", "''")}', [ref]16)
    $doc.Close([ref]0)
}} catch {{
    # Conversion failed — let the native fallback handle it
}} finally {{
    $word.Quit([ref]0)
    [System.Runtime.Interopservices.Marshal]::ReleaseComObject($word) | Out-Null
    Stop-Job $dialogJob -ErrorAction SilentlyContinue
    Remove-Job $dialogJob -Force -ErrorAction SilentlyContinue
}}
";
                        // [SECURITY]: Write script to temp file (prevent injection via FilePath)
                        string scriptPath = Path.Combine(Path.GetTempPath(), $"flyshelf_convert_{Guid.NewGuid():N}.ps1");
                        File.WriteAllText(scriptPath, script);

                        var psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File \"{scriptPath}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };

                        using (var process = Process.Start(psi))
                        {
                            // 60 second timeout — includes dialog dismissal time + Word processing
                            process?.WaitForExit(60000);
                            if (process != null && !process.HasExited)
                            {
                                try { process.Kill(true); } catch { }
                                Classes.Logger.LogAction("PDF2WORD", "Word COM timed out (60s) — falling through to native converter");
                            }
                        }

                        try { File.Delete(scriptPath); } catch { }

                        // Check if Word actually produced a valid file
                        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
                        {
                            converted = true;
                            Classes.Logger.LogAction("PDF2WORD", "Converted via Word COM successfully");
                        }
                    }

                    // ═══════════════════════════════════════════════════════
                    // STRATEGY 2: Native PdfPig + OpenXML — no dependencies
                    // Runs if: Word not installed, user ignored dialog, or
                    // Word failed to produce a valid file.
                    // ═══════════════════════════════════════════════════════
                    if (!converted)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast("Converting PDF to Word (native)...")
                        );

                        // Delete any partial Word output before native attempt
                        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }

                        converted = FlyShelf.Classes.PdfToWordConverter.Convert(FilePath, outputPath);

                        if (converted)
                            Classes.Logger.LogAction("PDF2WORD", "Converted via native PdfPig+OpenXML");
                    }

                    // ═══════════════════════════════════════════════════════
                    // STRATEGY 3: LibreOffice headless — last resort fallback
                    // ═══════════════════════════════════════════════════════
                    if (!converted)
                    {
                        // [FIX C4]: Use shared LibreOffice path resolver instead of hardcoded paths
                        string loPath = GetLibreOfficePath();

                        if (loPath != null)
                        {
                            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                                FlyShelf.Windows.ToastWindow.ShowToast("Converting via LibreOffice...")
                            );

                            string outDir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
                            var loPsi = new ProcessStartInfo
                            {
                                FileName = loPath,
                                Arguments = $"--headless --norestore --convert-to docx --outdir \"{outDir}\" \"{FilePath}\"",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };

                            using var loProcess = Process.Start(loPsi);
                            loProcess?.WaitForExit(30000);
                            if (loProcess != null && !loProcess.HasExited)
                            {
                                try { loProcess.Kill(); } catch { }
                            }

                            // LibreOffice generates output with original name + .docx
                            string loOutput = Path.Combine(outDir,
                                Path.GetFileNameWithoutExtension(FilePath) + ".docx");
                            if (File.Exists(loOutput) && new FileInfo(loOutput).Length > 100)
                            {
                                // Rename to our expected output path
                                if (loOutput != outputPath)
                                {
                                    try { File.Move(loOutput, outputPath, true); } catch { }
                                }
                                converted = true;
                                Classes.Logger.LogAction("PDF2WORD", "Converted via LibreOffice headless");
                            }
                        }
                    }

                    // ═══════════════════════════════════════════════════════
                    // RESULT — drop into clipboard + open in explorer
                    // ═══════════════════════════════════════════════════════
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        if (converted && File.Exists(outputPath))
                        {
                            var dataObj = new System.Windows.DataObject();
                            dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPath });
                            var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                            var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                            vm?.HandleDrop(dataObj, true);

                            Process.Start("explorer.exe", $"/select,\"{outputPath}\"");

                            FlyShelf.Windows.ToastWindow.ShowToast($"Converted: {Path.GetFileName(outputPath)}");
                            FlyShelf.Classes.LicenseManager.RecordDocConversion();
                        }
                        else
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast("Conversion failed — no converter available");
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        FlyShelf.Windows.ToastWindow.ShowToast($"PDF to Word error: {ex.Message}")
                    );
                }
            });
#endif
        }

        public void ManualScanQRCode()
        {
            if (!FlyShelf.Classes.LicenseManager.CanScanQr())
            {
                FlyShelf.Classes.UpgradePrompt.ShowQrScanLimit();
                return;
            }

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
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() => {
                            this.ItemType = ClipboardItemType.QRCode;
                            this.RawContent = result.Text;
                            this.EvaluateSmartActions();
                            this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ItemType)));
                            this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RawContent)));
                            this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsImagePreview)));

                            FlyShelf.Classes.ClipboardHelper.SafeSetText(result.Text, suppressEcho: true, echoDelayMs: 500);

                            FlyShelf.Windows.ToastWindow.ShowToast("QR Code Extracted & Copied!");
                            FlyShelf.Classes.LicenseManager.RecordQrScan();
                        });
                    }
                    else
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("No QR Code detected in image");
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
