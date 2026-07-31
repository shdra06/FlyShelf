// ---------------------------------------------------------------
// MainWindow.SendToDevice — One-way push of clipboard items to peers
// Security: Sender-initiated only; receiver cannot request or pull items
// ---------------------------------------------------------------
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FlyShelf.Classes;
using FlyShelf.ViewModels;
using MicaWPF.Controls;

namespace FlyShelf
{
    public partial class MainWindow : MicaWindow
    {
        // ═══════════════════════════════════════════════════
        // SEND TO DEVICE — Context menu handlers
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Dynamically populates the "Send to Device" submenu with connected peers.
        /// Called from CardContextMenu_Opened.
        /// </summary>
        internal void PopulateSendToDeviceMenu(ContextMenu cm)
        {
            try
            {
                // Find the SendToDeviceMenu MenuItem by Name
                MenuItem? sendMenu = null;
                foreach (var item in cm.Items)
                {
                    if (item is MenuItem mi && mi.Name == "SendToDeviceMenu")
                    {
                        sendMenu = mi;
                        break;
                    }
                }
                if (sendMenu == null) return;

                sendMenu.Items.Clear();

                var peers = PeerManager.Instance?.ConnectedPeers?.Values
                    .Where(p => p.IsAlive)
                    .ToList();

                if (peers == null || peers.Count == 0)
                {
                    var emptyItem = new MenuItem
                    {
                        Header = "No devices connected",
                        IsEnabled = false,
                        Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorDisabled")
                    };
                    sendMenu.Items.Add(emptyItem);
                }
                else
                {
                    foreach (var peer in peers)
                    {
                        var transportLabel = peer.Transport switch
                        {
                            "LAN" => "LAN",
                            "Cloudflare" => "Cloud",
                            _ => peer.Transport
                        };

                        var mi = new MenuItem
                        {
                            Header = $"{peer.DeviceName}  ({transportLabel})",
                            Tag = peer
                        };
                        mi.Click += SendToDevice_Click;
                        sendMenu.Items.Add(mi);
                    }

                    // "Send to All" option
                    sendMenu.Items.Add(new Separator());
                    var allMi = new MenuItem
                    {
                        Header = "Send to All Devices",
                        FontWeight = FontWeights.SemiBold
                    };
                    allMi.Click += SendToAllDevices_Click;
                    sendMenu.Items.Add(allMi);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SEND", $"PopulateSendToDeviceMenu error: {ex.Message}");
            }
        }

        private async void SendToDevice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuItem mi) return;
                if (mi.Tag is not PeerConnection peer) return;

                // Walk up to the ContextMenu to get the ClipboardItem
                var contextMenu = FindParentContextMenu(mi);
                if (contextMenu?.PlacementTarget is not FrameworkElement fe) return;
                if (fe.DataContext is not ClipboardItem item) return;

                await SendItemToPeerAsync(item, peer);
            }
            catch (Exception ex)
            {
                Logger.LogAction("SEND", $"SendToDevice_Click error: {ex.Message}");
            }
        }

        private async void SendToAllDevices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuItem mi) return;

                var contextMenu = FindParentContextMenu(mi);
                if (contextMenu?.PlacementTarget is not FrameworkElement fe) return;
                if (fe.DataContext is not ClipboardItem item) return;

                var peers = PeerManager.Instance?.ConnectedPeers?.Values
                    .Where(p => p.IsAlive)
                    .ToList();

                if (peers == null || peers.Count == 0)
                {
                    Windows.ToastWindow.ShowToast("No devices connected");
                    return;
                }

                int sent = 0, failed = 0;
                foreach (var peer in peers)
                {
                    try
                    {
                        await SendItemToPeerAsync(item, peer, showToast: false);
                        sent++;
                    }
                    catch
                    {
                        failed++;
                    }
                }

                Windows.ToastWindow.ShowToast(
                    failed == 0
                        ? $"Sent to {sent} device{(sent != 1 ?"s" : "")}"
                        : $"Sent to {sent}, failed {failed}"
                );
            }
            catch (Exception ex)
            {
                Logger.LogAction("SEND", $"SendToAllDevices_Click error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a clipboard item to a specific peer device via HTTP POST.
        /// One-way push only — the receiver cannot request or browse items.
        /// </summary>
        private async Task SendItemToPeerAsync(ClipboardItem item, PeerConnection peer, bool showToast = true)
        {
            if (string.IsNullOrEmpty(peer.ActiveUrl) || !peer.IsAlive)
            {
                if (showToast) Windows.ToastWindow.ShowToast($"{peer.DeviceName} is offline");
                return;
            }

            var client = HttpClientPool.Default;
            var pairingKey = SettingsManager.Current?.PairingKey ?? "";

            // Build clipboard payload
            var payload = new
            {
                Type = item.ItemType.ToString(),
                Raw = item.RawContent ?? "",
                Title = item.FileName ?? "",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SourceDeviceName = Environment.MachineName
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{peer.ActiveUrl}/api/clipboard")
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("X-Pairing-Key", pairingKey);
            request.Headers.TryAddWithoutValidation("X-FlyShelf-Client", "DesktopApp");

            using var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                if (showToast) Windows.ToastWindow.ShowToast($"Sent to {peer.DeviceName}");
                Logger.LogAction("SEND", $"Sent {item.ItemType} to {peer.DeviceName} via {peer.Transport}");
                SoundEffects.PlayTransferComplete();
            }
            else
            {
                if (showToast) Windows.ToastWindow.ShowToast($"Failed to send to {peer.DeviceName}");
                Logger.LogAction("SEND", $"Send failed to {peer.DeviceName}: {response.StatusCode}");
                SoundEffects.PlayError();
            }
        }

        /// <summary>
        /// Walks up the visual tree from a MenuItem to find its parent ContextMenu.
        /// </summary>
        private static ContextMenu? FindParentContextMenu(MenuItem mi)
        {
            DependencyObject? current = mi;
            while (current != null)
            {
                if (current is ContextMenu cm) return cm;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                    ?? LogicalTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
