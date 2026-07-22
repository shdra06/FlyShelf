// ---------------------------------------------------------------
// ConnectionQualityPanel — Per-device connection quality cards
// Shows latency, transport, uptime, and last activity for each peer.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using FlyShelf.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlyShelf.Controls
{
    public partial class ConnectionQualityPanel : UserControl
    {
        // Session start time — used for uptime calculation
        private static readonly DateTime _sessionStart = DateTime.UtcNow;

        // Latency cache: DeviceId → latency ms
        private readonly Dictionary<string, double> _latencyCache = new();

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        public ConnectionQualityPanel()
        {
            InitializeComponent();
        }

        // ═══════════════════════════════════════════════════════════
        // PUBLIC API — Called by parent panel
        // ═══════════════════════════════════════════════════════════

        /// <summary>Refreshes the quality cards for all alive peers.</summary>
        public void RefreshQuality()
        {
            try
            {
                QualityCardsList.Items.Clear();

                var peers = PeerManager.Instance?.ConnectedPeers?.Values
                    .Where(p => p.IsAlive)
                    .ToList();

                if (peers == null || peers.Count == 0)
                {
                    EmptyStateText.Visibility = Visibility.Visible;
                    return;
                }

                EmptyStateText.Visibility = Visibility.Collapsed;

                foreach (var peer in peers)
                {
                    try
                    {
                        var card = CreateQualityCard(peer);
                        QualityCardsList.Items.Add(card);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("QUALITY", $"Card error for {peer.DeviceId}: {ex.Message}");
                    }
                }

                // Fire off latency measurements in background
                _ = MeasureLatenciesAsync(peers);
            }
            catch (Exception ex)
            {
                Logger.LogAction("QUALITY", $"RefreshQuality error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CARD BUILDER
        // ═══════════════════════════════════════════════════════════

        private Border CreateQualityCard(PeerConnection peer)
        {
            double latencyMs = _latencyCache.TryGetValue(peer.DeviceId, out var cached) ? cached : -1;

            // ── Device Name ──
            var nameText = new TextBlock
            {
                Text = peer.DeviceName ?? peer.DeviceId,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ThemeColors.LightSlate),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130
            };

            // ── Transport Badge ──
            Color transportColor = peer.Transport == "LAN"
                ? ThemeColors.SuccessGreen
                : ThemeColors.Blue500;
            string transportLabel = peer.Transport == "LAN" ? "LAN" : "Cloud";

            var transportBadge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 1),
                Background = new SolidColorBrush(Color.FromArgb(0x25, transportColor.R, transportColor.G, transportColor.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, transportColor.R, transportColor.G, transportColor.B)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = transportLabel,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(transportColor)
                }
            };

            // ── Latency indicator ──
            Color latencyDotColor;
            string latencyText;
            if (latencyMs < 0)
            {
                latencyDotColor = ThemeColors.SlateGray;
                latencyText = "Measuring...";
            }
            else if (latencyMs < 5)
            {
                latencyDotColor = ThemeColors.SuccessGreen;
                latencyText = $"{latencyMs:F0}ms";
            }
            else if (latencyMs < 20)
            {
                latencyDotColor = ThemeColors.WarningAmber;
                latencyText = $"{latencyMs:F0}ms";
            }
            else
            {
                latencyDotColor = ThemeColors.ErrorRed;
                latencyText = $"{latencyMs:F0}ms";
            }

            var latencyDot = new Border
            {
                Width = 6, Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(latencyDotColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };

            var latencyLabel = new TextBlock
            {
                Text = latencyText,
                FontSize = 10,
                Foreground = new SolidColorBrush(latencyDotColor),
                VerticalAlignment = VerticalAlignment.Center
            };

            var latencyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            latencyRow.Children.Add(latencyDot);
            latencyRow.Children.Add(latencyLabel);

            // ── Uptime ──
            var uptime = DateTime.UtcNow - _sessionStart;
            string uptimeText = uptime.TotalHours >= 1
                ? $"Up {(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"Up {uptime.Minutes}m";

            var uptimeLabel = new TextBlock
            {
                Text = uptimeText,
                FontSize = 9,
                Foreground = new SolidColorBrush(ThemeColors.SlateDark),
                Margin = new Thickness(0, 0, 0, 1)
            };

            // ── Last Activity ──
            string lastSeenText = peer.LastSeen > DateTime.MinValue
                ? $"Last seen {FormatRelativeTime(peer.LastSeen)}"
                : "Just connected";

            var lastSeenLabel = new TextBlock
            {
                Text = lastSeenText,
                FontSize = 9,
                Foreground = new SolidColorBrush(ThemeColors.SlateDark)
            };

            // ── Card Layout ──
            var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            stack.Children.Add(nameText);
            stack.Children.Add(transportBadge);
            stack.Children.Add(latencyRow);
            stack.Children.Add(uptimeLabel);
            stack.Children.Add(lastSeenLabel);

            var card = new Border
            {
                Width = 160,
                MinHeight = 120,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                Child = stack,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            card.Background = new LinearGradientBrush(
                Color.FromRgb(0x0E, 0x13, 0x26),
                Color.FromRgb(0x12, 0x18, 0x2E),
                new Point(0, 0), new Point(1, 1));

            card.MouseEnter += (s, e) =>
            {
                card.Background = new SolidColorBrush(ThemeColors.NavyDark);
                card.BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep);
            };
            card.MouseLeave += (s, e) =>
            {
                card.Background = new LinearGradientBrush(
                    Color.FromRgb(0x0E, 0x13, 0x26),
                    Color.FromRgb(0x12, 0x18, 0x2E),
                    new Point(0, 0), new Point(1, 1));
                card.BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder);
            };

            return card;
        }

        // ═══════════════════════════════════════════════════════════
        // LATENCY MEASUREMENT
        // ═══════════════════════════════════════════════════════════

        private async System.Threading.Tasks.Task MeasureLatenciesAsync(List<PeerConnection> peers)
        {
            foreach (var peer in peers)
            {
                try
                {
                    string url = peer.ActiveUrl;
                    if (string.IsNullOrEmpty(url)) continue;

                    var sw = Stopwatch.StartNew();
                    var response = await _httpClient.GetAsync($"{url}/api/health");
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        _latencyCache[peer.DeviceId] = sw.ElapsedMilliseconds;
                    }
                }
                catch
                {
                    // Peer unreachable — keep old cached value
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════

        private static string FormatRelativeTime(DateTime utcTime)
        {
            var elapsed = DateTime.UtcNow - utcTime;
            if (elapsed.TotalSeconds < 10) return "just now";
            if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds}s ago";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }
}
