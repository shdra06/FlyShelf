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
            FlyShelf.Windows.ToastWindow.ShowToast("Code sandbox is not available in the Store version. Download the full version from https://fly-shelf.vercel.app/");
            return;
#else
            try
            {
                if (ItemType != ClipboardItemType.Code) return;

                if (string.IsNullOrEmpty(RawContent) && string.IsNullOrEmpty(FilePath)) return;

                string sandboxDir;
                string fullPath;

                if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                {
                    sandboxDir = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();
                    fullPath = FilePath;
                }
                else
                {
                    sandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(sandboxDir);
                    string filename = string.IsNullOrEmpty(FileName) ? "snippet.txt" : FileName;
                    fullPath = Path.Combine(sandboxDir, filename);
                    File.WriteAllText(fullPath, RawContent);
                }

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
            Classes.CodeExecutionEngine.Execute(this);
        }

        public void OpenInBrowser()
        {
            try
            {
                if (IsUrlPreview && !string.IsNullOrEmpty(RawContent))
                {
                    if (Uri.TryCreate(RawContent, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        _ = Task.Run(() => { try { Process.Start(new ProcessStartInfo { FileName = RawContent, UseShellExecute = true }); } catch { } });
                    }
                }
            }
            catch (Exception ex) { FlyShelf.Classes.Logger.LogAction("DEBUG", $"Browser Hook Failed: {ex.Message}"); }
        }

        public void RunAdminTerminal()
        {
#if MSIX_STORE
            FlyShelf.Windows.ToastWindow.ShowToast("Elevated terminal is not available in the Store version.");
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
                    Verb = "runas"
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
            Classes.CodeExecutionEngine.Execute(this);
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
                        string[] paths = RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
                        FlyShelf.Windows.ToastWindow.ShowToast($"Zip creation failed: {ex.Message}");
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
                FlyShelf.Windows.ToastWindow.ShowToast("No zip archive to sync. Create one first.");
                return;
            }

            try
            {
                var peerCount = FlyShelf.Classes.PeerManager.Instance?.AliveCount ?? 0;
                if (peerCount == 0)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No LAN peers connected.");
                    return;
                }

                var peerInstance = FlyShelf.Classes.PeerManager.Instance;
                if (peerInstance == null)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No LAN peers connected.");
                    return;
                }

                FlyShelf.Windows.ToastWindow.ShowToast("Syncing zip via LAN...");
                int delivered = await peerInstance.PushFileToAllPeers(
                    ZippedArchivePath, FileName ?? "Archive", "Archive");

                if (delivered > 0)
                    FlyShelf.Windows.ToastWindow.ShowToast($"Synced to {delivered} LAN peer(s)!");
                else
                    FlyShelf.Windows.ToastWindow.ShowToast("Failed to sync to any LAN peer.");
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("LAN SYNC ERR", ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast($"LAN sync failed: {ex.Message}");
            }
        }
    }
}
