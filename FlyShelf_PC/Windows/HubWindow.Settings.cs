// ---------------------------------------------------------------
// HubWindow — Diagnostics, Filters, Merge, Scroll & Lifecycle
// RunDiagnostics, Server restart, Filter/Search, Pin/Delete,
// Merge PDFs, Browser-style Smooth Scroll, OnClosed
// Split from HubWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Classes;
using FlyShelf.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Data;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ToastWindow.ShowToast("🔍 Network diagnostics started...");
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Logger.DumpNetworkDiagnostics();
                        Dispatcher.Invoke(() =>
                        {
                            ToastWindow.ShowToast("🔍 Network diagnostics captured!");
                            RefreshLogs_Click(null, null);
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => ToastWindow.ShowToast($"❌ Diagnostics failed: {ex.Message}"));
                    }
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
                FlyShelf.Classes.Logger.LogAction("UI", $"Failed to open Network Logs: {ex.Message}");
            }
        }

        private async void CopyDeviceLogs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            string activeUrl = btn.Tag?.ToString() ?? "";
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

                        string icon = type switch
                        {
                            "Text" => "📝", "Url" => "🔗", "Image" => "🖼️",
                            "QRCode" => "📱", "File" => "📎", "Pdf" => "📄", _ => "📋"
                        };

                        sb.AppendLine();
                        sb.AppendLine($"  {icon} [{idx}] {type.ToUpper()} — {time}");
                        if (!string.IsNullOrEmpty(title)) sb.AppendLine($"     Title:  {title}");
                        if (!string.IsNullOrEmpty(fileName) && fileName != title) sb.AppendLine($"     File:   {fileName}");
                        if (!string.IsNullOrEmpty(source)) sb.AppendLine($"     From:   {source} ({sourceType})");

                        if (type == "Text" || type == "Url")
                        {
                            string preview = raw.Length > 200 ? raw.Substring(0, 200) + "..." : raw;
                            preview = preview.Replace("\r\n", "\\n").Replace("\n", "\\n");
                            sb.AppendLine($"     Content: {preview}");
                        }
                        else if (type == "Image" || type == "QRCode")
                        {
                            if (!string.IsNullOrEmpty(previewUrl)) sb.AppendLine($"     Preview: {baseUrl}{previewUrl}");
                            if (!string.IsNullOrEmpty(downloadUrl) && downloadUrl.StartsWith("/")) sb.AppendLine($"     Download: {baseUrl}{downloadUrl}");
                        }
                        else if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            if (downloadUrl.StartsWith("/")) sb.AppendLine($"     Download: {baseUrl}{downloadUrl}");
                            else if (downloadUrl.StartsWith("http")) sb.AppendLine($"     Path: {downloadUrl}");
                        }
                    }

                    if (idx == 0) sb.AppendLine("  (clipboard is empty)");
                    else sb.AppendLine($"\n  — {idx} items on clipboard");
                }
                catch (Exception ex) { sb.AppendLine($"  ❌ Failed to fetch clipboard: {ex.Message}"); }

                // ── SECTION 3: Logs ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  NETWORK LOGS (last 200)                                │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    using var lc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var logReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{baseUrl}/api/logs?lines=200");
                    logReq.Headers.Add("X-FlyShelf-Client", "DesktopSync");
                    if (!string.IsNullOrEmpty(pairingKey)) logReq.Headers.Add("X-Pairing-Key", pairingKey);
                    if (!string.IsNullOrEmpty(pin)) logReq.Headers.Add("Authorization", $"Bearer {pin}");

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
                                    line = logEl.TryGetProperty("log", out var lpp) ? lpp.GetString() ?? "" : "";
                                else if (logEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                    line = logEl.GetString() ?? "";
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                if (line.Contains("[HTTP]") && line.Contains("GET /api/health")) continue;
                                if (line.Contains("[HTTP]") && line.Contains("GET /health")) continue;
                                sb.AppendLine(line);
                                count++;
                            }
                        }
                        sb.AppendLine($"\n— {count} log entries (health noise filtered)");
                    }
                    else { sb.AppendLine($"  ❌ HTTP {logResp.StatusCode}: {logJson}"); }
                }
                catch (Exception ex) { sb.AppendLine($"  ❌ Failed to fetch logs: {ex.Message}"); }

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
                var vm = DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                if (vm?.LocalServer == null) { ToastWindow.ShowToast("❌ Server instance not found"); return; }

                ServerDiagnosticsLog.Text = "⏳ Stopping server...\n";
                ServerDiagnosticsLog.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

                vm.LocalServer.Stop();
                ServerDiagnosticsLog.Text += "✅ Server stopped.\n⏳ Starting server...\n";

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    Dispatcher.Invoke(() =>
                    {
                        vm.LocalServer.Start();
                        vm.RefreshLocalServerData();
                        string diagnostics = GetServerDiagnostics();
                        ServerDiagnosticsLog.Text = diagnostics;
                        ServerDiagnosticsLog.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                        ToastWindow.ShowToast("🔄 Server restarted — check diagnostics below");
                    });
                });
            }
            catch (Exception ex)
            {
                ServerDiagnosticsLog.Text = $"❌ Restart failed: {ex.Message}";
                ServerDiagnosticsLog.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            }
        }

        private void CopyServerDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string diagnostics = GetServerDiagnostics();
                string systemInfo = $"=== FlyShelf Server Diagnostics ===\n" +
                    $"PC Name: {Environment.MachineName}\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $"User: {Environment.UserName}\n" +
                    $"Is Admin: {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"======================================\n\n{diagnostics}";
                Clipboard.SetText(systemInfo);
                ToastWindow.ShowToast("📋 Server diagnostics copied — share this with the developer!");
            }
            catch (Exception ex) { ToastWindow.ShowToast($"❌ Failed: {ex.Message}"); }
        }

        private string GetServerDiagnostics()
        {
            try
            {
                string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs", "activity_log.txt");
                if (!System.IO.File.Exists(logPath)) return "No log file found.";
                var allLines = System.IO.File.ReadAllLines(logPath);
                int startIdx = Math.Max(0, allLines.Length - 500);
                var relevantLines = new List<string>();
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
                var output = relevantLines.Count > 50 ? relevantLines.GetRange(relevantLines.Count - 50, 50) : relevantLines;
                return string.Join("\n", output);
            }
            catch (Exception ex) { return $"Error reading logs: {ex.Message}"; }
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && HubListView != null)
            {
                _currentFilterTag = rb.Tag as string ?? "All";
                ApplyFilters();
            }
        }

        private System.Windows.Threading.DispatcherTimer? _hubSearchDebounceTimer;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholderPanel != null)
                SearchPlaceholderPanel.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            
            if (HubListView != null)
            {
                if (_hubSearchDebounceTimer == null)
                {
                    _hubSearchDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    _hubSearchDebounceTimer.Tick += (s, args) => { _hubSearchDebounceTimer.Stop(); ApplyFilters(); };
                }
                else { _hubSearchDebounceTimer.Stop(); }
                _hubSearchDebounceTimer.Start();
            }
        }

        private void ApplyFilters()
        {
            if (HubListView.ItemsSource == null) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(HubListView.ItemsSource) as ListCollectionView;
            if (view == null) return;
            
            string queryClean = (SearchBox?.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(queryClean))
            {
                view.CustomSort = null;
            }
            else
            {
                view.CustomSort = new SearchResultComparer(queryClean);
            }

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
                    if (!string.IsNullOrWhiteSpace(queryClean))
                    {
                        string q = queryClean.ToLowerInvariant();
                        bool nameMatch = clip.FileName != null && clip.FileName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool contentMatch = clip.RawContent != null && clip.RawContent.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool formatMatch = clip.FormatIdentifier != null && clip.FormatIdentifier.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        
                        bool extMatch = !string.IsNullOrEmpty(clip.Extension) && clip.Extension.Replace(".", "").Trim().ToLowerInvariant() == q;
                        bool pathExtMatch = false;
                        if (!string.IsNullOrEmpty(clip.FilePath))
                        {
                            try
                            {
                                string ext = System.IO.Path.GetExtension(clip.FilePath).Replace(".", "").Trim().ToLowerInvariant();
                                pathExtMatch = ext == q;
                            }
                            catch { }
                        }
                        bool typeMatch = clip.ItemType.ToString().ToLowerInvariant() == q;

                        passesSearch = nameMatch || contentMatch || formatMatch || extMatch || pathExtMatch || typeMatch;
                    }
                    return passesType && passesSearch;
                }
                return false;
            };
            view.Refresh();
        }

        internal void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item) { _viewModel.TogglePin(item); e.Handled = true; }
        }

        internal void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item) { _viewModel.RemoveItem(item); e.Handled = true; }
        }

        private void UninstallFlyShelf_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "⚠️ Are you sure you want to uninstall FlyShelf?\n\n" +
                "This will permanently delete:\n" +
                "  • All clipboard history & images\n" +
                "  • All settings & preferences\n" +
                "  • All synced files\n" +
                "  • All paired device data\n" +
                "  • All logs & certificates\n" +
                "  • Auto-start registry entry\n\n" +
                "This action cannot be undone.",
                "Uninstall FlyShelf",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                var confirm = MessageBox.Show(
                    "Final confirmation: ALL FlyShelf data will be deleted and the app will close.\n\nProceed?",
                    "Confirm Uninstall",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Stop,
                    MessageBoxResult.No);

                if (confirm == MessageBoxResult.Yes)
                {
                    SettingsManager.PerformFullUninstall();
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // LICENSE ACTIVATION UI
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Refresh the license UI elements (badge, status, panels) based on current tier.</summary>
        internal void RefreshLicenseUI()
        {
            bool isPro = FlyShelf.Classes.LicenseManager.IsPro;

            // Runtime dynamic correction if downgraded/deactivated to Free tier
            if (!isPro)
            {
                bool settingsChanged = false;

                // 1. Correct Theme display mode and active mascot theme
                if (FlyShelf.Classes.SettingsManager.Current.ThemeDisplayMode == "glass")
                {
                    FlyShelf.Classes.SettingsManager.Current.ThemeDisplayMode = "mica";
                    FlyShelf.Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                    FlyShelf.Classes.ThemeManager.Instance.RemoveGlassTheme();
                    settingsChanged = true;
                }

                string activeThemeName = FlyShelf.Classes.SettingsManager.Current.ActiveThemeName ?? "";
                if (!string.IsNullOrEmpty(activeThemeName) && !FlyShelf.Classes.LicenseManager.CanUseTheme(activeThemeName))
                {
                    FlyShelf.Classes.SettingsManager.Current.ActiveThemeName = "";
                    if (FlyShelf.Classes.SettingsManager.Current.ThemeDisplayMode == "theme")
                    {
                        FlyShelf.Classes.SettingsManager.Current.ThemeDisplayMode = "mica";
                        FlyShelf.Classes.SettingsManager.Current.ClipboardWallpaperPath = "";
                    }
                    FlyShelf.Classes.ThemeManager.Instance.SetActiveTheme(null);
                    settingsChanged = true;
                }

                // 2. Correct history retention if set to Pro-only "Never"
                if (FlyShelf.Classes.SettingsManager.Current.ClipboardRetentionDays == 0)
                {
                    FlyShelf.Classes.SettingsManager.Current.ClipboardRetentionDays = 7;
                    settingsChanged = true;
                }

                // 3. Correct Cloudflare global tunnel
                if (FlyShelf.Classes.SettingsManager.Current.EnableGlobalCloudflare)
                {
                    FlyShelf.Classes.SettingsManager.Current.EnableGlobalCloudflare = false;
                    settingsChanged = true;
                    
                    // Stop Cloudflare tunnel dynamically!
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    if (mainWin != null && mainWin.ViewModel?.LocalServer != null)
                    {
                        mainWin.ViewModel.LocalServer.Stop();
                        // If they still want local LAN sync to be running:
                        if (FlyShelf.Classes.SettingsManager.Current.EnableLocalNetworkSync)
                        {
                            mainWin.ViewModel.LocalServer.Start();
                        }
                    }
                }

                if (settingsChanged)
                {
                    FlyShelf.Classes.SettingsManager.Save();
                }

                // Re-select the correct items in ComboBoxes if needed
                if (RetentionCombo != null)
                {
                    for (int i = 0; i < RetentionCombo.Items.Count; i++)
                    {
                        var item = RetentionCombo.Items[i] as System.Windows.Controls.ComboBoxItem;
                        if (item?.Tag?.ToString() == FlyShelf.Classes.SettingsManager.Current.ClipboardRetentionDays.ToString())
                        {
                            RetentionCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }

            // Restart sniffer to update dynamic watchers based on new license state
            var mainWinSniffer = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
            if (mainWinSniffer != null && mainWinSniffer.ViewModel?.Sniffer != null)
            {
                mainWinSniffer.ViewModel.Sniffer.StartSniffing();
            }

            // Title bar badges
            if (ProBadgeTitleBar != null)
                ProBadgeTitleBar.Visibility = isPro ? Visibility.Visible : Visibility.Collapsed;
            if (FreeBadgeTitleBar != null)
                FreeBadgeTitleBar.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;

            // Settings tab — License Key card
            if (SettingsProBadge != null)
                SettingsProBadge.Visibility = isPro ? Visibility.Visible : Visibility.Collapsed;

            // Sizing controls locked overlays for Free tier
            if (ClipboardSizeLockedOverlay != null)
                ClipboardSizeLockedOverlay.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;
            if (FlyShelfSizeLockedOverlay != null)
                FlyShelfSizeLockedOverlay.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;
            if (SmartHistoryCleanupLockedOverlay != null)
                SmartHistoryCleanupLockedOverlay.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;

            // Dynamically prefix/unprefix lock indicators on RetentionCombo items (Never is locked for Free)
            if (RetentionCombo != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in RetentionCombo.Items)
                {
                    string text = item.Content?.ToString() ?? "";
                    if (item.Tag?.ToString() == "0")
                    {
                        if (isPro)
                        {
                            if (text.StartsWith("🔒 ")) item.Content = text.Substring(2);
                        }
                        else
                        {
                            if (!text.StartsWith("🔒 ")) item.Content = "🔒 " + text;
                        }
                    }
                    else
                    {
                        // Remove lock from 14 and 30 days since they are now free
                        if (text.StartsWith("🔒 ")) item.Content = text.Substring(2);
                    }
                }
            }

            if (SettingsLicenseDesc != null)
            {
                if (isPro)
                {
                    SettingsLicenseDesc.Text = $"Active: {FlyShelf.Classes.LicenseManager.MaskedKey}";
                    if (SettingsLicenseKeyInput != null) SettingsLicenseKeyInput.Visibility = Visibility.Collapsed;
                    if (SettingsActivateBtn != null)
                    {
                        SettingsActivateBtn.Content = "Deactivate";
                        SettingsActivateBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
                    }
                }
                else
                {
                    SettingsLicenseDesc.Text = "Activate Pro to unlock unlimited features.";
                    if (SettingsLicenseKeyInput != null) SettingsLicenseKeyInput.Visibility = Visibility.Visible;
                    if (SettingsActivateBtn != null)
                    {
                        SettingsActivateBtn.Content = "Activate";
                        SettingsActivateBtn.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
                    }
                }
            }
            if (SettingsLicenseError != null) SettingsLicenseError.Visibility = Visibility.Collapsed;

            // Buy Premium buttons — hide for Pro, show for Free
            if (SettingsBuyPremiumBtn != null)
                SettingsBuyPremiumBtn.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;
            if (AboutBuyPremiumLink != null)
                AboutBuyPremiumLink.Visibility = isPro ? Visibility.Collapsed : Visibility.Visible;

            // About tab license card
            if (LicenseStatusTitle != null)
            {
                if (isPro)
                {
                    LicenseStatusTitle.Text = "FlyShelf Pro";
                    LicenseStatusDesc.Text = $"Activated on {FlyShelf.Classes.LicenseManager.ActivatedAt}";
                    LicenseStatusBadgeText.Text = "PRO";
                    LicenseStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 245, 158, 11));
                    LicenseStatusBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
                    LicenseStatusPanel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 245, 158, 11));
                    LicenseActivationPanel.Visibility = Visibility.Collapsed;
                    LicenseDeactivatePanel.Visibility = Visibility.Visible;
                    ActiveKeyDisplay.Text = FlyShelf.Classes.LicenseManager.MaskedKey;
                }
                else
                {
                    LicenseStatusTitle.Text = "Free Tier";
                    LicenseStatusDesc.Text = "Upgrade to Pro to unlock unlimited features";
                    LicenseStatusBadgeText.Text = "FREE";
                    LicenseStatusBadge.Background = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.SubtleFillColorTertiary");
                    LicenseStatusBadgeText.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary");
                    LicenseStatusPanel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 16, 185, 129));
                    LicenseActivationPanel.Visibility = Visibility.Visible;
                    LicenseDeactivatePanel.Visibility = Visibility.Collapsed;
                }
            }

            // Clear any error text
            if (LicenseErrorText != null)
                LicenseErrorText.Visibility = Visibility.Collapsed;

            // Re-populate theme combo box to update lock symbols when license changes
            PopulateThemeCombo();
        }

        private void ActivateLicense_Click(object sender, RoutedEventArgs e)
        {
            string key = LicenseKeyInput?.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(key))
            {
                LicenseErrorText.Text = "Please enter a license key.";
                LicenseErrorText.Visibility = Visibility.Visible;
                return;
            }

            bool success = FlyShelf.Classes.LicenseManager.ActivateLicense(key);

            if (success)
            {
                LicenseErrorText.Visibility = Visibility.Collapsed;
                LicenseKeyInput.Text = "";
                RefreshLicenseUI();
                FlyShelf.Windows.ToastWindow.ShowToast("Pro license activated successfully!");
            }
            else
            {
                LicenseErrorText.Text = "Invalid license key. Please check and try again.";
                LicenseErrorText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                LicenseErrorText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Settings tab license key — handles both Activate and Deactivate.</summary>
        private void SettingsActivateLicense_Click(object sender, RoutedEventArgs e)
        {
            if (FlyShelf.Classes.LicenseManager.IsPro)
            {
                // Currently Pro → Deactivate
                var result = System.Windows.MessageBox.Show(
                    "Are you sure you want to deactivate your Pro license?\nYou can reactivate anytime with your key.",
                    "Deactivate License",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    FlyShelf.Classes.LicenseManager.DeactivateLicense();
                    RefreshLicenseUI();
                    FlyShelf.Windows.ToastWindow.ShowToast("License deactivated — reverted to Free tier.");
                }
            }
            else
            {
                // Currently Free → Activate
                string key = SettingsLicenseKeyInput?.Text?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(key))
                {
                    SettingsLicenseError.Text = "Please enter a license key.";
                    SettingsLicenseError.Visibility = Visibility.Visible;
                    return;
                }

                bool success = FlyShelf.Classes.LicenseManager.ActivateLicense(key);

                if (success)
                {
                    SettingsLicenseError.Visibility = Visibility.Collapsed;
                    SettingsLicenseKeyInput.Text = "";
                    RefreshLicenseUI();
                    FlyShelf.Windows.ToastWindow.ShowToast("Pro license activated successfully!");
                }
                else
                {
                    SettingsLicenseError.Text = "Invalid license key. Please check and try again.";
                    SettingsLicenseError.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                    SettingsLicenseError.Visibility = Visibility.Visible;
                }
            }
        }

        private void DeactivateLicense_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to deactivate your Pro license?\nYou can reactivate anytime with your key.",
                "Deactivate License",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                FlyShelf.Classes.LicenseManager.DeactivateLicense();
                RefreshLicenseUI();
                FlyShelf.Windows.ToastWindow.ShowToast("License deactivated — reverted to Free tier.");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // BUY PREMIUM — Opens payment page in default browser
        // ═══════════════════════════════════════════════════════════════

        private void BuyPremium_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                string deviceId = FlyShelf.Classes.SettingsManager.Current.DeviceId ?? "";
                // TODO: Update this URL once you deploy to Firebase Hosting
#if MSIX_STORE
                FlyShelf.Windows.ToastWindow.ShowToast("ℹ️ Pro upgrade is available at flyshelf.app");
                return;
#else
                string paymentUrl = $"https://fly-shelf.vercel.app/pricing.html?deviceId={Uri.EscapeDataString(deviceId)}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = paymentUrl,
                    UseShellExecute = true
                });
#endif
                FlyShelf.Windows.ToastWindow.ShowToast("🛒 Opening payment page in your browser...");
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("LICENSE", $"Failed to open payment URL: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast("❌ Could not open browser. Please visit our website to purchase.");
            }
        }

        // ═══ Device Send, Archive, Merge & Selection moved to HubWindow.SettingsHandlers.cs ═══
    }
}
