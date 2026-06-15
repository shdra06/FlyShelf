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

namespace FlyShelf.ViewModels
{
    public partial class FlyShelfViewModel : INotifyPropertyChanged
    {
        public BulkObservableCollection<ClipboardItem> DroppedItems { get; } = new BulkObservableCollection<ClipboardItem>();
        // Limit parallel image/icon decodes to prevent memory spikes on bulk file copies
        private static readonly System.Threading.SemaphoreSlim _iconDecodeSemaphore = new System.Threading.SemaphoreSlim(2, 2);

        private bool _isPaginating = false;
        private bool _isDatabaseWriteSuspended = false;
        private bool _isSearchActive = false;

        public bool IsSearchActive
        {
            get => _isSearchActive;
            set
            {
                if (_isSearchActive != value)
                {
                    _isSearchActive = value;
                    OnPropertyChanged(nameof(IsSearchActive));
                }
            }
        }

        public bool IsDatabaseWriteSuspended
        {
            get => _isDatabaseWriteSuspended;
            set => _isDatabaseWriteSuspended = value;
        }

        private bool _isScrolling = false;
        public bool IsScrolling
        {
            get => _isScrolling;
            set
            {
                if (_isScrolling != value)
                {
                    _isScrolling = value;
                    OnPropertyChanged(nameof(IsScrolling));
                }
            }
        }

        private bool _allowHover = true;
        public bool AllowHover
        {
            get => _allowHover;
            set
            {
                if (_allowHover != value)
                {
                    _allowHover = value;
                    OnPropertyChanged(nameof(AllowHover));
                }
            }
        }




        /// <summary>
        /// Loads persisted clipboard history from disk and rebuilds Icon previews.
        /// Called once at app startup asynchronously.
        /// </summary>

        public async Task LoadPersistedHistoryAsync()
        {
            await LoadPinnedItemsAsync();
            
            _isPaginating = true;
            try
            {
                // Fire and forget sandbox scavenger in the background
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try { Classes.ClipboardHistoryManager.ScavengeSandboxDirectories(); }
                    catch { }
                });

                var items = await System.Threading.Tasks.Task.Run(() => Classes.ClipboardHistoryManager.LoadHistory());
                
                // Build lookup of items already loaded (e.g. pinned items from LoadPinnedItems)
                var existingKeys = new HashSet<string>();
                foreach (var existing in DroppedItems)
                {
                    string key = GetDeduplicationKey(existing);
                    if (!string.IsNullOrEmpty(key)) existingKeys.Add(key);
                }
                
                var allItems = new List<ClipboardItem>();
                var itemsNeedingIcons = new List<ClipboardItem>();
                foreach (var item in items)
                {
                    try
                    {
                        if (IsEffectivelyEmpty(item)) continue;
                        
                        string itemKey = GetDeduplicationKey(item);
                        if (!string.IsNullOrEmpty(itemKey) && !existingKeys.Add(itemKey))
                            continue;

                        if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(item.RawContent))
                            item.FileName = item.RawContent.Length > 800 ? item.RawContent.Substring(0, 800) + "..." : item.RawContent;

                        item.EvaluateSmartActions();

                        // Regenerate non-serialized icons (Icon is [JsonIgnore])
                        if (item.IsPassword)
                            item.GeneratePasswordIcon();
                        else if (item.ItemType == ClipboardItemType.Folder)
                            item.GenerateFolderIcon();
                        else if (item.ItemType == ClipboardItemType.Document && item.Extension == ".MD")
                            item.GenerateMarkdownIcon();

                        allItems.Add(item);
                    }
                    catch { }
                }

