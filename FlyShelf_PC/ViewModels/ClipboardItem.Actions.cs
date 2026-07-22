using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace FlyShelf.ViewModels
{
    public partial class ClipboardItem
    {

        public void OpenSandbox()
        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸ Code sandbox is not available in the Store version. Download the full version from https://fly-shelf.vercel.app/");
            return;
#else



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



                    // [SECURITY FIX v2.1.0]: Use full GUID to prevent predictable temp directory names (CWE-377)
                    sandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox", Guid.NewGuid().ToString("N"));



                    Directory.CreateDirectory(sandboxDir);



                    



                    string filename = string.IsNullOrEmpty(FileName) ? "snippet.txt" : FileName;



                    fullPath = Path.Combine(sandboxDir, filename);



                    



                    File.WriteAllText(fullPath, RawContent);



                }



                // [SECURITY FIX v2.1.0]: Launch VS Code directly instead of via cmd.exe
                // to prevent shell metacharacter injection in file paths (CWE-78)
                var startInfo = new ProcessStartInfo



                {



                    FileName = "code",



                    ArgumentList = { sandboxDir, fullPath },



                    UseShellExecute = false,



                    CreateNoWindow = true



                };



                FlyShelf.Classes.Logger.LogAction("SANDBOX EXECUTION", $"Launching VS Code payload. Target: {fullPath}");



                _ = Task.Run(() => { try { Process.Start(startInfo); } catch { } });



            }



            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("DEBUG", $"Sandbox Launch Failed: {ex.Message}");
            }
#endif
        }



        public void RunInTerminal()



        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸ Terminal execution is not available in the Store version.");
            return;
#else



            try



            {



                if (string.IsNullOrEmpty(RawContent) && string.IsNullOrEmpty(FilePath)) return;



                bool isPhysicalScript = !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);



                System.Windows.MessageBoxResult result = System.Windows.MessageBoxResult.Yes;



                if (!isPhysicalScript)



                {



                    // Truncate preview to prevent the MessageBox from overflowing the screen
                    string fullPreview = RawContent ?? "";
                    string displayPreview;
                    if (fullPreview.Length > 300)
                    {
                        displayPreview = string.Concat(fullPreview.AsSpan(0, 300), $"\n\n... ({fullPreview.Length - 300:N0} more characters truncated)");
                    }
                    else
                    {
                        displayPreview = fullPreview;
                    }

                    // Limit line count too — long single-line content is fine, but many lines overflow
                    var previewLines = displayPreview.Split('\n');
                    if (previewLines.Length > 20)
                    {
                        displayPreview = string.Join("\n", previewLines.Take(20)) + $"\n\n... ({previewLines.Length - 20} more lines truncated)";
                    }

                    result = System.Windows.MessageBox.Show(

                        "You are about to execute raw clipboard text directly in your native Command Prompt.\n\n" +
                        "Are you absolutely sure you want to run this command? Malicious scripts can heavily damage your operating system:\n\n" +
                        displayPreview,

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



                        // [SECURITY FIX]: Write clipboard text to a temp .bat file instead of 
                        // passing it inline to cmd.exe /k — prevents shell metacharacter injection 
                        // (CWE-78) via &&, |, ;, etc. in clipboard content.
                        string tempBat = Path.Combine(Path.GetTempPath(), $"FlyShelf_Run_{Guid.NewGuid():N}.bat");
                        File.WriteAllText(tempBat, $"@echo off\r\n{RawContent}\r\npause\r\ndel \"%~f0\"");
                        startInfo.Arguments = $"/c \"{tempBat}\"";
                    }



                    FlyShelf.Classes.Logger.LogAction("TERMINAL EXECUTION", $"Spawned native command prompt. Args: {startInfo.Arguments} | WorkingDir: {startInfo.WorkingDirectory}");



                    _ = Task.Run(() => { try { Process.Start(startInfo); } catch { } });



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



                    _ = Task.Run(() => { try { Process.Start(new ProcessStartInfo { FileName = RawContent, UseShellExecute = true }); } catch { } });



                }



            }



            catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("DEBUG", $"Browser Hook Failed: {ex.Message}"); }



        }



        public void RunAdminTerminal()



        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸ Elevated terminal is not available in the Store version.");
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



                _ = Task.Run(() => { try { Process.Start(startInfo); } catch { } });



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
            FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸ Code compilation is not available in the Store version.");
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



                    // [SECURITY FIX v2.1.0]: Use full GUID to prevent predictable temp file names (CWE-377)
                    sourceFile = Path.Combine(Path.GetTempPath(), $"FlyShelf_{Guid.NewGuid():N}.cpp");



                    File.WriteAllText(sourceFile, RawContent);



                    exeName = Path.Combine(Path.GetTempPath(), $"FlyShelf_{Guid.NewGuid():N}.exe");



                }



                



                // [SECURITY FIX]: Write compile command to temp .bat file to prevent command injection (CWE-78)
                string compileCommand = $"@echo off\r\ntitle FlyShelf C/C++ Compiler\r\necho [FlyShelf Engine] Executing g++ on payload...\r\ng++ \"{sourceFile}\" -o \"{exeName}\"\r\necho -----------------------------------------\r\n\"{exeName}\"";
                string batPath = Path.Combine(Path.GetTempPath(), $"flyshelf_compile_{Guid.NewGuid():N}.bat");
                File.WriteAllText(batPath, compileCommand);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"{batPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                _ = Task.Run(() => { try { Process.Start(startInfo); } catch { } });
                // Note: .bat cleanup is fire-and-forget; cmd.exe holds the file open while running



            }



            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Hardware Compiler Error"); }
