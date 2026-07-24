// ---------------------------------------------------------------
// HubWindow — Diagnostics, Filters, Merge, Scroll & Lifecycle
// RunDiagnostics, Server restart, Filter/Search, Pin/Delete,
// Merge PDFs, Browser-style Smooth Scroll, OnClosed
// Split from HubWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Globalization;
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
using FlyShelf.Helpers;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private static readonly string[] s_plusSeparator = new[] { " + " };

        // ═══ Summon Hotkey Customization ═══
        private bool _isRecordingHotkey = false;
        private uint _recordedModifier = 0;
        private uint _recordedKey = 0;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal void BuildHotkeyKeycaps()
        {
            if (HotkeyDisplay == null) return;
            HotkeyDisplay.Children.Clear();
            var display = SettingsManager.Current.HotkeyDisplayString;
            var parts = display.Split(s_plusSeparator, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    HotkeyDisplay.Children.Add(new TextBlock
                    {
                        Text = "+",
                        Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary"),
                        FontSize = 14, FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 6, 0)
                    });
                }
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(40, 99, 102, 241)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 6, 12, 6),
                    Child = new TextBlock
                    {
                        Text = parts[i],
                        Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary"),
                        FontSize = 14, FontWeight = FontWeights.SemiBold
                    }
                };
                HotkeyDisplay.Children.Add(border);
            }

            // Show reset button only if not default
            var s = SettingsManager.Current;
            ResetHotkeyBtn.Visibility = (s.HotkeyModifier == 0x0001 && s.HotkeyKey == 0x43)
                ? Visibility.Collapsed : Visibility.Visible;

            // Update dynamic labels elsewhere
            if (SummonHotkeyLabel != null)
                SummonHotkeyLabel.Text = $"{s.HotkeyDisplayString} / Widget popup";
            if (ShortcutsHotkeyLabel != null)
                ShortcutsHotkeyLabel.Text = s.HotkeyDisplayString.Replace(" ", "");
        }

        private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecordingHotkey)
            {
                StopRecording();
                return;
            }
            _isRecordingHotkey = true;
            _recordedModifier = 0;
            _recordedKey = 0;
            ChangeHotkeyBtn.Content = "Cancel";
            HotkeyRecorderBorder.Visibility = Visibility.Visible;
            HotkeyRecorderText.Text = "Press keys...";
            HotkeyWarningBar.Visibility = Visibility.Collapsed;
            HotkeyRecorderBorder.Focus();
            this.PreviewKeyDown += HotkeyRecorder_PreviewKeyDown;
            this.PreviewKeyUp += HotkeyRecorder_PreviewKeyUp;
        }

        private void StopRecording()
        {
            _isRecordingHotkey = false;
            ChangeHotkeyBtn.Content = "Change";
            HotkeyRecorderBorder.Visibility = Visibility.Collapsed;
            this.PreviewKeyDown -= HotkeyRecorder_PreviewKeyDown;
            this.PreviewKeyUp -= HotkeyRecorder_PreviewKeyUp;
        }

        private void HotkeyRecorder_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isRecordingHotkey) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Escape cancels
            if (key == Key.Escape)
            {
                StopRecording();
                return;
            }

            // Collect modifiers
            uint mod = 0;
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) mod |= 0x0002;
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) mod |= 0x0001;
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mod |= 0x0004;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mod |= 0x0008;

            bool isModifier = key == Key.LeftCtrl || key == Key.RightCtrl ||
                              key == Key.LeftAlt || key == Key.RightAlt ||
                              key == Key.LeftShift || key == Key.RightShift ||
                              key == Key.LWin || key == Key.RWin;

            if (isModifier)
            {
                _recordedModifier = mod;
                var mparts = new List<string>();
                if ((mod & 0x0002) != 0) mparts.Add("Ctrl");
                if ((mod & 0x0001) != 0) mparts.Add("Alt");
                if ((mod & 0x0004) != 0) mparts.Add("Shift");
                if ((mod & 0x0008) != 0) mparts.Add("Win");
                HotkeyRecorderText.Text = string.Join(" + ", mparts) + " + ...";
                return;
            }

            // Must have at least one modifier
            if (mod == 0)
            {
                HotkeyRecorderText.Text = "Need modifier key";
                return;
            }

            // Convert WPF Key to Win32 VK
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            _recordedModifier = mod;
            _recordedKey = vk;

            // Test registration
            bool available = TestHotkeyAvailability(mod, vk);

            if (available)
            {
                var s = SettingsManager.Current;
                s.HotkeyModifier = mod;
                s.HotkeyKey = vk;
                HotkeyWarningBar.Visibility = Visibility.Collapsed;
                StopRecording();
                BuildHotkeyKeycaps();
                ToastWindow.ShowToast($"✅ Hotkey changed to {s.HotkeyDisplayString}");
            }
            else
            {
                var keyName = AdvanceSettings.GetKeyName(vk);
                var fparts = new List<string>();
                if ((mod & 0x0002) != 0) fparts.Add("Ctrl");
                if ((mod & 0x0001) != 0) fparts.Add("Alt");
                if ((mod & 0x0004) != 0) fparts.Add("Shift");
                if ((mod & 0x0008) != 0) fparts.Add("Win");
                fparts.Add(keyName);
                HotkeyRecorderText.Text = string.Join("+", fparts) + " ❌";
                HotkeyWarningBar.Visibility = Visibility.Visible;
                HotkeyWarningText.Text = $"⚠️ {string.Join(" + ", fparts)} is used by another application";
            }
        }

        private void HotkeyRecorder_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            // No-op — hotkey captured on KeyDown
        }

        private bool TestHotkeyAvailability(uint mod, uint vk)
        {
            var mainWin = Application.Current.MainWindow;
            if (mainWin == null) return true;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(mainWin).Handle;
            if (hwnd == IntPtr.Zero) return true;

            const int TEST_HOTKEY_ID = 9999;
            bool ok = RegisterHotKey(hwnd, TEST_HOTKEY_ID, mod | 0x4000, vk);
            if (ok) UnregisterHotKey(hwnd, TEST_HOTKEY_ID);
            return ok;
        }

        private void ResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            var s = SettingsManager.Current;
            s.HotkeyModifier = 0x0001; // MOD_ALT
            s.HotkeyKey = 0x43; // VK_C
            HotkeyWarningBar.Visibility = Visibility.Collapsed;
            StopRecording();
            BuildHotkeyKeycaps();
            ToastWindow.ShowToast("✅ Hotkey reset to Alt + C");
        }

        private void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ToastWindow.ShowToast("🔍 Network diagnostics started...");
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Logger.DumpNetworkDiagnostics();
                        Dispatcher.Invoke(() =>
                        {
                            ToastWindow.ShowToast("🔍  Network diagnostics captured!");
#if !MSIX_STORE
                            RefreshLogs_Click(null, null);
#endif
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
            catch { } // Best-effort: failure is acceptable
        }

#if !MSIX_STORE
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
                WindowHelper.ShowInForeground(_networkLogsWindow);
            }
            catch { } // Best-effort: failure is acceptable
        }
