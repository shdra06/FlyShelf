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
                
                if (tag == "Logs") RefreshLogs_Click(null, null);
                if (tag == "Settings") PopulateThemeCombo();
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

        private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RetentionCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
            {
                if (int.TryParse(selected.Tag.ToString(), out int days))
                {
                    SettingsManager.Current.ClipboardRetentionDays = days;
                    SettingsManager.Save();
                }
            }
        }

        private void ResetClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MediumFormWidth = 368;
            SettingsManager.Current.MediumFormHeight = 528;
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
        // ═══ Logs, Diagnostics & Drag-Drop moved to HubWindow.Logs.cs ═══
    }
}
