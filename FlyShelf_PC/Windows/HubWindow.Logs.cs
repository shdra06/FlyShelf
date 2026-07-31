// ---------------------------------------------------------------
// HubWindow — Logs, Diagnostics & Drag-Drop
// RefreshLogs, SendAllLogs, CopyNetworkLogs, SendLogsToDashboard
// Window_Drop, DragEnter, DragOver, DragLeave
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
#if !MSIX_STORE
        private async void RefreshLogs_Click(object? sender, RoutedEventArgs? e)
        {
            // Unified log viewer was removed — logs are accessed via Send All Logs / Copy Logs buttons.
            // This method is kept as a refresh entry point that re-populates the server diagnostics panel.
            try
            {
                if (ServerDiagnosticsLog != null)
                {
                    ServerDiagnosticsLog.Text = await System.Threading.Tasks.Task.Run(() => GetServerDiagnostics());
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private async void SendAllLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var report = await System.Threading.Tasks.Task.Run(() =>
                {
                    // Build a comprehensive diagnostic report from all log sources,
                    // filtering out redundant GET /api/health noise
                    var rpt = new System.Text.StringBuilder();
                    string logsDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "FlyShelf","Logs");

                    // Helper: filter out standalone GET /api/health lines (noise from 60s health monitor)
                    // Keep lines that mention health in an ERROR context
                    Func<string, bool> isUsefulLine = (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return false;
                        // Skip pure health-check spam: "[...] [HTTP] [...] GET /api/health"
                        if (line.Contains("[HTTP]", StringComparison.Ordinal) && line.Contains("GET /api/health", StringComparison.Ordinal)) return false;
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
                            string pattern = line.Length > 28 ? line[28..].Trim() : line;
                            
                            if (pattern == lastPattern)
                            {
                                repeatCount++;
                            }
                            else
                            {
                                // Flush previous repeat group
                                if (repeatCount > 2)
                                {
                                    dedupedLines.Add(string.Create(CultureInfo.InvariantCulture, $"repeated {repeatCount}× (collapsed)"));
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
                            dedupedLines.Add(string.Create(CultureInfo.InvariantCulture, $"repeated {repeatCount}× (collapsed)"));
                        }

                        if (dedupedLines.Any())
                        {
                            rpt.AppendLine("");
                            rpt.AppendLine("  ACTIVITY LOG");
                            rpt.AppendLine("");
                            foreach (var line in dedupedLines) rpt.AppendLine(line);
                        }
                    }

                    // 2. Network Diagnostics Log
                    string netLogFile = Logger.GetNetworkLogPath();
                    if (System.IO.File.Exists(netLogFile))
                    {
                        var lines = System.IO.File.ReadAllLines(netLogFile).Where(isUsefulLine);
                        if (lines.Any())
                        {
                            rpt.AppendLine();
                            rpt.AppendLine("");
                            rpt.AppendLine("  NETWORK DIAGNOSTICS");
                            rpt.AppendLine("");
                            foreach (var line in lines) rpt.AppendLine(line);
                        }
                    }

                    // 3. Server Troubleshooting (already filtered by GetServerDiagnostics, but also strip health)
                    string serverDiag = GetServerDiagnostics();
                    if (!string.IsNullOrWhiteSpace(serverDiag) && !serverDiag.StartsWith("No", StringComparison.Ordinal))
                    {
                        var lines = serverDiag.Split('\n').Where(l => isUsefulLine(l));
                        if (lines.Any())
                        {
                            rpt.AppendLine();
                            rpt.AppendLine("");
                            rpt.AppendLine("  SERVER TROUBLESHOOTING");
                            rpt.AppendLine("");
                            foreach (var line in lines) rpt.AppendLine(line.TrimEnd('\r'));
                        }
                    }

                    return rpt;
                });

                if (report.Length == 0)
                {
                    ToastWindow.ShowToast("No logs to send");
                    return;
                }

                // Prepend system info header
                var header = new System.Text.StringBuilder();
                header.AppendLine("");
                header.AppendLine("  FlyShelf Full Diagnostic Report");
                header.AppendLine(CultureInfo.InvariantCulture, $"  PC: {Environment.MachineName}");
                header.AppendLine(CultureInfo.InvariantCulture, $"  OS: {Environment.OSVersion}");
                header.AppendLine(CultureInfo.InvariantCulture, $"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine(CultureInfo.InvariantCulture, $"  Version: {UpdateManager.CurrentVersion}");
                header.AppendLine("");
                header.AppendLine();
                header.Append(report);

                Classes.ClipboardHelper.SafeSetText(header.ToString());
                ToastWindow.ShowToast("All logs copied to clipboard (health-check noise filtered)  paste and send!");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to copy: {ex.Message}");
            }
        }

        private void CopyNetworkLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logs = Logger.GetRecentNetworkLogs(200);
                Classes.ClipboardHelper.SafeSetText(logs);
                ToastWindow.ShowToast("Network logs copied to clipboard (last 200 lines)");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed to copy: {ex.Message}");
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
                    ToastWindow.ShowToast("No network logs to send");
                    SendLogsToDashboardBtn.IsEnabled = true;
                    return;
                }

                // ── Always save a local diagnostic file ──
                string logsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf","Logs");
                System.IO.Directory.CreateDirectory(logsDir);
                string deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                string deviceTag = deviceName.Replace("","_").Replace("/","_");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                string fileName = $"diagnostic_{deviceTag}_{timestamp}.log";
                string filePath = System.IO.Path.Combine(logsDir, fileName);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("");
                sb.AppendLine(CultureInfo.InvariantCulture, $"FlyShelf Diagnostic Log  {deviceName}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  PC Host:  {Environment.MachineName}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  OS:       {Environment.OSVersion}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Entries:  {logLines.Count}");
                sb.AppendLine("");
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
                        string serverUrl = vm.LocalServer.ServerUrl?.TrimEnd('/') ?? "http://localhost:8999";  // CA1866: char overload already used
                        var client = HttpClientPool.Quick;
                        var json = System.Text.Json.JsonSerializer.Serialize(logLines);
                        var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        content.Headers.Add("X-FlyShelf-Client","DesktopApp");
                        content.Headers.Add("X-Device-Name", deviceName);
                        var res = await client.PostAsync($"{serverUrl}/api/logs", content);
                        dashboardSuccess = res.IsSuccessStatusCode;
                    }
                    catch { /* Server POST failed  file is still saved */ }
                }

                string msg = string.Create(CultureInfo.InvariantCulture, $"{logLines.Count} entries saved  {fileName}");
                if (dashboardSuccess) msg +="\n Also pushed to web dashboard";
                msg += $"\n  {logsDir}";
                ToastWindow.ShowToast(msg);

                // Open the Logs folder so user can grab the file
                try { System.Diagnostics.Process.Start("explorer.exe", logsDir); } catch { } // Best-effort: failure is acceptable
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Failed: {ex.Message}");
            }
            finally
            {
                SendLogsToDashboardBtn.IsEnabled = true;
            }
        }
#endif // !MSIX_STORE

#if MSIX_STORE
        // ─── Store-build stubs: XAML still references these Click handlers ───
        private void RefreshLogs_Click(object? sender, RoutedEventArgs? e) { }
        private void SendAllLogs_Click(object sender, RoutedEventArgs e) { }
        private void CopyNetworkLogs_Click(object sender, RoutedEventArgs e) { }
        private async void SendLogsToDashboard_Click(object sender, RoutedEventArgs e) { await System.Threading.Tasks.Task.CompletedTask; }
#endif

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            try
            {
                _viewModel.HandleDrop(e.Data, true);
                e.Handled = true;
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            // [FIX DD-1]: Accept all drag formats unconditionally — COM queries can stall 50-135ms.
            // The drop handler validates format presence anyway.
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
        }

    }
}
