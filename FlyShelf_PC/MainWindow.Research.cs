// ---------------------------------------------------------------
// MainWindow — Networking Panel Coordinator (Thin Shell)
// Decomposition Phase 2: All networking content UI logic moved to
// Controls/NetworkingPanelControl.xaml.cs. This file only handles
// panel open/close coordination, mutual exclusion, timer management,
// and event subscriptions for PeerManager/LanTransferManager.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlyShelf.Windows;

namespace FlyShelf
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        // FIELDS
        // ═══════════════════════════════════════════════════════════

        private bool _isResearchActive;
        private DispatcherTimer? _transferRefreshTimer; // C2 fix: live progress updates
        public bool IsResearchActive => _isResearchActive;
        private Brush? _originalResearchHeaderBg;

        /// <summary>
        /// Guard flag: when true, clipboard changes from networking copy/export
        /// won't be re-captured.
        /// </summary>
        internal static bool _suppressResearchCapture = false;

        private static readonly SolidColorBrush _researchHeaderBrush =
            new(Color.FromRgb(0x0D, 0x11, 0x17)); // Dark theme for networking panel

        // ═══════════════════════════════════════════════════════════
        // TOGGLE RESEARCH PANEL
        // ═══════════════════════════════════════════════════════════

        private void ResearchToggle_Click(object sender, RoutedEventArgs e)
        {
#if MSIX_STORE
            return; // Networking hidden in Store build
#else
            if (_isResearchActive)
                CloseResearchPanel();
            else
                OpenResearchPanel();
#endif
        }

        // Aero bottom bar uses MouseLeftButtonDown (MouseButtonEventArgs), not Click (RoutedEventArgs)
        private void AltResearch_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ResearchToggle_Click(sender, new RoutedEventArgs());
        }

        private void OpenResearchPanel()
        {
            // Close other modes
            if (_isNotesActive) CloseNotesPanel(immediate: true);
            if (_isTodoActive) CloseTodoPanel(immediate: true);
            if (_isAiSettingsActive) CloseAiSettingsPanel(immediate: true);
            if (_isSearchActive) CloseSearch(switchingPanel: true);
            if (_isFilterBarActive) ToggleFilterBar(false);
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            _isResearchActive = true;

            // Update taskbar/alt-tab title
            Title = "Networking";

            // Update window activation style so clicking it works
            UpdateWindowActivationStyle();

            // Force-activate and topmost-cycle to grab OS focus
            ActivateResearchWindow();

            // Hide clipboard, show networking panel
            ShelfListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ResearchPanel.Visibility = Visibility.Visible;

            // Clear residual animation so opacity is clean
            ResearchPanel.BeginAnimation(OpacityProperty, null);
            ResearchPanel.Opacity = 1;

            // HEADER: Match the opaque dark theme
            if (_originalResearchHeaderBg == null)
                _originalResearchHeaderBg = HeaderAndFiltersStack.Background;
            HeaderAndFiltersStack.Background = _researchHeaderBrush;
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(HeaderAndFiltersStack, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(HeaderAndFiltersStack, ClearTypeHint.Enabled);

            // Swap button to clipboard icon (acts as "go back" button)
            ResearchToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24
            };
            ResearchToggleBtn.ToolTip = "Back to Clipboard";

            // Animate in
            var slideAnim = AnimationHelper.SlideIn(fromY: -12, durationMs: 200);
            var fadeAnim = AnimationHelper.FadeIn(durationMs: 200);
            if (ResearchPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            ResearchPanel.BeginAnimation(OpacityProperty, fadeAnim);

            // Wire up UserControl events (unsubscribe first to avoid duplicates)
            NetworkingContent.CloseRequested -= OnNetworkingContent_CloseRequested;
            NetworkingContent.CloseRequested += OnNetworkingContent_CloseRequested;
            NetworkingContent.OpenTransferManagerRequested -= OnNetworkingContent_OpenTransferManagerRequested;
            NetworkingContent.OpenTransferManagerRequested += OnNetworkingContent_OpenTransferManagerRequested;

            // Populate device list, file queue, and active transfers (delegated to UserControl)
            NetworkingContent.RefreshDevices();
            NetworkingContent.RefreshQueue();
            NetworkingContent.RefreshTransfers();

            // C2 fix: Start timer for live transfer progress updates
            _transferRefreshTimer?.Stop();
            _transferRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _transferRefreshTimer.Tick += (s, e) =>
            {
                if (_isResearchActive && LanTransferManager.Instance?.ActiveCount > 0)
                    NetworkingContent.RefreshTransfers();
                else if (LanTransferManager.Instance?.ActiveCount == 0)
                    _transferRefreshTimer?.Stop();
            };
            // Subscribe to TransferStarted to auto-start timer on new transfers
            if (LanTransferManager.Instance != null)
            {
                LanTransferManager.Instance.TransferStarted -= OnTransferStarted_RefreshTimer;
                LanTransferManager.Instance.TransferStarted += OnTransferStarted_RefreshTimer;
            }
            if (LanTransferManager.Instance?.ActiveCount > 0)
                _transferRefreshTimer.Start();

            // Fix 3: Auto-refresh on peer connect/disconnect
            if (PeerManager.Instance != null)
            {
                PeerManager.Instance.PeerConnected -= OnNetPanel_PeerChanged;
                PeerManager.Instance.PeerDisconnected -= OnNetPanel_PeerDisconnected;
                PeerManager.Instance.PeerConnected += OnNetPanel_PeerChanged;
                PeerManager.Instance.PeerDisconnected += OnNetPanel_PeerDisconnected;
            }

            // Trigger nearby scan so Nearby Devices populate immediately
            _ = Task.Run(async () =>
            {
                try { await (NearbyDiscovery.Instance?.BroadcastProbe() ?? Task.CompletedTask); } catch { } // Best-effort: failure is acceptable
            });

            Logger.LogAction("NETWORK", "Networking panel opened");
        }

        private void OnTransferStarted_RefreshTimer(LanTransferSession session)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isResearchActive)
                {
                    NetworkingContent.RefreshTransfers();
                    _transferRefreshTimer?.Start();
                }
            });
        }

        /// <summary>
        /// Force the MainWindow to become the active foreground window.
        /// </summary>
        private void ActivateResearchWindow()
        {
            SuppressDwmBorder();
            this.Activate();
            if (!this.Topmost)
            {
                this.Topmost = true;
                this.Topmost = false;
            }
            this.Focus();
        }

        private void CloseResearchPanel(bool immediate = false)
        {
            if (!_isResearchActive) return;

            _isResearchActive = false;
            if (_isSearchActive)
            {
                CloseSearch();
            }

            // Unsubscribe auto-refresh events
            _transferRefreshTimer?.Stop();
            _transferRefreshTimer = null;
            if (LanTransferManager.Instance != null)
                LanTransferManager.Instance.TransferStarted -= OnTransferStarted_RefreshTimer;
            if (PeerManager.Instance != null)
            {
                PeerManager.Instance.PeerConnected -= OnNetPanel_PeerChanged;
                PeerManager.Instance.PeerDisconnected -= OnNetPanel_PeerDisconnected;
            }

            // Restore taskbar/alt-tab title
            Title = "FlyShelf";

            // Restore non-activating window style
            UpdateWindowActivationStyle();

            // Restore button icon and tooltip
            ResearchToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Wifi124
            };
            ResearchToggleBtn.ToolTip = "Networking — Send files to connected devices";
            ResearchToggleBtn.ClearValue(ForegroundProperty);

            // HEADER: Restore original transparent/Mica background
            HeaderAndFiltersStack.Background = _originalResearchHeaderBg ?? Brushes.Transparent;
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(HeaderAndFiltersStack, TextRenderingMode.Auto);
            RenderOptions.SetClearTypeHint(HeaderAndFiltersStack, ClearTypeHint.Auto);

            if (immediate)
            {
                // Instant close — no animation
                ResearchPanel.BeginAnimation(OpacityProperty, null);
                ResearchPanel.Opacity = 0;
                ResearchPanel.Visibility = Visibility.Collapsed;
                // BUGFIX: Clear the fade-out animation on ShelfListView — OpenResearchPanel animates
                // its opacity to 0 during the panel entry transition. Without this reset,
                // the list is Visible but fully transparent on re-summon (empty box ghost).
                ShelfListView.BeginAnimation(OpacityProperty, null);
                ShelfListView.Opacity = 1;
                ShelfListView.Visibility = Visibility.Visible;
                EmptyStatePanel.ClearValue(VisibilityProperty);

                Logger.LogAction("NETWORK", "Networking panel closed (immediate)");
                return;
            }

            // Animate out
            var slideAnim = AnimationHelper.SlideOut(toY: -12, durationMs: 180);
            var fadeAnim = AnimationHelper.FadeOut(durationMs: 180);

            if (ResearchPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            fadeAnim.Completed += (s, ev) =>
            {
                if (!_isResearchActive)
                {
                    ResearchPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    EmptyStatePanel.ClearValue(VisibilityProperty);
                }
            };
            ResearchPanel.BeginAnimation(OpacityProperty, fadeAnim);

            Logger.LogAction("NETWORK", "Networking panel closed");
        }

        /// PreviewMouseDown on the entire panel grid.
        /// Ensures the window captures OS focus when user clicks inside.
        private void ResearchPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!this.IsActive)
            {
                SuppressDwmBorder();
                this.Activate();
                if (!this.Topmost)
                {
                    this.Topmost = true;
                    this.Topmost = false;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // USERCONTROL EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════

        private void OnNetworkingContent_CloseRequested(object? sender, EventArgs e)
        {
            CloseResearchPanel();
        }

        private void OnNetworkingContent_OpenTransferManagerRequested(object? sender, EventArgs e)
        {
            try
            {
                CloseResearchPanel(immediate: true);
                OpenHubWindow();
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Open hub error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // FIX 3: AUTO-REFRESH EVENT HANDLERS
        // ═══════════════════════════════════════════════════════════

        private void OnNetPanel_PeerChanged(string deviceId, string transport)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isResearchActive)
                    NetworkingContent.RefreshDevices();
            });
        }

        private void OnNetPanel_PeerDisconnected(string deviceId)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_isResearchActive)
                    NetworkingContent.RefreshDevices();
            });
        }
    }
}
