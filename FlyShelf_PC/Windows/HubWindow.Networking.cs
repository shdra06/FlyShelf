// ---------------------------------------------------------------
// HubWindow — Networking Command Center Handlers
// Inner tab switching (Devices/Queue/History/Nearby), file staging,
// transfer history UI, nearby device scanning, and send-to context menus.
// Split from HubWindow.Advanced.cs for modularity.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using FlyShelf.ViewModels;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private string _activeNetworkSubTab = "Devices";
        private DispatcherTimer? _networkRefreshTimer;
        private DispatcherTimer? _historyRefreshTimer = null;

        /// <summary>
        /// Initialize the Networking command center — called from HubWindow constructor after InitializeComponent.
        /// </summary>
        private void InitializeNetworkingHub()
        {
            try
            {
                // Initialize singletons
                if (TransferHistory.Instance == null)
                    _ = new TransferHistory();
                if (NetworkFileQueue.Instance == null)
                    _ = new NetworkFileQueue();

                // Wire up File Queue tab buttons
                if (FileQueueAddBtn != null) FileQueueAddBtn.Click += AddFilesToQueue_Click;
                if (FileQueueSendAllBtn != null) FileQueueSendAllBtn.Click += SendAllToAllPeers_Click;
                if (FileQueueClearBtn != null) FileQueueClearBtn.Click += ClearQueue_Click;

                // Wire up drag-drop on the queue drop zone
                if (FileQueueDropZone != null)
                {
                    FileQueueDropZone.AllowDrop = true;
                    FileQueueDropZone.Drop += QueueDrop_Handler;
                    FileQueueDropZone.DragOver += QueueDragOver_Handler;
                }

                // Wire up History tab buttons
                if (HistoryExportCsvBtn != null) HistoryExportCsvBtn.Click += ExportHistory_Click;
                if (HistoryClearBtn != null) HistoryClearBtn.Click += ClearHistory_Click;
                if (HistorySearchBox != null) HistorySearchBox.TextChanged += HistorySearch_TextChanged;

                // Wire up History filter RadioButtons
                if (HistoryFilterAll != null) HistoryFilterAll.Checked += (s, e) => { _historyFilter = "All"; RefreshHistoryDisplay(); };
                if (HistoryFilterSent != null) HistoryFilterSent.Checked += (s, e) => { _historyFilter = "Sent"; RefreshHistoryDisplay(); };
                if (HistoryFilterReceived != null) HistoryFilterReceived.Checked += (s, e) => { _historyFilter = "Received"; RefreshHistoryDisplay(); };
                if (HistoryFilterFailed != null) HistoryFilterFailed.Checked += (s, e) => { _historyFilter = "Failed"; RefreshHistoryDisplay(); };

                // Wire up Nearby tab buttons
                if (NearbyScanBtn != null) NearbyScanBtn.Click += ScanNearby_Click;
                if (NearbyManualConnectBtn != null) NearbyManualConnectBtn.Click += ConnectByIp_Click;

                // 3-second refresh timer for queue/history displays
                _networkRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _networkRefreshTimer.Tick += (s, e) =>
                {
                    if (NetworkGrid?.Visibility == Visibility.Visible)
                    {
                        RefreshNetworkQueueDisplay();
                        RefreshNetworkStatusBar();
                        // Auto-refresh Nearby tab if visible
                        if (_activeNetworkSubTab == "Nearby")
                            RefreshNearbyDevices();
                    }
                };
                _networkRefreshTimer.Start();

                // H-08: Stop timer when window is hidden to avoid wasted work
                this.IsVisibleChanged += (s, e) =>
                {
                    if (IsVisible)
                        _networkRefreshTimer?.Start();
                    else
                        _networkRefreshTimer?.Stop();
                };

                Logger.LogAction("NETWORK_HUB","Networking Command Center initialized");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK_HUB", $"Init error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // INNER TAB SWITCHING
        // ═══════════════════════════════════════════════════════════════

        private void NetworkSubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                _activeNetworkSubTab = tag;
                SwitchNetworkSubTab(tag);
            }
        }

        private void SwitchNetworkSubTab(string tag)
        {
            // Visibility is handled by NetworkInnerTab_Checked in HubWindow.xaml.cs
            // Here we programmatically check the correct RadioButton and refresh data
            if (tag == "Devices" && NetworkTabDevices != null) NetworkTabDevices.IsChecked = true;
            if (tag == "Queue" && NetworkTabFileQueue != null) NetworkTabFileQueue.IsChecked = true;
            if (tag == "History" && NetworkTabHistory != null) NetworkTabHistory.IsChecked = true;
            if (tag == "Nearby" && NetworkTabNearby != null) NetworkTabNearby.IsChecked = true;

            // ─── Fast peer status refresh: 5s timer when Devices tab is active ───
            if (tag == "Devices")
            {
                if (_peerFastRefreshTimer == null)
                {
                    _peerFastRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    _peerFastRefreshTimer.Tick += (s, ev) =>
                    {
                        RefreshPairedDevicesList();
                        RefreshDevices_Click(null, null);
                    };
                }
                _peerFastRefreshTimer.Start();
            }
            else
            {
                _peerFastRefreshTimer?.Stop();
            }

            // Refresh data when switching to a tab
            if (tag == "Queue") RefreshNetworkQueueDisplay();
            if (tag == "History") RefreshHistoryDisplay();
            if (tag == "Nearby") RefreshNearbyDevices();
            if (tag == "Devices")
            {
                RefreshDevices_Click(null, null);
                RefreshPairedDevicesList();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // NETWORK STATUS BAR
        // ═══════════════════════════════════════════════════════════════

        private void RefreshNetworkStatusBar()
        {
            try
            {
                // Update peer count badge in header
                if (NetStatusPeerCount != null)
                {
                    int aliveCount = PeerManager.Instance?.AliveCount ?? 0;
                    NetStatusPeerCount.Text = $"{aliveCount} peer{(aliveCount != 1 ? "s" : "")}";
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void NetworkOpenTransferManager_Click(object sender, RoutedEventArgs e)
        {
            try { TransferManagerWindow.ShowOrActivate(); } catch { } // Best-effort: failure is acceptable
        }

        // ═══════════════════════════════════════════════════════════════
        // FILE QUEUE TAB
        // ═══════════════════════════════════════════════════════════════

        private void RefreshNetworkQueueDisplay()
        {
            try
            {
                if (FileQueueItemsControl != null && NetworkFileQueue.Instance != null)
                {
                    FileQueueItemsControl.ItemsSource = NetworkFileQueue.Instance.StagedFiles;
                }
                if (FileQueueCountBadge != null && NetworkFileQueue.Instance != null)
                {
                    int count = NetworkFileQueue.Instance.StagedFiles.Count;
                    FileQueueCountBadge.Text = $"{count} file{(count != 1 ? "s" : "")}";
                }
                if (FileQueueEmptyState != null && NetworkFileQueue.Instance != null)
                {
                    FileQueueEmptyState.Visibility = NetworkFileQueue.Instance.StagedFiles.Count == 0
                        ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch { } // Best-effort: failure is acceptable

            // Update transfer speed dashboard
            try
            {
                var manager = LanTransferManager.Instance;
                if (manager != null)
                {
                    // M-09: Snapshot ObservableCollection before LINQ to prevent concurrent modification
                    var transfersSnapshot = manager.ActiveTransfers.ToArray();
                    var activeSessions = transfersSnapshot.Where(s => s.IsActive).ToArray();
                    double totalUpload = activeSessions.Where(s => s.Direction == TransferDirection.Send).Sum(s => s.SpeedBps);
                    double totalDownload = activeSessions.Where(s => s.Direction == TransferDirection.Receive).Sum(s => s.SpeedBps);
                    int activeCount = activeSessions.Length;

                    if (DashboardUploadSpeed != null) DashboardUploadSpeed.Text = LanTransferSession.FormatSpeed(totalUpload);
                    if (DashboardDownloadSpeed != null) DashboardDownloadSpeed.Text = LanTransferSession.FormatSpeed(totalDownload);
                    if (DashboardActiveCount != null) DashboardActiveCount.Text = $"{activeCount} active";
                    if (DashboardActiveDot != null) DashboardActiveDot.Visibility = activeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (TransferSpeedDashboard != null) TransferSpeedDashboard.Visibility = (activeCount > 0 || transfersSnapshot.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("HUB_NET", $"Speed dashboard update failed: {ex.Message}");
            }
        }

        private void AddFilesToQueue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Multiselect = true,
                    Title = "Add Files to Network Queue",
                    Filter = "All Files (*.*)|*.*"
                };
                if (dialog.ShowDialog() == true)
                {
                    NetworkFileQueue.Instance?.StageFiles(dialog.FileNames);
                    RefreshNetworkQueueDisplay();
                    Logger.LogAction("NETWORK_HUB", $"Queued {dialog.FileNames.Length} file(s) for sending");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK_HUB", $"Add files error: {ex.Message}");
            }
        }

        private async void SendAllToAllPeers_Click(object sender, RoutedEventArgs e)
        {
            if (NetworkFileQueue.Instance == null) return;
            try
            {
                if (sender is Button btn)
                {
                    btn.IsEnabled = false;
                    btn.Content ="Sending...";
                }

                await NetworkFileQueue.Instance.SendAllToAll();
                RefreshNetworkQueueDisplay();

                if (sender is Button btn2)
                {
                    btn2.IsEnabled = true;
                    btn2.Content ="Send All to All Peers";
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK_HUB", $"Send all error: {ex.Message}");
                if (sender is Button btn3) { btn3.IsEnabled = true; btn3.Content ="Send All to All Peers"; }
            }
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            NetworkFileQueue.Instance?.ClearAll();
            RefreshNetworkQueueDisplay();
        }

        private void ClearSentFromQueue_Click(object sender, RoutedEventArgs e)
        {
            NetworkFileQueue.Instance?.ClearSent();
            RefreshNetworkQueueDisplay();
        }

        private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StagedFile file)
            {
                NetworkFileQueue.Instance?.Remove(file);
                RefreshNetworkQueueDisplay();
            }
        }

        private async void SendQueueItemToAll_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
                if (sender is FrameworkElement fe && fe.DataContext is StagedFile file)
                {
                    await (NetworkFileQueue.Instance?.SendFileToAll(file) ?? System.Threading.Tasks.Task.CompletedTask);
                    RefreshNetworkQueueDisplay();
                }
            });
        }

        private void OpenQueueItemFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is StagedFile file)
            {
                try
                {
                    if (File.Exists(file.FilePath))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file.FilePath}\"");
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }
        }

        // Queue drag-drop
        private void QueueDrop_Handler(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        NetworkFileQueue.Instance?.StageFiles(files);
                        RefreshNetworkQueueDisplay();
                        Logger.LogAction("NETWORK_HUB", $"Drag-dropped {files.Length} file(s) to queue");
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void QueueDragOver_Handler(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // ═══════════════════════════════════════════════════════════════
        // DEVICE CONTEXT MENU — SEND FILE TO SPECIFIC DEVICE
        // ═══════════════════════════════════════════════════════════════

        private void DeviceSendFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                string? deviceId = fe.Tag as string;
                if (string.IsNullOrEmpty(deviceId)) return;

                var peer = PeerManager.Instance?.ConnectedPeers.Values
                    .FirstOrDefault(p => p.DeviceId == deviceId && p.IsAlive);
                if (peer == null)
                {
                    ToastWindow.ShowToast("Device not connected");
                    return;
                }

                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Multiselect = true,
                    Title = $"Send Files to {peer.DeviceName}",
                    Filter = "All Files (*.*)|*.*"
                };
                if (dialog.ShowDialog() == true)
                {
                    foreach (var filePath in dialog.FileNames)
                    {
                        NetworkFileQueue.Instance?.StageFile(filePath);
                        var stagedFile = NetworkFileQueue.Instance?.StagedFiles
                            .FirstOrDefault(f => f.FilePath == filePath);
                        if (stagedFile != null)
                        {
                            _ = NetworkFileQueue.Instance?.SendFile(stagedFile, peer);
                        }
                    }
                    RefreshNetworkQueueDisplay();
                }
            }
        }

        private void DeviceCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string url)
            {
                try
                {
                    ClipboardHelper.SafeSetText(url);
                    ToastWindow.ShowToast("IP copied to clipboard");
                }
                catch { } // Best-effort: failure is acceptable
            }
        }

        private void DeviceViewHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string deviceId)
            {
            // Switch to History tab and filter by device
                _activeNetworkSubTab = "History";
                SwitchNetworkSubTab("History");
                // Refresh with device filter
                RefreshHistoryDisplay(deviceId);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // HISTORY TAB
        // ═══════════════════════════════════════════════════════════════

        private string _historyFilter = "All";
        private string _historySearchQuery = "";

        private void RefreshHistoryDisplay(string? filterDeviceId = null)
        {
            try
            {
                if (TransferHistory.Instance == null || HistoryItemsControl == null) return;

                IEnumerable<TransferHistoryEntry> entries = TransferHistory.Instance.Entries;

                // Apply status filter
                if (_historyFilter == "Sent")
                    entries = entries.Where(e => e.Direction == "Sent");
                else if (_historyFilter == "Received")
                    entries = entries.Where(e => e.Direction == "Received");
                else if (_historyFilter == "Failed")
                    entries = entries.Where(e => e.Status == "Failed" || e.Status =="Cancelled");

                // Apply device filter
                if (!string.IsNullOrEmpty(filterDeviceId))
                    entries = entries.Where(e => e.PeerDeviceId == filterDeviceId);

                // Apply search
                if (!string.IsNullOrEmpty(_historySearchQuery))
                    entries = TransferHistory.Instance.Search(_historySearchQuery);

                HistoryItemsControl.ItemsSource = entries.ToList();

                // Update stats
                if (HistoryStatsSent != null)
                {
                    long totalSent = TransferHistory.Instance.TotalBytesSent;
                    HistoryStatsSent.Text = $"Sent: {TransferHistory.Instance.TotalSentCount} files ({FormatSize(totalSent)})";
                }
                if (HistoryStatsReceived != null)
                {
                    long totalReceived = TransferHistory.Instance.TotalBytesReceived;
                    HistoryStatsReceived.Text = $"Received: {TransferHistory.Instance.TotalReceivedCount} files ({FormatSize(totalReceived)})";
                }

                if (HistoryEmptyState != null)
                {
                    HistoryEmptyState.Visibility = !entries.Any() ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void HistoryFilterTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string filter)
            {
                _historyFilter = filter;
                RefreshHistoryDisplay();
            }
        }

        private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _historySearchQuery = tb.Text;
                RefreshHistoryDisplay();
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            TransferHistory.Instance?.ClearAll();
            RefreshHistoryDisplay();
        }

        private async void ExportHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string csv = TransferHistory.Instance?.ExportCsv() ?? "";
                if (!string.IsNullOrEmpty(csv))
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = $"FlyShelf_TransferHistory_{DateTime.Now:yyyyMMdd}",
                        DefaultExt = ".csv",
                        Filter = "CSV Files (*.csv)|*.csv"
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        await File.WriteAllTextAsync(dialog.FileName, csv);
                        ToastWindow.ShowToast($"History exported to {Path.GetFileName(dialog.FileName)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK_HUB", $"Export error: {ex.Message}");
            }
        }

        private async void RetryTransfer_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
                if (sender is FrameworkElement fe && fe.DataContext is TransferHistoryEntry entry)
                {
                    // Re-stage the file and send
                    if (!string.IsNullOrEmpty(entry.FileName))
                    {
                        // Try to find the file — history entries may not have the full path
                        // so we show a toast indicating retry
                        ToastWindow.ShowToast($"Retry not available yet  re-send via File Queue");
                    }
                }
                await System.Threading.Tasks.Task.CompletedTask;
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // NEARBY TAB
        // ═══════════════════════════════════════════════════════════════

        private void ScanNearby_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn)
                {
                    btn.IsEnabled = false;
                    btn.Content ="Scanning...";
                }

                // Trigger UDP broadcast for nearby discovery
                if (NearbyDiscovery.Instance != null)
                {
                    _ = NearbyDiscovery.Instance.BroadcastProbe();
                }
                else
                {
                    ToastWindow.ShowToast("Nearby discovery not available yet");
                }

                // Re-enable after 5 seconds
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += (s, ev) =>
                {
                    timer.Stop();
                    if (sender is Button btn2)
                    {
                        btn2.IsEnabled = true;
                        btn2.Content ="Scan for Nearby Devices";
                    }
                    RefreshNearbyDevices();
                };
                timer.Start();
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void RefreshNearbyDevices()
        {
            try
            {
                if (NearbyDevicesPanel != null && NearbyDiscovery.Instance != null)
                {
                    NearbyDevicesPanel.ItemsSource = NearbyDiscovery.Instance.DiscoveredDevices.ToList();
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private async void ConnectByIp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? ip = NearbyManualIpInput?.Text?.Trim();
                if (string.IsNullOrEmpty(ip))
                {
                    ToastWindow.ShowToast("Enter an IP address");
                    return;
                }

                // Try to connect to the device at the given IP
                int port = NetworkSyncServer.Instance?.CurrentPort ?? 8080;
                string url = ip.Contains(':') ? $"http://{ip}" : $"http://{ip}:{port}";

                ToastWindow.ShowToast($"Connecting to {ip}...");

                // Use PeerManager to add manual peer and attempt handshake
                if (PeerManager.Instance != null)
                {
                    string deviceId = $"manual_{ip.Replace('.', '_').Replace(':', '_')}";
                    bool success = await PeerManager.Instance.AddManualPeer(deviceId, ip, url);
                    if (success)
                    {
                        ToastWindow.ShowToast($"Connected to device at {ip}");
                        RefreshDevices_Click(null, null);
                    }
                    else
                    {
                        ToastWindow.ShowToast($"Could not reach {ip}  check IP and firewall");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK_HUB", $"Manual connect error: {ex.Message}");
                ToastWindow.ShowToast($"Connection failed: {ex.Message}");
            }
        }

        private void AddNearbyDevice_Click(object sender, RoutedEventArgs e)
        {
            // Add a discovered nearby device to session-only pairing
            if (sender is FrameworkElement fe && fe.DataContext is NearbyDeviceInfo device)
            {
                try
                {
                    _ = NearbyDiscovery.Instance?.ConnectToDevice(device);
                    ToastWindow.ShowToast($"Connecting to {device.DeviceName}...");
                }
                catch { } // Best-effort: failure is acceptable
            }
        }

        private async void PairNearbyDevice_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
                if (sender is FrameworkElement fe && fe.DataContext is NearbyDeviceInfo device)
                {
                    ToastWindow.ShowToast($"Sending pair request to {device.DeviceName}...");

                    // Generate nonce for this pairing session
                    string nonce = Guid.NewGuid().ToString("N");
                    string myDeviceId = SettingsManager.Current.DeviceId ?? Environment.MachineName;
                    string myDeviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

                    var pairRequest = new
                    {
                        deviceId = myDeviceId,
                        deviceName = myDeviceName,
                        deviceType = "PC",
                        nonce = nonce,
                        httpPort = NetworkSyncServer.Instance?.CurrentPort ?? 8080,
                        transferPort = LanTransferEngine.TRANSFER_PORT
                    };

                    string url = $"http://{device.IpAddress}:{device.HttpPort}/api/lan/pair-request";
                    var http = HttpClientPool.Download; // Use Download (10min timeout)  60s CTS limits actual wait
                    var content = new System.Net.Http.StringContent(
                        System.Text.Json.JsonSerializer.Serialize(pairRequest),
                        System.Text.Encoding.UTF8, "application/json");

                    using var pairCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60)); // 60s for user to accept
                    var response = await http.PostAsync(url, content, pairCts.Token);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;
                    bool accepted = root.TryGetProperty("accepted", out var ap) && ap.GetBoolean();

                    if (accepted)
                    {
                        string remoteDeviceId = root.TryGetProperty("deviceId", out var rid) ? rid.GetString() ?? device.DeviceId : device.DeviceId;
                        string remoteDeviceName = root.TryGetProperty("deviceName", out var rn) ? rn.GetString() ?? device.DeviceName : device.DeviceName;
                        string sharedSecret = root.TryGetProperty("sharedSecret", out var ss) ? ss.GetString() ??"" : "";
                        int remoteHttpPort = root.TryGetProperty("httpPort", out var rhp) ? rhp.GetInt32() : device.HttpPort;
                        int remoteTransferPort = root.TryGetProperty("transferPort", out var rtp) ? rtp.GetInt32() : device.TransferPort;

                        // Store paired device locally
                        DevicePairingManager.PairDeviceViaLan(remoteDeviceId, remoteDeviceName, 
                            device.DeviceType, device.IpAddress, sharedSecret);

                        // Auto-connect via PeerManager
                        string lanUrl = $"http://{device.IpAddress}:{remoteHttpPort}";
                        _ = PeerManager.Instance?.AddManualPeer(remoteDeviceId, remoteDeviceName, lanUrl, remoteTransferPort);

                        device.IsPaired = true;
                        device.IsConnected = true;
                        RefreshNearbyDevices();
                        RefreshPairedDevicesList();

                        ToastWindow.ShowToast($"Paired with {remoteDeviceName} via LAN!");
                    }
                    else
                    {
                        ToastWindow.ShowToast($"{device.DeviceName} rejected the pair request");
                    }
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // CLIPBOARD AUTO-STAGING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Called from the clipboard monitor when a new file is copied.
        /// Auto-stages it into the network file queue.
        /// </summary>
        public void AutoStageClipboardFile(ClipboardItem item)
        {
            try
            {
                if (NetworkFileQueue.Instance == null) return;
                if (item == null) return;

                string? filePath = item.FilePath;
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                // Don't auto-stage if it's a tiny text file or temp file
                var fi = new FileInfo(filePath);
                if (fi.Length < 1024) return; // Skip files < 1KB
                if (filePath.Contains("FlyShelf_Chunks", StringComparison.Ordinal) || filePath.Contains("FS_Upload_", StringComparison.Ordinal)) return;

                Dispatcher.InvokeAsync(() =>
                {
                    NetworkFileQueue.Instance.StageFile(filePath);
                    RefreshNetworkQueueDisplay();
                    Logger.LogAction("NETWORK_HUB", $"Auto-staged clipboard file: {fi.Name} ({FormatSize(fi.Length)})");
                });
            }
            catch { } // Best-effort: failure is acceptable
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
