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
                        passesSearch = (clip.FileName != null && clip.FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                       (clip.RawContent != null && clip.RawContent.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                       (clip.FormatIdentifier != null && clip.FormatIdentifier.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    return passesType && passesSearch;
                }
                return false;
            };
            view.Refresh();
        }

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item) { _viewModel.TogglePin(item); e.Handled = true; }
        }

        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item) { _viewModel.RemoveItem(item); e.Handled = true; }
        }

        /// <summary>
        /// Shows a device picker popup and sends the group/archive .zip to the selected device.
        /// </summary>
        private async void SendToDevice_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe) return;
            var item = fe.DataContext as ClipboardItem;
            if (item == null) return;

            // Determine the file to send
            string fileToSend = "";
            if (item.ItemType == ClipboardItemType.Group || item.ItemType == ClipboardItemType.Folder)
            {
                fileToSend = item.ZippedArchivePath;
                if (string.IsNullOrEmpty(fileToSend) || !System.IO.File.Exists(fileToSend))
                {
                    ToastWindow.ShowToast("⏳ Zip is still being prepared, try again in a moment...");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(item.FilePath) && System.IO.File.Exists(item.FilePath))
            {
                fileToSend = item.FilePath;
            }
            else
            {
                ToastWindow.ShowToast("❌ No file available to send");
                return;
            }

            // Build the device picker popup
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = fe,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var popupBorder = new Border
            {
                Background = (Brush)FindResource("MicaWPF.Brushes.ApplicationBackgroundFillColorBase"),
                BorderBrush = (Brush)FindResource("MicaWPF.Brushes.ControlStrokeColorDefault"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8),
                MinWidth = 200,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20, ShadowDepth = 4, Opacity = 0.3, Color = Colors.Black
                }
            };

            var stack = new StackPanel();
            var header = new TextBlock
            {
                Text = "📡 Send to Device",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary"),
                Margin = new Thickness(8, 4, 8, 8)
            };
            stack.Children.Add(header);

            // Get alive peers from PeerManager
            var peerManager = PeerManager.Instance;
            var peers = peerManager?.ConnectedPeers?.Values
                .Where(p => p.IsAlive)
                .ToList() ?? new List<Classes.PeerConnection>();

            if (peers.Count == 0)
            {
                var noPeers = new TextBlock
                {
                    Text = "No devices connected",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary"),
                    Margin = new Thickness(8, 4, 8, 8)
                };
                stack.Children.Add(noPeers);

                // Also offer Firebase ForceSend as fallback
                var forceSendBtn = new Button
                {
                    Content = "☁️ Send via Cloud (all paired)",
                    FontSize = 12,
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(4, 4, 4, 4),
                    Cursor = Cursors.Hand
                };
                string capturedFile = fileToSend;
                var capturedItem = item;
                forceSendBtn.Click += async (s, args) =>
                {
                    popup.IsOpen = false;
                    ToastWindow.ShowToast($"☁️ Sending {capturedItem.FileName} via cloud...");
                    var syncItem = new ClipboardItem(capturedFile);
                    var devices = await CloudDiscoveryManager.GetActiveDevices();
                    var deviceIds = devices.Select(d => d.Id).ToList();
                    if (deviceIds.Count > 0)
                    {
                        int sent = await CloudDiscoveryManager.ForceSendToDevices(
                            new List<ClipboardItem> { syncItem }, deviceIds);
                        ToastWindow.ShowToast(sent > 0 ? $"✅ Sent to {sent} device(s) via cloud!" : "❌ Cloud send failed");
                    }
                    else { ToastWindow.ShowToast("❌ No paired devices found"); }
                };
                stack.Children.Add(forceSendBtn);
            }
            else
            {
                foreach (var peer in peers)
                {
                    string deviceId = peer.DeviceId;
                    string deviceName = peer.DeviceName;
                    string transport = peer.Transport;

                    var deviceBtn = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = new Thickness(2),
                        Cursor = Cursors.Hand,
                        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
                    };
                    deviceBtn.MouseEnter += (s, args) =>
                        deviceBtn.Background = (Brush)FindResource("MicaWPF.Brushes.SubtleFillColorTertiary");
                    deviceBtn.MouseLeave += (s, args) =>
                        deviceBtn.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

                    var deviceStack = new StackPanel { Orientation = Orientation.Horizontal };

                    string typeEmoji = deviceId.StartsWith("Mobile_", StringComparison.OrdinalIgnoreCase) ? "📱" : "💻";
                    string transportEmoji = transport == "LAN" ? "📡" : "🌐";

                    deviceStack.Children.Add(new TextBlock
                    {
                        Text = typeEmoji,
                        FontSize = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    });

                    var nameStack = new StackPanel();
                    nameStack.Children.Add(new TextBlock
                    {
                        Text = deviceName,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary")
                    });
                    nameStack.Children.Add(new TextBlock
                    {
                        Text = $"{transportEmoji} {transport}",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary")
                    });
                    deviceStack.Children.Add(nameStack);
                    deviceBtn.Child = deviceStack;

                    string capturedDeviceId = deviceId;
                    string capturedDeviceName = deviceName;
                    string capturedFile = fileToSend;
                    var capturedItem = item;

                    deviceBtn.MouseLeftButtonDown += async (s, args) =>
                    {
                        popup.IsOpen = false;
                        ToastWindow.ShowToast($"📡 Sending {capturedItem.FileName} to {capturedDeviceName}...");

                        bool success = false;
                        try
                        {
                            success = await PeerManager.Instance.PushFileToSinglePeer(
                                capturedDeviceId, capturedFile, capturedItem.FileName, "Archive");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("SEND_TO_DEVICE", $"P2P send failed: {ex.Message}");
                        }

                        if (success)
                        {
                            ToastWindow.ShowToast($"✅ Sent to {capturedDeviceName}!");
                        }
                        else
                        {
                            ToastWindow.ShowToast($"❌ Direct send failed to {capturedDeviceName} — trying cloud...");
                            // Fallback to ForceSend via cloud
                            try
                            {
                                var syncItem = new ClipboardItem(capturedFile);
                                int sent = await CloudDiscoveryManager.ForceSendToDevices(
                                    new List<ClipboardItem> { syncItem }, new List<string> { capturedDeviceId });
                                ToastWindow.ShowToast(sent > 0
                                    ? $"✅ Sent to {capturedDeviceName} via cloud!"
                                    : $"❌ Cloud send also failed to {capturedDeviceName}");
                            }
                            catch (Exception ex2)
                            {
                                ToastWindow.ShowToast($"❌ All send methods failed: {ex2.Message}");
                            }
                        }
                    };

                    stack.Children.Add(deviceBtn);
                }
            }

            popupBorder.Child = stack;
            popup.Child = popupBorder;
            popup.IsOpen = true;
        }

        /// <summary>
        /// Context menu wrapper for Send to Device — extracts the ClipboardItem from MenuItem.DataContext
        /// </summary>
        private void ContextMenu_SendToDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var item = fe.DataContext as ClipboardItem;
            if (item == null) return;

            // Close the context menu first
            if (fe is System.Windows.Controls.MenuItem mi && mi.Parent is System.Windows.Controls.ContextMenu cm)
                cm.IsOpen = false;

            // Reuse the SendToDevice_Click logic by creating a simulated event
            // We need to show the popup relative to the HubListView
            Dispatcher.InvokeAsync(() =>
            {
                var fakeArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = fe
                };
                SendToDevice_Click(fe, fakeArgs);
            });
        }

        /// <summary>
        /// Extract a .zip archive to a user-chosen folder
        /// </summary>
        private void ExtractArchive_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var item = fe.DataContext as ClipboardItem;
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            try
            {
                string ext = System.IO.Path.GetExtension(item.FilePath).ToLowerInvariant();
                if (ext != ".zip" && ext != ".apk")
                {
                    ToastWindow.ShowToast("⚠️ Only .zip and .apk extraction is supported");
                    return;
                }

                // Extract to a subfolder next to the archive (or in Downloads if the archive is in a temp path)
                string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                string baseDir = System.IO.Path.GetDirectoryName(item.FilePath) ?? "";
                
                // If zip is in a temp folder, extract to Downloads instead
                string tempDir = System.IO.Path.GetTempPath();
                if (baseDir.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    baseDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                }

                string targetDir = System.IO.Path.Combine(baseDir, baseName);
                System.IO.Directory.CreateDirectory(targetDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(item.FilePath, targetDir, overwriteFiles: true);
                ToastWindow.ShowToast($"✅ Extracted to {targetDir}");
                System.Diagnostics.Process.Start("explorer.exe", targetDir);
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Extract failed: {ex.Message}");
                Logger.LogAction("EXTRACT", $"Archive extraction error: {ex}");
            }
        }

        private void ExpandToggleSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item) { item.IsExpanded = !item.IsExpanded; }
            e.Handled = true;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool anyUnselected = _viewModel.DroppedItems.Any(i => !i.IsCheckedForMerge);
            foreach (var item in _viewModel.DroppedItems) { item.IsCheckedForMerge = anyUnselected; }
            UpdateMergeButton();
        }

        private void HubListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMergeButton();

        private void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => UpdateMergeButton(), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateMergeButton()
        {
            if (_viewModel == null || MergePdfFloatingBar == null) return;
            var checkedPdfs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedDocs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedImages = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            int totalChecked = checkedPdfs.Count + checkedDocs.Count + checkedImages.Count;

            if (totalChecked >= 2 || (checkedDocs.Count == 1 && checkedPdfs.Count == 0 && checkedImages.Count == 0))
            {
                MergePdfFloatingBar.Visibility = Visibility.Visible;
                if (checkedImages.Count > 0 && checkedPdfs.Count == 0 && checkedDocs.Count == 0)
                { MergeBarText.Text = $"{checkedImages.Count} Images selected"; MergeBarBtn.Content = "Merge Images"; MergeBarBtn.ToolTip = $"Merge {checkedImages.Count} images into a single PDF"; }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedImages.Count == 0 && checkedDocs.Count == 1)
                { MergeBarText.Text = "1 DOC selected"; MergeBarBtn.Content = "Convert to PDF"; MergeBarBtn.ToolTip = "Convert DOC/DOCX to PDF"; }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedImages.Count == 0)
                { MergeBarText.Text = $"{checkedDocs.Count} DOCs selected"; MergeBarBtn.Content = "Convert DOCs"; MergeBarBtn.ToolTip = $"Convert {checkedDocs.Count} DOC files to PDF"; }
                else
                { MergeBarText.Text = $"{totalChecked} Files selected"; MergeBarBtn.Content = "Merge Files"; MergeBarBtn.ToolTip = $"Convert & merge all {totalChecked} files"; }
            }
            else { MergePdfFloatingBar.Visibility = Visibility.Collapsed; }
        }

        private async void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            var checkedPdfs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedDocs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedImages = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var convertedPdfPaths = new List<string>();

            if (checkedDocs.Count > 0)
            {
                ToastWindow.ShowToast($"📄 Converting {checkedDocs.Count} DOC file(s) to PDF...");
                foreach (var doc in checkedDocs)
                {
                    string pdfPath = await ConversionUtils.ConvertDocToPdfAsync(doc.FilePath);
                    if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath)) convertedPdfPaths.Add(pdfPath);
                    else ToastWindow.ShowToast($"❌ Failed to convert: {doc.FileName}");
                }
            }

            if (checkedImages.Count > 0)
            {
                ToastWindow.ShowToast($"🖼️ Formatting {checkedImages.Count} image(s) to PDF...");
                foreach (var img in checkedImages)
                {
                    try
                    {
                        string pdfPath = await System.Threading.Tasks.Task.Run(() => ConversionUtils.ConvertImageToPdf(img.FilePath));
                        if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath)) convertedPdfPaths.Add(pdfPath);
                    }
                    catch (Exception ex) { ToastWindow.ShowToast($"❌ Failed to format: {img.FileName}"); Logger.LogAction("IMAGE2PDF_ERR", ex.ToString()); }
                }
            }

            if (checkedPdfs.Count == 0 && checkedDocs.Count + checkedImages.Count == convertedPdfPaths.Count && convertedPdfPaths.Count == 1)
            {
                foreach (var item in _viewModel.DroppedItems) item.IsCheckedForMerge = false;
                UpdateMergeButton();
                var newItem = new ClipboardItem(convertedPdfPaths[0]);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                ToastWindow.ShowToast("✅ Converted to PDF");
                return;
            }

            var allPdfs = new List<ClipboardItem>();
            allPdfs.AddRange(checkedPdfs);
            foreach (string path in convertedPdfPaths) allPdfs.Add(new ClipboardItem(path));

            if (allPdfs.Count > 1)
            {
                foreach (var item in _viewModel.DroppedItems) item.IsCheckedForMerge = false;
                UpdateMergeButton();
                var win = new PdfMergeWindow(allPdfs, _viewModel);
                App.ActiveMergeWindow = win;
                win.Closed += (_, __) => App.ActiveMergeWindow = null;
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                win.Topmost = true; win.Show(); win.Activate(); win.Focus(); win.Topmost = false;
            }
            else if (allPdfs.Count == 1)
            {
                foreach (var item in _viewModel.DroppedItems) item.IsCheckedForMerge = false;
                UpdateMergeButton();
                var newItem = new ClipboardItem(allPdfs[0].FilePath);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                ToastWindow.ShowToast("✅ PDF added to clipboard");
            }
            else { ToastWindow.ShowToast("Select 2+ files to merge, or 1 image/doc to convert."); }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_devicePairedHandler != null) { DevicePairingManager.OnDevicePaired -= _devicePairedHandler; _devicePairedHandler = null; }
            if (_viewModel?.DroppedItems != null) { _viewModel.DroppedItems.CollectionChanged -= DroppedItems_CollectionChanged; }
            if (_deviceRefreshTimer != null) { _deviceRefreshTimer.Stop(); _deviceRefreshTimer = null; }
        }

    }
}
