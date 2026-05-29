using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    public class DocumentSniffer
    {
        private List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private FlyShelfViewModel _viewModel;
        private System.Collections.Concurrent.ConcurrentDictionary<string, byte> _recentlyTriggeredFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        public DocumentSniffer(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public void StartSniffing()
        {
            StopSniffing();

            var pathsToWatch = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent")
            };

            // Custom sniffer folders are a Pro feature
            if (LicenseManager.IsPro)
            {
                pathsToWatch.AddRange(SettingsManager.Current.CustomSnifferPaths);
            }

            foreach (var path in pathsToWatch.Distinct())
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(path);
                        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
                        watcher.Filter = "*.*";
                        watcher.Created += OnFileDetected;
                        watcher.Changed += OnFileDetected;
                        watcher.Renamed += OnFileDetected;
                        watcher.EnableRaisingEvents = true;
                        _watchers.Add(watcher);
                        
                        Logger.LogAction("SNIFFER", $"Active listening on: {path}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("SNIFFER ERROR", $"Watch failed on {path}: {ex.Message}");
                    }
                }
            }
        }

        public void StopSniffing()
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
        }

        private void OnFileDetected(object sender, FileSystemEventArgs e)
        {
            // Wrap in Task.Run to avoid async void and prevent event thread crashes
            _ = Task.Run(async () =>
            {
                try
                {
                    await OnFileDetectedCore(e);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("SNIFFER ERROR", $"OnFileDetected crash: {ex.Message}");
                }
            });
        }

        private async Task<bool> WaitForFileReadyAsync(string filePath, int maxRetries = 15, int initialDelayMs = 200)
        {
            int currentRetry = 0;
            int delay = initialDelayMs;

            while (currentRetry < maxRetries)
            {
                if (!File.Exists(filePath))
                {
                    await Task.Delay(delay);
                    currentRetry++;
                    delay = Math.Min(delay * 2, 2000);
                    continue;
                }

                try
                {
                    using (var fs1 = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long size1 = fs1.Length;
                        if (size1 > 0)
                        {
                            await Task.Delay(150);
                            using (var fs2 = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                if (fs2.Length == size1)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // Locked by another process
                }
                catch (Exception)
                {
                }

                currentRetry++;
                await Task.Delay(delay);
                delay = Math.Min(delay * 2, 2000);
            }

            return false;
        }

        private async Task OnFileDetectedCore(FileSystemEventArgs e)
        {
            string ext = Path.GetExtension(e.FullPath).ToLower();
            if (ext != ".pdf" && ext != ".docx" && ext != ".doc" && ext != ".lnk") return;

            string fileName = Path.GetFileName(e.FullPath);
            if (fileName.StartsWith("~$")) return;

            // Debouncing fast duplicate events from web browsers downloading chunks
            if (_recentlyTriggeredFiles.ContainsKey(e.FullPath)) return;
            
            _recentlyTriggeredFiles.TryAdd(e.FullPath, 0);

            string targetPath = e.FullPath;

            if (ext == ".lnk")
            {
                try
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t != null)
                    {
                        dynamic shell = Activator.CreateInstance(t);
                        var shortcut = shell.CreateShortcut(e.FullPath);
                        targetPath = shortcut.TargetPath;
                        
                        if (string.IsNullOrEmpty(targetPath)) return;
                        
                        string targetExt = Path.GetExtension(targetPath).ToLower();
                        if (targetExt != ".docx" && targetExt != ".doc" && targetExt != ".pdf") return;
                    }
                    else return;
                }
                catch { return; }
            }

            bool isReady = await WaitForFileReadyAsync(targetPath);
            if (!isReady)
            {
                _recentlyTriggeredFiles.TryRemove(e.FullPath, out _);
                return;
            }

            try
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dataObj = new System.Windows.DataObject();
                    var dropList = new System.Collections.Specialized.StringCollection { targetPath };
                    dataObj.SetFileDropList(dropList);
                    _viewModel.HandleDrop(dataObj, true);
                    
                    string sizeStr = "";
                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            sizeStr = $" ({FlyShelf.Classes.FormatHelper.FormatSize(new FileInfo(targetPath).Length)})";
                        }
                    }
                    catch { }
                    string friendlyType = FlyShelf.Classes.FormatHelper.GetFileTypeFriendly(targetPath);
                    FlyShelf.Windows.ToastWindow.ShowToast($"{friendlyType} sniffed{sizeStr} 📄");
                });
            }
            catch 
            {
                _recentlyTriggeredFiles.TryRemove(e.FullPath, out _);
            }

            await Task.Delay(13000);
            _recentlyTriggeredFiles.TryRemove(e.FullPath, out _);
        }
    }
}
