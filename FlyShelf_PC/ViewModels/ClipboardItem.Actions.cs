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




    }
}
