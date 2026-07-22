// ═══════════════════════════════════════════════════════════════════════
// HubWindow.Navigation.cs — Tab navigation, dashboard card routing,
// network inner-tab switching, hyperlink handlers, and onboarding.
// Part of the HubWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Windows;
using System.Windows.Input;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private void AnimateTabSwitch(System.Windows.FrameworkElement? panel, bool show)
        {
            if (panel == null) return;
            if (show)
            {
                panel.Visibility = System.Windows.Visibility.Visible;
                panel.Opacity = 0;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                panel.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);
            }
            else
            {
                panel.Visibility = System.Windows.Visibility.Collapsed;
                panel.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            }
        }

        /// <summary>
        /// Programmatically navigates to the specified tab by tag name.
        /// Valid tags: Dashboard, History, Network, Settings, Logs, About, Tutorial
        /// </summary>
        public void NavigateToTab(string tag)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var item in RootNavigation.MenuItems)
                {
                    if (item is Wpf.Ui.Controls.NavigationViewItem navItem)
                    {
                        navItem.IsActive = ((navItem.Tag as string) == tag);
                    }
                }

                AnimateTabSwitch(DashboardGrid, tag == "Dashboard");
                AnimateTabSwitch(HistoryGrid, tag == "History");
                AnimateTabSwitch(NetworkGrid, tag == "Network");
                AnimateTabSwitch(SettingsGrid, tag == "Settings");
                AnimateTabSwitch(LogsGrid, tag == "Logs");
                AnimateTabSwitch(AboutGrid, tag == "About");
                AnimateTabSwitch(TutorialGrid, tag == "Tutorial");

                if (tag == "Settings")
                {
                    PopulateThemeCombo();
                    HighlightActiveColorTheme();
                    UpdateAlignButtonsVisualState();
                    if (WidgetPositioningSection != null)
                    {
                        WidgetPositioningSection.Visibility = SettingsManager.Current.EnableTaskbarWidget
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
                if (tag == "History")
                {
                    _hubThumbnailRetryCount = 0;
                    Dispatcher.InvokeAsync(() => RenderHubVisibleThumbnails(),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            });
        }

        private void Nav_Click(object sender, MouseButtonEventArgs e)
        {
            if (e != null) e.Handled = true;
            if (sender is FrameworkElement fe)
            {
                string tag = fe.Tag as string;
                if (string.IsNullOrEmpty(tag)) return;
                foreach (var item in RootNavigation.MenuItems)
                {
                    if (item is Wpf.Ui.Controls.NavigationViewItem navItem)
                    {
                        navItem.IsActive = ((navItem.Tag as string) == tag);
                    }
                }
                
                AnimateTabSwitch(DashboardGrid, tag == "Dashboard");
                AnimateTabSwitch(HistoryGrid, tag == "History");
                AnimateTabSwitch(NetworkGrid, tag == "Network");
                AnimateTabSwitch(SettingsGrid, tag == "Settings");
                AnimateTabSwitch(AiGrid, tag == "AI");
#if MSIX_STORE
                AnimateTabSwitch(LogsGrid, false);
#else
                AnimateTabSwitch(LogsGrid, tag == "Logs");
                if (tag == "Logs") RefreshLogs_Click(null, null);
#endif
                AnimateTabSwitch(AboutGrid, tag == "About");
                AnimateTabSwitch(TutorialGrid, tag == "Tutorial");
                
                if (tag == "Settings")
                {
                    PopulateThemeCombo();
                    HighlightActiveColorTheme();
                    UpdateAlignButtonsVisualState();
                    // Force-sync widget positioning section visibility
                    if (WidgetPositioningSection != null)
                    {
                        WidgetPositioningSection.Visibility = SettingsManager.Current.EnableTaskbarWidget
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                }
                if (tag == "AI")
                {
                    PopulateHubAiSettings();
                }
                if (tag == "Network")
                {
                    RefreshDevices_Click(null, null);
                    RefreshQRCode();
                    RefreshPairedDevicesList();
                    // Auto-populate server diagnostics
#if !MSIX_STORE
                    if (ServerDiagnosticsLog != null)
                    {
                        ServerDiagnosticsLog.Text = GetServerDiagnostics();
                    }
#endif

                    _pairingHandshakeTimer?.Start();
                }
                else
                {
                    _pairingHandshakeTimer?.Stop();
                }
                if (tag == "History")
                {
                    // Render visible thumbnails when switching to the Clipboard/History tab
                    _hubThumbnailRetryCount = 0;
                    Dispatcher.InvokeAsync(() => RenderHubVisibleThumbnails(),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        private void NetworkInnerTab_Checked(object sender, RoutedEventArgs e)
        {
            // Guard: panels may not be loaded yet during InitializeComponent
            if (NetworkDevicesTab == null || NetworkFileQueueTab == null || NetworkHistoryTab == null || NetworkNearbyTab == null)
                return;

            AnimateTabSwitch(NetworkDevicesTab, NetworkTabDevices?.IsChecked == true);
            AnimateTabSwitch(NetworkFileQueueTab, NetworkTabFileQueue?.IsChecked == true);
            AnimateTabSwitch(NetworkHistoryTab, NetworkTabHistory?.IsChecked == true);
            AnimateTabSwitch(NetworkNearbyTab, NetworkTabNearby?.IsChecked == true);
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

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch { } // Best-effort: failure is acceptable
            e.Handled = true;
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

        private void CloudflareWebsiteLink_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("HYPERLINK_ERROR", $"Failed to open cloudflare download link: {ex.Message}");
            }
        }

        private void ReplayOnboarding_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var onboarding = new OnboardingWindow();
                onboarding.Owner = this;
                onboarding.ShowDialog();
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("TUTORIAL", $"Replay onboarding failed: {ex.Message}");
            }
        }
    }
}