                // Combine the already loaded pinned items and newly loaded history items
                var combinedItems = new List<ClipboardItem>();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    combinedItems.AddRange(DroppedItems);
                    DroppedItems.Clear();
                });

                combinedItems.AddRange(allItems);

                // Sort by DateCopied descending to preserve chronological order across both pinned and normal items
                var sortedItems = combinedItems.OrderByDescending(x => x.DateCopied).ToList();

                if (sortedItems.Count > 0)
                {
                    // PERF: Load the first 40 items synchronously to make startup summon instantaneous
                    var initialBatch = sortedItems.Take(40).ToList();
                    await Application.Current.Dispatcher.InvokeAsync(() => DroppedItems.AddRange(initialBatch));

                    // Stream the remaining items in background chunks to keep UI 100% responsive
                    var remainingItems = sortedItems.Skip(40).ToList();
                    if (remainingItems.Count > 0)
                    {
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            const int chunkSize = 50;
                            for (int i = 0; i < remainingItems.Count; i += chunkSize)
                            {
                                var chunk = remainingItems.Skip(i).Take(chunkSize).ToList();
                                await System.Threading.Tasks.Task.Delay(50); // Yield UI thread budget
                                await Application.Current.Dispatcher.InvokeAsync(() => DroppedItems.AddRange(chunk));
                            }
                        });
                    }
                }

                OnPropertyChanged(nameof(ShelfVisibility));

                // Auto-save on collection changes (throttled — max once every 2s)
                DroppedItems.CollectionChanged += (s, e) =>
                {
                    if (!_isDatabaseWriteSuspended && !_isPaginating)
                    {
                        SchedulePersistHistory();
                    }
                };

                // Start the auto-cleanup timer
                StartAutoCleanupTimer();

                // Only decode icons for the first ~12 items (visible viewport) at startup,
                // but keep image/QR thumbnails capped at the top 5 to keep RAM extremely low.
                var visibleBatch = sortedItems.Take(12).ToList();
                int startupImageCount = 0;
                foreach (var item in visibleBatch)
                {
                    bool isImage = item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode;
                    bool needsIcon = isImage
                        && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);

                    if (needsIcon)
                    {
                        startupImageCount++;
                        if (startupImageCount > 5)
                        {
                            // Cap image decoding to the top 5 at startup to ensure RAM stays under 50 MB
                            continue;
                        }
                    }

                    bool needsFileIcon = !isImage && (item.ItemType == ClipboardItemType.File || item.ItemType == ClipboardItemType.Document ||
                        item.ItemType == ClipboardItemType.Pdf || item.ItemType == ClipboardItemType.Archive ||
                        item.ItemType == ClipboardItemType.Video || item.ItemType == ClipboardItemType.Audio ||
                        item.ItemType == ClipboardItemType.Presentation) && !string.IsNullOrEmpty(item.FilePath);
                    if (needsIcon || needsFileIcon) itemsNeedingIcons.Add(item);
                }

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
                                    var icon = LoadImageThumbnail(item.FilePath, 300);
                                    if (icon != null)
                                    {
                                        await Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            item.Icon = icon;
                                            item.IsLoadedHighQuality = true;
                                        });
                                    }
                                }
                                else if (!string.IsNullOrEmpty(item.FilePath))
                                {
                                    var icon = GetIcon(item.FilePath);
                                    if (icon != null)
                                        await Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                                }
                            }
                            catch { }
                            finally { _iconDecodeSemaphore.Release(); }
                        }
                    });
                }

                _isPaginating = false;
            }
            finally
            {
                _isPaginating = false;
            }
        }

        /// <summary>
        /// Triggers a debounced save of the current clipboard history.
        /// </summary>
        private void PersistHistory()
        {
            var fullHistory = DroppedItems.ToList();
            Classes.ClipboardHistoryManager.SaveHistoryDebounced(fullHistory);
        }

        // ═══ PERF: Throttled persist — prevents serialization storms during rapid clipboard use ═══
        private System.Threading.Timer? _persistThrottleTimer;
        private volatile bool _persistScheduled;
        private static readonly object _persistLock = new object();

        /// <summary>
        /// Schedules a PersistHistory call with a 2-second cooldown.
        /// Multiple calls within the window are coalesced into a single persist.
        /// The DroppedItems snapshot is taken at fire time (not call time) to capture
        /// the latest state and avoid unnecessary intermediate snapshots.
        /// </summary>
        private void SchedulePersistHistory()
        {
            if (_persistScheduled) return; // Already scheduled — skip
            _persistScheduled = true;

            lock (_persistLock)
            {
                _persistThrottleTimer?.Dispose();
                _persistThrottleTimer = new System.Threading.Timer(_ =>
                {
                    _persistScheduled = false;
                    try
                    {
                        // Take snapshot on dispatcher, then save off-thread
                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                        {
                            if (_isDatabaseWriteSuspended || _isPaginating) return;
                            PersistHistory();
                        });
                    }
                    catch { }
                }, null, 2000, System.Threading.Timeout.Infinite);
            }
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
        private static readonly Regex _rxCode = new Regex(@"(#include\s*[<""]|<iostream>|<stdio\.h>|<stdlib\.h>|<string\.h>|<cstdlib>|<vector>|<map>|<algorithm>|std::|printf\s*\(|scanf\s*\(|malloc\s*\(|free\s*\(|sizeof\s*\(|typedef\s|struct\s+\w+|enum\s+\w+|public\s+class\s|private\s+(void|int|string|static)|protected\s|int\s+main\s*\(|void\s+main\s*\(|using\s+namespace\s|#define\s|#ifdef|#ifndef|#pragma|template\s*<|namespace\s+\w+|def\s+\w+\s*\(|class\s+\w+\s*[(:]\s|import\s+(os|sys|json|re|math|numpy|pandas|flask|django|requests|typing|collections|pathlib|subprocess|asyncio|datetime)|from\s+\w+\s+import|if\s+__name__\s*==|print\s*\(|lambda\s|self\.|__init__|@(staticmethod|classmethod|property|override|Deprecated)|public\s+static\s+(void|int)|System\.(out|in|err)\.|new\s+\w+\s*[(<\[]|throws\s|implements\s|extends\s|interface\s+\w+|abstract\s+class|Console\.\w+|=>\s*\{|=>\s*[^;]+;|\{""|var\s+\w+\s*=|let\s+\w+\s*=|const\s+\w+\s*=|<\/?(html|div|span|script|style|body|head|table|form)|function\s+\w+\s*\(|console\.(log|error|warn)\(|require\s*\(|module\.exports|export\s+(default|const|function|class)|async\s+function|await\s|try\s*\{|catch\s*\(|switch\s*\(|for\s*\(.*;\s*.*;\s*|while\s*\(|SELECT\s+.*\s+FROM|INSERT\s+INTO|UPDATE\s+\w+\s+SET|CREATE\s+TABLE)", RegexOptions.Compiled);
        private static readonly Regex _rxUtmClean = new Regex(@"(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxCpp = new Regex(@"(cout|cin|endl|cerr)\s*<<", RegexOptions.Compiled);
        private static readonly Regex _rxC = new Regex(@"\b(printf|scanf|malloc|free|sizeof|typedef|struct\s+\w+)\s*[\(;]", RegexOptions.Compiled);
        private static readonly Regex _rxPython = new Regex(@"(def\s+\w+\s*\(|import\s+(os|sys|json|re|math|numpy|pandas|flask|django|requests|typing|pathlib)|from\s+\w+\s+import|if\s+__name__\s*==|self\.|__init__|lambda\s|print\s*\(|class\s+\w+\s*[\(:]|@(staticmethod|classmethod|property)|except\s|elif\s|raise\s)", RegexOptions.Compiled);
        private static readonly Regex _rxJava = new Regex(@"(public\s+static\s+void\s+main|System\.(out|in|err)\.|import\s+java\.|throws\s|implements\s|extends\s|interface\s+\w+|abstract\s+class|@Override|@Deprecated|\.println\()", RegexOptions.Compiled);
        private static readonly Regex _rxJs = new Regex(@"(function\s+\w+\s*\(|console\.(log|error|warn)\(|require\s*\(|module\.exports|export\s+(default|const|function|class)|async\s+function|await\s|const\s+\w+\s*=\s*(require|\(|async|\{)|=>\s*\{)", RegexOptions.Compiled);
        private static readonly Regex _rxCs = new Regex(@"(using\s+System|var\s+\w+\s*=\s*new|async\s+Task)", RegexOptions.Compiled);
        private static readonly Regex _rxSql = new Regex(@"(SELECT\s+.*\s+FROM|INSERT\s+INTO|CREATE\s+(TABLE|DATABASE)|ALTER\s+TABLE|WHERE\s+\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxHtml = new Regex(@"<\/?(html|div|span|body|script|style|form|table)[\s>]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxSlashTimer = new Regex(@"^\/\d+$", RegexOptions.Compiled);
        private static readonly Regex _rxFunction = new Regex(@"\b(void|int|string|double|float|bool|var|let|const)?\s*\w+\s*\([^)]*\)\s*({|;|=>)");

        private int _currentMode = 0; // 0=Mini, 1=Medium, 2=Full
        public int CurrentMode
        {
            get => _currentMode;
            set
            {
                if (_currentMode == value) return; // PERF: skip cascading layout notifications when unchanged
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
                if (CurrentMode == 0) return FlyShelf.Classes.SettingsManager.Current.MiniFormHeight;
                if (CurrentMode == 1) return FlyShelf.Classes.SettingsManager.Current.MediumFormHeight;
                return (int)SystemParameters.WorkArea.Height - 100;
            }
        }
        
        public int CurrentFlyShelfWidth
        {
            get
            {
                if (CurrentMode == 0) return FlyShelf.Classes.SettingsManager.Current.MiniFormWidth;
                if (CurrentMode == 1) return FlyShelf.Classes.SettingsManager.Current.MediumFormWidth;
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
        public ICommand TogglePinCommand { get; }
        public ICommand LaunchSandboxCommand { get; }
        public ICommand LaunchTerminalCommand { get; }
        public ICommand OpenInBrowserCommand { get; }
        public ICommand RunAdminTerminalCommand { get; }
        public ICommand ToggleCloudDiscoveryCommand { get; }
        public ICommand CompileNativeCommand { get; }
        public ICommand ConvertDocumentCommand { get; }
        public ICommand ExtractTextCommand { get; }
        public ICommand ExtractTableCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand CopyRawContentCommand { get; }
        public ICommand MergeSelectedPdfsCommand { get; }
        public ICommand OpenFileLocationCommand { get; }
        public ICommand ConvertImageToPdfCommand { get; }
        public ICommand GoogleSearchCommand { get; }
        public ICommand ConvertPdfToWordCommand { get; }
        public ICommand ManualScanQRCodeCommand { get; }
        
        public FlyShelf.Classes.NetworkSyncServer LocalServer { get; private set; }
        public FlyShelf.Classes.DocumentSniffer Sniffer { get; private set; }
        public FlyShelf.Classes.CloudDiscoveryListener CloudListener { get; private set; }

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
            TogglePinCommand = new RelayCommand<ClipboardItem>(TogglePin);
            LaunchSandboxCommand = new RelayCommand<ClipboardItem>(LaunchSandbox);
            LaunchTerminalCommand = new RelayCommand<ClipboardItem>(item => item?.RunInTerminal());
            OpenInBrowserCommand = new RelayCommand<ClipboardItem>(item => item?.OpenInBrowser());
            RunAdminTerminalCommand = new RelayCommand<ClipboardItem>(item => item?.RunAdminTerminal());
            CompileNativeCommand = new RelayCommand<ClipboardItem>(item => item?.CompileAndRunNative());
            ConvertDocumentCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertDocumentTask());
            ConvertImageToPdfCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertImageToPdf());
            GoogleSearchCommand = new RelayCommand<ClipboardItem>(item => item?.GoogleSearch());
            ConvertPdfToWordCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertPdfToWordTask());
            ManualScanQRCodeCommand = new RelayCommand<ClipboardItem>(item => item?.ManualScanQRCode());

            ExtractTextCommand = new RelayCommand<ClipboardItem>(item => item?.ExtractText());
            ExtractTableCommand = new RelayCommand<ClipboardItem>(item => item?.ExtractTable());
            SaveSettingsCommand = new RelayCommand(SaveGlobalSettings);
            ToggleCloudDiscoveryCommand = new RelayCommand(() => {
                FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery = !FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery;
            });
            CopyRawContentCommand = new RelayCommand<ClipboardItem>(item => {
                if (item == null) return;
                try
                {
                    string content = !string.IsNullOrEmpty(item.RawContent) ? item.RawContent : item.FileName;
                    if (!string.IsNullOrEmpty(content))
                        Classes.ClipboardHelper.SafeSetText(content);
                    FlyShelf.Windows.ToastWindow.ShowToast("Copied to clipboard! 📋");
                    try { Classes.AnimationTriggerService.Instance.OnCopy(); } catch { }
                }
                catch { }
            });
            MergeSelectedPdfsCommand = new RelayCommand(() => {
                var pdfs = DroppedItems.Where(i => i.ItemType == ClipboardItemType.Pdf && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
                if (pdfs.Count < 2)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Select at least 2 PDFs to merge.");
                    return;
                }
                var win = new FlyShelf.Windows.PdfMergeWindow(pdfs, this);
                win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
                win.Topmost = true;
                win.Show();
                win.Activate();
                win.Focus();
                win.Topmost = false;
            });
            OpenFileLocationCommand = new RelayCommand<ClipboardItem>(item => {
                if (item == null) return;
                if (item.ItemType == ClipboardItemType.Group)
                {
                    string[] paths = item.RawContent.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    FlyShelf.Classes.ShellExplorerHelper.OpenFilesAndSelect(paths);
                    return;
                }
                if (string.IsNullOrEmpty(item.FilePath)) return;
                
                bool exists = System.IO.File.Exists(item.FilePath) || System.IO.Directory.Exists(item.FilePath);
                if (exists)
                {
                    try
                    {
                        if (item.ItemType == ClipboardItemType.Folder)
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                        }
                        else
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"/select,\"{item.FilePath}\"",
                                UseShellExecute = true
                            });
                        }
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
                        try
                        {
                            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("EXPLORER", $"Fallback open failed: {ex.Message}");
                        }
                    }
                }
            });
            
            LocalServer = new FlyShelf.Classes.NetworkSyncServer(this);
            Sniffer = new FlyShelf.Classes.DocumentSniffer(this);
            CloudListener = new FlyShelf.Classes.CloudDiscoveryListener(this);
            

            
            FlyShelf.Classes.SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.EnableLocalNetworkSync))
                {
                    if (FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync) LocalServer.Start();
                    else LocalServer.Stop();
                    OnPropertyChanged(nameof(LocalServer));
                }
                else if (e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.EnableLocalLAN))
                {
                    // LAN toggle changed — auto-manage the master server toggle
                    bool lanOn = FlyShelf.Classes.SettingsManager.Current.EnableLocalLAN;
                    bool cfOn = FlyShelf.Classes.SettingsManager.Current.EnableGlobalCloudflare;
                    
                    if (lanOn || cfOn)
                    {
                        // At least one transport active — ensure server is running
                        if (!FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync)
                            FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync = true;
                    }
                    else
                    {
                        // Both transports off — stop the server
                        FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync = false;
                    }
                    
                    // Force PeerManager to re-handshake with new transport preference
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        if (FlyShelf.Classes.PeerManager.Instance != null) await FlyShelf.Classes.PeerManager.Instance.ForceResync();
                    });
                    FlyShelf.Classes.Logger.LogAction("SETTINGS", $"LAN transport: {(lanOn ? "ON" : "OFF")}");
                }
                else if (e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.EnableCloudDiscovery))
                {
                    if (FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery)
                    {
                        CloudListener.StartPolling();
                    }
                    else
                    {
                        CloudListener.StopPolling();
                    }
                }
                else if (e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.MiniFormWidth) ||
                         e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.MiniFormHeight) ||
                         e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.MediumFormWidth) ||
                         e.PropertyName == nameof(FlyShelf.Classes.AdvanceSettings.MediumFormHeight))
                {
                    OnPropertyChanged(nameof(CurrentFlyShelfMaxHeight));
                    OnPropertyChanged(nameof(CurrentFlyShelfWidth));
                }
            };
            
            // Background Boot Optimization: Shift heavy DNS polling, port binding, and I/O sniffing completely off 
            // the main UI Constructor thread so FlyShelf can bootstrap in under 50ms natively!
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Auto-reconcile: server should be up if either transport is on
                bool lanOn = FlyShelf.Classes.SettingsManager.Current.EnableLocalLAN;
                bool cfOn = FlyShelf.Classes.SettingsManager.Current.EnableGlobalCloudflare;
                if ((lanOn || cfOn) && !FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync)
                {
                    FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync = true;
                }
                
                if (FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync) 
                {
                    LocalServer.Start();
                }
                FlyShelf.Classes.SyncQueue.Start(); // Guaranteed-delivery sync queue
                Sniffer.StartSniffing();
                if (FlyShelf.Classes.SettingsManager.Current.EnableCloudDiscovery)
                {
                    CloudListener.StartPolling();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);

            DroppedItems.CollectionChanged += (s, e) =>
            {
                UpdateFirstTenFlags();
            };
        }

        private System.Windows.Threading.DispatcherTimer? _firstTenDebounceTimer;

        public void UpdateFirstTenFlags()
        {
            // PERF: Only the first 11 items can ever change their IsFirstTenItem state.
            // Items beyond index 10 are already false and won't fire PropertyChanged.
            int limit = Math.Min(DroppedItems.Count, 11);
            for (int i = 0; i < limit; i++)
            {
                var item = DroppedItems[i];
                if (item != null)
                {
                    item.IsFirstTenItem = i < 10;
                }
            }
        }



    

        /// <summary>Moves an item to the top of the list without triggering clipboard copy or sync.</summary>
        public void MoveItemToTop(ClipboardItem item)
        {
            if (item == null || !DroppedItems.Contains(item)) return;
            
            int oldIndex = DroppedItems.IndexOf(item);
            if (oldIndex == 0) return; // Already at top
            
            var oldDate = item.DateCopied;
            // Update timestamp so it sorts as newest
            item.DateCopied = DateTime.Now;
            
            // Move without triggering add/sync logic
            DroppedItems.Move(oldIndex, 0);
            
            // Persist the updated order via debounced JSON save
            PersistHistory();
        }

        /// <summary>
        /// Creates a progress placeholder card at index 0 for incoming file transfers â‰¥ 10MB.
        /// The placeholder is visible in the UI and displays live download progress.
        /// Call SwapPlaceholderWithCompleted() when the transfer finishes.
        /// </summary>
        public ClipboardItem CreateTransferPlaceholder(string fileName, long totalBytes, string sourceDevice, string transferMethod, string sourceDeviceType)
        {
            var placeholder = new ClipboardItem
            {
                FileName = $"⏳ Receiving {fileName}...",
                Extension = "DOWNLOADING",
                ItemType = ClipboardItemType.File,
                FormattedSize = FormatBytesStatic(totalBytes),
                TransferProgress = 0.1,
                TransferStatusText = $"Connecting to {sourceDevice}...",
                RawContent = $"⏳ Downloading from {sourceDevice}...",
                SourceDeviceName = sourceDevice,
                SourceDeviceType = sourceDeviceType,
                TransferMethod = transferMethod
            };
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                DroppedItems.Insert(0, placeholder);
                OnPropertyChanged(nameof(ShelfVisibility));
            });
            return placeholder;
        }

        /// <summary>
        /// Replaces a progress placeholder with the completed ClipboardItem at the same position.
        /// Writes the file to OS clipboard and persists to JSON history.
        /// </summary>
        public void SwapPlaceholderWithCompleted(ClipboardItem placeholder, ClipboardItem completed)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                int idx = DroppedItems.IndexOf(placeholder);
                if (idx >= 0)
                {
                    DroppedItems.RemoveAt(idx);
                    DroppedItems.Insert(idx, completed);
                }
                else
                {
                    // Placeholder was removed (e.g. user deleted it) — insert at top
                    DroppedItems.Insert(0, completed);
                }
                OnPropertyChanged(nameof(ShelfVisibility));
            });
        }

        public static string FormatBytesStatic(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

    }

    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotification = false;

        public BulkObservableCollection() : base() { }
        public BulkObservableCollection(IEnumerable<T> collection) : base(collection) { }

        protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotification)
            {
                base.OnCollectionChanged(e);
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (!_suppressNotification)
            {
                base.OnPropertyChanged(e);
            }
        }

        public void AddRange(IEnumerable<T> range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));

            _suppressNotification = true;
            try
            {
                foreach (var item in range)
                {
                    Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
                OnPropertyChanged(new PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
        }

        public void InsertRange(int index, IEnumerable<T> range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));

            _suppressNotification = true;
            try
            {
                int current = index;
                foreach (var item in range)
                {
                    Insert(current++, item);
                }
            }
            finally
            {
                _suppressNotification = false;
                OnPropertyChanged(new PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
        }

        public void RemoveRange(IEnumerable<T> range)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));

            _suppressNotification = true;
            try
            {
                foreach (var item in range)
                {
                    Remove(item);
                }
            }
            finally
            {
                _suppressNotification = false;
                OnPropertyChanged(new PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
        }
    }
}