#endif // !MSIX_STORE

#if MSIX_STORE
        // ─── Store-build stub: XAML references this Click handler ───
        private void OpenNetworkLogs_Click(object sender, RoutedEventArgs e) { }
#endif

        // ═══ LAN TRANSFER MANAGER WINDOW ═══
        private static TransferManagerWindow? _transferManagerWindow;
        private void OpenTransferManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TransferManagerWindow.ShowOrActivate();
            }
            catch { } // Best-effort: failure is acceptable
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
                    ClipboardHelper.SafeSetText($"⚠ Device '{deviceName}' has no active URL — cannot fetch remote data.\nDevice may be offline. Try Force Sync first.");
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
                sb.AppendLine(CultureInfo.InvariantCulture, $"  FlyShelf Remote Diagnostic — {deviceName}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  URL: {activeUrl}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Fetched: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("═══════════════════════════════════════════════════════════");

                // ── SECTION 1: Health ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  DEVICE HEALTH                                          │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    var hc = HttpClientPool.Default;
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
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Version:    v{version}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Device ID:  {devId}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Type:       {devType}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Uptime:     {uptimeStr}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Peers:      {peers} connected");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  LAN:        {(string.IsNullOrEmpty(lanUrl) ? "—" : lanUrl)}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Cloudflare: {(string.IsNullOrEmpty(cfUrl) ? "—" : cfUrl)}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  ❌ Failed to fetch health: {ex.Message}");
                }

                // ── SECTION 2: Clipboard Contents ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  CLIPBOARD CONTENTS                                     │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    var sc = HttpClientPool.Default;
                    var syncReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{baseUrl}/api/sync");
                    if (!string.IsNullOrEmpty(pairingKey))
                        syncReq.Headers.Add("X-Pairing-Key", pairingKey);
                    if (!string.IsNullOrEmpty(pin))
                        syncReq.Headers.Add("Authorization", $"Bearer {pin}");
                    syncReq.Headers.Add("X-FlyShelf-Client", "DesktopSync");

                    var syncHttpResp = await sc.SendAsync(syncReq);
                    var syncResp = await syncHttpResp.Content.ReadAsStringAsync();
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
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  {icon} [{idx}] {type.ToUpper(CultureInfo.InvariantCulture)} — {time}");
                        if (!string.IsNullOrEmpty(title)) sb.AppendLine(CultureInfo.InvariantCulture, $"     Title:  {title}");
                        if (!string.IsNullOrEmpty(fileName) && fileName != title) sb.AppendLine(CultureInfo.InvariantCulture, $"     File:   {fileName}");
                        if (!string.IsNullOrEmpty(source)) sb.AppendLine(CultureInfo.InvariantCulture, $"     From:   {source} ({sourceType})");

                        if (type == "Text" || type == "Url")
                        {
                            string preview = raw.Length > 200 ? string.Concat(raw.AsSpan(0, 200), "...") : raw;
                            preview = preview.Replace("\r\n", "\\n").Replace("\n", "\\n");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"     Content: {preview}");
                        }
                        else if (type == "Image" || type == "QRCode")
                        {
                            if (!string.IsNullOrEmpty(previewUrl)) sb.AppendLine(CultureInfo.InvariantCulture, $"     Preview: {baseUrl}{previewUrl}");
                            if (!string.IsNullOrEmpty(downloadUrl) && downloadUrl.StartsWith('/')) sb.AppendLine(CultureInfo.InvariantCulture, $"     Download: {baseUrl}{downloadUrl}");
                        }
                        else if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            if (downloadUrl.StartsWith('/')) sb.AppendLine(CultureInfo.InvariantCulture, $"     Download: {baseUrl}{downloadUrl}");
                            else if (downloadUrl.StartsWith("http", StringComparison.Ordinal)) sb.AppendLine(CultureInfo.InvariantCulture, $"     Path: {downloadUrl}");
                        }
                    }

                    if (idx == 0) sb.AppendLine("  (clipboard is empty)");
                    else sb.AppendLine(CultureInfo.InvariantCulture, $"\n  — {idx} items on clipboard");
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  ❌ Failed to fetch clipboard: {ex.Message}"); }

                // ── SECTION 3: Logs ──
                sb.AppendLine();
                sb.AppendLine("┌─────────────────────────────────────────────────────────┐");
                sb.AppendLine("│  NETWORK LOGS (last 200)                                │");
                sb.AppendLine("└─────────────────────────────────────────────────────────┘");
                try
                {
                    var lc = HttpClientPool.Default;
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
                                if (line.Contains("[HTTP]", StringComparison.Ordinal) && (line.Contains("GET /api/health", StringComparison.Ordinal) || line.Contains("GET /health", StringComparison.Ordinal))) continue;
                                sb.AppendLine(line);
                                count++;
                            }
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture, $"\n— {count} log entries (health noise filtered)");
                    }
                    else { sb.AppendLine(CultureInfo.InvariantCulture, $"  ❌ HTTP {logResp.StatusCode}: {logJson}"); }
                }
                catch (Exception ex) { sb.AppendLine(CultureInfo.InvariantCulture, $"  ❌ Failed to fetch logs: {ex.Message}"); }

                ClipboardHelper.SafeSetText(sb.ToString());
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
                ServerDiagnosticsLog.Foreground = TryFindResource("WarningColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

                vm.LocalServer.Stop();
                ServerDiagnosticsLog.Text += "✅ Server stopped.\n⏳ Starting server...\n";

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            vm.LocalServer.Start();
                            vm.RefreshLocalServerData();
                            string diagnostics = GetServerDiagnostics();
                            if (ServerDiagnosticsLog != null)
                            {
                                ServerDiagnosticsLog.Text = diagnostics;
                                ServerDiagnosticsLog.Foreground = TryFindResource("SuccessColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                            }
                            ToastWindow.ShowToast("🔄 Server restarted — check diagnostics below");
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Server restart error: {ex.Message}"); }
                    });
                });
            }
            catch (Exception ex)
            {
                ServerDiagnosticsLog.Text = $"❌ Restart failed: {ex.Message}";
                ServerDiagnosticsLog.Foreground = TryFindResource("DangerColor") as Brush ?? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            }
        }

        private async void CopyServerDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string diagnostics = await System.Threading.Tasks.Task.Run(() => GetServerDiagnostics());
                string systemInfo = $"=== FlyShelf Server Diagnostics ===\n" +
                    $"PC Name: {Environment.MachineName}\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $"User: {Environment.UserName}\n" +
                    $"Is Admin: {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"======================================\n\n{diagnostics}";
                ClipboardHelper.SafeSetText(systemInfo);
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
                    if (line.Contains("[BIND]", StringComparison.Ordinal) || line.Contains("[NETWORK", StringComparison.Ordinal) || line.Contains("[TCP PROXY]", StringComparison.Ordinal) ||
                        line.Contains("[CLOUDFLARE]", StringComparison.Ordinal) || line.Contains("[CF_STDERR]", StringComparison.Ordinal) || line.Contains("[HEARTBEAT]", StringComparison.Ordinal) ||
                        line.Contains("[FIREBASE SYNC]", StringComparison.Ordinal) || line.Contains("[DIAGNOSTICS]", StringComparison.Ordinal) || (line.Contains("[HTTP]", StringComparison.Ordinal) && line.Contains("health", StringComparison.Ordinal)))
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
                
                // Toggle image grid mode when "Image" filter is active
                bool isImageMode = _currentFilterTag == "Image";
                HubListView.Visibility = isImageMode ? Visibility.Collapsed : Visibility.Visible;
                if (ImageGridScroll != null)
                    ImageGridScroll.Visibility = isImageMode ? Visibility.Visible : Visibility.Collapsed;
                
                ApplyFilters();
            }
        }

        private void ImageGrid_Copy(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ClipboardItem clip)
            {
                try
                {
                    var dataObj = new System.Windows.DataObject();
                    if (!string.IsNullOrEmpty(clip.FilePath) && System.IO.File.Exists(clip.FilePath))
                    {
                        var dropList = new System.Collections.Specialized.StringCollection();
                        dropList.Add(clip.FilePath);
                        dataObj.SetFileDropList(dropList);
                        dataObj.SetData(System.Windows.DataFormats.Text, clip.FilePath);
                    }
                    else if (!string.IsNullOrEmpty(clip.RawContent))
                    {
                        dataObj.SetData(System.Windows.DataFormats.Text, clip.RawContent);
                    }
                    System.Windows.Clipboard.SetDataObject(dataObj, true);
                }
                catch { }
            }
        }

        private void ImageGrid_Delete(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ClipboardItem clip)
            {
                _viewModel?.DroppedItems?.Remove(clip);
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
            if (HubListView == null) return;
            // Use the Hub's ISOLATED CollectionView (not GetDefaultView which is shared with MainWindow)
            var view = _hubCollectionViewSource?.View as ListCollectionView;
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
                        
                        bool extMatch = !string.IsNullOrEmpty(clip.Extension) && string.Equals(clip.Extension.Replace(".", "", StringComparison.Ordinal).Trim(), q, StringComparison.OrdinalIgnoreCase);
                        bool pathExtMatch = false;
                        if (!string.IsNullOrEmpty(clip.FilePath))
                        {
                            try
                            {
                                string ext = System.IO.Path.GetExtension(clip.FilePath).Replace(".", "", StringComparison.Ordinal).Trim();
                                pathExtMatch = string.Equals(ext, q, StringComparison.OrdinalIgnoreCase);
                            }
                            catch { } // Best-effort: failure is acceptable
                        }
                        bool typeMatch = string.Equals(clip.ItemType.ToString(), q, StringComparison.OrdinalIgnoreCase);

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
                        if (item?.Tag?.ToString() == FlyShelf.Classes.SettingsManager.Current.ClipboardRetentionDays.ToString(CultureInfo.InvariantCulture))
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
                            if (text.StartsWith("[Locked] ", StringComparison.Ordinal)) item.Content = text.Substring(9);
                        }
                        else
                        {
                            if (!text.StartsWith("[Locked] ", StringComparison.Ordinal)) item.Content = "[Locked] " + text;
                        }
                    }
                    else
                    {
                        // Remove lock from 14 and 30 days since they are now free
                        if (text.StartsWith("[Locked] ", StringComparison.Ordinal)) item.Content = text.Substring(9);
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
                    var warnBrush = TryFindResource("WarningColor") as System.Windows.Media.SolidColorBrush;
                    var warnColor = warnBrush?.Color ?? FlyShelf.Helpers.ThemeColors.WarningAmber;
                    LicenseStatusBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, warnColor.R, warnColor.G, warnColor.B));
                    LicenseStatusBadgeText.Foreground = warnBrush ?? new System.Windows.Media.SolidColorBrush(warnColor);
                    LicenseStatusPanel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(26, warnColor.R, warnColor.G, warnColor.B));
                    LicenseActivationPanel.Visibility = Visibility.Collapsed;
                    LicenseDeactivatePanel.Visibility = Visibility.Visible;
                    ActiveKeyDisplay.Text = FlyShelf.Classes.LicenseManager.MaskedKey;
                }
                else
                {
                    LicenseStatusTitle.Text = "Free Tier";
                    LicenseStatusDesc.Text = "Upgrade to Pro to unlock unlimited features";
                    LicenseStatusBadgeText.Text = "FREE";
                    LicenseStatusBadge.Background = (TryFindResource("MicaWPF.Brushes.SubtleFillColorTertiary") as System.Windows.Media.Brush) ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 128, 128, 128));
                    LicenseStatusBadgeText.Foreground = (TryFindResource("MicaWPF.Brushes.TextFillColorTertiary") as System.Windows.Media.Brush) ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
                    var successBrush = TryFindResource("SuccessColor") as System.Windows.Media.SolidColorBrush;
                    var successColor = successBrush?.Color ?? FlyShelf.Helpers.ThemeColors.SuccessGreen;
                    LicenseStatusPanel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(26, successColor.R, successColor.G, successColor.B));
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

        private async void ActivateLicense_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Button;
            try
            {
                string key = LicenseKeyInput?.Text?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(key))
                {
                    if (LicenseErrorText != null)
                    {
                        LicenseErrorText.Text = "Please enter a license key.";
                        LicenseErrorText.Visibility = Visibility.Visible;
                    }
                    return;
                }

                // Disable button and show progress
                btn?.SetValue(IsEnabledProperty, false);
                if (LicenseErrorText != null) { LicenseErrorText.Text = "Activating..."; LicenseErrorText.Foreground = TryFindResource("WarningColor") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); LicenseErrorText.Visibility = Visibility.Visible; }

                bool success = await FlyShelf.Classes.LicenseManager.ActivateLicenseAsync(key);

                if (success)
                {
                    if (LicenseErrorText != null) LicenseErrorText.Visibility = Visibility.Collapsed;
                    if (LicenseKeyInput != null) LicenseKeyInput.Text = "";
                    RefreshLicenseUI();
                    FlyShelf.Windows.ToastWindow.ShowToast("Pro license activated successfully!");
                }
                else
                {
                    if (LicenseErrorText != null)
                    {
                        LicenseErrorText.Text = "Invalid license key. Please check and try again.";
                        LicenseErrorText.Foreground = TryFindResource("DangerColor") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        LicenseErrorText.Visibility = Visibility.Visible;
                    }
                }

                btn?.SetValue(IsEnabledProperty, true);
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("UI", $"Activation click failed: {ex.Message}");
                if (LicenseErrorText != null)
                {
                    LicenseErrorText.Text = $"Activation failed: {ex.Message}";
                    LicenseErrorText.Foreground = TryFindResource("DangerColor") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                    LicenseErrorText.Visibility = Visibility.Visible;
                }
                btn?.SetValue(IsEnabledProperty, true);
            }
        }

        /// <summary>Settings tab license key — handles both Activate and Deactivate.</summary>
        private async void SettingsActivateLicense_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
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

                    bool success = await FlyShelf.Classes.LicenseManager.ActivateLicenseAsync(key);

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
                        SettingsLicenseError.Foreground = TryFindResource("DangerColor") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        SettingsLicenseError.Visibility = Visibility.Visible;
                    }
                }
            });
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
            FlyShelf.Classes.UpgradePrompt.OpenSecureCheckout(this);
        }

        // ═══════════════════════════════════════════════════════════════
        // AI SETTINGS — Separate AI Tab
        // ═══════════════════════════════════════════════════════════════

        internal void PopulateHubAiSettings()
        {
            var settings = SettingsManager.Current;

            // API Key — show masked
            if (!string.IsNullOrEmpty(settings.AiApiKey))
            {
                string key = settings.AiApiKey;
                HubAiApiKeyBox.Text = key.Length > 8 ? string.Concat(key.AsSpan(0, 4), "...", key.AsSpan(key.Length - 4)) : "••••••••";
                HubAiApiKeyBox.Tag = "masked";
                HubAiApiKeyStatus.Text = "✅ API key configured";
                HubAiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            else
            {
                HubAiApiKeyBox.Text = "";
                HubAiApiKeyBox.Tag = null;
                HubAiApiKeyStatus.Text = "⚠️ No API key set — paste one above to enable cloud AI";
                HubAiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            }

            // Show detected provider
            UpdateHubProviderStatus();

            // Method combo
            var method = settings.DefaultAiMethod?.ToLowerInvariant() ?? "auto";
            for (int i = 0; i < HubAiMethodCombo.Items.Count; i++)
            {
                if (HubAiMethodCombo.Items[i] is ComboBoxItem ci && ci.Tag as string == method)
                {
                    HubAiMethodCombo.SelectedIndex = i;
                    break;
                }
            }

            // Status
            UpdateHubAiStatus();
        }

        private void UpdateHubProviderStatus()
        {
            string active = AiProviderService.Instance.ActiveProviderName;
            HubAiProviderStatus.Text = $"Detected provider: {active}";
        }

        private void UpdateHubAiStatus()
        {
            var provider = AiProviderService.Instance.ActiveProviderName;
            bool hasKey = AiProviderService.Instance.HasCloudApiKey;
            HubAiCurrentStatus.Text = $"Provider: {provider} | API Key: {(hasKey ? "✅ Configured" : "❌ Not set")} | AI: {(SettingsManager.Current.AiEnabled ? "Enabled" : "Disabled")}";
        }

        private void HubAiApiKeySave_Click(object sender, RoutedEventArgs e)
        {
            string newKey = HubAiApiKeyBox.Text?.Trim() ?? "";
            if (HubAiApiKeyBox.Tag as string == "masked") return;

            SettingsManager.Current.AiApiKey = newKey;
            // Auto-detect provider from key and save
            SettingsManager.Current.AiProvider = "auto";
            SettingsManager.Save();

            if (!string.IsNullOrEmpty(newKey))
            {
                HubAiApiKeyBox.Text = newKey.Length > 8 ? string.Concat(newKey.AsSpan(0, 4), "...", newKey.AsSpan(newKey.Length - 4)) : "••••••••";
                HubAiApiKeyBox.Tag = "masked";
                HubAiApiKeyStatus.Text = "✅ API key saved and encrypted!";
                HubAiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            else
            {
                HubAiApiKeyStatus.Text = "⚠️ API key cleared";
                HubAiApiKeyStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            }
            UpdateHubProviderStatus();
            UpdateHubAiStatus();
        }

        private void HubAiMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HubAiMethodCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag)
            {
                SettingsManager.Current.DefaultAiMethod = tag;
                SettingsManager.Save();
            }
        }

        // ═══ Device Send, Archive, Merge & Selection moved to HubWindow.SettingsHandlers.cs ═══
    }
}

