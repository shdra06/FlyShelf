// ---------------------------------------------------------------
// HubWindow — Tabs & Appearance Handlers
// Theme, Wallpaper, QR Pairing, Color Tools, Mascot Themes
// Split from HubWindow.Advanced.cs for modularity
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
using FlyShelf.Helpers;

namespace FlyShelf.Windows
{
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
                                        if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = null;
                                        if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Visible;
                                    }
                                });
                                return;
                            }

                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(wallpaperPath, UriKind.Absolute);
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 300;
                            bmp.EndInit();
                            bmp.Freeze();

                            Dispatcher.InvokeAsync(() =>
                            {
                                if (SettingsManager.Current.ClipboardWallpaperPath == wallpaperPath)
                                {
                                    if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = bmp;
                                    if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Collapsed;
                                }
                            });
                        }
                        catch
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = null;
                                if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Visible;
                            });
                        }
                    });
                }
                else
                {
                    if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = null;
                    if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Visible;
                }

                // Blur + dark fallback based on ThemeDisplayMode and EnableBlurBehind
                string mode = SettingsManager.Current.ThemeDisplayMode ?? "desktop";
                bool blurEnabled = SettingsManager.Current.EnableBlurBehind && NativeMethods.ShouldUseBlur();

                if (blurEnabled)
                {
                    // HubWindow ALWAYS gets Mica blur — the glass/acrylic display mode only affects the clipboard
                    this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.Mica;
                    this.Background = System.Windows.Media.Brushes.Transparent;
                    if (RootGrid != null) RootGrid.Background = null;
                    // Force dark caption color — Hub is always dark regardless of color theme
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            int colorDark = 0x00202020;
                            NativeMethods.DwmSetWindowAttribute(hwnd, 35, ref colorDark, sizeof(int));
                        }
                    } catch { } // Best-effort: failure is acceptable
                }
                else
                {
                    this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                    var bgColor = System.Windows.Media.Color.FromRgb(18, 18, 26);
                    var bgBrush = new System.Windows.Media.SolidColorBrush(bgColor);
                    this.Background = bgBrush;
                    if (RootGrid != null) RootGrid.Background = bgBrush;
                    // Force title bar to match the dark fallback
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            int dwmColor = (26 << 16) | (18 << 8) | 18;
                            NativeMethods.DwmSetWindowAttribute(hwnd, 35, ref dwmColor, sizeof(int));
                        }
                    } catch { } // Best-effort: failure is acceptable
                }

                // Color scheme — Hub is ALWAYS dark mode
                // Color themes only affect the clipboard popup via AltClipboard tokens
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

                    // Hub always uses Dark MicaWPF theme
                    foreach (var dict in mergedDicts)
                    {
                        if (dict is MicaWPF.Styles.ThemeDictionary md)
                            md.Theme = MicaWPF.Core.Enums.WindowsTheme.Dark;
                    }

                    // Dark mode accent override — prevent system accent color bleeding
                    var overrides = new ResourceDictionary();
                    overrides["FlyShelf.ThemeOverride"] = true;
                    overrides["MicaWPF.Brushes.SystemAccentColor"] = new System.Windows.Media.SolidColorBrush(FlyShelf.Helpers.ThemeColors.IndigoAccent);
                    overrides["MicaWPF.Brushes.SystemAccentColorLight1"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(129, 132, 255));
                    overrides["MicaWPF.Brushes.SystemAccentColorLight2"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(159, 162, 255));
                    overrides["MicaWPF.Brushes.SystemAccentColorDark1"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(79, 82, 221));
                    mergedDicts.Add(overrides);
                }
                catch { /* Theme switching may not be supported on all versions */ }

                // Re-apply window backdrop and background
                NativeMethods.ApplyWindowBackdropAndBackground(this, RootGrid);
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Apply failed: {ex.Message}");
            }
        }

        private void ChooseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (!LicenseManager.CanSetCustomWallpaper())
            {
                UpgradePrompt.ShowCustomWallpaperLimit(this);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose Clipboard Wallpaper",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                SettingsManager.Current.ManualWallpaperPath = dialog.FileName;
                SettingsManager.Current.ClipboardWallpaperPath = dialog.FileName;

                // FIX: Auto-switch to FlyShelf (desktop) mode when user picks a wallpaper.
                // Mica and Acrylic modes explicitly clear ClipboardWallpaperPath, so a custom
                // wallpaper would never display unless the mode is "desktop" or "theme".
                string currentMode = SettingsManager.Current.ThemeDisplayMode ?? "desktop";
                if (currentMode == "mica" || currentMode == "glass")
                {
                    // Clean up Glass theme if switching away from glass
                    if (currentMode == "glass")
                        ThemeManager.Instance.RemoveGlassTheme();

                    SettingsManager.Current.ThemeDisplayMode = "desktop";
                    HighlightActiveDisplayMode();
                }

                SettingsManager.Save();
                ApplyTheme();
                RespawnClipboardPreview();
            }
        }

        private void RemoveWallpaper_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.ManualWallpaperPath = "";
            SettingsManager.Current.ClipboardWallpaperPath = "";
            SettingsManager.Save();
            ApplyTheme();
            RespawnClipboardPreview();
        }

        private void BlurToggle_Changed(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            ApplyTheme();
            RespawnClipboardPreview();
        }

        private void ColorScheme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SettingsManager.Save();
            ApplyTheme();
        }

        // ═══ Cloud Status + QR Timer ═══
        private System.Windows.Threading.DispatcherTimer? _cloudStatusTimer;

        /// <summary>Start a 2-second timer that refreshes cloud URL status and auto-regenerates QR when Cloudflare URL becomes available.</summary>
        private void StartCloudStatusTimer()
        {
            if (_cloudStatusTimer != null) return;
            _cloudStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _cloudStatusTimer.Tick += (_, _) =>
            {
                RefreshCloudStatus();
                // Auto-refresh QR once the global URL appears
                var globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
                if (!string.IsNullOrEmpty(globalUrl))
                {
                    RefreshQRCode();
                }
            };
            _cloudStatusTimer.Start();
            Logger.LogAction("CLOUD-TIMER", "Cloud status auto-refresh started (2s interval)");
        }

        /// <summary>Update the Cloud URL text, status indicator dot, and disabled row visibility.</summary>
        private void RefreshCloudStatus()
        {
            try
            {
                if (CloudUrlText == null || CloudStatusText == null) return;
                bool cloudEnabled = SettingsManager.Current.EnableGlobalCloudflare;
                
                string globalUrl = CloudDiscoveryManager.CachedGlobalUrl ?? "";
                if (string.IsNullOrEmpty(globalUrl) || !globalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                {
                    string daemonUrl = _viewModel?.LocalServer?.GlobalUrl ?? "";
                    if (!string.IsNullOrEmpty(daemonUrl) && daemonUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                    {
                        globalUrl = daemonUrl;
                    }
                }

                // Show/hide disabled row
                if (CloudDisabledRow != null)
                    CloudDisabledRow.Visibility = cloudEnabled ? Visibility.Collapsed : Visibility.Visible;

                bool isLive = cloudEnabled && !string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase);

                if (isLive)
                {
                    // Tunnel active with URL
                    CloudUrlText.Text = globalUrl;
                    CloudUrlText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));
                    if (CopyCloudUrlBtn != null) CopyCloudUrlBtn.Visibility = Visibility.Visible;
                    if (CloudStatusDot != null) CloudStatusDot.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));
                    CloudStatusText.Text = "Tunnel Active";
                    CloudStatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981"));
                }
                else if (cloudEnabled)
                {
                    string statusMsg = _viewModel?.LocalServer?.GlobalUrl;
                    if (string.IsNullOrEmpty(statusMsg) || statusMsg == "Offline")
                    {
                        statusMsg = "Starting tunnel...";
                    }

                    // Cloudflare enabled but URL not yet ready
                    CloudUrlText.Text = statusMsg;
                    CloudUrlText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
                    if (CopyCloudUrlBtn != null) CopyCloudUrlBtn.Visibility = Visibility.Collapsed;
                    if (CloudStatusDot != null) CloudStatusDot.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
                    CloudStatusText.Text = statusMsg;
                    CloudStatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
                }
            }
            catch (Exception ex) { Logger.LogAction("CLOUD-STATUS", $"Refresh failed: {ex.Message}"); }
        }

        private void CopyCloudUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = CloudDiscoveryManager.CachedGlobalUrl ?? "";
                if (string.IsNullOrEmpty(url) || !url.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                {
                    url = _viewModel?.LocalServer?.GlobalUrl ?? "";
                }
                if (!string.IsNullOrEmpty(url) && url.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                {
                    ClipboardHelper.SafeSetText(url);
                    Logger.LogAction("COPY", $"Cloud URL copied: {url}");
                    ToastWindow.ShowToast("Cloud URL copied to clipboard!");
                }
            }
            catch (Exception ex) { Logger.LogAction("COPY", $"Cloud URL copy failed: {ex.Message}"); }
        }

        // ═══ QR Code Pairing Handlers ═══

        private void RefreshQRCode()
        {
            try
            {
                if (PairingQRImage == null) return;
                string localUrl = _viewModel.LocalServer?.DisplayUrl ?? "";
                string globalUrl = CloudDiscoveryManager.CachedGlobalUrl ?? "";
                if (string.IsNullOrEmpty(globalUrl) || !globalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                {
                    globalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";
                }
                bool hasCloud = !string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com", StringComparison.OrdinalIgnoreCase);
                if (!hasCloud) globalUrl = "";

                string pin = SettingsManager.Current.WebClientPinToken;
                bool hasLan = !string.IsNullOrEmpty(localUrl);
                bool cloudEnabled = SettingsManager.Current.EnableGlobalCloudflare;

                Logger.LogAction("QR", $"Refresh: LAN={hasLan} ({localUrl}), Cloud={hasCloud} ({globalUrl}), CloudEnabled={cloudEnabled}");

                if (!hasLan && !hasCloud)
                {
                    // No URLs at all — show status overlay
                    if (QrStatusOverlay != null)
                    {
                        QrStatusOverlay.Visibility = Visibility.Visible;
                        if (QrStatusText != null)
                        {
                            if (cloudEnabled)
                                QrStatusText.Text = "⏳ Generating public URL...\nWaiting for Cloudflare tunnel";
                            else
                                QrStatusText.Text = "⚠ No network\nConnect to Wi-Fi or enable Cloudflare";
                        }
                    }
                    return;
                }

                // We have at least one URL — generate QR
                if (QrStatusOverlay != null) QrStatusOverlay.Visibility = Visibility.Collapsed;

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

        private async void RefreshPairedDevicesList()
        {
            try
            {
                var devices = DevicePairingManager.GetPairedDevices();
                var peerStatuses = PeerManager.Instance?.GetPeerStatuses();
                var activeCloudDevices = await CloudDiscoveryManager.GetActiveDevices();

                // Build merged list with live status (checks direct P2P connection + Firebase live presence)
                var mergedList = devices.Select(d =>
                {
                    var peer = peerStatuses?.FirstOrDefault(p =>
                        string.Equals(p.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.DeviceName, d.DeviceName, StringComparison.OrdinalIgnoreCase));

                    var cloudDev = activeCloudDevices.FirstOrDefault(cd =>
                        string.Equals(cd.Id, d.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(cd.Name, d.DeviceName, StringComparison.OrdinalIgnoreCase));

                    bool peerAlive = peer?.IsAlive ?? false;
                    bool cloudAlive = cloudDev.IsOnline;
                    bool isAlive = peerAlive || cloudAlive;
                    string transport = peerAlive ? (peer?.Transport ?? "LAN") : (cloudAlive ? "Cloud" : "offline");

                    return new PeerStatusItem
                    {
                        DeviceId = d.DeviceId,
                        DeviceName = d.DeviceName,
                        IsAlive = isAlive,
                        Transport = isAlive ? transport : "offline",
                        IsLanActive = transport == "LAN" || (!string.IsNullOrEmpty(peer?.LanUrl) && isAlive),
                        IsCloudActive = transport == "Cloud" || (!string.IsNullOrEmpty(peer?.CloudflareUrl) && isAlive) || cloudAlive,
                        // SECURITY: URL fields intentionally not populated — no URL exposure in UI
                        StatusText = peerAlive
                            ? $"Connected via {peer?.Transport ?? "LAN"}"
                            : (cloudAlive ? "Online — Cloud available" : "Offline")
                    };
                }).ToList();

                if (PeerStatusPanel != null) PeerStatusPanel.ItemsSource = mergedList;
                if (NoPairedDevicesText != null) NoPairedDevicesText.Visibility = mergedList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                int onlineCount = mergedList.Count(p => p.IsAlive);
                if (PeerCountBadge != null) PeerCountBadge.Text = $"{onlineCount} online";
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
            Windows.ToastWindow.ShowToast("New QR code generated!");
        }

        private void CopyPairingInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localUrl = _viewModel.LocalServer?.DisplayUrl ?? "";
                string globalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";
                string pin = SettingsManager.Current.WebClientPinToken;
                string payload = DevicePairingManager.BuildQRPayload(localUrl, globalUrl, pin);
                if (ClipboardHelper.SafeSetText(payload))
                {
                    Windows.ToastWindow.ShowToast("Pairing info copied!");
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private async void ForcePeerSync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Windows.ToastWindow.ShowToast("Syncing with devices...");
                if (PeerManager.Instance != null)
                {
                    await PeerManager.Instance.ForceResync();
                }
                DevicePairingManager.CheckForHandshakes();
                RefreshPairedDevicesList();
                RefreshDevices_Click(null, null);
                Windows.ToastWindow.ShowToast("Sync complete!");
            }
            catch (Exception ex)
            {
                Logger.LogAction("HUB", $"Force sync failed: {ex.Message}");
                Windows.ToastWindow.ShowToast("Sync completed");
            }
        }

        private void RemovePairedDevice_Click(object sender, RoutedEventArgs e)
        {
            string? deviceId = null;
            if (sender is FrameworkElement fe)
                deviceId = fe.Tag as string;

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                DevicePairingManager.RemoveDevice(deviceId);
                RefreshPairedDevicesList();
                RefreshDevices_Click(null, null);
                Windows.ToastWindow.ShowToast("Device unpaired and removed");
            }
        }

        private async void GeneratePairingCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PairingCodeDisplay.Text = "...";
                string code = await DevicePairingManager.PublishPairingCode();
                PairingCodeDisplay.Text = code;
                Windows.ToastWindow.ShowToast($"Code generated: {code} (expires in 5 min)");
            }
            catch (Exception ex)
            {
                PairingCodeDisplay.Text = "ERROR";
                Logger.LogAction("PAIR CODE", $"Generate failed: {ex.Message}");
            }
        }

        private async void ConnectByCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SafeAsyncHandler.RunAsync(async () =>
                {
                string code = RemoteCodeInput?.Text?.Trim().ToUpper(CultureInfo.InvariantCulture) ?? "";
                if (string.IsNullOrEmpty(code) || code.Length != 6)
                {
                    Windows.ToastWindow.ShowToast("Enter a 6-character code");
                    return;
                }

                Windows.ToastWindow.ShowToast($"Looking up {code}...");

                var (success, deviceName) = await DevicePairingManager.ConnectByCode(code);
                if (success)
                {
                    Windows.ToastWindow.ShowToast($"Paired with {deviceName}!");
                    RefreshPairedDevicesList();
                    RemoteCodeInput.Text = "";

                    // Restart Firebase listener so it reads from the newly adopted pairing key scope
                    _viewModel.CloudListener?.StopPolling();
                    _viewModel.CloudListener?.StartPolling();
                    Logger.LogAction("PAIR CODE","Firebase listener restarted for new pairing key scope");

                    // Immediately attempt P2P connection to the new device for instant LAN/Cloud status
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (PeerManager.Instance != null)
                            {
                                await PeerManager.Instance.ForceResync();
                                _ = Dispatcher.InvokeAsync(() => RefreshPairedDevicesList());
                            }
                        }
                        catch (Exception ex) { Logger.LogAction("PAIR CODE", $"Post-pair ForceResync failed: {ex.Message}"); }
                    });
                }
                else if (!string.IsNullOrEmpty(deviceName))
                {
                    Windows.ToastWindow.ShowToast($"Found {deviceName} but couldn't connect  make sure it's online");
                }
                else
                {
                    Windows.ToastWindow.ShowToast($"Code {code} not found  check the other device has internet and re-generate the code");
                    Logger.LogAction("PAIR CODE", $"Code {code} lookup returned null  not found in Firebase");
                }
                });
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("CRASH", $"ConnectByCode_Click: {ex}");
            }
        }

        // ═══ Color Copy Handlers ═══

        internal void CopyColorHex_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                if (ClipboardHelper.SafeSetText(Classes.ColorHelper.ToHex(item.ColorR, item.ColorG, item.ColorB)))
                {
                    Windows.ToastWindow.ShowToast($"Hex copied: {item.DetectedColor}");
                }
                else
                {
                    Windows.ToastWindow.ShowToast("Clipboard busy  try again");
                }
            }
        }

        internal void CopyColorRgb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                string rgb = Classes.ColorHelper.ToRgb(item.ColorR, item.ColorG, item.ColorB);
                if (ClipboardHelper.SafeSetText(rgb))
                {
                    Windows.ToastWindow.ShowToast($"RGB copied: {rgb}");
                }
                else
                {
                    Windows.ToastWindow.ShowToast("Clipboard busy  try again");
                }
            }
        }

        internal void CopyColorHsl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                string hsl = Classes.ColorHelper.ToHsl(item.ColorR, item.ColorG, item.ColorB);
                if (ClipboardHelper.SafeSetText(hsl))
                {
                    Windows.ToastWindow.ShowToast($"HSL copied: {hsl}");
                }
                else
                {
                    Windows.ToastWindow.ShowToast("Clipboard busy  try again");
                }
            }
        }

        // ═══ Mascot Theme Handlers ═══

        internal void PopulateThemeCombo()
        {
            try
            {
                if (ThemeCombo == null) return;

                // Suppress SelectionChanged during programmatic population
                ThemeCombo.SelectionChanged -= ThemeCombo_SelectionChanged;

                ThemeCombo.Items.Clear();

                // "None" option to disable mascot themes and revert to default FlyShelf wallpaper
                ThemeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "None (Default)", Tag ="" });

                // Only mascot theme packs — display modes (Mica/Acrylic/FlyShelf) are now in Background Style cards
                var themes = ThemeManager.Instance.GetInstalledThemes();
                int selectedIdx = 0; // Default to "None (Default)"
                string savedMode = SettingsManager.Current.ThemeDisplayMode ?? "desktop";
                string activeTheme = SettingsManager.Current.ActiveThemeName ?? "";

                // Blocklisted themes — removed from the product
                var blockedThemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gravity Cat" };

                foreach (var theme in themes)
                {
                    // Skip blocklisted themes
                    if (blockedThemes.Contains(theme.Name)) continue;

                    // Skip themes with no resolved animation files (e.g. "FlyShelf Default" template)
                    bool hasRealSprites = theme.Animations != null && theme.Animations.Values.Any(a => 
                        !string.IsNullOrEmpty(a.ResolvedFilePath) && System.IO.File.Exists(a.ResolvedFilePath));
                    if (!hasRealSprites) continue;

                    int idx = ThemeCombo.Items.Count;
                    string themeLabel = LicenseManager.CanUseTheme(theme.Name) ? theme.Name : "" + theme.Name;
                    ThemeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = themeLabel, Tag = theme.Name });

                    if (savedMode == "theme" && theme.Name.Equals(activeTheme, System.StringComparison.OrdinalIgnoreCase))
                        selectedIdx = idx;
                }

                ThemeCombo.SelectedIndex = selectedIdx;

                // Re-hook the event handler now that population is done
                ThemeCombo.SelectionChanged += ThemeCombo_SelectionChanged;

                // Update Acrylic Blur label based on license state
                if (DisplayBtn_Glass_Label != null)
                {
                    DisplayBtn_Glass_Label.Text = LicenseManager.CanUseGlassTheme() ? "Acrylic Blur" : "Acrylic Blur";
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME UI", $"PopulateThemeCombo failed: {ex.Message}");
                // Ensure event handler is re-attached even on error
                try { ThemeCombo.SelectionChanged += ThemeCombo_SelectionChanged; } catch { } // Best-effort: failure is acceptable
            }
        }

        private void RevertThemeComboSelection()
        {
            if (ThemeCombo == null) return;
            ThemeCombo.SelectionChanged -= ThemeCombo_SelectionChanged;
            try
            {
                string activeTheme = SettingsManager.Current.ActiveThemeName ?? "";
                int selectedIdx = 0;

                for (int i = 0; i < ThemeCombo.Items.Count; i++)
                {
                    if (ThemeCombo.Items[i] is System.Windows.Controls.ComboBoxItem cbi && 
                        cbi.Tag?.ToString()?.Equals(activeTheme, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        selectedIdx = i;
                        break;
                    }
                }
                ThemeCombo.SelectedIndex = selectedIdx;
            }
            finally
            {
                ThemeCombo.SelectionChanged += ThemeCombo_SelectionChanged;
            }
        }

        private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem selected)
            {
                string tag = selected.Tag?.ToString() ?? "";

                // Skip placeholder "No themes installed"
                if (tag == "__none__") return;

                // "None (Default)" — disable mascot theme, revert to desktop wallpaper
                if (string.IsNullOrEmpty(tag))
                {
                    // Clean up Glass theme if currently active
                    if (SettingsManager.Current.ThemeDisplayMode == "glass")
                        ThemeManager.Instance.RemoveGlassTheme();

                    ThemeManager.Instance.SetActiveTheme(null);
                    SettingsManager.Current.ThemeDisplayMode = "desktop";
                    SettingsManager.Save();
                    HighlightActiveDisplayMode();
                    ToastWindow.ShowToast("Default FlyShelf wallpaper");

                    Dispatcher.InvokeAsync(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        RefreshWallpaperPreview();
                    });

                    RespawnClipboardPreview();
                    return;
                }

                // Check Pro permissions for Mascot Themes
                if (!Classes.LicenseManager.CanUseTheme(tag))
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        Windows.ToastWindow.ShowToast("Unlock Premium to use this option!"));
                    UpgradePrompt.ShowThemeLimit(this);
                    RevertThemeComboSelection();
                    return;
                }

                // Clean up Glass theme if currently active
                if (SettingsManager.Current.ThemeDisplayMode == "glass")
                    ThemeManager.Instance.RemoveGlassTheme();

                // Custom mascot theme — theme wallpaper + mascot
                SettingsManager.Current.ThemeDisplayMode = "theme";
                ThemeManager.Instance.SetActiveTheme(tag);

                SettingsManager.Save();
                HighlightActiveDisplayMode();

                // Refresh wallpaper preview after a short delay so the 
                // _themeChangedHandler's Dispatcher.InvokeAsync has time to update ClipboardWallpaperPath
                Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(200);
                    RefreshWallpaperPreview();
                });

                // Respawn clipboard so the user sees the mascot theme applied immediately
                RespawnClipboardPreview();
            }
        }

        /// <summary>
        /// Updates the wallpaper preview thumbnail in the Theme & Appearance section
        /// to reflect the current ClipboardWallpaperPath.
        /// </summary>
        private void RefreshWallpaperPreview()
        {
            try
            {
                string wp = SettingsManager.Current.ClipboardWallpaperPath;
                if (string.IsNullOrEmpty(wp) || !System.IO.File.Exists(wp))
                {
                    if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = null;
                    if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Visible;
                }
                else
                {
                    // Load preview on background thread to avoid UI stutter
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(wp, UriKind.Absolute);
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 300;
                            bmp.EndInit();
                            bmp.Freeze();

                            Dispatcher.InvokeAsync(() =>
                            {
                                if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = bmp;
                                if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Collapsed;
                            });
                        }
                        catch
                        {
                            Dispatcher.InvokeAsync(() =>
                            {
                                if (WallpaperPreviewImg != null) WallpaperPreviewImg.Source = null;
                                if (NoWallpaperText != null) NoWallpaperText.Visibility = Visibility.Visible;
                            });
                        }
                    });
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void OpenThemesFolder_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string themesDir = ThemeManager.Instance.ThemesDirectory;
                if (!System.IO.Directory.Exists(themesDir))
                    System.IO.Directory.CreateDirectory(themesDir);
                System.Diagnostics.Process.Start("explorer.exe", themesDir);
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void ImportTheme_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import Mascot Theme",
                    Filter = "FlyShelf Theme|*.flyshelf-theme;*.flyshelftheme|ZIP Archive|*.zip|All Files|*.*"
                };
                if (dialog.ShowDialog() == true)
                {
                    string importedName = ThemeManager.Instance.ImportTheme(dialog.FileName);
                    if (importedName != null)
                    {
                        ToastWindow.ShowToast($"Theme '{importedName}' imported!");
                        ThemeManager.Instance.SetActiveTheme(importedName);
                        PopulateThemeCombo();
                    }
                    else
                    {
                        ToastWindow.ShowToast("Invalid theme file  must contain a manifest.json");
                    }
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Import failed: {ex.Message}");
            }
        }

        private void DeleteTheme_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string activeTheme = SettingsManager.Current.ActiveThemeName;
                if (string.IsNullOrEmpty(activeTheme))
                {
                    ToastWindow.ShowToast("No theme is currently active");
                    return;
                }

                var result = MessageBox.Show(
                    $"Delete the theme '{activeTheme}'?\n\nThis will permanently remove the theme folder from your Themes directory.",
                    "Delete Theme",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    bool deleted = ThemeManager.Instance.DeleteTheme(activeTheme);
                    if (deleted)
                    {
                        ToastWindow.ShowToast($"Theme '{activeTheme}' deleted");
                        PopulateThemeCombo();
                    }
                    else
                    {
                        ToastWindow.ShowToast("Could not delete theme folder");
                    }
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Delete failed: {ex.Message}");
            }
        }

        // ═══ Display Mode Handlers ═══

        /// <summary>
        /// Respawns the MainWindow clipboard after a theme change so the user can
        /// immediately see the effect. Uses a short delay to let the theme engine
        /// finish processing before the clipboard appears.
        /// </summary>
        private void RespawnClipboardPreview()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(350);
                try
                {
                    var mainWin = Application.Current.MainWindow as MainWindow;
                    if (mainWin == null) return;

                    // Position the clipboard at the right-center of the primary screen
                    var workArea = SystemParameters.WorkArea;
                    double targetX = workArea.Right - 200;
                    double targetY = workArea.Top + (workArea.Height / 2);

                    mainWin.ShowNearPosition(targetX, targetY, mode: 1, isPersistent: false, stealFocus: false);
                }
                catch { } // Best-effort: failure is acceptable
            });
        }

        private void DisplayMode_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border && border.Tag is string tag)
            {
                // Check Pro permissions for Glass UI
                if (tag == "__glass__" && !Classes.LicenseManager.CanUseGlassTheme())
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        Windows.ToastWindow.ShowToast("Unlock Premium to use this option!"));
                    UpgradePrompt.ShowThemeLimit(this);
                    return;
                }

                // Always clean up Glass theme first when switching away
                if (tag != "__glass__" && SettingsManager.Current.ThemeDisplayMode =="glass")
                    ThemeManager.Instance.RemoveGlassTheme();

                if (tag == "__mica__")
                {
                    // Mica Blur mode — pure system blur, no wallpaper, no mascot
                    SettingsManager.Current.ThemeDisplayMode = "mica";
                    ThemeManager.Instance.SetActiveTheme(null);
                    ToastWindow.ShowToast("Mica Blur");
                }
                else if (tag == "__glass__")
                {
                    // Glass mode — glassmorphism UI (frosted buttons, translucent cards)
                    SettingsManager.Current.ThemeDisplayMode = "glass";
                    ThemeManager.Instance.SetActiveTheme(null);
                    ThemeManager.Instance.ApplyGlassTheme();
                    ThemeManager.Instance.ApplyAeroThemeOverrides("__glass__");
                    ToastWindow.ShowToast("Acrylic Blur");
                }
                else if (tag == "__desktop__")
                {
                    // FlyShelf mode — desktop wallpaper on clipboard, no mascot
                    SettingsManager.Current.ThemeDisplayMode = "desktop";
                    ThemeManager.Instance.SetActiveTheme(null);
                    ToastWindow.ShowToast("FlyShelf");
                }

                SettingsManager.Save();
                HighlightActiveDisplayMode();
                ApplyTheme(); // Force HubWindow to re-apply its own backdrop to match the new display mode

                // Respawn the clipboard so the user can immediately see the theme change
                RespawnClipboardPreview();

                // Refresh wallpaper preview after a short delay
                Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(200);
                    RefreshWallpaperPreview();
                });
            }
        }

        internal void HighlightActiveDisplayMode()
        {
            try
            {
                string mode = SettingsManager.Current.ThemeDisplayMode ?? "desktop";
                var defaultBrush = Application.Current.FindResource("MicaWPF.Brushes.ControlStrokeColorDefault") as Brush
                                   ?? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

                // Map mode tag → accent color for active border
                var modeAccents = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
                {
                    { "mica",    ThemeColors.GrayMuted },
                    { "glass",   Color.FromRgb(59, 130, 246)  },
                    { "desktop", Color.FromRgb(217, 119, 6)  },
                };

                var buttons = new (System.Windows.Controls.Border btn, string modeKey)[]
                {
                    (DisplayBtn_Mica, "mica"),
                    (DisplayBtn_Glass, "glass"),
                    (DisplayBtn_Desktop, "desktop"),
                };

                foreach (var (btn, modeKey) in buttons)
                {
                    if (btn == null) continue;
                    // "theme" mode means a mascot theme is active, so no display mode card should be highlighted
                    bool isActive = mode == modeKey;

                    if (isActive && modeAccents.TryGetValue(modeKey, out var accentColor))
                    {
                        btn.BorderBrush = new SolidColorBrush(accentColor);
                        btn.BorderThickness = new Thickness(2);
                    }
                    else
                    {
                        // Use SetResourceReference to preserve DynamicResource binding across theme changes
                        btn.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "MicaWPF.Brushes.ControlStrokeColorDefault");
                        btn.BorderThickness = new Thickness(1);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("DISPLAY_MODE_UI", $"HighlightActiveDisplayMode failed: {ex.Message}");
            }
        }

        // ═══ Color Theme Handlers ═══

        private void ColorTheme_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border && border.Tag is string themeName)
            {
                if (themeName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    // Reset: remove color theme dictionary entirely
                    ThemeManager.Instance.RemoveColorTheme();
                    SettingsManager.Current.ColorThemeName = "Default";

                    // Auto-apply FlyShelf (desktop wallpaper) display mode alongside Default theme
                    if (SettingsManager.Current.ThemeDisplayMode == "glass")
                        ThemeManager.Instance.RemoveGlassTheme();
                    SettingsManager.Current.ThemeDisplayMode = "desktop";
                    ThemeManager.Instance.SetActiveTheme(null);

                    SettingsManager.Save();
                    HighlightActiveColorTheme();
                    HighlightActiveDisplayMode();
                    ApplyTheme(); // Re-apply HubWindow backdrop for the new display mode

                    // CRITICAL FIX: Explicitly force the MainWindow theme handler to re-apply the
                    // desktop wallpaper now. SetProperty is a no-op when ThemeDisplayMode was already
                    // "desktop", so PropertyChanged never fires and the wallpaper never updates.
                    // Raising ActiveThemeChanged with null triggers the handler unconditionally.
                    ThemeManager.Instance.ForceThemeRefresh();
                    ToastWindow.ShowToast("Default + FlyShelf");

                    // Refresh wallpaper preview after the MainWindow theme handler has applied the desktop wallpaper
                    Dispatcher.InvokeAsync(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        RefreshWallpaperPreview();
                    });
                }
                else
                {
                    ThemeManager.Instance.ApplyColorTheme(themeName);

                    // FIX: Color themes with bundled wallpapers only display in "desktop" mode.
                    // Auto-switch from mica/glass to desktop so the user sees the theme wallpaper.
                    string currentMode = SettingsManager.Current.ThemeDisplayMode ?? "desktop";
                    if (currentMode == "mica" || currentMode == "glass")
                    {
                        if (currentMode == "glass")
                            ThemeManager.Instance.RemoveGlassTheme();

                        SettingsManager.Current.ThemeDisplayMode = "desktop";
                        HighlightActiveDisplayMode();
                    }

                    SettingsManager.Save();
                    HighlightActiveColorTheme();
                    ToastWindow.ShowToast($"Color theme: {themeName}");

                    // Refresh wallpaper preview after a short delay for the theme to apply its wallpaper
                    Dispatcher.InvokeAsync(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(300);
                        RefreshWallpaperPreview();
                    });
                }

                // Respawn clipboard so the user sees the color change immediately
                RespawnClipboardPreview();
            }
        }

        internal void HighlightActiveColorTheme()
        {
            try
            {
                string active = SettingsManager.Current.ColorThemeName ?? "Midnight";
                var defaultBrush = Application.Current.FindResource("MicaWPF.Brushes.ControlStrokeColorDefault") as Brush
                                   ?? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

                // Map theme name → accent color for the active border
                var themeAccents = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Midnight", ThemeColors.IndigoAccent },
                    { "Ocean",    Color.FromRgb(8, 145, 178)  },
                    { "Sunset",   Color.FromRgb(234, 88, 12)  },
                    { "Emerald",  Color.FromRgb(5, 150, 105)  },
                    { "Lavender", Color.FromRgb(124, 58, 237) },
                    { "ArcticSnow",    Color.FromRgb(79, 70, 229)  },
                    { "Default",  ThemeColors.GrayMuted },
                };

                // Find all theme buttons (including Default)
                var buttons = new[] { ThemeBtn_Midnight, ThemeBtn_Ocean, ThemeBtn_Sunset, ThemeBtn_Emerald, ThemeBtn_Lavender, ThemeBtn_ArcticSnow, ThemeBtn_Default };

                foreach (var btn in buttons)
                {
                    if (btn == null) continue;
                    string btnTheme = btn.Tag?.ToString() ?? "";
                    bool isActive = btnTheme.Equals(active, StringComparison.OrdinalIgnoreCase);

                    if (isActive && themeAccents.TryGetValue(btnTheme, out var accentColor))
                    {
                        btn.BorderBrush = new SolidColorBrush(accentColor);
                        btn.BorderThickness = new Thickness(2);
                    }
                    else
                    {
                        // Use SetResourceReference to preserve DynamicResource binding across theme changes
                        btn.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "MicaWPF.Brushes.ControlStrokeColorDefault");
                        btn.BorderThickness = new Thickness(1);
                    }
                }

                // Also refresh display mode highlights
                HighlightActiveDisplayMode();
            }
            catch (Exception ex)
            {
                Logger.LogAction("COLOR_THEME_UI", $"HighlightActiveColorTheme failed: {ex.Message}");
            }
        }
    }

    public sealed class GroupDisplayItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DeviceList { get; set; } = "";
    }
}
