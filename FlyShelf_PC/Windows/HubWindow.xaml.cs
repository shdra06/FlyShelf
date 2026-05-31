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

#if MSIX_STORE
            // In Microsoft Store builds, suppress showing the in-app autoupdater card to comply with Store policies
            if (UpdateSectionCard != null)
            {
                UpdateSectionCard.Visibility = Visibility.Collapsed;
            }
#endif
            if (StartupHelper.IsPackaged())
            {
                if (UpdateSectionCard != null)
                {
                    UpdateSectionCard.Visibility = Visibility.Collapsed;
                }
            }

#if !DEBUG
            // Release builds: hide developer-only UI (System Logs tab, Network live logs button)
            if (LogsNavItem != null) LogsNavItem.Visibility = Visibility.Collapsed;
            if (NetworkLiveLogsBtn != null) NetworkLiveLogsBtn.Visibility = Visibility.Collapsed;
#endif

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

            IsVisibleChanged += (s, ev) =>
            {
                if (IsVisible)
                {
                    _deviceRefreshTimer?.Start();
                    RefreshDevices_Click(null, null);

                    // Resume handshake timer if we are on Network tab
                    if (NetworkGrid != null && NetworkGrid.Visibility == Visibility.Visible)
                    {
                        _pairingHandshakeTimer?.Start();
                    }
                }
                else
                {
                    _deviceRefreshTimer?.Stop();
                    _pairingHandshakeTimer?.Stop();
                }
            };

            Loaded += (s, ev) =>
            {
                // Defer wiring and refreshing to background dispatcher frames so that
                // the main window shell and layout appear instantly without any initial blocking ticks.
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshDevices_Click(null, null);
                    // Hook window-level smooth scrolling with elegant dedicated SmoothScrollPCApp
                    Classes.SmoothScrollPCApp.AttachToWindow(this);

                    // Initialize retention ComboBox from saved setting
                    if (RetentionCombo != null)
                    {
                        int retention = SettingsManager.Current.ClipboardRetentionDays;
                        if (retention == 0 && !LicenseManager.IsPro)
                        {
                            retention = 7;
                            SettingsManager.Current.ClipboardRetentionDays = 7;
                            SettingsManager.Save();
                        }
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

                    // Correct ThemeDisplayMode and ActiveThemeName if Free user bypassed
                    if (!LicenseManager.IsPro)
                    {
                        bool changed = false;
                        if (SettingsManager.Current.ThemeDisplayMode == "glass")
                        {
                            SettingsManager.Current.ThemeDisplayMode = "mica";
                            changed = true;
                        }
                        string activeThemeName = SettingsManager.Current.ActiveThemeName ?? "";
                        if (!string.IsNullOrEmpty(activeThemeName) && !LicenseManager.CanUseTheme(activeThemeName))
                        {
                            SettingsManager.Current.ActiveThemeName = "";
                            if (SettingsManager.Current.ThemeDisplayMode == "theme")
                            {
                                SettingsManager.Current.ThemeDisplayMode = "mica";
                            }
                            changed = true;
                        }
                        if (changed)
                        {
                            SettingsManager.Save();
                        }
                    }

                    // Initialize license UI (Pro badge, status card)
                    RefreshLicenseUI();
                    UpdateAlignButtonsVisualState();

                    // Force-sync Widget Positioning section visibility on load
                    if (WidgetPositioningSection != null)
                    {
                        WidgetPositioningSection.Visibility = SettingsManager.Current.EnableTaskbarWidget
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            };
            Unloaded += (s, ev) =>
            {
                // Detach window-level smooth scrolling to prevent memory leaks
                Classes.SmoothScrollPCApp.DetachFromWindow(this);

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
            ToastWindow.ShowToast($"Cleared {count} items 🗑ï¸");
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

                // Actively optimize and release memory whenever the HubWindow is closed/hidden
                var mainWin = Application.Current.MainWindow as MainWindow;
                mainWin?.OptimizeMemoryUsage();
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
                    int colorNone = DWMWA_COLOR_DARK_GRAY;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());

                    // Force dark caption color to prevent title bar red accent color bleeding
                    int captionColor = 0x00202020;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, Marshal.SizeOf<int>());
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
                if (AboutGrid != null) AboutGrid.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
                
                if (tag == "Logs") RefreshLogs_Click(null, null);
                if (tag == "Settings")
                {
                    PopulateThemeCombo();
                    UpdateAlignButtonsVisualState();
                    // Force-sync widget positioning section visibility
                    if (WidgetPositioningSection != null)
                    {
                        WidgetPositioningSection.Visibility = SettingsManager.Current.EnableTaskbarWidget
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
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

        private void PrivacyPolicyLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/shdra06/FlyShelf/blob/main/PRIVACY_POLICY.md",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("HYPERLINK_ERROR", $"Failed to open privacy policy: {ex.Message}");
            }
        }

        private void GitHubLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/shdra06/FlyShelf",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("HYPERLINK_ERROR", $"Failed to open github link: {ex.Message}");
            }
        }

        private bool _isRetentionChanging = false;
        private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRetentionChanging) return;

            if (RetentionCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
            {
                if (int.TryParse(selected.Tag.ToString(), out int days))
                {
                    if (days == 0 && !LicenseManager.IsPro)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            Windows.ToastWindow.ShowToast("🔒 Unlock Premium to use this option!"));

                        _isRetentionChanging = true;
                        try
                        {
                            for (int i = 0; i < RetentionCombo.Items.Count; i++)
                            {
                                if (RetentionCombo.Items[i] is ComboBoxItem cbi && cbi.Tag?.ToString() == "7")
                                {
                                    RetentionCombo.SelectedIndex = i;
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            _isRetentionChanging = false;
                        }

                        MessageBox.Show(
                            "Disabling auto-cleanup (Never delete unpinned history) is a Pro feature.\n\nUpgrade to Pro to unlock the Never option!",
                            "FlyShelf — Pro Feature",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        UpgradePrompt.ShowActivationDialog(this);
                        return;
                    }

                    SettingsManager.Current.ClipboardRetentionDays = days;
                    SettingsManager.Save();
                }
            }
        }

        private void ResetClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MediumFormWidth = 360;
            SettingsManager.Current.MediumFormHeight = 380;
            SettingsManager.Save();
        }

        private void ResetFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MiniFormWidth = 260;
            SettingsManager.Current.MiniFormHeight = 260;
            SettingsManager.Save();
        }

        private void SizingLockedCard_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToastWindow.ShowToast("🔒 Unlock Premium to use this option!");
            UpgradePrompt.ShowActivationDialog(this);
            e.Handled = true;
        }

        // Clipboard +/- steppers
        private void ClipW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Min(500, SettingsManager.Current.MediumFormWidth + 5); }
        private void ClipW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Max(200, SettingsManager.Current.MediumFormWidth - 5); }
        private void ClipH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Min(700, SettingsManager.Current.MediumFormHeight + 5); }
        private void ClipH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Max(300, SettingsManager.Current.MediumFormHeight - 5); }

        // FlyShelf +/- steppers
        private void DropW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Min(400, SettingsManager.Current.MiniFormWidth + 5); }
        private void DropW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Max(180, SettingsManager.Current.MiniFormWidth - 5); }
        private void DropH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Min(350, SettingsManager.Current.MiniFormHeight + 5); }
        private void DropH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Max(100, SettingsManager.Current.MiniFormHeight - 5); }

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

        private void SweepDuplicates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "This will scan your entire clipboard history, locate duplicate items (exact same text or exact same file path), and delete all older duplicates, keeping only the most recent version.\n\nPinned items are completely safe and will not be touched.\n\nAre you sure you want to proceed?",
                    "Duplicate Sweeper",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                var items = _viewModel.DroppedItems.ToList();
                if (items.Count == 0)
                {
                    ToastWindow.ShowToast("Clipboard history is empty. 📋");
                    return;
                }

                var itemsToDelete = new List<ClipboardItem>();
                
                // Group duplicates using normalized keys (case-insensitive file path, exact text content)
                var groups = items.GroupBy(item => {
                    if (!string.IsNullOrEmpty(item.FilePath))
                        return "F:" + item.FilePath.ToLowerInvariant().Replace('\\', '/');
                    if (!string.IsNullOrEmpty(item.RawContent))
                        return "T:" + item.RawContent;
                    if (!string.IsNullOrEmpty(item.FileName))
                        return "N:" + item.FileName;
                    return string.Empty;
                });

                foreach (var g in groups)
                {
                    if (string.IsNullOrEmpty(g.Key)) continue;

                    // Sort by newest DateCopied descending
                    var sortedGroup = g.OrderByDescending(x => x.DateCopied).ToList();
                    
                    // The first item (index 0) is kept as the most recent.
                    // For the remaining items in the group, we delete them if they are not pinned.
                    for (int i = 1; i < sortedGroup.Count; i++)
                    {
                        var duplicateItem = sortedGroup[i];
                        if (!duplicateItem.IsPinned)
                        {
                            itemsToDelete.Add(duplicateItem);
                        }
                    }
                }

                if (itemsToDelete.Count == 0)
                {
                    ToastWindow.ShowToast("No duplicates found! ✨");
                    return;
                }

                // Perform fast bulk removal
                _viewModel.BulkRemoveItems(itemsToDelete);

                ToastWindow.ShowToast($"Successfully swept {itemsToDelete.Count} duplicate(s)! 🧹");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to sweep duplicates: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CleanHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CleanupTimeframeCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
                {
                    string tag = selected.Tag.ToString();
                    TimeSpan selectedTimeSpan;
                    string timeframeName;

                    switch (tag)
                    {
                        case "1h":
                            selectedTimeSpan = TimeSpan.FromHours(1);
                            timeframeName = "Last 1 Hour";
                            break;
                        case "6h":
                            selectedTimeSpan = TimeSpan.FromHours(6);
                            timeframeName = "Last 6 Hours";
                            break;
                        case "9h":
                            selectedTimeSpan = TimeSpan.FromHours(9);
                            timeframeName = "Last 9 Hours";
                            break;
                        case "24h":
                            selectedTimeSpan = TimeSpan.FromHours(24);
                            timeframeName = "Last 24 Hours";
                            break;
                        case "2d":
                            selectedTimeSpan = TimeSpan.FromDays(2);
                            timeframeName = "Last 2 Days";
                            break;
                        default:
                            return;
                    }

                    var confirm = MessageBox.Show(
                        $"Are you sure you want to permanently delete all unpinned clipboard entries from the {timeframeName}?\n\nPinned entries will remain secure and untouched.",
                        "Smart History Cleanup",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (confirm != MessageBoxResult.Yes) return;

                    DateTime threshold = DateTime.Now.Subtract(selectedTimeSpan);
                    var itemsToDelete = _viewModel.DroppedItems
                        .Where(item => !item.IsPinned && item.DateCopied >= threshold)
                        .ToList();

                    if (itemsToDelete.Count == 0)
                    {
                        ToastWindow.ShowToast($"No entries found from the {timeframeName}. ✨");
                        return;
                    }

                    _viewModel.BulkRemoveItems(itemsToDelete);

                    ToastWindow.ShowToast($"Successfully deleted {itemsToDelete.Count} entry/entries! 🧹");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clean history: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateAlignButtonsVisualState()
        {
            if (AlignAutoBtn == null || AlignLeftBtn == null || AlignStartBtn == null || AlignTrayBtn == null || AlignCustomBtn == null)
                return;

            int align = SettingsManager.Current.WidgetTaskbarAlignment;
            
            // Set appearance of active button to Primary, others to Secondary
            AlignAutoBtn.Appearance = align == -1 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignLeftBtn.Appearance = align == 0 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignStartBtn.Appearance = align == 1 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignTrayBtn.Appearance = align == 2 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
            AlignCustomBtn.Appearance = align == 3 ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;

            // Show/hide relevant sliders
            if (PixelOffsetContainer != null)
                PixelOffsetContainer.Visibility = align != 3 ? Visibility.Visible : Visibility.Collapsed;
            if (PercentagePositionContainer != null)
                PercentagePositionContainer.Visibility = align == 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AlignAuto_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = -1;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignLeft_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = 0;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignStart_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = 1;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignTray_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.WidgetTaskbarAlignment = 2;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void AlignCustom_Click(object sender, RoutedEventArgs e)
        {
            int currentOffset = SettingsManager.Current.WidgetHorizontalOffset;
            // Reset to center (50%) if the current offset is out of range for percentage mode,
            // or if it's 0 (which places widget behind Start button — invisible)
            if (currentOffset <= 0 || currentOffset > 100)
            {
                SettingsManager.Current.WidgetHorizontalOffset = 50; // default to center (50%)
            }
            SettingsManager.Current.WidgetTaskbarAlignment = 3;
            SettingsManager.Save();
            UpdateAlignButtonsVisualState();
        }

        private void TaskbarWidgetToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Force-update Widget Positioning section visibility from code-behind
            // as a robust fallback in case the XAML BooleanToVisibilityConverter binding doesn't fire
            if (WidgetPositioningSection != null)
            {
                WidgetPositioningSection.Visibility = SettingsManager.Current.EnableTaskbarWidget
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (SettingsManager.Current.EnableTaskbarWidget)
            {
                UpdateAlignButtonsVisualState();
            }
        }

        // ═══ Logs, Diagnostics & Drag-Drop moved to HubWindow.Logs.cs ═══
    }
}
