// ---------------------------------------------------------------
// HubWindow — Tabs & Appearance Handlers
// Theme, Wallpaper, QR Pairing, Color Tools, Mascot Themes
// Split from HubWindow.Advanced.cs for modularity
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
                            bmp.DecodePixelWidth = 300;
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

        // ═══ Mascot Theme Handlers ═══

        internal void PopulateThemeCombo()
        {
            try
            {
                if (ThemeCombo == null) return;
                ThemeCombo.Items.Clear();

                // Option 1: Classic look — no mascot, uses desktop wallpaper
                ThemeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "FlyShelf (Classic)", Tag = "" });

                // Option 2: The first valid installed mascot theme (if any)
                var themes = ThemeManager.Instance.GetInstalledThemes();
                int selectedIdx = 0;
                string activeTheme = SettingsManager.Current.ActiveThemeName ?? "";

                if (themes.Count > 0)
                {
                    var firstTheme = themes[0];
                    ThemeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = firstTheme.Name, Tag = firstTheme.Name });
                    if (firstTheme.Name.Equals(activeTheme, System.StringComparison.OrdinalIgnoreCase))
                        selectedIdx = 1;
                }

                ThemeCombo.SelectedIndex = selectedIdx;
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME UI", $"PopulateThemeCombo failed: {ex.Message}");
            }
        }

        private void ThemeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selected)
            {
                string themeName = selected.Tag?.ToString() ?? "";
                ThemeManager.Instance.SetActiveTheme(themeName);
            }
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
            catch { }
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
                        ToastWindow.ShowToast($"🎨 Theme '{importedName}' imported!");
                        ThemeManager.Instance.SetActiveTheme(importedName);
                        PopulateThemeCombo();
                    }
                    else
                    {
                        ToastWindow.ShowToast("❌ Invalid theme file — must contain a manifest.json");
                    }
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Import failed: {ex.Message}");
            }
        }

        private void DeleteTheme_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string activeTheme = SettingsManager.Current.ActiveThemeName;
                if (string.IsNullOrEmpty(activeTheme))
                {
                    ToastWindow.ShowToast("⚠️ No theme is currently active");
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
                        ToastWindow.ShowToast($"🗑️ Theme '{activeTheme}' deleted");
                        PopulateThemeCombo();
                    }
                    else
                    {
                        ToastWindow.ShowToast("❌ Could not delete theme folder");
                    }
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Delete failed: {ex.Message}");
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
