using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Classes;
using FlyShelf.ViewModels;
using MicaWPF.Controls;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Threading.Tasks;
using static FlyShelf.Classes.NativeMethods;

namespace FlyShelf.Windows
{
    public partial class HubWindow : MicaWindow
    {
        private FlyShelfViewModel _viewModel;
        private UpdateManager _updateManager = new UpdateManager();
        private bool _updateDownloaded = false;
        private System.Windows.Threading.DispatcherTimer? _deviceRefreshTimer;
        private Action<string>? _devicePairedHandler;
        private System.Windows.Threading.DispatcherTimer? _pairingHandshakeTimer;
        private Action<string, string>? _peerConnectedHandler;
        private Action<string>? _peerDisconnectedHandler;
        private Action<string, string>? _transportSwitchedHandler;

        public HubWindow(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.DroppedItems.CollectionChanged += DroppedItems_CollectionChanged;
            // Theme override dictionary modification thrashes visual tree at construction,
            // so we only apply the theme in OnSourceInitialized when the handle is active.

            // Auto-refresh device list when a new device pairs
            _devicePairedHandler = (deviceName) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshDevices_Click(null, null);
                    RefreshPairedDevicesList();
                });
            };
            DevicePairingManager.OnDevicePaired += _devicePairedHandler;

            // Real-time peer status updates — refresh UI when peers connect/disconnect
            _peerConnectedHandler = (deviceId, transport) => Dispatcher.InvokeAsync(() => RefreshPairedDevicesList());
            _peerDisconnectedHandler = (deviceId) => Dispatcher.InvokeAsync(() => RefreshPairedDevicesList());
            _transportSwitchedHandler = (deviceId, newTransport) => Dispatcher.InvokeAsync(() => RefreshPairedDevicesList());

            if (PeerManager.Instance != null)
            {
                PeerManager.Instance.PeerConnected += _peerConnectedHandler;
                PeerManager.Instance.PeerDisconnected += _peerDisconnectedHandler;
                PeerManager.Instance.TransportSwitched += _transportSwitchedHandler;
            }

            // Show real version from assembly
            string v = UpdateManager.CurrentVersion;
            VersionBadgeText.Text = $"v{v}";
            CurrentVersionText.Text = $"v{v}";

            // Wire up UpdateManager events
            _updateManager.StatusChanged += (msg) => Dispatcher.Invoke(() =>
            {
                UpdateStatusText.Text = msg;
                UpdateProgressPanel.Visibility = Visibility.Visible;
            });
            _updateManager.DownloadProgressChanged += (pct) => Dispatcher.Invoke(() =>
            {
                UpdatePctText.Text = $"{pct}%";
                // Animate progress bar width
                double parentWidth = UpdateProgressPanel.ActualWidth - 24; // minus padding
                UpdateProgressBar.Width = Math.Max(0, parentWidth * pct / 100.0);
            });
            _updateManager.UpdateCheckCompleted += (hasUpdate) => Dispatcher.Invoke(async () =>
            {
                if (hasUpdate)
                {
                    LatestVersionText.Text = $"â†’ v{_updateManager.LatestVersion} available!";
                    ChangelogText.Text = _updateManager.Changelog;
                    ChangelogPanel.Visibility = Visibility.Visible;
                    UpdateBtn.Content = "Downloading...";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Visible;

                    // Auto-download immediately
                    bool success = await _updateManager.DownloadAndApplyUpdateAsync();
                    if (success)
                    {
                        UpdateBtn.Content = "Restarting...";
                        UpdateStatusText.Text = "âœ… Update downloaded! Restarting now...";
                        UpdatePctText.Text = "100%";

                        // Auto-apply after a brief moment so user sees the status
                        await Task.Delay(1500);
                        _updateManager.ApplyUpdateAndRestart();
                    }
                    else
                    {
                        UpdateBtn.Content = "Retry Download";
                        UpdateBtn.IsEnabled = true;
                    }
                }
                else
                {
                    UpdateBtn.Content = "âœ“ Up to Date";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Collapsed;

                    // Re-enable after 3s so user can re-check for newer updates
                    await Task.Delay(3000);
                    UpdateBtn.Content = "Check Again";
                    UpdateBtn.IsEnabled = true;
                }
            });

            // No auto-update at startup â€” manual only via the button

            // Active fast-polling timer for pairing handshakes (runs every 2 seconds when Network tab is visible)
            _pairingHandshakeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pairingHandshakeTimer.Tick += async (s, ev) =>
            {
                try
                {
                    await DevicePairingManager.CheckForHandshakes();
                }
                catch (Exception ex)
                {
                    Logger.LogAction("PAIR TIMER", $"Handshake check failed: {ex.Message}");
                }
            };

            // Auto-refresh device list every 30 seconds + on initial load
            _deviceRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _deviceRefreshTimer.Tick += (s, ev) => RefreshDevices_Click(null, null);
            _deviceRefreshTimer.Start();
            Loaded += (s, ev) =>
            {
                // Defer wiring and refreshing to background dispatcher frames so that
                // the main window shell and layout appear instantly without any initial blocking ticks.
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshDevices_Click(null, null);
                    // LIST profile for clipboard items (very slow, precise)
                    Classes.SmoothScroll.AttachList(HubListView);

                    // Initialize retention ComboBox from saved setting
                    if (RetentionCombo != null)
                    {
                        int retention = SettingsManager.Current.ClipboardRetentionDays;
                        for (int i = 0; i < RetentionCombo.Items.Count; i++)
                        {
                            if (RetentionCombo.Items[i] is ComboBoxItem cbi && cbi.Tag?.ToString() == retention.ToString())
                            {
                                RetentionCombo.SelectedIndex = i;
                                break;
                            }
                        }
                        if (RetentionCombo.SelectedIndex < 0) RetentionCombo.SelectedIndex = 0; // default to 7 days
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            };
            Unloaded += (s, ev) =>
            {
                // Unregister SmoothScroll hooks to prevent memory leaks
                Classes.SmoothScroll.Detach(HubListView);

                // Clean up static rendering hook for kinetic scrolling to prevent memory leak
                if (_isKineticScrolling)
                {
                    _isKineticScrolling = false;
                    CompositionTarget.Rendering -= KineticScroll_Rendering;
                }

                // Stop auto-refresh device timer
                if (_deviceRefreshTimer != null)
                {
                    _deviceRefreshTimer.Stop();
                    _deviceRefreshTimer = null;
                }

                // Stop and clean up fast-polling handshake timer
                if (_pairingHandshakeTimer != null)
                {
                    _pairingHandshakeTimer.Stop();
                    _pairingHandshakeTimer = null;
                }

                // Clean up PeerManager static event subscriptions to prevent memory leak
                if (PeerManager.Instance != null)
                {
                    PeerManager.Instance.PeerConnected -= _peerConnectedHandler;
                    PeerManager.Instance.PeerDisconnected -= _peerDisconnectedHandler;
                    PeerManager.Instance.TransportSwitched -= _transportSwitchedHandler;
                }

                // Clean up DevicePairingManager static event subscription to prevent memory leak
                if (_devicePairedHandler != null)
                {
                    DevicePairingManager.OnDevicePaired -= _devicePairedHandler;
                }
            };
        }

        private System.Windows.Threading.DispatcherTimer? _collectionChangedDebounce;

        private void DroppedItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // PERF: Debounce filter refresh to avoid re-evaluating 2000+ items on every rapid collection change.
            // Uses InvokeAsync at Background priority so the UI thread finishes layout first.
            if (_collectionChangedDebounce == null)
            {
                _collectionChangedDebounce = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _collectionChangedDebounce.Tick += (s, args) =>
                {
                    _collectionChangedDebounce.Stop();
                    ApplyFilters();
                    UpdateEmptyState();
                };
            }
            _collectionChangedDebounce.Stop();
            _collectionChangedDebounce.Start();
        }

        private void UpdateEmptyState()
        {
            if (EmptyStatePanel != null)
            {
                EmptyStatePanel.Visibility = _viewModel.DroppedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                try { Clipboard.SetText(url); btn.Content = "Copied!"; System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => btn.Content = "Copy")); } catch { }
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.DroppedItems.Count == 0) return;
            int count = _viewModel.DroppedItems.Count;
            _viewModel.ClearShelf();
            UpdateEmptyState();
            ToastWindow.ShowToast($"Cleared {count} items ðŸ—‘ï¸");
        }

        private bool _isApplicationShuttingDown = false;

        public void ForceShutdownRelease()
        {
            _isApplicationShuttingDown = true;
            this.Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isApplicationShuttingDown)
            {
                e.Cancel = true;
                this.Hide();

                // Stop fast-polling when window is hidden
                _pairingHandshakeTimer?.Stop();
            }
            base.OnClosing(e);
        }
        
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SuppressWindowBorder();
            ApplyTheme();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SuppressWindowBorder();

            // Resume fast-polling if we are currently on the Network tab
            if (NetworkGrid != null && NetworkGrid.Visibility == Visibility.Visible)
            {
                _pairingHandshakeTimer?.Start();
            }
        }

        /// <summary>
        /// Removes the red DWM window border by setting DWMWA_BORDER_COLOR to DWMWA_COLOR_NONE.
        /// </summary>
        private void SuppressWindowBorder()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int colorNone = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
                }
            }
            catch { }
        }


        private void Nav_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                string tag = fe.Tag as string;
                
                foreach (var item in RootNavigation.MenuItems)
                {
                    if (item is Wpf.Ui.Controls.NavigationViewItem navItem)
                    {
                        navItem.IsActive = ((navItem.Tag as string) == tag);
                    }
                }
                
                if (DashboardGrid != null) DashboardGrid.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
                if (HistoryGrid != null) HistoryGrid.Visibility = tag == "History" ? Visibility.Visible : Visibility.Collapsed;
                if (NetworkGrid != null) NetworkGrid.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
                if (SettingsGrid != null) SettingsGrid.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
                if (LogsGrid != null) LogsGrid.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
                
                if (tag == "Logs") RefreshLogs_Click(null, null);
                if (tag == "Settings") PopulateThemeCombo();
                if (tag == "Network")
                {
                    RefreshDevices_Click(null, null);
                    RefreshQRCode();
                    RefreshPairedDevicesList();
                    // Auto-populate server diagnostics
                    if (ServerDiagnosticsLog != null)
                    {
                        ServerDiagnosticsLog.Text = GetServerDiagnostics();
                    }

                    _pairingHandshakeTimer?.Start();
                }
                else
                {
                    _pairingHandshakeTimer?.Stop();
                }
            }
        }

        private void DashboardCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                foreach (var item in RootNavigation.MenuItems)
                {
                    if (item is FrameworkElement navItem && navItem.Tag as string == tag)
                    {
                        navItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
                        Nav_Click(navItem, null);
                        break;
                    }
                }
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            MessageBox.Show("Configuration updated successfully.", "FlyShelf", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RetentionCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
            {
                if (int.TryParse(selected.Tag.ToString(), out int days))
                {
                    SettingsManager.Current.ClipboardRetentionDays = days;
                    SettingsManager.Save();
                }
            }
        }

        private void ResetClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MediumFormWidth = 400;
            SettingsManager.Current.MediumFormHeight = 650;
            SettingsManager.Save();
        }

        private void ResetFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MiniFormWidth = 260;
            SettingsManager.Current.MiniFormHeight = 260;
            SettingsManager.Save();
        }

        // Clipboard +/- steppers
        private void ClipW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Min(500, SettingsManager.Current.MediumFormWidth + 25); }
        private void ClipW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Max(200, SettingsManager.Current.MediumFormWidth - 25); }
        private void ClipH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Min(700, SettingsManager.Current.MediumFormHeight + 25); }
        private void ClipH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Max(300, SettingsManager.Current.MediumFormHeight - 25); }

        // FlyShelf +/- steppers
        private void DropW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Min(400, SettingsManager.Current.MiniFormWidth + 20); }
        private void DropW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Max(180, SettingsManager.Current.MiniFormWidth - 20); }
        private void DropH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Min(350, SettingsManager.Current.MiniFormHeight + 25); }
        private void DropH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Max(100, SettingsManager.Current.MiniFormHeight - 25); }

        // Live Preview buttons
        private void PreviewClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            // The HubWindow itself IS the clipboard preview â€” just flash to show effect
            this.Width = SettingsManager.Current.MediumFormWidth;
            this.Height = SettingsManager.Current.MediumFormHeight;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void PreviewFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mainWin = Application.Current.MainWindow as MainWindow;
                if (mainWin != null)
                {
                    var screen = SystemParameters.WorkArea;
                    mainWin.ShowNearPosition(screen.Width / 2, screen.Height / 2, 0, false, false);
                }
            }
            catch { }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void RefreshLogs_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                string logsDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");

                // 1. Main activity log
                string logFile = System.IO.Path.Combine(logsDir, "activity_log.txt");
                if (System.IO.File.Exists(logFile))
                {
                    string activityLog = System.IO.File.ReadAllText(logFile);
                    sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                    sb.AppendLine("  ACTIVITY LOG");
                    sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                    sb.AppendLine(activityLog);
                }

                // 2. Network diagnostics log
                string netLogFile = Logger.GetNetworkLogPath();
                if (System.IO.File.Exists(netLogFile))
                {
                    string netLog = System.IO.File.ReadAllText(netLogFile);
                    sb.AppendLine();
                    sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                    sb.AppendLine("  NETWORK DIAGNOSTICS");
                    sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                    sb.AppendLine(netLog);
                }

                // 3. Server bind/troubleshooting diagnostics
                string serverDiag = GetServerDiagnostics();
                if (!string.IsNullOrWhiteSpace(serverDiag) && !serverDiag.StartsWith("No"))
                {
                    sb.AppendLine();
                    sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                    sb.AppendLine("  SERVER TROUBLESHOOTING");
                    sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                    sb.AppendLine(serverDiag);
                }

                LogsTextBox.Text = sb.Length > 0 ? sb.ToString() : "No logs recorded yet.";
                LogsTextBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = $"Failed to parse logs: {ex.Message}";
            }
        }

        private void SendAllLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Build a comprehensive diagnostic report from all log sources,
                // filtering out redundant GET /api/health noise
                var report = new System.Text.StringBuilder();
                string logsDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");

                // Helper: filter out standalone GET /api/health lines (noise from 60s health monitor)
                // Keep lines that mention health in an ERROR context
                Func<string, bool> isUsefulLine = (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return false;
                    // Skip pure health-check spam: "[...] [HTTP] [...] GET /api/health"
                    if (line.Contains("[HTTP]") && line.Contains("GET /api/health")) return false;
                    return true;
                };

                // 1. Activity Log
                string logFile = System.IO.Path.Combine(logsDir, "activity_log.txt");
                if (System.IO.File.Exists(logFile))
                {
                    var rawLines = System.IO.File.ReadAllLines(logFile).Where(isUsefulLine).ToList();
                    
                    // Deduplicate consecutive repeated messages (e.g., "Iqoo unreachable" every 30s)
                    var dedupedLines = new List<string>();
                    string lastPattern = "";
                    int repeatCount = 0;
                    string firstRepeatLine = "";

                    foreach (var line in rawLines)
                    {
                        // Extract the message portion (after timestamp) for pattern matching
                        string pattern = line.Length > 28 ? line.Substring(28).Trim() : line;
                        
                        if (pattern == lastPattern)
                        {
                            repeatCount++;
                        }
                        else
                        {
                            // Flush previous repeat group
                            if (repeatCount > 2)
                            {
                                dedupedLines.Add($"    â†‘â†‘â†‘ repeated {repeatCount}Ã— (collapsed)");
                            }
                            else if (repeatCount == 2)
                            {
                                dedupedLines.Add(firstRepeatLine); // just show the 2nd one
                            }
                            
                            dedupedLines.Add(line);
                            lastPattern = pattern;
                            repeatCount = 1;
                            firstRepeatLine = line;
                        }
                    }
                    // Flush final group
                    if (repeatCount > 2)
                    {
                        dedupedLines.Add($"    â†‘â†‘â†‘ repeated {repeatCount}Ã— (collapsed)");
                    }

                    if (dedupedLines.Any())
                    {
                        report.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                        report.AppendLine("  ACTIVITY LOG");
                        report.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                        foreach (var line in dedupedLines) report.AppendLine(line);
                    }
                }

                // 2. Network Diagnostics Log
                string netLogFile = Logger.GetNetworkLogPath();
                if (System.IO.File.Exists(netLogFile))
                {
                    var lines = System.IO.File.ReadAllLines(netLogFile).Where(isUsefulLine);
                    if (lines.Any())
                    {
                        report.AppendLine();
                        report.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                        report.AppendLine("  NETWORK DIAGNOSTICS");
                        report.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                        foreach (var line in lines) report.AppendLine(line);
                    }
                }

                // 3. Server Troubleshooting (already filtered by GetServerDiagnostics, but also strip health)
                string serverDiag = GetServerDiagnostics();
                if (!string.IsNullOrWhiteSpace(serverDiag) && !serverDiag.StartsWith("No"))
                {
                    var lines = serverDiag.Split('\n').Where(l => isUsefulLine(l));
                    if (lines.Any())
                    {
                        report.AppendLine();
                        report.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                        report.AppendLine("  SERVER TROUBLESHOOTING");
                        report.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                        foreach (var line in lines) report.AppendLine(line.TrimEnd('\r'));
                    }
                }

                if (report.Length == 0)
                {
                    ToastWindow.ShowToast("âš ï¸ No logs to send");
                    return;
                }

                // Prepend system info header
                var header = new System.Text.StringBuilder();
                header.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                header.AppendLine($"  FlyShelf Full Diagnostic Report");
                header.AppendLine($"  PC: {Environment.MachineName}");
                header.AppendLine($"  OS: {Environment.OSVersion}");
                header.AppendLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine($"  Version: {UpdateManager.CurrentVersion}");
                header.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                header.AppendLine();
                header.Append(report);

                Clipboard.SetText(header.ToString());
                ToastWindow.ShowToast("ðŸ“‹ All logs copied to clipboard (health-check noise filtered) â€” paste and send!");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"âŒ Failed to copy: {ex.Message}");
            }
        }

        private void CopyNetworkLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logs = Logger.GetRecentNetworkLogs(200);
                Clipboard.SetText(logs);
                ToastWindow.ShowToast("ðŸ“‹ Network logs copied to clipboard (last 200 lines)");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"âŒ Failed to copy: {ex.Message}");
            }
        }
        private async void SendLogsToDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SendLogsToDashboardBtn.IsEnabled = false;
                var vm = DataContext as FlyShelf.ViewModels.FlyShelfViewModel;

                // Gather PC logs
                string pcLogs = Logger.GetRecentNetworkLogs(500);
                var logLines = string.IsNullOrWhiteSpace(pcLogs)
                    ? new List<string>()
                    : pcLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => $"[PC] {line.Trim()}")
                        .ToList();

                if (logLines.Count == 0)
                {
                    ToastWindow.ShowToast("âš ï¸ No network logs to send");
                    SendLogsToDashboardBtn.IsEnabled = true;
                    return;
                }

                // â”€â”€ Always save a local diagnostic file â”€â”€
                string logsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
                System.IO.Directory.CreateDirectory(logsDir);
                string deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                string deviceTag = deviceName.Replace(" ", "_").Replace("/", "_");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = $"diagnostic_{deviceTag}_{timestamp}.log";
                string filePath = System.IO.Path.Combine(logsDir, fileName);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                sb.AppendLine($"  FlyShelf Diagnostic Log â€” {deviceName}");
                sb.AppendLine($"  Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  PC Host:  {Environment.MachineName}");
                sb.AppendLine($"  OS:       {Environment.OSVersion}");
                sb.AppendLine($"  Entries:  {logLines.Count}");
                sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
                sb.AppendLine();
                foreach (var line in logLines)
                    sb.AppendLine(line);

                await System.IO.File.WriteAllTextAsync(filePath, sb.ToString());

                // â”€â”€ Also POST to dashboard if server is running â”€â”€
                bool dashboardSuccess = false;
                if (vm?.LocalServer != null)
                {
                    try
                    {
                        string serverUrl = vm.LocalServer.ServerUrl?.TrimEnd('/') ?? "http://localhost:8999";
                        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        var json = System.Text.Json.JsonSerializer.Serialize(logLines);
                        var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        content.Headers.Add("X-FlyShelf-Client", "DesktopApp");
                        content.Headers.Add("X-Device-Name", deviceName);
                        var res = await client.PostAsync($"{serverUrl}/api/logs", content);
                        dashboardSuccess = res.IsSuccessStatusCode;
                    }
                    catch { /* Server POST failed â€” file is still saved */ }
                }

                string msg = $"âœ… {logLines.Count} entries saved â†’ {fileName}";
                if (dashboardSuccess) msg += "\nðŸ“Š Also pushed to web dashboard";
                msg += $"\nðŸ“ {logsDir}";
                ToastWindow.ShowToast(msg);

                // Open the Logs folder so user can grab the file
                try { System.Diagnostics.Process.Start("explorer.exe", logsDir); } catch { }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"âŒ Failed: {ex.Message}");
            }
            finally
            {
                SendLogsToDashboardBtn.IsEnabled = true;
            }
        }

    }
}