#endif



        }



        /// <summary>
        /// On-demand zip creation for Group and Folder items.
        /// Called when user clicks the "Convert to .zip" hover button.
        /// </summary>
        public void CreateZipArchive()
        {
            if (HasZipArchive) return; // Already created

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (ItemType == ClipboardItemType.Group)
                    {
                        // Group: zip all file paths stored in RawContent
                        string[] paths = RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        // [SECURITY FIX v2.1.0]: Use full GUID to prevent predictable temp file names (CWE-377)
                        string tempZip = Path.Combine(Path.GetTempPath(), $"FlyShelf_Group_{Guid.NewGuid():N}.zip");
                        if (File.Exists(tempZip)) File.Delete(tempZip);

                        using (var archive = System.IO.Compression.ZipFile.Open(tempZip, System.IO.Compression.ZipArchiveMode.Create))
                        {
                            foreach (var path in paths)
                            {
                                string trimmed = path.Trim();
                                if (File.Exists(trimmed))
                                {
                                    archive.CreateEntryFromFile(trimmed, Path.GetFileName(trimmed), System.IO.Compression.CompressionLevel.Fastest);
                                }
                                else if (Directory.Exists(trimmed))
                                {
                                    string dirName = Path.GetFileName(trimmed);
                                    foreach (var file in Directory.GetFiles(trimmed, "*", SearchOption.AllDirectories))
                                    {
                                        string relativePath = Path.GetRelativePath(trimmed, file);
                                        string entryName = Path.Combine(dirName, relativePath);
                                        archive.CreateEntryFromFile(file, entryName, System.IO.Compression.CompressionLevel.Fastest);
                                    }
                                }
                            }
                        }

                        ZippedArchivePath = tempZip;
                        FlyShelf.Classes.Logger.LogAction("GROUP ZIP", $"Created zip on demand: {tempZip}");
                    }
                    else if (ItemType == ClipboardItemType.Folder && !string.IsNullOrEmpty(FilePath) && Directory.Exists(FilePath))
                    {
                        // Folder: zip the entire directory
                        // [SECURITY FIX v2.1.0]: Use full GUID to prevent predictable temp file names (CWE-377)
                        string tempZip = Path.Combine(Path.GetTempPath(), $"FlyShelf_{Guid.NewGuid():N}.zip");
                        if (File.Exists(tempZip)) File.Delete(tempZip);
                        System.IO.Compression.ZipFile.CreateFromDirectory(FilePath, tempZip, System.IO.Compression.CompressionLevel.Fastest, true);

                        ZippedArchivePath = tempZip;
                        var zipInfo = new FileInfo(tempZip);
                        long folderSize = Directory.GetFiles(FilePath, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                        FormattedSize = $"{FormatBytes(folderSize)} -> {FormatBytes(zipInfo.Length)} zipped";
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FormattedSize)));
                        });
                        FlyShelf.Classes.Logger.LogAction("FOLDER ZIP", $"Created zip on demand: {tempZip}");
                    }

                    // Show toast on UI thread
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("Zip archive created!");
                    });
                }
                catch (Exception ex)
                {
                    FlyShelf.Classes.Logger.LogAction("ZIP CREATE ERR", ex.Message);
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"âŒ Zip creation failed: {ex.Message}");
                    });
                }
            });
        }

        /// <summary>
        /// Sends the zip archive to all alive LAN peers only (no Cloudflare).
        /// Called when user clicks the "Sync via LAN" hover button.
        /// </summary>
        public async Task SyncZipViaLanAsync()
        {
            if (!HasZipArchive)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸ No zip archive to sync. Create one first.");
                return;
            }

            try
            {
                var peerCount = FlyShelf.Classes.PeerManager.Instance?.AliveCount ?? 0;
                if (peerCount == 0)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸  No LAN peers connected.");
                    return;
                }

                var peerInstance = FlyShelf.Classes.PeerManager.Instance;
                if (peerInstance == null)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ No LAN peers connected.");
                    return;
                }

                FlyShelf.Windows.ToastWindow.ShowToast("📡 Syncing zip via LAN...");
                int delivered = await peerInstance.PushFileToAllPeers(
                    ZippedArchivePath, FileName ?? "Archive", "Archive");

                if (delivered > 0)
                    FlyShelf.Windows.ToastWindow.ShowToast($"ðŸ“¡ Synced to {delivered} LAN peer(s)!");
                else
                    FlyShelf.Windows.ToastWindow.ShowToast("âš ï¸  Failed to sync to any LAN peer.");
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("LAN SYNC ERR", ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast($"âŒ LAN sync failed: {ex.Message}");
            }
        }
    }
}

