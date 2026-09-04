// ═══════════════════════════════════════════════════════════════════════
// HubWindow.Navigation.cs — Tab navigation, dashboard card routing,
// network inner-tab switching, hyperlink handlers, and onboarding.
// Part of the HubWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Linq;
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
        /// Valid tags: Dashboard, History, Network, Personalization, Settings, About
        /// </summary>
        public void NavigateToTab(string tag)
        {
            Dispatcher.Invoke(() =>
            {
                // Check the matching sidebar RadioButton
                SelectSidebarTab(tag);
                SwitchToTab(tag);
            });
        }

        private System.Collections.Generic.List<System.Windows.Controls.RadioButton>? _cachedNavTabs;

        private System.Collections.Generic.List<System.Windows.Controls.RadioButton> GetNavTabs()
        {
            if (_cachedNavTabs == null || _cachedNavTabs.Count == 0)
            {
                _cachedNavTabs = FindVisualChildren<System.Windows.Controls.RadioButton>(this)
                    .Where(rb => rb.GroupName == "NavTabs")
                    .ToList();
            }
            return _cachedNavTabs;
        }

        private void SelectSidebarTab(string tag)
        {
            foreach (var rb in GetNavTabs())
            {
                if (rb.Tag as string == tag)
                {
                    rb.IsChecked = true;
                    break;
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var grandchild in FindVisualChildren<T>(child)) yield return grandchild;
            }
        }

        private void SidebarNav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb)
            {
                string tag = rb.Tag as string;
                if (string.IsNullOrEmpty(tag)) return;
                SwitchToTab(tag);
            }
        }

        private void SwitchToTab(string tag)
        {
            AnimateTabSwitch(DashboardGrid, tag == "Dashboard");
            AnimateTabSwitch(HistoryGrid, tag == "History");
            AnimateTabSwitch(NetworkGrid, tag == "Network");
            AnimateTabSwitch(PersonalizationGrid, tag == "Personalization");
            AnimateTabSwitch(SettingsGrid, tag == "Settings");
            AnimateTabSwitch(AiGrid, tag == "AI");
            AnimateTabSwitch(LogsGrid, tag == "Logs");
            AnimateTabSwitch(AboutGrid, tag == "About");
            
            if (tag == "Logs")
            {
                LogsPageControl?.RefreshLogs();
            }
            
            if (tag == "Personalization")
            {
                PopulateThemeCombo();
                HighlightActiveColorTheme();
                HighlightActiveDisplayMode();
                RefreshWallpaperPreview();
            }
            if (tag == "Settings")
            {
                UpdateAlignButtonsVisualState();
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
                RefreshCloudStatus();
                RefreshPairedDevicesList();
                StartCloudStatusTimer();
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
            if (tag == "About")
            {
#if !MSIX_STORE
                RefreshLogs_Click(null, null);
#endif
            }
            if (tag == "History")
            {
                _hubThumbnailRetryCount = 0;
                Dispatcher.InvokeAsync(() => RenderHubVisibleThumbnails(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                Dispatcher.InvokeAsync(() => SearchBox?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        // Keep Nav_Click for backward compatibility (DashboardCard routing etc.)
        private void Nav_Click(object sender, MouseButtonEventArgs e)
        {
            if (e != null) e.Handled = true;
            if (sender is FrameworkElement fe)
            {
                string tag = fe.Tag as string;
                if (string.IsNullOrEmpty(tag)) return;
                NavigateToTab(tag);
            }
        }

        private void DashboardCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                NavigateToTab(tag);
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
