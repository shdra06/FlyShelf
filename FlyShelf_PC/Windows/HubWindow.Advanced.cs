// ---------------------------------------------------------------
// HubWindow — Advanced Features
// SnifferPaths, Device Management, Device Groups, Updates,
// Kinetic Scroll, Theming, Wallpaper, QR Pairing, Color Tools
// Split from HubWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using FlyShelf.Classes;
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private async void RefreshDevices_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                // Run the device list fetching and network classification on a background ThreadPool thread
                var result = await System.Threading.Tasks.Task.Run(async () =>
                {
                    // Clean up stale/unpaired ghosts in background
                    _ = CloudDiscoveryManager.CleanupStaleDevices();

                    var devices = await CloudDiscoveryManager.GetActiveDevices();
                    string myId = SettingsManager.Current.DeviceId ?? "";
                    string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

                    var lanItems = new System.Collections.Generic.List<DeviceDisplayItem>();
                    var cloudItems = new System.Collections.Generic.List<DeviceDisplayItem>();

                    // Get this PC's own URLs for the self entry
                    string myLocalUrl = _viewModel.LocalServer?.ServerUrl ?? "";
                    string myGlobalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";

                    // Always add self to LAN — this device IS the local host
                    lanItems.Add(new DeviceDisplayItem
                    {
                        DeviceName = myName + " (You)",
                        DeviceType = "PC",
                        IsOnline = true,
                        ConnectionType = "Local",
                        LastSeen = "Online now",
                        LocalIp = myLocalUrl,
                        GlobalUrl = myGlobalUrl
                    });

                    // ═ ═ ═ Use PeerManager's CONFIRMED connection data ═ ═ ═
                    // PeerManager has already handshaked with each peer and knows the exact transport.
                    var peerStatuses = PeerManager.Instance?.GetPeerStatuses() 
                        ?? new System.Collections.Generic.List<PeerStatusItem>();
                    var confirmedPeerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var peer in peerStatuses)
                    {
                        // Skip self
                        if (string.Equals(peer.DeviceId, myId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(peer.DeviceName, myName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        confirmedPeerIds.Add(peer.DeviceId);

                        if (peer.IsAlive && peer.Transport == "LAN")
                        {
                            // Confirmed LAN connection via PeerManager handshake
                            lanItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = peer.DeviceName,
                                DeviceType = "PC",
                                IsOnline = true,
                                ConnectionType = "Local",
                                LastSeen = "LAN active",
                            });
                        }
                        else if (peer.IsAlive && peer.Transport == "Cloudflare")
                        {
                            // Confirmed Cloudflare connection via PeerManager handshake
                            cloudItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = peer.DeviceName,
                                DeviceType = "PC",
                                IsOnline = true,
                                ConnectionType = "Cloud",
                                LastSeen = "Cloudflare tunnel active",
                            });
                        }
                    }

                    // ═ ═ ═ Non-PeerManager devices (phones, other platforms from Firebase) ═ ═ ═
                    var pingTasks = devices
                        .Where(d => !confirmedPeerIds.Contains(d.Id) &&
                                    !string.Equals(d.Id, myId, StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(d.Name, myName, StringComparison.OrdinalIgnoreCase))
                        .Select(async d =>
                        {
                            bool isLan = false;
                            if (!string.IsNullOrEmpty(d.LocalIp))
                            {
                                // Build a proper URL for the health check
                                string checkUrl = d.LocalIp;
                                if (!checkUrl.StartsWith("http", StringComparison.Ordinal)) checkUrl ="http://" + checkUrl;
                                if (!checkUrl.Contains(':')) checkUrl += ":8999";
                                
                                try
                                {
                                    var pingClient = HttpClientPool.Quick;
                                    using var pingCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
                                    var resp = await pingClient.GetAsync(checkUrl.TrimEnd('/') + "/api/health", pingCts.Token).ConfigureAwait(false);
                                    isLan = resp.IsSuccessStatusCode;
                                }
                                catch { isLan = false; }
                            }
                            return (Device: d, IsLan: isLan);
                        });

                    var classified = await System.Threading.Tasks.Task.WhenAll(pingTasks);

                    foreach (var c in classified)
                    {
                        var d = c.Device;
                        if (c.IsLan && d.IsOnline)
                        {
                            // Confirmed reachable on LAN — place in LAN column only
                            lanItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = d.Name,
                                DeviceType = d.Type,
                                IsOnline = true,
                                ConnectionType = "Local",
                            });
                        }
                        else if (d.IsOnline)
                        {
                            // Online but NOT on LAN — Cloud only
                            cloudItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = d.Name,
                                DeviceType = d.Type,
                                IsOnline = true,
                                ConnectionType = "Cloud",
                            });
                        }
                    }

                    return (LanItems: lanItems, CloudItems: cloudItems);
                });

                // Update UI on dispatcher
                if (LanDevicesPanel != null) LanDevicesPanel.ItemsSource = result.LanItems;
                if (CloudDevicesPanel != null) CloudDevicesPanel.ItemsSource = result.CloudItems;

                // Show/hide empty text — lanItems always has self, so "No LAN devices" means no OTHER LAN peers
                if (LanEmptyText != null) LanEmptyText.Visibility = result.LanItems.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
                if (CloudEmptyText != null) CloudEmptyText.Visibility = result.CloudItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (NoDevicesPanel != null) NoDevicesPanel.Visibility = (result.LanItems.Count <= 1 && result.CloudItems.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

                // Also refresh groups
                RefreshGroups();
            }
            catch (Exception ex)
            {
                Logger.LogAction("DEVICES UI", $"Failed to refresh: {ex.Message}");
            }
        }

        private void DeviceInfo_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DeviceDisplayItem device)
            {
                var cm = new System.Windows.Controls.ContextMenu();

                // Device Info — shows connection type and status only (no URLs)
                var infoItem = new System.Windows.Controls.MenuItem();
                var infoHeader = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                infoHeader.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Info24, FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
                infoHeader.Children.Add(new TextBlock { Text = "Device Info" });
                infoItem.Header = infoHeader;
                infoItem.Click += (_, _) =>
                {
                    string transport = device.ConnectionType == "Cloud" ? "Secure Cloud Tunnel" : "Local Network";
                    string info = $"Device: {device.DeviceName}\n" +
                                  $"Type: {device.DeviceType}\n" +
                                  $"Status: {(device.IsOnline ? "Online" : "Offline")}\n" +
                                  $"Transport: {transport}";
                    Windows.ToastWindow.ShowToast(info, 4000);
                };
                cm.Items.Add(infoItem);

                cm.PlacementTarget = fe;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Extracts the subnet prefix from an IP address (first 3 octets): "192.168.1.106" → "192.168.1"
        /// </summary>


        // ═ ═ ═ Device Groups (Firebase-synced) ═ ═ ═

        private async void RefreshGroups()
        {
            try
            {
                var groups = await CloudDiscoveryManager.GetDeviceGroups();
                var displayItems = groups.Select(g => new GroupDisplayItem
                {
                    Id = g.Id,
                    Name = g.Name,
                    DeviceList = string.Join(", ", g.DeviceNames ?? new List<string>())
                }).ToList();
                
                if (GroupsPanel != null) GroupsPanel.ItemsSource = displayItems;
                if (NoGroupsText != null) NoGroupsText.Visibility = displayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.LogAction("GROUPS UI", $"Failed to refresh groups: {ex.Message}");
            }
        }

        private async void CreateGroupBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = ShowInputDialog("Enter group name:","Create Device Group","");
                if (string.IsNullOrWhiteSpace(name)) return;

                var devices = await CloudDiscoveryManager.GetActiveDevices();
                var deviceNames = devices.Where(d => d.IsOnline).Select(d => d.Name).ToList();
                string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                if (!deviceNames.Contains(myName)) deviceNames.Insert(0, myName);

                var prompt = "Available devices (enter numbers separated by commas):\n";
                for (int i = 0; i < deviceNames.Count; i++)
                    prompt += $"  {i + 1}. {deviceNames[i]}\n";

                var input = ShowInputDialog(prompt, "Select Devices for Group", string.Join(",", Enumerable.Range(1, deviceNames.Count)));
                if (string.IsNullOrWhiteSpace(input)) return;

                var selected = new List<string>();
                foreach (var numStr in input.Split(','))
                {
                    if (int.TryParse(numStr.Trim(), out int idx) && idx >= 1 && idx <= deviceNames.Count)
                        selected.Add(deviceNames[idx - 1]);
                }
                if (selected.Count == 0) { Windows.ToastWindow.ShowToast("No devices selected.", 2000); return; }

                var groupId = $"grp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                await CloudDiscoveryManager.SaveDeviceGroup(groupId, name.Trim(), selected);
                RefreshGroups();
            }
            catch (Exception ex) { Logger.LogAction("GROUPS UI", $"Create error: {ex.Message}"); }
        }


        private async void EditGroupBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.Button btn && btn.Tag is string groupId)
                {
                    var groups = await CloudDiscoveryManager.GetDeviceGroups();
                    var group = groups.FirstOrDefault(g => g.Id == groupId);
                    if (group == null) return;

                    var name = ShowInputDialog("Edit group name:","Edit Group", group.Name);
                    if (string.IsNullOrWhiteSpace(name)) return;

                    var devices = await CloudDiscoveryManager.GetActiveDevices();
                    var deviceNames = devices.Where(d => d.IsOnline).Select(d => d.Name).ToList();
                    string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                    if (!deviceNames.Contains(myName)) deviceNames.Insert(0, myName);
                    foreach (var dn in group.DeviceNames ?? new List<string>())
                        if (!deviceNames.Contains(dn)) deviceNames.Add(dn);

                    var prompt = "Select devices (enter numbers, comma-separated):\n";
                    var preSelected = new List<int>();
                    for (int i = 0; i < deviceNames.Count; i++)
                    {
                        bool inGroup = (group.DeviceNames ?? new List<string>()).Contains(deviceNames[i]);
                        prompt += $"{i + 1}. {deviceNames[i]}{(inGroup ? "" : "")}\n";
                        if (inGroup) preSelected.Add(i + 1);
                    }

                    var input = ShowInputDialog(prompt, "Edit Devices", string.Join(",", preSelected));
                    if (string.IsNullOrWhiteSpace(input)) return;

                    var selected = new List<string>();
                    foreach (var numStr in input.Split(','))
                    {
                        if (int.TryParse(numStr.Trim(), out int idx) && idx >= 1 && idx <= deviceNames.Count)
                            selected.Add(deviceNames[idx - 1]);
                    }

                    await CloudDiscoveryManager.SaveDeviceGroup(groupId, name.Trim(), selected);
                    RefreshGroups();
                }
            }
            catch (Exception ex) { Logger.LogAction("GROUPS UI", $"Edit error: {ex.Message}"); }
        }

        private async void DeleteGroupBtn_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
                if (sender is System.Windows.Controls.Button btn && btn.Tag is string groupId)
                {
                    var result = MessageBox.Show("Delete this group?","Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes) return;

                    await CloudDiscoveryManager.DeleteDeviceGroup(groupId);
                    RefreshGroups();
                }
            });
        }

        /// <summary>
        /// Pure WPF input dialog — no System.Windows.Forms dependency.
        /// Uses theme-aware colors from Application resources.
        /// </summary>
        private string ShowInputDialog(string message, string title, string defaultValue)
        {
            // Resolve theme brushes with fallbacks
            var app = Application.Current;
            var bgBrush = app?.TryFindResource("ThemeWindowFallback") as Brush ?? new SolidColorBrush(Color.FromRgb(26, 31, 46));
            var fgBrush = app?.TryFindResource("ThemeTextPrimary") as Brush ?? Brushes.White;
            var inputBgBrush = app?.TryFindResource("ThemeOverlayBg") as Brush ?? new SolidColorBrush(Color.FromRgb(15, 17, 24));
            var borderBrush = app?.TryFindResource("ThemeOverlayBorder") as Brush ?? new SolidColorBrush(Color.FromRgb(42, 47, 58));

            var dlg = new Window
            {
                Title = title,
                Width = 420, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = bgBrush
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = message,
                Foreground = fgBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var input = new System.Windows.Controls.TextBox
            {
                Text = defaultValue,
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Background = inputBgBrush,
                Foreground = fgBrush,
                BorderBrush = borderBrush,
                Margin = new Thickness(0, 0, 0, 12)
            };
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 80, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Padding = new Thickness(0, 6, 0, 6), IsCancel = true };

            string result = null;
            okBtn.Click += (s, ev) => { result = input.Text; dlg.Close(); };
            cancelBtn.Click += (s, ev) => { dlg.Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            sp.Children.Add(tb);
            sp.Children.Add(input);
            sp.Children.Add(btnPanel);
            dlg.Content = sp;
            dlg.Owner = this;
            dlg.ShowDialog();
            return result;
        }


        private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
                string btnContent = UpdateBtn.Content?.ToString() ?? string.Empty;

                if (btnContent.Contains("Restart", StringComparison.Ordinal))
                {
                    _updateManager.ApplyUpdateAndRestart();
                    return;
                }

                if (btnContent.Contains("Retry", StringComparison.Ordinal))
                {
                    // Retry: re-download + auto-apply
                    UpdateBtn.IsEnabled = false;
                    UpdateBtn.Content = "Downloading...";
                    UpdateProgressPanel.Visibility = Visibility.Visible;

                    bool success = await _updateManager.DownloadAndApplyUpdateAsync();
                    if (success)
                    {
                        UpdateBtn.Content = "Restarting...";
                        UpdateStatusText.Text ="Update downloaded! Restarting now...";
                        await Task.Delay(1500);
                        _updateManager.ApplyUpdateAndRestart();
                    }
                    else
                    {
                        UpdateBtn.Content = "Retry Download";
                        UpdateBtn.IsEnabled = true;
                    }
                    return;
                }

                // Default: Check for updates (UpdateCheckCompleted event handles the UI)
                UpdateBtn.Content = "Checking...";
                UpdateBtn.IsEnabled = false;
                UpdateProgressPanel.Visibility = Visibility.Visible;
                ChangelogPanel.Visibility = Visibility.Collapsed;
                LatestVersionText.Text = "";

                await _updateManager.CheckForUpdateAsync();
            });
        }

        private async void RedownloadBtn_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
                RedownloadBtn.IsEnabled = false;
                UpdateBtn.IsEnabled = false;
                UpdateProgressPanel.Visibility = Visibility.Visible;
                UpdateStatusText.Text = $"Finding v{UpdateManager.CurrentVersion} on GitHub...";
                UpdatePctText.Text = "";

                bool success = await _updateManager.RedownloadCurrentVersionAsync();
                if (success)
                {
                    RedownloadBtn.IsEnabled = false;
                    UpdateBtn.Content = "Restarting...";
                    UpdateStatusText.Text = $"v{UpdateManager.CurrentVersion} re-downloaded! Restarting now...";
                    UpdatePctText.Text = "100%";

                    await Task.Delay(1500);
                    _updateManager.ApplyUpdateAndRestart();
                }
                else
                {
                    RedownloadBtn.IsEnabled = true;
                    UpdateBtn.IsEnabled = true;
                    UpdateStatusText.Text ="Redownload failed  check your internet connection.";
                }
            });
        }

    } // end HubWindow class

    public sealed class DeviceDisplayItem
    {
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public bool IsOnline { get; set; }
        public string ConnectionType { get; set; } = "Local";
        public string LastSeen { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public string GlobalUrl { get; set; } = "";
        public string ConnectionInfo
        {
            get
            {
                if (DeviceName.Contains("(You)", StringComparison.Ordinal))
                {
                    return"Local Host";
                }
                if (!IsOnline)
                {
                    return"Offline";
                }
                return ConnectionType == "Cloud" ? "Cloudflare Active" : "LAN Active";
            }
        }
    }
}
