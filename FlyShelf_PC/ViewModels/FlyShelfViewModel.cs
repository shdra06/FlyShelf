using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace AdvanceClip.ViewModels
{
    public partial class FlyShelfViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ClipboardItem> DroppedItems { get; } = new ObservableCollection<ClipboardItem>();
        private Stack<System.Collections.Generic.List<ClipboardItem>> _deletedItemsHistory = new Stack<System.Collections.Generic.List<ClipboardItem>>();
        // Limit parallel image/icon decodes to prevent memory spikes on bulk file copies
        private static readonly System.Threading.SemaphoreSlim _iconDecodeSemaphore = new System.Threading.SemaphoreSlim(2, 2);


        /// <summary>
        /// Loads persisted clipboard history from disk and rebuilds Icon previews.
        /// Called once at app startup.
        /// </summary>
        public void LoadPersistedHistory()
        {
            var items = Classes.ClipboardHistoryManager.LoadHistory();
            
            // Build lookup of items already loaded (e.g. pinned items from LoadPinnedItems)
            // to prevent duplicates on restart
            var existingKeys = new HashSet<string>();
            foreach (var existing in DroppedItems)
            {
                string key = GetDeduplicationKey(existing);
                if (!string.IsNullOrEmpty(key)) existingKeys.Add(key);
            }
            
            // Phase 1: Add all items IMMEDIATELY with no icons — makes the UI appear instantly
            var itemsNeedingIcons = new List<ClipboardItem>();
            foreach (var item in items)
            {
                if (IsEffectivelyEmpty(item)) continue;
                
                string itemKey = GetDeduplicationKey(item);
                if (!string.IsNullOrEmpty(itemKey) && !existingKeys.Add(itemKey))
                    continue;

                // Heal legacy items
                if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(item.RawContent))
                    item.FileName = item.RawContent.Length > 800 ? item.RawContent.Substring(0, 800) + "..." : item.RawContent;

                item.EvaluateSmartActions();
                DroppedItems.Add(item);
                
                // Queue for background icon loading
                bool needsIcon = (item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                    && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
                bool needsFileIcon = !needsIcon && (item.ItemType == ClipboardItemType.File || item.ItemType == ClipboardItemType.Document ||
                    item.ItemType == ClipboardItemType.Pdf || item.ItemType == ClipboardItemType.Archive ||
                    item.ItemType == ClipboardItemType.Video || item.ItemType == ClipboardItemType.Audio ||
                    item.ItemType == ClipboardItemType.Presentation) && !string.IsNullOrEmpty(item.FilePath);
                if (needsIcon || needsFileIcon)
                    itemsNeedingIcons.Add(item);
            }
            OnPropertyChanged(nameof(ShelfVisibility));

            // Wire up auto-save on any collection change
            DroppedItems.CollectionChanged += (s, e) =>
            {
                Classes.ClipboardHistoryManager.SaveHistoryDebounced(DroppedItems);
            };

            // Phase 2: Load icons in background — batched to limit memory pressure
            if (itemsNeedingIcons.Count > 0)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    foreach (var item in itemsNeedingIcons)
                    {
                        await _iconDecodeSemaphore.WaitAsync();
                        try
                        {
                            if ((item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                                && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                            {
                                var icon = LoadImageThumbnail(item.FilePath);
                                if (icon != null)
                                    Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                            }
                            else if (!string.IsNullOrEmpty(item.FilePath))
                            {
                                var icon = GetIcon(item.FilePath);
                                if (icon != null)
                                    Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                            }
                        }
                        catch { }
                        finally { _iconDecodeSemaphore.Release(); }
                    }
                });
            }
        }

        /// <summary>
        /// Triggers a debounced save of the current clipboard history.
        /// Call after property changes on items (pin, etc.)
        /// </summary>
        private void PersistHistory()
        {
            Classes.ClipboardHistoryManager.SaveHistoryDebounced(DroppedItems);
        }

        /// <summary>
        /// Public wrapper for PersistHistory — used by MainWindow for bulk operations.
        /// </summary>
        public void PersistHistoryPublic() => PersistHistory();

        /// <summary>
        /// Returns true if the item has no displayable content (no text, no file, no image).
        /// Used to filter out ghost/empty items during load and insertion.
        /// </summary>
        private static bool IsEffectivelyEmpty(ClipboardItem item)
        {
            // Images are never "empty" — even if the file is deleted, they had valid content when captured
            if (item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                return false;
            
            bool hasText = !string.IsNullOrWhiteSpace(item.RawContent);
            bool hasFile = !string.IsNullOrWhiteSpace(item.FilePath);
            bool hasName = !string.IsNullOrWhiteSpace(item.FileName);
            
            return !hasText && !hasFile && !hasName;
        }

        /// <summary>
        /// Returns a unique key for deduplication. Uses FilePath for file-based items, RawContent for text items.
        /// </summary>
        private static string GetDeduplicationKey(ClipboardItem item)
        {
            if (!string.IsNullOrEmpty(item.FilePath))
                return "F:" + item.FilePath;
            if (!string.IsNullOrEmpty(item.RawContent))
                return "T:" + item.RawContent;
            if (!string.IsNullOrEmpty(item.FileName))
                return "N:" + item.FileName;
            return string.Empty;
        }

        // Pre-compiled regex patterns for text classification — avoids recompilation on every clipboard event
        private static readonly Regex _rxTerminal = new Regex(@"(PS C:\\|~\$|root@|npm run|npm install|git clone|git commit|sudo |apt-get|docker run)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxCode = new Regex(@"(#include\s|<iostream>|<stdio\.h>|std::|printf\(|public class |private void |int main\(\)|using namespace |def\s+\w+\(|import\s+(os|sys|java|React)|class\s+[A-Z]\w*|Console\.WriteLine|=>\s*\{|\{""|\[\{""|<\/?(html|div|span|script|style|body|head)|function\s+\w+\(|console\.log\(|require\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxUtmClean = new Regex(@"(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private int _currentMode = 0; // 0=Mini, 1=Medium, 2=Full
        public int CurrentMode
        {
            get => _currentMode;
            set
            {
                _currentMode = value;
                OnPropertyChanged(nameof(CurrentMode));
                OnPropertyChanged(nameof(IsMiniMode));
                OnPropertyChanged(nameof(CurrentFlyShelfMaxHeight));
                OnPropertyChanged(nameof(CurrentFlyShelfWidth));
            }
        }
        
        public bool IsMiniMode => CurrentMode == 0;
        public bool IsMediumMode => CurrentMode == 1;
        public bool IsFullMode => CurrentMode == 2;

        public int CurrentFlyShelfMaxHeight
        {
            get
            {
                if (CurrentMode == 0) return AdvanceClip.Classes.SettingsManager.Current.MiniFormHeight;
                if (CurrentMode == 1) return AdvanceClip.Classes.SettingsManager.Current.MediumFormHeight;
                return (int)SystemParameters.WorkArea.Height - 100;
            }
        }
        
        public int CurrentFlyShelfWidth
        {
            get
            {
                if (CurrentMode == 0) return AdvanceClip.Classes.SettingsManager.Current.MiniFormWidth;
                if (CurrentMode == 1) return AdvanceClip.Classes.SettingsManager.Current.MediumFormWidth;
                return 850;
            }
        }

        private bool _isSending;
        public bool IsSending { get => _isSending; set { _isSending = value; OnPropertyChanged(nameof(IsSending)); } }
        
        private string _sendingText = "Sending";
        public string SendingText { get => _sendingText; set { _sendingText = value; OnPropertyChanged(nameof(SendingText)); } }

        public ICommand ClearCommand { get; }
        public ICommand RemoveItemCommand { get; }
        public ICommand OpenItemCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand TogglePinCommand { get; }
        public ICommand LaunchSandboxCommand { get; }
        public ICommand LaunchTerminalCommand { get; }
        public ICommand OpenInBrowserCommand { get; }
        public ICommand RunAdminTerminalCommand { get; }
        public ICommand ToggleGlobalFirebaseCommand { get; }
        public ICommand CompileNativeCommand { get; }
        public ICommand ConvertDocumentCommand { get; }
        public ICommand ExtractTextCommand { get; }
        public ICommand ExtractTableCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand CopyRawContentCommand { get; }
        public ICommand MergeSelectedPdfsCommand { get; }
        public ICommand OpenFileLocationCommand { get; }
        public ICommand ConvertImageToPdfCommand { get; }
        
        public AdvanceClip.Classes.NetworkSyncServer LocalServer { get; private set; }
        public AdvanceClip.Classes.DocumentSniffer Sniffer { get; private set; }
        public AdvanceClip.Classes.FirebaseListener CloudListener { get; private set; }

        public void RefreshLocalServerData()
        {
            OnPropertyChanged(nameof(LocalServer));
        }

        public FlyShelfViewModel()
        {
            ClearCommand = new RelayCommand(ClearShelf);
            RemoveItemCommand = new RelayCommand<ClipboardItem>(RemoveItem);
            OpenItemCommand = new RelayCommand<ClipboardItem>(OpenItem);
            ClearAllCommand = new RelayCommand(ClearShelf);
            UndoCommand = new RelayCommand(UndoDelete);
            TogglePinCommand = new RelayCommand<ClipboardItem>(TogglePin);
            LaunchSandboxCommand = new RelayCommand<ClipboardItem>(LaunchSandbox);
            LaunchTerminalCommand = new RelayCommand<ClipboardItem>(item => item?.RunInTerminal());
            OpenInBrowserCommand = new RelayCommand<ClipboardItem>(item => item?.OpenInBrowser());
            RunAdminTerminalCommand = new RelayCommand<ClipboardItem>(item => item?.RunAdminTerminal());
            CompileNativeCommand = new RelayCommand<ClipboardItem>(item => item?.CompileAndRunNative());
            ConvertDocumentCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertDocumentTask());
            ConvertImageToPdfCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertImageToPdf());

            ExtractTextCommand = new RelayCommand<ClipboardItem>(item => item?.ExtractText());
            ExtractTableCommand = new RelayCommand<ClipboardItem>(item => item?.ExtractTable());
            SaveSettingsCommand = new RelayCommand(SaveGlobalSettings);
            ToggleGlobalFirebaseCommand = new RelayCommand(() => {
                AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync = !AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync;
            });
            CopyRawContentCommand = new RelayCommand<ClipboardItem>(item => {
                if (item == null) return;
                try
                {
                    string content = !string.IsNullOrEmpty(item.RawContent) ? item.RawContent : item.FileName;
                    if (!string.IsNullOrEmpty(content))
                        System.Windows.Clipboard.SetText(content);
                    AdvanceClip.Windows.ToastWindow.ShowToast("Copied to clipboard! 📋");
                }
                catch { }
            });
            MergeSelectedPdfsCommand = new RelayCommand(() => {
                var pdfs = DroppedItems.Where(i => i.ItemType == ClipboardItemType.Pdf && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
                if (pdfs.Count < 2)
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast("Select at least 2 PDFs to merge.");
                    return;
                }
                var win = new AdvanceClip.Windows.PdfMergeWindow(pdfs, this);
                win.ShowDialog();
            });
            OpenFileLocationCommand = new RelayCommand<ClipboardItem>(item => {
                if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
                
                bool exists = System.IO.File.Exists(item.FilePath) || System.IO.Directory.Exists(item.FilePath);
                if (exists)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{item.FilePath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("EXPLORER", $"Open failed: {ex.Message}");
                    }
                }
                else
                {
                    // File/folder doesn't exist — open parent folder instead
                    string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                    }
                }
            });
            
            LocalServer = new AdvanceClip.Classes.NetworkSyncServer(this);
            Sniffer = new AdvanceClip.Classes.DocumentSniffer(this);
            CloudListener = new AdvanceClip.Classes.FirebaseListener(this);
            

            
            AdvanceClip.Classes.SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.EnableLocalNetworkSync))
                {
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync) LocalServer.Start();
                    else LocalServer.Stop();
                    OnPropertyChanged(nameof(LocalServer));
                }
                else if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.EnableLocalLAN))
                {
                    // LAN toggle changed — auto-manage the master server toggle
                    bool lanOn = AdvanceClip.Classes.SettingsManager.Current.EnableLocalLAN;
                    bool cfOn = AdvanceClip.Classes.SettingsManager.Current.EnableGlobalCloudflare;
                    
                    if (lanOn || cfOn)
                    {
                        // At least one transport active — ensure server is running
                        if (!AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync)
                            AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync = true;
                    }
                    else
                    {
                        // Both transports off — stop the server
                        AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync = false;
                    }
                    
                    // Force PeerManager to re-handshake with new transport preference
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        if (AdvanceClip.Classes.PeerManager.Instance != null) await AdvanceClip.Classes.PeerManager.Instance.ForceResync();
                    });
                    AdvanceClip.Classes.Logger.LogAction("SETTINGS", $"LAN transport: {(lanOn ? "ON" : "OFF")}");
                }
                else if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.EnableGlobalFirebaseSync))
                {
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                    {
                        CloudListener.StartPolling();
                    }
                    else
                    {
                        CloudListener.StopPolling();
                    }
                }
                else if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MiniFormWidth) ||
                         e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MiniFormHeight) ||
                         e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MediumFormWidth) ||
                         e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MediumFormHeight))
                {
                    OnPropertyChanged(nameof(CurrentFlyShelfMaxHeight));
                    OnPropertyChanged(nameof(CurrentFlyShelfWidth));
                }
            };
            
            LoadPinnedItems();

            // Background Boot Optimization: Shift heavy DNS polling, port binding, and I/O sniffing completely off 
            // the main UI Constructor thread so AdvanceClip can bootstrap in under 50ms natively!
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    // Auto-reconcile: server should be up if either transport is on
                    bool lanOn = AdvanceClip.Classes.SettingsManager.Current.EnableLocalLAN;
                    bool cfOn = AdvanceClip.Classes.SettingsManager.Current.EnableGlobalCloudflare;
                    if ((lanOn || cfOn) && !AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync)
                    {
                        AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync = true;
                    }
                    
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync) 
                    {
                        LocalServer.Start();
                    }
                    AdvanceClip.Classes.SyncQueue.Start(); // Guaranteed-delivery sync queue
                    Sniffer.StartSniffing();
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                    {
                        CloudListener.StartPolling();
                    }
                });
            }, System.Windows.Threading.DispatcherPriority.Background);
        }



    

        public void RemoveItem(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                // Structural Lock: Pinned items cannot be deleted unless physically unpinned first!
                if (item.IsPinned) return; 

                _deletedItemsHistory.Push(new System.Collections.Generic.List<ClipboardItem> { item });
                DroppedItems.Remove(item);
                OnPropertyChanged(nameof(ShelfVisibility));

                // Cleanup: delete backing file (temp or persistent image)
                CleanupTempFile(item.FilePath);
                Classes.ClipboardHistoryManager.DeletePersistentImage(item);
            }
        }

        /// <summary>
        /// Deletes the backing file only if it resides inside the system temp directory.
        /// User's real files (dragged from Explorer) are never touched.
        /// </summary>
        private void CleanupTempFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                string tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
                string fileDir = Path.GetDirectoryName(filePath)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
                if (fileDir.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(filePath);
                }
            }
            catch { /* Silently ignore - file may be locked */ }
        }

        public void TogglePin(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                item.IsPinned = !item.IsPinned;
                SavePinnedItems();
                PersistHistory(); // Save pin state change
                
                // The user explicitly requested Pinned items to remain strictly invisible to the Delete feature
                // WITHOUT physically sorting them to the top of the Stack anymore.
                // We just toggle the state and let them sit natively wherever they are!
            }
        }

        public void OpenItem(ClipboardItem item)
        {
            item?.Execute();
        }

        public void ClearShelf()
        {
            var volatileItems = DroppedItems.Where(i => !i.IsPinned).ToList();
            if (volatileItems.Count > 0)
            {
                _deletedItemsHistory.Push(volatileItems);
                foreach(var vi in volatileItems) DroppedItems.Remove(vi);
                OnPropertyChanged(nameof(ShelfVisibility));
                SavePinnedItems();
            }
        }
        
        public void SortForContext(string currentContextTitle)
        {
            if (string.IsNullOrWhiteSpace(currentContextTitle)) return;
            
            var itemsList = DroppedItems.ToList();
            var sorted = itemsList.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.AssociatedContextTitle) && string.Equals(x.AssociatedContextTitle, currentContextTitle, StringComparison.OrdinalIgnoreCase))
                                  .ThenByDescending(x => x.DateCopied)
                                  .ToList();
                                  
            bool needsReorder = false;
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].IsSuggestedContext = !string.IsNullOrWhiteSpace(sorted[i].AssociatedContextTitle) && string.Equals(sorted[i].AssociatedContextTitle, currentContextTitle, StringComparison.OrdinalIgnoreCase);
                if (i < DroppedItems.Count && !object.ReferenceEquals(DroppedItems[i], sorted[i])) 
                {
                    needsReorder = true;
                }
            }

            if (needsReorder)
            {
                // AdvanceClip Phase 2.1: Use logical pointer swapping rather than destructive visual tree clears!
                // This eliminates the 1.5s visual freeze spike on large payload buffers!
                for (int i = 0; i < sorted.Count; i++)
                {
                    var actualIndex = DroppedItems.IndexOf(sorted[i]);
                    if (actualIndex != -1 && actualIndex != i)
                    {
                        DroppedItems.Move(actualIndex, i);
                    }
                }
            }
        }

        private string GetDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "FlyShelf");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pinned_items.json");
        }

        public void SavePinnedItems()
        {
            try
            {
                var pinned = DroppedItems.Where(i => i.IsPinned).ToList();
                File.WriteAllText(GetDbPath(), JsonSerializer.Serialize(pinned));
            }
            catch { }
        }

        public void LoadPinnedItems()
        {
            try
            {
                string path = GetDbPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var docs = JsonSerializer.Deserialize<List<ClipboardItem>>(json);
                    if (docs != null)
                    {
                        var seenKeys = new HashSet<string>();
                        foreach (var d in docs)
                        {
                            // Skip duplicates within the pinned file itself
                            string key = GetDeduplicationKey(d);
                            if (!string.IsNullOrEmpty(key) && !seenKeys.Add(key))
                                continue;
                            
                            d.IsPinned = true;
                            if (d.ItemType == ClipboardItemType.File && !string.IsNullOrEmpty(d.FilePath))
                            {
                                System.Threading.Tasks.Task.Run(() => {
                                    var icon = GetIcon(d.FilePath);
                                    if (icon != null) Application.Current.Dispatcher.Invoke(() => d.Icon = icon);
                                });
                            }
                            else if (d.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(d.FilePath) && File.Exists(d.FilePath))
                            {
                                string imagePath = d.FilePath;
                                var capturedD = d;
                                System.Threading.Tasks.Task.Run(() => {
                                    try 
                                    {
                                        var bmp = new BitmapImage();
                                        bmp.BeginInit();
                                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                                        bmp.DecodePixelWidth = 250;
                                        bmp.UriSource = new Uri(imagePath);
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        Application.Current.Dispatcher.InvokeAsync(() => capturedD.Icon = bmp);
                                    } catch { }
                                });
                            }
                            DroppedItems.Add(d);
                        }
                    }
                }
            }
            catch { }
        }
        public void UndoDelete()
        {
            if (_deletedItemsHistory.Count > 0)
            {
                var restoredItems = _deletedItemsHistory.Pop();
                foreach (var item in restoredItems)
                {
                    if (!DroppedItems.Contains(item))
                        DroppedItems.Add(item);
                }
                OnPropertyChanged(nameof(ShelfVisibility));
                SavePinnedItems();
            }
        }

        public void LaunchSandbox(ClipboardItem item)
        {
            if (item != null) item.OpenSandbox();
        }

        private string AutoFormatCode(string raw)
        {
            try
            {
                var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var formatted = new System.Text.StringBuilder();
                int indentLevel = 0;
                string tab = "    ";

                foreach (var line in lines)
                {
                    string cleanLine = line.Trim();
                    if (string.IsNullOrEmpty(cleanLine)) continue;
                    
                    if (cleanLine.StartsWith("}")) indentLevel = Math.Max(0, indentLevel - 1);
                    
                    formatted.AppendLine(string.Concat(Enumerable.Repeat(tab, indentLevel)) + cleanLine);
                    
                    if (cleanLine.EndsWith("{")) indentLevel++;
                }
                return formatted.ToString().TrimEnd();
            }
            catch { return raw; }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
        }

        // ═══ Cloud Echo Prevention ═══
        // Tracks content that arrived from Firebase/cloud so HandleDrop doesn't re-push it
        private static readonly Dictionary<string, long> _recentCloudContent = new();
        private static readonly object _cloudContentLock = new();
        
        /// <summary>Mark content as cloud-sourced so it won't be re-pushed to Firebase.</summary>
        public void MarkAsCloudSourced(string contentFingerprint)
        {
            lock (_cloudContentLock)
            {
                _recentCloudContent[contentFingerprint] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PruneCloudContent();
            }
        }
        
        /// <summary>Check if content was recently received from cloud (shouldn't be re-pushed).</summary>
        private bool IsCloudSourced(string contentFingerprint)
        {
            lock (_cloudContentLock)
            {
                PruneCloudContent();
                return _recentCloudContent.ContainsKey(contentFingerprint) && 
                       (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _recentCloudContent[contentFingerprint]) < 30_000;
            }
        }

        /// <summary>Evicts stale entries (>30s) and enforces a hard cap of 100.</summary>
        private void PruneCloudContent()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stale = _recentCloudContent.Where(kv => now - kv.Value > 30_000).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _recentCloudContent.Remove(k);
            // Hard cap — remove oldest if still too large
            while (_recentCloudContent.Count > 100)
            {
                var oldest = _recentCloudContent.OrderBy(kv => kv.Value).First().Key;
                _recentCloudContent.Remove(oldest);
            }
        }

        /// <summary>
        /// Unified file sync: Cloudflare tunnel → Firebase Storage → log-only fallback.
        /// Replaces 3 previously duplicated sync blocks.
        /// </summary>
        private async System.Threading.Tasks.Task SyncFileToDevicesAsync(string filePath, ClipboardItem item, long maxFirebaseBytes = 25 * 1024 * 1024, string label = "FILE")
        {
            try
            {
                long fSize = new FileInfo(filePath).Length;
                var srv = LocalServer;
                bool tunnelOk = AdvanceClip.Classes.FirebaseSyncManager.CachedTunnelVerified;

                // PRIORITY 1: Direct P2P push to connected peers via Cloudflare tunnel
                if (srv != null && !string.IsNullOrEmpty(srv.GlobalUrl) && srv.GlobalUrl.Contains("trycloudflare.com") && tunnelOk)
                {
                    string downloadUrl = $"{srv.GlobalUrl}/download?path={Uri.EscapeDataString(filePath)}";
                    AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"Sending '{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) via Cloudflare P2P");
                    var syncItem = item.CloneForSync(downloadUrl);
                    await AdvanceClip.Classes.FirebaseSyncManager.PushToGlobalSync(syncItem);
                    Application.Current.Dispatcher.Invoke(() =>
                        AdvanceClip.Windows.ToastWindow.ShowToast($"{label} ({FormatFileSize(fSize)}) synced via P2P \ud83c\udf10"));
                    return;
                }

                // No Cloudflare tunnel available — file cannot be synced remotely
                // Firebase Storage is NEVER used for content transfer
                AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"'{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) — no Cloudflare tunnel, file not synced");
                Application.Current.Dispatcher.Invoke(() =>
                    AdvanceClip.Windows.ToastWindow.ShowToast($"\u26a0\ufe0f {Path.GetFileName(filePath)} ({FormatFileSize(fSize)}) — needs Cloudflare tunnel"));
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"Error: {ex.Message}");
            }
        }

        private const int MAX_UNPINNED_ITEMS = 500;

        /// <summary>
        /// Prunes oldest unpinned items beyond the cap to prevent unbounded memory growth.
        /// </summary>
        private void PruneOldItems()
        {
            while (DroppedItems.Count > MAX_UNPINNED_ITEMS)
            {
                // Find the last unpinned item
                ClipboardItem? oldest = null;
                for (int i = DroppedItems.Count - 1; i >= 0; i--)
                {
                    if (!DroppedItems[i].IsPinned) { oldest = DroppedItems[i]; break; }
                }
                if (oldest != null) DroppedItems.Remove(oldest);
                else break; // all items are pinned
            }
        }

        public Visibility ShelfVisibility => DroppedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    }
}
