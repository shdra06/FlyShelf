using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdvanceClip.Classes;
using AdvanceClip.ViewModels;
using MicaWPF.Controls;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Threading.Tasks;
using static AdvanceClip.Classes.NativeMethods;

namespace AdvanceClip.Windows
{
    public partial class HubWindow : MicaWindow
    {
        private FlyShelfViewModel _viewModel;
        private UpdateManager _updateManager = new UpdateManager();
        private bool _updateDownloaded = false;
        private System.Windows.Threading.DispatcherTimer? _deviceRefreshTimer;

        public HubWindow(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.DroppedItems.CollectionChanged += DroppedItems_CollectionChanged;
            ApplyTheme();

            // Auto-refresh device list when a new device pairs
            DevicePairingManager.OnDevicePaired += (deviceName) =>
            {
                Dispatcher.InvokeAsync(() => RefreshDevices_Click(null, null));
            };

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
                    LatestVersionText.Text = $"→ v{_updateManager.LatestVersion} available!";
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
                        UpdateStatusText.Text = "✅ Update downloaded! Restarting now...";
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
                    UpdateBtn.Content = "✓ Up to Date";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Collapsed;

                    // Re-enable after 3s so user can re-check for newer updates
                    await Task.Delay(3000);
                    UpdateBtn.Content = "Check Again";
                    UpdateBtn.IsEnabled = true;
                }
            });

            // No auto-update at startup — manual only via the button

            // Auto-refresh device list every 30 seconds + on initial load
            _deviceRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _deviceRefreshTimer.Tick += (s, ev) => RefreshDevices_Click(null, null);
            _deviceRefreshTimer.Start();
            Loaded += (s, ev) =>
            {
                RefreshDevices_Click(null, null);
                // LIST profile for clipboard items (very slow, precise)
                Classes.SmoothScroll.AttachList(HubListView);
                // PAGE profile for everything else (settings, diagnostics — normal speed)
                Classes.SmoothScroll.AttachToWindow(this);
            };
        }

        private void DroppedItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyFilters();
                UpdateEmptyState();
            });
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
            ToastWindow.ShowToast($"Cleared {count} items 🗑️");
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
            }
            base.OnClosing(e);
        }
        
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SuppressWindowBorder();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SuppressWindowBorder();
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
                if (AutomationGrid != null) AutomationGrid.Visibility = tag == "Automation" ? Visibility.Visible : Visibility.Collapsed;
                if (NetworkGrid != null) NetworkGrid.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
                if (SettingsGrid != null) SettingsGrid.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
                if (LogsGrid != null) LogsGrid.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
                
                if (tag == "Logs") RefreshLogs_Click(null, null);
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
            // The HubWindow itself IS the clipboard preview — just flash to show effect
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
                    sb.AppendLine("══════════════════════════════════════════════════════════");
                    sb.AppendLine("  ACTIVITY LOG");
                    sb.AppendLine("══════════════════════════════════════════════════════════");
                    sb.AppendLine(activityLog);
                }

                // 2. Network diagnostics log
                string netLogFile = Logger.GetNetworkLogPath();
                if (System.IO.File.Exists(netLogFile))
                {
                    string netLog = System.IO.File.ReadAllText(netLogFile);
                    sb.AppendLine();
                    sb.AppendLine("══════════════════════════════════════════════════════════");
                    sb.AppendLine("  NETWORK DIAGNOSTICS");
                    sb.AppendLine("══════════════════════════════════════════════════════════");
                    sb.AppendLine(netLog);
                }

                // 3. Server bind/troubleshooting diagnostics
                string serverDiag = GetServerDiagnostics();
                if (!string.IsNullOrWhiteSpace(serverDiag) && !serverDiag.StartsWith("No"))
                {
                    sb.AppendLine();
                    sb.AppendLine("══════════════════════════════════════════════════════════");
                    sb.AppendLine("  SERVER TROUBLESHOOTING");
                    sb.AppendLine("══════════════════════════════════════════════════════════");
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
                                dedupedLines.Add($"    ↑↑↑ repeated {repeatCount}× (collapsed)");
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
                        dedupedLines.Add($"    ↑↑↑ repeated {repeatCount}× (collapsed)");
                    }

                    if (dedupedLines.Any())
                    {
                        report.AppendLine("══════════════════════════════════════════════════════════");
                        report.AppendLine("  ACTIVITY LOG");
                        report.AppendLine("══════════════════════════════════════════════════════════");
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
                        report.AppendLine("══════════════════════════════════════════════════════════");
                        report.AppendLine("  NETWORK DIAGNOSTICS");
                        report.AppendLine("══════════════════════════════════════════════════════════");
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
                        report.AppendLine("══════════════════════════════════════════════════════════");
                        report.AppendLine("  SERVER TROUBLESHOOTING");
                        report.AppendLine("══════════════════════════════════════════════════════════");
                        foreach (var line in lines) report.AppendLine(line.TrimEnd('\r'));
                    }
                }

                if (report.Length == 0)
                {
                    ToastWindow.ShowToast("⚠️ No logs to send");
                    return;
                }

                // Prepend system info header
                var header = new System.Text.StringBuilder();
                header.AppendLine("═══════════════════════════════════════════════════════════");
                header.AppendLine($"  FlyShelf Full Diagnostic Report");
                header.AppendLine($"  PC: {Environment.MachineName}");
                header.AppendLine($"  OS: {Environment.OSVersion}");
                header.AppendLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine($"  Version: {UpdateManager.CurrentVersion}");
                header.AppendLine("═══════════════════════════════════════════════════════════");
                header.AppendLine();
                header.Append(report);

                Clipboard.SetText(header.ToString());
                ToastWindow.ShowToast("📋 All logs copied to clipboard (health-check noise filtered) — paste and send!");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed to copy: {ex.Message}");
            }
        }

        private void CopyNetworkLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logs = Logger.GetRecentNetworkLogs(200);
                Clipboard.SetText(logs);
                ToastWindow.ShowToast("📋 Network logs copied to clipboard (last 200 lines)");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed to copy: {ex.Message}");
            }
        }
        private async void SendLogsToDashboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SendLogsToDashboardBtn.IsEnabled = false;
                var vm = DataContext as AdvanceClip.ViewModels.FlyShelfViewModel;

                // Gather PC logs
                string pcLogs = Logger.GetRecentNetworkLogs(500);
                var logLines = string.IsNullOrWhiteSpace(pcLogs)
                    ? new List<string>()
                    : pcLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => $"[PC] {line.Trim()}")
                        .ToList();

                if (logLines.Count == 0)
                {
                    ToastWindow.ShowToast("⚠️ No network logs to send");
                    SendLogsToDashboardBtn.IsEnabled = true;
                    return;
                }

                // ── Always save a local diagnostic file ──
                string logsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
                System.IO.Directory.CreateDirectory(logsDir);
                string deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                string deviceTag = deviceName.Replace(" ", "_").Replace("/", "_");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = $"diagnostic_{deviceTag}_{timestamp}.log";
                string filePath = System.IO.Path.Combine(logsDir, fileName);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine($"  FlyShelf Diagnostic Log — {deviceName}");
                sb.AppendLine($"  Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  PC Host:  {Environment.MachineName}");
                sb.AppendLine($"  OS:       {Environment.OSVersion}");
                sb.AppendLine($"  Entries:  {logLines.Count}");
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine();
                foreach (var line in logLines)
                    sb.AppendLine(line);

                await System.IO.File.WriteAllTextAsync(filePath, sb.ToString());

                // ── Also POST to dashboard if server is running ──
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
                    catch { /* Server POST failed — file is still saved */ }
                }

                string msg = $"✅ {logLines.Count} entries saved → {fileName}";
                if (dashboardSuccess) msg += "\n📊 Also pushed to web dashboard";
                msg += $"\n📁 {logsDir}";
                ToastWindow.ShowToast(msg);

                // Open the Logs folder so user can grab the file
                try { System.Diagnostics.Process.Start("explorer.exe", logsDir); } catch { }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed: {ex.Message}");
            }
            finally
            {
                SendLogsToDashboardBtn.IsEnabled = true;
            }
        }

        private void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.DumpNetworkDiagnostics();
                ToastWindow.ShowToast("🔍 Network diagnostics captured!");
                // Refresh the log view after a brief delay to let the buffer flush
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    Dispatcher.Invoke(() => RefreshLogs_Click(null, null));
                });
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Diagnostics failed: {ex.Message}");
            }
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
                if (!System.IO.Directory.Exists(logsDir)) System.IO.Directory.CreateDirectory(logsDir);
                System.Diagnostics.Process.Start("explorer.exe", logsDir);
            }
            catch { }
        }

        private static NetworkLogsWindow? _networkLogsWindow;
        private void OpenNetworkLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_networkLogsWindow != null && _networkLogsWindow.IsLoaded)
                {
                    _networkLogsWindow.Activate();
                    _networkLogsWindow.Focus();
                    return;
                }
                _networkLogsWindow = new NetworkLogsWindow();
                _networkLogsWindow.Show();
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("UI", $"Failed to open Network Logs: {ex.Message}");
            }
        }

        private async void CopyDeviceLogs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            string activeUrl = btn.Tag?.ToString() ?? "";
            // Get device name from DataContext
            string deviceName = "Unknown";
            if (btn.DataContext is PeerStatusItem psi)
                deviceName = psi.DeviceName;

            btn.IsEnabled = false;
            var origContent = btn.Content;
            btn.Content = "⏳...";

            try
            {
                if (string.IsNullOrEmpty(activeUrl))
                {
                    Clipboard.SetText($"⚠ Device '{deviceName}' has no active URL — cannot fetch remote data.\nDevice may be offline. Try Force Sync first.");
                    ToastWindow.ShowToast($"⚠ {deviceName} is offline");
                    btn.Content = "❌ Offline";
                    await Task.Delay(1500);
                    return;
                }

                string baseUrl = activeUrl.TrimEnd('/');
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                string pin = SettingsManager.Current?.WebClientPinToken;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════════════════");
                sb.AppendLine($"  FlyShelf Remote Diagnostic — {deviceName}");
                sb.AppendLine($"  URL: {activeUrl}");
                sb.AppendLine($"  Fetched: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("═══════════════════════════════════════════════════════════");

                // ── SECTION 1: Health ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  DEVICE HEALTH                                          │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    using var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    var healthResp = await hc.GetStringAsync($"{baseUrl}/api/health");
                    using var healthDoc = System.Text.Json.JsonDocument.Parse(healthResp);
                    var h = healthDoc.RootElement;

                    string version = h.TryGetProperty("version", out var vp) ? vp.GetString() ?? "?" : "?";
                    string devId = h.TryGetProperty("deviceId", out var dp) ? dp.GetString() ?? "?" : "?";
                    string devType = h.TryGetProperty("deviceType", out var dtp) ? dtp.GetString() ?? "?" : "?";
                    int uptime = h.TryGetProperty("uptime", out var up) ? up.GetInt32() : 0;
                    int peers = h.TryGetProperty("peers", out var pp) ? pp.GetInt32() : 0;
                    string lanUrl = "", cfUrl = "";
                    if (h.TryGetProperty("transport", out var tr))
                    {
                        lanUrl = tr.TryGetProperty("lan", out var lp) ? lp.GetString() ?? "" : "";
                        cfUrl = tr.TryGetProperty("cloudflare", out var cp) ? cp.GetString() ?? "" : "";
                    }

                    string uptimeStr = uptime >= 3600 ? $"{uptime/3600}h {(uptime%3600)/60}m" : $"{uptime/60}m {uptime%60}s";
                    sb.AppendLine($"  Version:    v{version}");
                    sb.AppendLine($"  Device ID:  {devId}");
                    sb.AppendLine($"  Type:       {devType}");
                    sb.AppendLine($"  Uptime:     {uptimeStr}");
                    sb.AppendLine($"  Peers:      {peers} connected");
                    sb.AppendLine($"  LAN:        {(string.IsNullOrEmpty(lanUrl) ? "—" : lanUrl)}");
                    sb.AppendLine($"  Cloudflare: {(string.IsNullOrEmpty(cfUrl) ? "—" : cfUrl)}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  ❌ Failed to fetch health: {ex.Message}");
                }

                // ── SECTION 2: Clipboard Contents ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  CLIPBOARD CONTENTS                                     │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    using var sc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    if (!string.IsNullOrEmpty(pairingKey))
                        sc.DefaultRequestHeaders.Add("X-Pairing-Key", pairingKey);
                    if (!string.IsNullOrEmpty(pin))
                        sc.DefaultRequestHeaders.Add("Authorization", $"Bearer {pin}");
                    sc.DefaultRequestHeaders.Add("X-FlyShelf-Client", "DesktopSync");

                    var syncResp = await sc.GetStringAsync($"{baseUrl}/api/sync");
                    using var syncDoc = System.Text.Json.JsonDocument.Parse(syncResp);

                    int idx = 0;
                    foreach (var item in syncDoc.RootElement.EnumerateArray())
                    {
                        idx++;
                        string type = item.TryGetProperty("Type", out var tp) ? tp.GetString() ?? "?" : "?";
                        string title = item.TryGetProperty("Title", out var ttp) ? ttp.GetString() ?? "" : "";
                        string raw = item.TryGetProperty("Raw", out var rp) ? rp.GetString() ?? "" : "";
                        string fileName = item.TryGetProperty("FileName", out var fnp) ? fnp.GetString() ?? "" : "";
                        string time = item.TryGetProperty("Time", out var tmp) ? tmp.GetString() ?? "" : "";
                        string source = item.TryGetProperty("SourceDeviceName", out var sp) ? sp.GetString() ?? "" : "";
                        string sourceType = item.TryGetProperty("SourceDeviceType", out var stp) ? stp.GetString() ?? "" : "";
                        string previewUrl = item.TryGetProperty("PreviewUrl", out var pvp) ? pvp.GetString() ?? "" : "";
                        string downloadUrl = item.TryGetProperty("DownloadUrl", out var dup) ? dup.GetString() ?? "" : "";

                        // Type icon
                        string icon = type switch
                        {
                            "Text" => "📝",
                            "Url" => "🔗",
                            "Image" => "🖼️",
                            "QRCode" => "📱",
                            "File" => "📎",
                            "Pdf" => "📄",
                            _ => "📋"
                        };

                        sb.AppendLine();
                        sb.AppendLine($"  {icon} [{idx}] {type.ToUpper()} — {time}");
                        if (!string.IsNullOrEmpty(title))
                            sb.AppendLine($"     Title:  {title}");
                        if (!string.IsNullOrEmpty(fileName) && fileName != title)
                            sb.AppendLine($"     File:   {fileName}");
                        if (!string.IsNullOrEmpty(source))
                            sb.AppendLine($"     From:   {source} ({sourceType})");

                        // Content preview (truncated for text, full URLs)
                        if (type == "Text" || type == "Url")
                        {
                            string preview = raw.Length > 200 ? raw.Substring(0, 200) + "..." : raw;
                            // Replace newlines for cleaner log
                            preview = preview.Replace("\r\n", "\\n").Replace("\n", "\\n");
                            sb.AppendLine($"     Content: {preview}");
                        }
                        else if (type == "Image" || type == "QRCode")
                        {
                            if (!string.IsNullOrEmpty(previewUrl))
                                sb.AppendLine($"     Preview: {baseUrl}{previewUrl}");
                            if (!string.IsNullOrEmpty(downloadUrl) && downloadUrl.StartsWith("/"))
                                sb.AppendLine($"     Download: {baseUrl}{downloadUrl}");
                        }
                        else if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            if (downloadUrl.StartsWith("/"))
                                sb.AppendLine($"     Download: {baseUrl}{downloadUrl}");
                            else if (downloadUrl.StartsWith("http"))
                                sb.AppendLine($"     Path: {downloadUrl}");
                        }
                    }

                    if (idx == 0)
                        sb.AppendLine("  (clipboard is empty)");
                    else
                        sb.AppendLine($"\n  — {idx} items on clipboard");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  ❌ Failed to fetch clipboard: {ex.Message}");
                }

                // ── SECTION 3: Logs ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  NETWORK LOGS (last 200)                                │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    using var lc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var logReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                        $"{baseUrl}/api/logs?lines=200");
                    logReq.Headers.Add("X-FlyShelf-Client", "DesktopSync");
                    if (!string.IsNullOrEmpty(pairingKey))
                        logReq.Headers.Add("X-Pairing-Key", pairingKey);
                    if (!string.IsNullOrEmpty(pin))
                        logReq.Headers.Add("Authorization", $"Bearer {pin}");

                    var logResp = await lc.SendAsync(logReq);
                    string logJson = await logResp.Content.ReadAsStringAsync();

                    if (logResp.IsSuccessStatusCode)
                    {
                        using var logDoc = System.Text.Json.JsonDocument.Parse(logJson);
                        int count = 0;
                        if (logDoc.RootElement.TryGetProperty("logs", out var logsArr))
                        {
                            foreach (var logEl in logsArr.EnumerateArray())
                            {
                                string line = "";
                                if (logEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                                    line = logEl.TryGetProperty("log", out var lp) ? lp.GetString() ?? "" : "";
                                else if (logEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                    line = logEl.GetString() ?? "";

                                if (string.IsNullOrWhiteSpace(line)) continue;
                                // Filter health-check noise
                                if (line.Contains("[HTTP]") && line.Contains("GET /api/health")) continue;
                                if (line.Contains("[HTTP]") && line.Contains("GET /health")) continue;

                                sb.AppendLine(line);
                                count++;
                            }
                        }
                        sb.AppendLine($"\n— {count} log entries (health noise filtered)");
                    }
                    else
                    {
                        sb.AppendLine($"  ❌ HTTP {logResp.StatusCode}: {logJson}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  ❌ Failed to fetch logs: {ex.Message}");
                }

                Clipboard.SetText(sb.ToString());
                ToastWindow.ShowToast($"📋 Full diagnostic from {deviceName} copied!");
                btn.Content = "✅ Copied";
                await Task.Delay(1500);
            }
            catch (TaskCanceledException)
            {
                ToastWindow.ShowToast($"⏱ Timeout fetching from {deviceName}");
                btn.Content = "❌ Timeout";
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed: {ex.Message}");
                btn.Content = "❌ Error";
                await Task.Delay(1500);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = origContent;
            }
        }

        private string _currentFilterTag = "All";

        private void RestartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var vm = DataContext as AdvanceClip.ViewModels.FlyShelfViewModel;
                if (vm?.LocalServer == null) { ToastWindow.ShowToast("❌ Server instance not found"); return; }

                ServerDiagnosticsLog.Text = "⏳ Stopping server...\n";
                ServerDiagnosticsLog.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber

                vm.LocalServer.Stop();
                ServerDiagnosticsLog.Text += "✅ Server stopped.\n⏳ Starting server...\n";

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000); // Brief cooldown
                    Dispatcher.Invoke(() =>
                    {
                        vm.LocalServer.Start();
                        vm.RefreshLocalServerData();

                        // Read the BIND/PROXY/NETWORK log lines from the activity log
                        string diagnostics = GetServerDiagnostics();
                        ServerDiagnosticsLog.Text = diagnostics;
                        ServerDiagnosticsLog.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // green
                        ToastWindow.ShowToast("🔄 Server restarted — check diagnostics below");
                    });
                });
            }
            catch (Exception ex)
            {
                ServerDiagnosticsLog.Text = $"❌ Restart failed: {ex.Message}";
                ServerDiagnosticsLog.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            }
        }

        private void CopyServerDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string diagnostics = GetServerDiagnostics();
                string systemInfo = $"=== AdvanceClip Server Diagnostics ===\n" +
                    $"PC Name: {Environment.MachineName}\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $"User: {Environment.UserName}\n" +
                    $"Is Admin: {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"======================================\n\n{diagnostics}";
                Clipboard.SetText(systemInfo);
                ToastWindow.ShowToast("📋 Server diagnostics copied — share this with the developer!");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed: {ex.Message}");
            }
        }

        private string GetServerDiagnostics()
        {
            try
            {
                string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs", "activity_log.txt");
                if (!System.IO.File.Exists(logPath)) return "No log file found.";

                // Read last 500 lines and filter for server-related entries
                var allLines = System.IO.File.ReadAllLines(logPath);
                int startIdx = Math.Max(0, allLines.Length - 500);
                var relevantLines = new System.Collections.Generic.List<string>();
                for (int i = startIdx; i < allLines.Length; i++)
                {
                    string line = allLines[i];
                    if (line.Contains("[BIND]") || line.Contains("[NETWORK") || line.Contains("[TCP PROXY]") ||
                        line.Contains("[CLOUDFLARE]") || line.Contains("[CF_STDERR]") || line.Contains("[HEARTBEAT]") ||
                        line.Contains("[FIREBASE SYNC]") || line.Contains("[DIAGNOSTICS]") || line.Contains("[HTTP]") && line.Contains("health"))
                    {
                        relevantLines.Add(line);
                    }
                }

                if (relevantLines.Count == 0) return "No server log entries found in last 500 lines.";

                // Take last 50 relevant lines
                var output = relevantLines.Count > 50
                    ? relevantLines.GetRange(relevantLines.Count - 50, 50)
                    : relevantLines;

                return string.Join("\n", output);
            }
            catch (Exception ex)
            {
                return $"Error reading logs: {ex.Message}";
            }
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb && HubListView != null)
            {
                _currentFilterTag = rb.Tag as string ?? "All";
                ApplyFilters();
            }
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (SearchPlaceholderPanel != null)
                SearchPlaceholderPanel.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            
            if (HubListView != null)
            {
                ApplyFilters();
            }
        }

        private void ApplyFilters()
        {
            if (HubListView.ItemsSource == null) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(HubListView.ItemsSource);
            
            string query = SearchBox?.Text?.ToLowerInvariant() ?? "";

            view.Filter = item =>
            {
                if (item is ClipboardItem clip)
                {
                    bool passesType = true;
                    switch (_currentFilterTag)
                    {
                        case "Code": passesType = clip.ItemType == ClipboardItemType.Code; break;
                        case "Image": passesType = clip.ItemType == ClipboardItemType.Image || clip.ItemType == ClipboardItemType.QRCode; break;
                        case "Url": passesType = clip.ItemType == ClipboardItemType.Url; break;
                        case "Pdf": passesType = clip.ItemType == ClipboardItemType.Pdf; break;
                        case "Document": passesType = clip.ItemType == ClipboardItemType.Document; break;
                        case "Video": passesType = clip.ItemType == ClipboardItemType.Video; break;
                        case "Text": passesType = clip.ItemType == ClipboardItemType.Text; break;
                        case "All": passesType = true; break;
                    }

                    bool passesSearch = true;
                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        passesSearch = (clip.FileName?.ToLowerInvariant().Contains(query) == true) ||
                                       (clip.RawContent?.ToLowerInvariant().Contains(query) == true) ||
                                       (clip.FormatIdentifier?.ToLowerInvariant().Contains(query) == true);
                    }

                    return passesType && passesSearch;
                }
                return false;
            };
            view.Refresh();
        }

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                _viewModel.TogglePin(item);
                e.Handled = true;
            }
        }

        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                _viewModel.RemoveItem(item);
                e.Handled = true;
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool anyUnselected = _viewModel.DroppedItems.Any(i => !i.IsCheckedForMerge);
            foreach (var item in _viewModel.DroppedItems)
            {
                item.IsCheckedForMerge = anyUnselected; // Toggle: if any unselected, select all; otherwise deselect all
            }
            UpdateMergeButton();
        }

        private void HubListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateMergeButton();
        }

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            // Defer so the two-way binding updates IsCheckedForMerge first
            Dispatcher.InvokeAsync(() => UpdateMergeButton(), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateMergeButton()
        {
            if (_viewModel == null || MergePdfFloatingBar == null) return;
            var checkedPdfs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Pdf
                            && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            if (checkedPdfs.Count >= 2)
            {
                MergePdfFloatingBar.Visibility = Visibility.Visible;
                MergeBarText.Text = $"{checkedPdfs.Count} PDFs selected";
            }
            else
            {
                MergePdfFloatingBar.Visibility = Visibility.Collapsed;
            }
        }

        private void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            var pdfs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Pdf
                            && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            if (pdfs.Count < 2)
            {
                ToastWindow.ShowToast("Check at least 2 PDFs to merge.");
                return;
            }
            var win = new PdfMergeWindow(pdfs, _viewModel);
            App.ActiveMergeWindow = win;
            win.Closed += (_, __) => App.ActiveMergeWindow = null;
            win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            win.Topmost = true;
            win.Show();
            win.Activate();
            win.Focus();
            win.Topmost = false;
        }

    }
}
