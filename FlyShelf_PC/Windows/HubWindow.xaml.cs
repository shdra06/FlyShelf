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
        private Action<string>? _updateStatusChangedHandler;
        private Action<int>? _updateDownloadProgressChangedHandler;
        private Action<bool>? _updateCheckCompletedHandler;

        private static readonly RoutedCommand FocusSearchCommand = new RoutedCommand();
        private static readonly RoutedCommand ToggleSidebarCommand = new RoutedCommand();
        private static readonly RoutedCommand Tab1Command = new RoutedCommand();
        private static readonly RoutedCommand Tab2Command = new RoutedCommand();
        private static readonly RoutedCommand Tab3Command = new RoutedCommand();
        private static readonly RoutedCommand Tab4Command = new RoutedCommand();
        private static readonly RoutedCommand Tab5Command = new RoutedCommand();
        private static readonly RoutedCommand Tab6Command = new RoutedCommand();
        private static readonly RoutedCommand Tab7Command = new RoutedCommand();
        private static readonly RoutedCommand Tab8Command = new RoutedCommand();

        // ΓòÉΓòÉΓòÉ Hub Thumbnail Rendering ΓòÉΓòÉΓòÉ
        private System.Windows.Threading.DispatcherTimer? _hubScrollHighQualityTimer;

        // ═══ Peer fast-refresh & coast prefetch (restored from deleted partials) ═══
        private System.Windows.Threading.DispatcherTimer? _peerFastRefreshTimer;
        private Action? _coastPrefetchHandler;

        // ═══ ISOLATED CollectionView ═══
        // Hub owns its own CollectionViewSource so filter changes here
        // do NOT affect the main clipboard's default ICollectionView.
        private System.Windows.Data.CollectionViewSource _hubCollectionViewSource = null!;

        public HubWindow(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            InitializeComponent();

            // Keyboard shortcuts
            this.CommandBindings.Add(new CommandBinding(FocusSearchCommand, (s, e) => { SearchBox?.Focus(); }));
            this.InputBindings.Add(new InputBinding(FocusSearchCommand, new KeyGesture(Key.F, ModifierKeys.Control)));

            this.CommandBindings.Add(new CommandBinding(ToggleSidebarCommand, (s, e) => { SidebarCollapse_Click(null, null); }));
            this.InputBindings.Add(new InputBinding(ToggleSidebarCommand, new KeyGesture(Key.B, ModifierKeys.Control)));

            var tabs = new (RoutedCommand cmd, Key key, string tag)[] {
                (Tab1Command, Key.D1, "History"),
                (Tab2Command, Key.D2, "Dashboard"),
                (Tab3Command, Key.D3, "Network"),
                (Tab4Command, Key.D4, "Personalization"),
                (Tab5Command, Key.D5, "Settings"),
                (Tab6Command, Key.D6, "AI"),
                (Tab7Command, Key.D7, "Logs"),
                (Tab8Command, Key.D8, "About")
            };
            foreach (var t in tabs)
            {
                this.CommandBindings.Add(new CommandBinding(t.cmd, (s, e) => NavigateToTab(t.tag)));
                this.InputBindings.Add(new InputBinding(t.cmd, new KeyGesture(t.key, ModifierKeys.Control)));
            }

            this.PreviewKeyDown += HubWindow_PreviewKeyDown;

            // ═══ Create an ISOLATED collection view for the Hub ═══
            // WPF's CollectionViewSource.GetDefaultView() returns a singleton per collection.
            // Both Hub and MainWindow were sharing that singleton, so filter changes leaked.
            // By creating a separate CollectionViewSource, the Hub gets its own ICollectionView.
            _hubCollectionViewSource = new System.Windows.Data.CollectionViewSource
            {
                Source = _viewModel.DroppedItems
            };
            // Bind Hub controls to the isolated view instead of the shared default view
            if (HubListView != null)
                HubListView.ItemsSource = _hubCollectionViewSource.View;
            if (ImageGridControl != null)
                ImageGridControl.ItemsSource = _hubCollectionViewSource.View;

            if (_viewModel?.DroppedItems != null)
                _viewModel.DroppedItems.CollectionChanged += DroppedItems_CollectionChanged;
            // Theme override dictionary modification thrashes visual tree at construction,
            // so we only apply the theme in OnSourceInitialized when the handle is active.

            // Build hotkey keycaps for the settings tab
            BuildHotkeyKeycaps();
            InitCleanupDefaults();

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
            _peerConnectedHandler = (deviceId, transport) => Dispatcher.InvokeAsync(() =>
            {
                RefreshPairedDevicesList();
                RefreshDevices_Click(null, null);
            });
            _peerDisconnectedHandler = (deviceId) => Dispatcher.InvokeAsync(() =>
            {
                RefreshPairedDevicesList();
                RefreshDevices_Click(null, null);
            });
            _transportSwitchedHandler = (deviceId, newTransport) => Dispatcher.InvokeAsync(() =>
            {
                RefreshPairedDevicesList();
                RefreshDevices_Click(null, null);
            });

            if (PeerManager.Instance != null)
            {
                PeerManager.Instance.PeerConnected += _peerConnectedHandler;
                PeerManager.Instance.PeerDisconnected += _peerDisconnectedHandler;
                PeerManager.Instance.TransportSwitched += _transportSwitchedHandler;
            }

            // Show real version from assembly
            string v = UpdateManager.CurrentVersion;
            if (VersionBadgeText != null) VersionBadgeText.Text = $"v{v}";
            if (CurrentVersionText != null) CurrentVersionText.Text = $"v{v}";

#if MSIX_STORE
            // In Microsoft Store builds, suppress showing the in-app autoupdater card to comply with Store policies
            if (UpdateSectionCard != null)
            {
                UpdateSectionCard.Visibility = Visibility.Collapsed;
            }
            // Show the "download from website for Cloudflare" notice in the About section
            if (MsixStoreNotice != null)
            {
                MsixStoreNotice.Visibility = Visibility.Visible;
            }
#endif

            // ═══ HUB UPDATE BANNER ═══
            // Show the update notification banner if an update was already detected
            if (UpdateManager.GlobalUpdateAvailable)
            {
                if (HubUpdateBannerText != null) HubUpdateBannerText.Text = $"FlyShelf v{UpdateManager.GlobalLatestVersion} is available — update now!";
                if (HubUpdateBanner != null) HubUpdateBanner.Visibility = Visibility.Visible;
            }
            // Subscribe to future update detections
            UpdateManager.GlobalUpdateStatusChanged += OnHubGlobalUpdateStatusChanged;

#if false // LogsNavItem removed during sidebar restructure
            // Release builds: hide developer-only UI (System Logs tab)
            if (LogsNavItem != null) LogsNavItem.Visibility = Visibility.Collapsed;
#endif

            // Wire up UpdateManager events
            _updateStatusChangedHandler = (msg) => Dispatcher.Invoke(() =>
            {
                UpdateStatusText.Text = msg;
                UpdateProgressPanel.Visibility = Visibility.Visible;
            });
            _updateManager.StatusChanged += _updateStatusChangedHandler;

            _updateDownloadProgressChangedHandler = (pct) => Dispatcher.Invoke(() =>
            {
                UpdatePctText.Text = $"{pct}%";
                // Animate progress bar width
                double parentWidth = UpdateProgressPanel.ActualWidth - 24; // minus padding
                UpdateProgressBar.Width = Math.Max(0, parentWidth * pct / 100.0);
            });
            _updateManager.DownloadProgressChanged += _updateDownloadProgressChangedHandler;

            _updateCheckCompletedHandler = (hasUpdate) => Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (hasUpdate)
                {
                    LatestVersionText.Text = $"v{_updateManager.LatestVersion} available!";
                    ChangelogText.Text = _updateManager.Changelog;
                    ChangelogPanel.Visibility = Visibility.Visible;
                    UpdateBtn.Content = "Downloading...";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Visible;

                    // Auto-download immediately
                    bool success = await _updateManager.DownloadAndApplyUpdateAsync();
                    if (success)
                    {
                        UpdateBtn.Content = "Ready to Restart";
                        UpdateStatusText.Text = "Update downloaded! Ready to restart.";
                        UpdatePctText.Text = "100%";

                        var result = System.Windows.MessageBox.Show(
                            "Update downloaded successfully!\n\nRestart FlyShelf now to apply the update?",
                            "FlyShelf Update Ready",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Question);
                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            UpdateBtn.Content = "Restarting...";
                            UpdateStatusText.Text = "Restarting now...";
                            _updateManager.ApplyUpdateAndRestart();
                        }
                        else
                        {
                            UpdateStatusText.Text = "Update ready — restart FlyShelf when convenient.";
                        }
                    }
                    else
                    {
                        UpdateBtn.Content = "Retry Download";
                        UpdateBtn.IsEnabled = true;
                    }
                }
                else
                {
                    UpdateBtn.Content = "Up to Date";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Collapsed;

                    // Re-enable after 3s so user can re-check for newer updates
                    await Task.Delay(3000);
                    UpdateBtn.Content = "Check Again";
                    UpdateBtn.IsEnabled = true;
                }
                }
                catch (Exception ex)
                {
                    FlyShelf.Classes.Logger.LogAction("CRASH", $"UpdateCheck: {ex}");
                }
            });
            _updateManager.UpdateCheckCompleted += _updateCheckCompletedHandler;

            // No auto-update at startup ΓÇö manual only via the button

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

            // Initialize Networking Command Center (file queue, history, nearby discovery)
            InitializeNetworkingHub();

            IsVisibleChanged += (s, ev) =>
            {
                if (IsVisible)
                {
                    // Re-subscribe collection and update manager handlers when window is re-shown
                    // (OnClosing unsubscribes them when the window is hidden)
                    if (_viewModel?.DroppedItems != null)
                    {
                        _viewModel.DroppedItems.CollectionChanged -= DroppedItems_CollectionChanged;
                        _viewModel.DroppedItems.CollectionChanged += DroppedItems_CollectionChanged;
                    }
                    if (_updateManager != null)
                    {
                        if (_updateStatusChangedHandler != null)
                        {
                            _updateManager.StatusChanged -= _updateStatusChangedHandler;
                            _updateManager.StatusChanged += _updateStatusChangedHandler;
                        }
                        if (_updateDownloadProgressChangedHandler != null)
                        {
                            _updateManager.DownloadProgressChanged -= _updateDownloadProgressChangedHandler;
                            _updateManager.DownloadProgressChanged += _updateDownloadProgressChangedHandler;
                        }
                        if (_updateCheckCompletedHandler != null)
                        {
                            _updateManager.UpdateCheckCompleted -= _updateCheckCompletedHandler;
                            _updateManager.UpdateCheckCompleted += _updateCheckCompletedHandler;
                        }
                    }

                    _deviceRefreshTimer?.Start();
                    RefreshDevices_Click(null, null);

                    // Resume handshake timer if we are on Network tab
                    if (NetworkGrid != null && NetworkGrid.Visibility == Visibility.Visible)
                    {
                        _pairingHandshakeTimer?.Start();
                    }

                    // Render visible image thumbnails when the HubWindow becomes visible
                    // (they may have been evicted by OptimizeMemoryUsage while the window was hidden)
                    if (HistoryGrid != null && HistoryGrid.Visibility == Visibility.Visible)
                    {
                        _hubThumbnailRetryCount = 0;
                        Dispatcher.InvokeAsync(() => RenderHubVisibleThumbnails(),
                            System.Windows.Threading.DispatcherPriority.Loaded);
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
#if MSIX_STORE
                // Production Store build: hide all log/diagnostics UI ΓÇö not relevant for end users
                if (LogsNavItem != null)        LogsNavItem.Visibility        = Visibility.Collapsed;
                if (LogsGrid != null)           LogsGrid.Visibility           = Visibility.Collapsed;
                if (FindName("NetworkLiveLogsBtn") is UIElement nlBtn) nlBtn.Visibility = Visibility.Collapsed;
#endif
                // Defer wiring and refreshing to background dispatcher frames so that
                // the main window shell and layout appear instantly without any initial blocking ticks.
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshDevices_Click(null, null);
                    // Initialize clustered category card counts
                    RefreshClusteredCounts();
                    // Hook window-level smooth scrolling with elegant dedicated SmoothScrollPCApp
                    Classes.SmoothScrollPCApp.AttachToWindow(this);

                    // Hook scroll-based thumbnail rendering on HubListView
                    HubListView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(HubListView_ScrollChanged));

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
                            if (RetentionCombo.Items[i] is ComboBoxItem cbi && cbi.Tag?.ToString() == retention.ToString(System.Globalization.CultureInfo.InvariantCulture))
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
                            SettingsManager.Current.ThemeDisplayMode = "desktop";
                            changed = true;
                        }
                        string activeThemeName = SettingsManager.Current.ActiveThemeName ?? "";
                        if (!string.IsNullOrEmpty(activeThemeName) && !LicenseManager.CanUseTheme(activeThemeName))
                        {
                            SettingsManager.Current.ActiveThemeName = "";
                            if (SettingsManager.Current.ThemeDisplayMode == "theme")
                            {
                                SettingsManager.Current.ThemeDisplayMode = "desktop";
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

                // [FIX H-1]: Clean up UpdateManager static event subscription to prevent memory leak
                UpdateManager.GlobalUpdateStatusChanged -= OnHubGlobalUpdateStatusChanged;
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
                    // Refresh clustered category counts when items change
                    if (_currentFilterTag == "All" && ClusteredPanel?.Visibility == Visibility.Visible)
                        RefreshClusteredCounts();
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
            string? url = null;
            if (sender is FrameworkElement fe)
                url = fe.Tag as string;

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (ClipboardHelper.SafeSetText(url))
                {
                    ToastWindow.ShowToast("URL copied!");
                }
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.DroppedItems.Count == 0) return;
            int count = _viewModel.DroppedItems.Count;
            _viewModel.ClearShelf();
            UpdateEmptyState();
            ToastWindow.ShowToast($"Cleared {count} items");
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

                // ΓòÉΓòÉΓòÉ TIMER CLEANUP: Stop all DispatcherTimers when window is hidden ΓòÉΓòÉΓòÉ
                // Prevents timers from firing in the background, wasting CPU and
                // potentially accessing disposed/stale UI elements.
                _pairingHandshakeTimer?.Stop();
                _deviceRefreshTimer?.Stop();
                _hubScrollHighQualityTimer?.Stop();
                _collectionChangedDebounce?.Stop();
                // Timers declared in partial classes (HubWindow.Networking.cs, HubWindow.Settings.cs)
                _networkRefreshTimer?.Stop();
                _historyRefreshTimer?.Stop();
                _hubSearchDebounceTimer?.Stop();

                // Cancel any in-progress update download
                _updateManager.CancelDownload();

                if (_viewModel?.DroppedItems != null)
                    _viewModel.DroppedItems.CollectionChanged -= DroppedItems_CollectionChanged;

                if (_updateStatusChangedHandler != null)
                    _updateManager.StatusChanged -= _updateStatusChangedHandler;
                if (_updateDownloadProgressChangedHandler != null)
                    _updateManager.DownloadProgressChanged -= _updateDownloadProgressChangedHandler;
                if (_updateCheckCompletedHandler != null)
                    _updateManager.UpdateCheckCompleted -= _updateCheckCompletedHandler;

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
                    int colorNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE ΓÇö fully invisible border
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());

                    // Force dark caption color to prevent title bar red accent color bleeding
                    int captionColor = 0x00202020;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, Marshal.SizeOf<int>());
                }
            }
            catch { } // Best-effort: failure is acceptable
        }



        // ═══ NavigateToTab, Nav_Click, NetworkInnerTab_Checked, DashboardCard_Click → HubWindow.Navigation.cs ═══
        // ═══ Hyperlink_RequestNavigate → HubWindow.Navigation.cs ═══

        // ΓòÉΓòÉΓòÉ HUB UPDATE BANNER HANDLERS ΓòÉΓòÉΓòÉ
        private bool _hubUpdateBannerDismissed = false;

        private void OnHubGlobalUpdateStatusChanged(bool updateAvailable)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (updateAvailable && !_hubUpdateBannerDismissed)
                {
                    if (HubUpdateBannerText != null) HubUpdateBannerText.Text = $"FlyShelf v{UpdateManager.GlobalLatestVersion} is available — update now!";
                    if (HubUpdateBanner != null) HubUpdateBanner.Visibility = Visibility.Visible;
                }
            });
        }

        private void HubUpdateBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (StartupHelper.IsPackaged())
                {
                    // MSIX/Store install ΓÇö open the Store app directly
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ms-windows-store://pdp/?ProductId=9PM37CMM3T72",
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Non-packaged (sideload) ΓÇö navigate to Settings tab which has the updater
                    NavigateToTab("Settings");
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("UPDATE_BANNER", $"Hub: Failed to open store/settings: {ex.Message}");
            }
        }

        private void HubUpdateBannerDismiss_Click(object sender, RoutedEventArgs e)
        {
            _hubUpdateBannerDismissed = true;
            HubUpdateBanner.Visibility = Visibility.Collapsed;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            Windows.ToastWindow.ShowToast("Configuration updated successfully.", 2000);
        }


        // ═══ PrivacyPolicyLink_Click, GitHubLink_Click, CloudflareWebsiteLink_Navigate → HubWindow.Navigation.cs ═══
        // ═══ RetentionCombo_SelectionChanged → HubWindow.History.cs ═══
        // ═══ Incognito, Size steppers/preview → HubWindow.UIHandlers.cs ═══

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

        // ═══ SweepDuplicates_Click, CleanHistory_Click → HubWindow.History.cs ═══
        // ═══ UpdateAlignButtonsVisualState, Align*_Click, TaskbarWidgetToggle_Changed → HubWindow.UIHandlers.cs ═══
        // ═══ Hub Thumbnail Rendering → HubWindow.Thumbnails.cs ═══
        // ═══ ReplayOnboarding_Click → HubWindow.Navigation.cs ═══

        private void HubWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HistoryGrid != null && HistoryGrid.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Delete)
                {
                    if (HubListView?.SelectedItem is ClipboardItem item)
                    {
                        _viewModel.RemoveItem(item);
                        e.Handled = true;
                    }

                }
                else if (e.Key == Key.Space)
                {
                    if (SearchBox != null && SearchBox.IsFocused) return;
                    if (e.OriginalSource is System.Windows.Controls.TextBox) return;

                    if (HubListView?.SelectedItem is ClipboardItem item)
                    {
                        (Application.Current.MainWindow as MainWindow)?.ShowQuickLookForItem(item);
                        e.Handled = true;
                    }

                }
            }
        }
    }
}

