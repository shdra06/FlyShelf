// ---------------------------------------------------------------
// HubWindow � Advanced Features
// SnifferPaths, Device Management, Device Groups, Updates,
// Kinetic Scroll, Theming, Wallpaper, QR Pairing, Color Tools
// Split from HubWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using FlyShelf.Classes;
using System;
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
        private void AddSnifferPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Folder to Auto-Sniff for PDF / Word Documents",
                Multiselect = false
            };
            
            if (dialog.ShowDialog() == true)
            {
                string path = dialog.FolderName;
                if (!SettingsManager.Current.CustomSnifferPaths.Contains(path))
                {
                    SettingsManager.Current.CustomSnifferPaths.Add(path);
                    SettingsManager.Save();
                    
                    if (_viewModel.Sniffer != null)
                    {
                        _viewModel.Sniffer.StopSniffing();
                        _viewModel.Sniffer.StartSniffing();
                    }
                }
            }
        }

        private void ClearSnifferPaths_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.CustomSnifferPaths.Clear();
            SettingsManager.Save();
            
            if (_viewModel.Sniffer != null)
            {
                _viewModel.Sniffer.StopSniffing();
                _viewModel.Sniffer.StartSniffing();
            }
        }

        private async void RefreshDevices_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                // Run the device list fetching and network classification on a background ThreadPool thread
                var result = await System.Threading.Tasks.Task.Run(async () =>
                {
                    var devices = await CloudDiscoveryManager.GetActiveDevices();
                    string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

                    var lanItems = new System.Collections.Generic.List<DeviceDisplayItem>();
                    var cloudItems = new System.Collections.Generic.List<DeviceDisplayItem>();

                    // Get this PC's own URLs for the self entry
                    string myLocalUrl = _viewModel.LocalServer?.ServerUrl ?? "";
                    string myGlobalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";

                    // Always add self to LAN — this device IS a LAN device
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

                    // ═══ Use PeerManager's CONFIRMED connection data ═══
                    // PeerManager has already handshaked with each peer and knows the exact transport.
                    // This is the ground truth — no guessing needed.
                    var peerStatuses = PeerManager.Instance?.GetPeerStatuses() 
                        ?? new System.Collections.Generic.List<PeerStatus>();
                    var confirmedPeerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var peer in peerStatuses)
                    {
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
                                LastSeen = $"LAN active — {peer.ActiveUrl}",
                                LocalIp = peer.LanUrl,
                                GlobalUrl = peer.CloudflareUrl
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
                                LocalIp = peer.LanUrl,
                                GlobalUrl = peer.CloudflareUrl
                            });
                        }
                        else
                        {
                            // Peer known to PeerManager but currently dead
                            cloudItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = peer.DeviceName,
                                DeviceType = "PC",
                                IsOnline = false,
                                ConnectionType = "Cloud",
                                LastSeen = $"Last seen: {peer.LastSeen:HH:mm:ss}",
                                LocalIp = peer.LanUrl,
                                GlobalUrl = peer.CloudflareUrl
                            });
                        }
                    }

                    // ═══ Non-PeerManager devices (phones, other platforms from Firebase) ═══
                    // These are devices we don't have a direct P2P handshake with.
                    // Classify by checking if they share our LAN subnet AND respond to a health check.
                    var pingTasks = devices
                        .Where(d => !confirmedPeerIds.Contains(d.Id))
                        .Select(async d =>
                        {
                            bool isLan = false;
                            if (!string.IsNullOrEmpty(d.LocalIp))
                            {
                                // Build a proper URL for the health check
                                string checkUrl = d.LocalIp;
                                if (!checkUrl.StartsWith("http")) checkUrl = "http://" + checkUrl;
                                if (!checkUrl.Contains(":")) checkUrl += ":8999";
                                
                                try
                                {
                                    using var pingClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(1000) };
                                    var resp = await pingClient.GetAsync(checkUrl.TrimEnd('/') + "/api/health");
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
                        if (c.IsLan)
                        {
                            // Confirmed reachable on LAN — place in LAN column only
                            lanItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = d.Name,
                                DeviceType = d.Type,
                                IsOnline = d.IsOnline,
                                ConnectionType = "Local",
                                LocalIp = d.LocalIp,
                                GlobalUrl = d.GlobalUrl
                            });
                        }
                        else if (d.IsOnline)
                        {
                            // Online but NOT on LAN — Cloud only
                            cloudItems.Add(new DeviceDisplayItem
                            {
                                DeviceName = d.Name,
                                DeviceType = d.Type,
                                IsOnline = d.IsOnline,
                                ConnectionType = "Cloud",
                                LocalIp = d.LocalIp,
                                GlobalUrl = d.GlobalUrl
                            });
                        }
                    }

                    return (LanItems: lanItems, CloudItems: cloudItems);
                });

                // Update UI on dispatcher
                LanDevicesPanel.ItemsSource = result.LanItems;
                CloudDevicesPanel.ItemsSource = result.CloudItems;

                // Show/hide empty text — lanItems always has self, so "No LAN devices" means no OTHER LAN peers
                LanEmptyText.Visibility = result.LanItems.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
                CloudEmptyText.Visibility = result.CloudItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                NoDevicesPanel.Visibility = (result.LanItems.Count <= 1 && result.CloudItems.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

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
                string info = $"Device: {device.DeviceName}\n" +
                              $"Type: {device.DeviceType}\n" +
                              $"Status: {(device.IsOnline ? "Online" : "Offline")}\n";

                if (!string.IsNullOrEmpty(device.LocalIp))
                    info += $"\nLocal URL: {device.LocalIp}";
                if (!string.IsNullOrEmpty(device.GlobalUrl))
                    info += $"\nCloudflare URL: {device.GlobalUrl}";

                if (string.IsNullOrEmpty(device.LocalIp) && string.IsNullOrEmpty(device.GlobalUrl))
                    info += "\nNo connection URLs available.";

                // Copy to clipboard on right-click for convenience
                string copyUrl = !string.IsNullOrEmpty(device.GlobalUrl) ? device.GlobalUrl : device.LocalIp;
                if (!string.IsNullOrEmpty(copyUrl))
                {
                    try { Clipboard.SetText(copyUrl); info += "\n\n✅ URL copied to clipboard!"; } catch { }
                }

                MessageBox.Show(info, $"Device Info — {device.DeviceName}", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Extracts the subnet prefix from an IP address (first 3 octets): "192.168.1.106" → "192.168.1"
        /// </summary>


        // ═══ Device Groups (Firebase-synced) ═══

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
                
                GroupsPanel.ItemsSource = displayItems;
                NoGroupsText.Visibility = displayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                var name = ShowInputDialog("Enter group name:", "Create Device Group", "");
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
                if (selected.Count == 0) { MessageBox.Show("No devices selected."); return; }

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

                    var name = ShowInputDialog("Edit group name:", "Edit Group", group.Name);
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
                        prompt += $"  {i + 1}. {deviceNames[i]}{(inGroup ? " ★" : "")}\n";
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
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string groupId)
            {
                var result = MessageBox.Show("Delete this group?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;

                await CloudDiscoveryManager.DeleteDeviceGroup(groupId);
                RefreshGroups();
            }
        }

        /// <summary>
        /// Pure WPF input dialog — no System.Windows.Forms dependency.
        /// </summary>
        private static string ShowInputDialog(string message, string title, string defaultValue)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 420, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(26, 31, 46))
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var input = new System.Windows.Controls.TextBox
            {
                Text = defaultValue,
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(15, 17, 24)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 47, 58)),
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
            dlg.ShowDialog();
            return result;
        }

        private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            string btnContent = UpdateBtn.Content?.ToString() ?? "";

            if (btnContent.Contains("Restart"))
            {
                _updateManager.ApplyUpdateAndRestart();
                return;
            }

            if (btnContent.Contains("Retry"))
            {
                // Retry: re-download + auto-apply
                UpdateBtn.IsEnabled = false;
                UpdateBtn.Content = "Downloading...";
                UpdateProgressPanel.Visibility = Visibility.Visible;

                bool success = await _updateManager.DownloadAndApplyUpdateAsync();
                if (success)
                {
                    UpdateBtn.Content = "Restarting...";
                    UpdateStatusText.Text = "✅ Update downloaded! Restarting now...";
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
        }

        private async void RedownloadBtn_Click(object sender, RoutedEventArgs e)
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
                UpdateStatusText.Text = $"✅ v{UpdateManager.CurrentVersion} re-downloaded! Restarting now...";
                UpdatePctText.Text = "100%";

                await Task.Delay(1500);
                _updateManager.ApplyUpdateAndRestart();
            }
            else
            {
                RedownloadBtn.IsEnabled = true;
                UpdateBtn.IsEnabled = true;
                UpdateStatusText.Text = "❌ Redownload failed — check your internet connection.";
            }
        }

        // ═══ Kinetic Smooth Scroll Engine ═══
        private ScrollViewer _activeScrollViewer;
        private double _scrollVelocity;
        private bool _isKineticScrolling;

        private void HubWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Walk up visual tree to find nearest active ScrollViewer
            var element = e.OriginalSource as DependencyObject;
            ScrollViewer sv = null;
            while (element != null)
            {
                if (element is ScrollViewer found && found.ScrollableHeight > 0)
                {
                    sv = found;
                    break;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            if (sv == null) return;

            e.Handled = true;
            _activeScrollViewer = sv;

            // Add velocity: ~4px per delta unit, accumulates for fast flicks
            _scrollVelocity += -e.Delta / 120.0 * 4.0;

            // Start the rendering loop if not already running
            if (!_isKineticScrolling)
            {
                _isKineticScrolling = true;
                CompositionTarget.Rendering += KineticScroll_Rendering;
            }
        }

        private void KineticScroll_Rendering(object sender, EventArgs e)
        {
            if (_activeScrollViewer == null || Math.Abs(_scrollVelocity) < 0.3)
            {
                // Stop when velocity is negligible
                _scrollVelocity = 0;
                _isKineticScrolling = false;
                CompositionTarget.Rendering -= KineticScroll_Rendering;
                return;
            }

            double newOffset = _activeScrollViewer.VerticalOffset + _scrollVelocity;
            newOffset = Math.Max(0, Math.Min(newOffset, _activeScrollViewer.ScrollableHeight));
            _activeScrollViewer.ScrollToVerticalOffset(newOffset);

            // Apply friction — 0.92 gives natural deceleration (like Chrome)
            _scrollVelocity *= 0.92;
        }

    } // end HubWindow class

    public class DeviceDisplayItem
    {
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public bool IsOnline { get; set; }
        public string ConnectionType { get; set; } = "Local";
        public string LastSeen { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public string GlobalUrl { get; set; } = "";
        public string ConnectionInfo => !string.IsNullOrEmpty(GlobalUrl) ? "🌐 Cloudflare Active" : !string.IsNullOrEmpty(LocalIp) ? "📡 LAN" : "";
    }

    public partial class HubWindow
    {
        // ═══ Theme & Appearance Handlers ═══

        private void ApplyTheme()
        {
            try
            {
                // Apply DWM Immersive Dark Mode attribute so the title bar and Mica backdrop respect our theme choice
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        bool isLight = SettingsManager.Current.ColorScheme == 1;
                        int darkValue = isLight ? 0 : 1;
                        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));
                    }
                }
                catch { }

                // Wallpaper preview (asynchronously decoded to prevent UI thread blocking)
                string wallpaperPath = SettingsManager.Current.ClipboardWallpaperPath;
                if (!string.IsNullOrEmpty(wallpaperPath))
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            if (!System.IO.File.Exists(wallpaperPath))
                            {
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (SettingsManager.Current.ClipboardWallpaperPath == wallpaperPath)
                                    {
                                        WallpaperPreviewImg.Source = null;
                                        NoWallpaperText.Visibility = Visibility.Visible;
                                    }
                                });
                                return;
                            }

                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(wallpaperPath, UriKind.Absolute);
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 400;
                            bmp.EndInit();
                            bmp.Freeze();

                            Dispatcher.InvokeAsync(() =>
                            {
                                if (SettingsManager.Current.ClipboardWallpaperPath == wallpaperPath)
                                {
                                    WallpaperPreviewImg.Source = bmp;
                                    NoWallpaperText.Visibility = Visibility.Collapsed;
                                }
                            });
                        }
                        catch
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                WallpaperPreviewImg.Source = null;
                                NoWallpaperText.Visibility = Visibility.Visible;
                            });
                        }
                    });
                }
                else
                {
                    WallpaperPreviewImg.Source = null;
                    NoWallpaperText.Visibility = Visibility.Visible;
                }

                // Blur + dark fallback when Mica is off
                if (SettingsManager.Current.EnableBlurBehind && NativeMethods.ShouldUseBlur())
                {
                    this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.Mica;
                    this.Background = System.Windows.Media.Brushes.Transparent;
                    if (RootGrid != null) RootGrid.Background = null;
                    // Reset caption to default (transparent for Mica)
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            int colorDefault = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE = transparent for Mica
                            NativeMethods.DwmSetWindowAttribute(hwnd, 35, ref colorDefault, sizeof(int));
                        }
                    } catch { }
                }
                else
                {
                    this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                    bool isLight = SettingsManager.Current.ColorScheme == 1;
                    var bgColor = isLight ? System.Windows.Media.Color.FromRgb(245, 246, 248) : System.Windows.Media.Color.FromRgb(18, 18, 26);
                    var bgBrush = new System.Windows.Media.SolidColorBrush(bgColor);
                    this.Background = bgBrush;
                    if (RootGrid != null) RootGrid.Background = bgBrush;
                    // Force title bar to match the fallback color via DWM (DWMWA_CAPTION_COLOR = 35)
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            int dwmColor = isLight ? ((248 << 16) | (246 << 8) | 245) : ((26 << 16) | (18 << 8) | 18);
                            NativeMethods.DwmSetWindowAttribute(hwnd, 35, ref dwmColor, sizeof(int));
                        }
                    } catch { }
                }

                // Color scheme — always dark mode (Light mode removed)
                // Force ColorScheme to 0 (dark) in case old settings had 1 (light)
                if (SettingsManager.Current.ColorScheme != 0)
                    SettingsManager.Current.ColorScheme = 0;

                try
                {
                    var mergedDicts = Application.Current.Resources.MergedDictionaries;

                    // Remove any previous theme override dictionaries
                    for (int i = mergedDicts.Count - 1; i >= 0; i--)
                    {
                        var d = mergedDicts[i];
                        if (d.Source == null && d.Contains("FlyShelf.ThemeOverride"))
                            mergedDicts.RemoveAt(i);
                    }

                    // Ensure MicaWPF is set to Dark
                    foreach (var dict in mergedDicts)
                    {
                        if (dict is MicaWPF.Styles.ThemeDictionary md)
                            md.Theme = MicaWPF.Core.Enums.WindowsTheme.Dark;
                    }

                    // Dark mode accent override — prevent system accent color bleeding
                    var overrides = new ResourceDictionary();
                    overrides["FlyShelf.ThemeOverride"] = true;
                    overrides["MicaWPF.Brushes.SystemAccentColor"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 102, 241));
                    overrides["MicaWPF.Brushes.SystemAccentColorLight1"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(129, 132, 255));
                    overrides["MicaWPF.Brushes.SystemAccentColorLight2"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(159, 162, 255));
                    overrides["MicaWPF.Brushes.SystemAccentColorDark1"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(79, 82, 221));
                    mergedDicts.Add(overrides);
                }
                catch { /* Theme switching may not be supported on all versions */ }

                // Re-apply window backdrop and background (Mica dark or solid dark fallback)
                NativeMethods.ApplyWindowBackdropAndBackground(this, RootGrid);
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Apply failed: {ex.Message}");
            }
        }

        private void ChooseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose Clipboard Wallpaper",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                SettingsManager.Current.ClipboardWallpaperPath = dialog.FileName;
                SettingsManager.Save();
                ApplyTheme();
            }
        }

        private void RemoveWallpaper_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.ClipboardWallpaperPath = "";
            SettingsManager.Save();
            ApplyTheme();
        }

        private void BlurToggle_Changed(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            ApplyTheme();
        }

        private void ColorScheme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SettingsManager.Save();
            ApplyTheme();
        }

        // ═══ QR Code Pairing Handlers ═══

        private void RefreshQRCode()
        {
            try
            {
                if (PairingQRImage == null) return;
                string localUrl = _viewModel.LocalServer?.DisplayUrl ?? "";
                string globalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";
                string pin = SettingsManager.Current.WebClientPinToken;

                var qr = DevicePairingManager.GenerateQRCode(localUrl, globalUrl, pin, 250);
                if (qr != null)
                {
                    PairingQRImage.Source = qr;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("QR", $"Refresh failed: {ex.Message}");
            }
        }

        private void RefreshPairedDevicesList()
        {
            try
            {
                var devices = DevicePairingManager.GetPairedDevices();
                var peerStatuses = PeerManager.Instance?.GetPeerStatuses();

                // Build merged list with live P2P status
                var mergedList = devices.Select(d =>
                {
                    var peer = peerStatuses?.FirstOrDefault(p => p.DeviceId == d.DeviceId);
                    return new PeerStatusItem
                    {
                        DeviceId = d.DeviceId,
                        DeviceName = d.DeviceName,
                        IsAlive = peer?.IsAlive ?? false,
                        Transport = peer?.Transport ?? "offline",
                        IsLanActive = !string.IsNullOrEmpty(peer?.LanUrl) && (peer?.IsAlive ?? false),
                        IsCloudActive = !string.IsNullOrEmpty(peer?.CloudflareUrl) && (peer?.IsAlive ?? false),
                        StatusText = peer?.IsAlive == true
                            ? $"Connected via {peer.Transport} • Last seen {peer.LastSeen:HH:mm:ss}"
                            : "Offline"
                    };
                }).ToList();

                PeerStatusPanel.ItemsSource = mergedList;
                NoPairedDevicesText.Visibility = mergedList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                int onlineCount = mergedList.Count(p => p.IsAlive);
                PeerCountBadge.Text = $"{onlineCount} online";
            }
            catch (Exception ex)
            {
                Logger.LogAction("QR", $"Refresh paired list failed: {ex.Message}");
            }
        }

        private void RegenerateQR_Click(object sender, RoutedEventArgs e)
        {
            DevicePairingManager.RegeneratePairingKey();
            RefreshQRCode();
            Windows.ToastWindow.ShowToast("New QR code generated! ✅");
        }

        private void CopyPairingInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localUrl = _viewModel.LocalServer?.DisplayUrl ?? "";
                string globalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";
                string pin = SettingsManager.Current.WebClientPinToken;
                string payload = DevicePairingManager.BuildQRPayload(localUrl, globalUrl, pin);
                System.Windows.Clipboard.SetText(payload);
                Windows.ToastWindow.ShowToast("Pairing info copied! 📋");
            }
            catch { }
        }

        private async void ForcePeerSync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Windows.ToastWindow.ShowToast("🔄 Force syncing peers...");
                if (PeerManager.Instance != null)
                {
                    await PeerManager.Instance.ForceResync();
                }
                RefreshPairedDevicesList();
                Windows.ToastWindow.ShowToast("✅ Peer sync complete!");
            }
            catch (Exception ex)
            {
                Logger.LogAction("HUB", $"Force sync failed: {ex.Message}");
                Windows.ToastWindow.ShowToast("⚠️ Sync failed — check logs");
            }
        }

        private void RemovePairedDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string deviceId)
            {
                DevicePairingManager.RemoveDevice(deviceId);
                RefreshPairedDevicesList();
                Windows.ToastWindow.ShowToast("Device removed ✕");
            }
        }

        private async void GeneratePairingCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PairingCodeDisplay.Text = "...";
                string code = await DevicePairingManager.PublishPairingCode();
                PairingCodeDisplay.Text = code;
                Windows.ToastWindow.ShowToast($"Code generated: {code} (expires in 5 min) 🔑");
            }
            catch (Exception ex)
            {
                PairingCodeDisplay.Text = "ERROR";
                Logger.LogAction("PAIR CODE", $"Generate failed: {ex.Message}");
            }
        }

        private async void ConnectByCode_Click(object sender, RoutedEventArgs e)
        {
            string code = RemoteCodeInput?.Text?.Trim().ToUpper() ?? "";
            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                Windows.ToastWindow.ShowToast("⚠️ Enter a 6-character code");
                return;
            }

            Windows.ToastWindow.ShowToast($"Looking up {code}...");

            try
            {
                var (success, deviceName) = await DevicePairingManager.ConnectByCode(code);
                if (success)
                {
                    Windows.ToastWindow.ShowToast($"✅ Paired with {deviceName}!");
                    RefreshPairedDevicesList();
                    RemoteCodeInput.Text = "";

                    // Restart Firebase listener so it reads from the newly adopted pairing key scope
                    _viewModel.CloudListener?.StopPolling();
                    _viewModel.CloudListener?.StartPolling();
                    Logger.LogAction("PAIR CODE", "Firebase listener restarted for new pairing key scope");
                }
                else if (!string.IsNullOrEmpty(deviceName))
                {
                    Windows.ToastWindow.ShowToast($"⚠️ Found {deviceName} but couldn't connect — make sure it's online");
                }
                else
                {
                    Windows.ToastWindow.ShowToast("❌ Code not found or expired");
                }
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"❌ Connection failed: {ex.Message}");
                Logger.LogAction("PAIR CODE", $"ConnectByCode UI error: {ex.Message}");
            }
        }

        // ═══ Color Copy Handlers ═══

        private void CopyColorHex_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                try { System.Windows.Clipboard.SetText(Classes.ColorHelper.ToHex(item.ColorR, item.ColorG, item.ColorB)); Windows.ToastWindow.ShowToast($"Hex copied: {item.DetectedColor} 🎨"); }
                catch { Windows.ToastWindow.ShowToast("Clipboard busy — try again"); }
            }
        }

        private void CopyColorRgb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                string rgb = Classes.ColorHelper.ToRgb(item.ColorR, item.ColorG, item.ColorB);
                try { System.Windows.Clipboard.SetText(rgb); Windows.ToastWindow.ShowToast($"RGB copied: {rgb} 🎨"); }
                catch { Windows.ToastWindow.ShowToast("Clipboard busy — try again"); }
            }
        }

        private void CopyColorHsl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                string hsl = Classes.ColorHelper.ToHsl(item.ColorR, item.ColorG, item.ColorB);
                try { System.Windows.Clipboard.SetText(hsl); Windows.ToastWindow.ShowToast($"HSL copied: {hsl} 🎨"); }
                catch { Windows.ToastWindow.ShowToast("Clipboard busy — try again"); }
            }
        }
    }

    public class GroupDisplayItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DeviceList { get; set; } = "";
    }
}

