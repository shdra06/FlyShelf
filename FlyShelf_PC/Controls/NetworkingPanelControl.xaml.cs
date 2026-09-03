// ---------------------------------------------------------------
// NetworkingPanelControl — Self-contained Networking Panel UserControl
// Extracted from MainWindow.Research.cs (Decomposition Phase 2).
// Contains all networking content logic: device list, file queue,
// transfer cards, pairing UI, nearby devices, drag-drop.
// MainWindow coordinates panel open/close via events.
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
using FlyShelf.Helpers;

namespace FlyShelf.Controls
{
    public partial class NetworkingPanelControl : UserControl
    {
        /// <summary>Fired when the user clicks the Back button to close the panel.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>Fired when the user requests to open the Transfer Manager (Hub Window).</summary>
        public event EventHandler? OpenTransferManagerRequested;

        public NetworkingPanelControl()
        {
            InitializeComponent();

            DevicePairingManager.OnDevicePaired += _ => Dispatcher.InvokeAsync(() => RefreshDevices());

            Loaded += (s, e) =>
            {
                if (PeerManager.Instance != null)
                {
                    PeerManager.Instance.PeerConnected += (id, t) => Dispatcher.InvokeAsync(() => RefreshDevices());
                    PeerManager.Instance.PeerDisconnected += (id) => Dispatcher.InvokeAsync(() => RefreshDevices());
                    PeerManager.Instance.TransportSwitched += (id, t) => Dispatcher.InvokeAsync(() => RefreshDevices());
                }
                RefreshDevices();
            };
        }

        // ═══════════════════════════════════════════════════════════
        // PUBLIC API — called by MainWindow coordinator
        // ═══════════════════════════════════════════════════════════

        /// <summary>Refreshes the connected devices strip.</summary>
        public void RefreshDevices()
        {
            try
            {
                if (NetPanelDeviceList == null) return;
                NetPanelDeviceList.Children.Clear();

                var displayPeers = new Dictionary<string, PeerConnection>(StringComparer.OrdinalIgnoreCase);

                // 1. Add active PeerManager peers
                if (PeerManager.Instance?.ConnectedPeers != null)
                {
                    foreach (var p in PeerManager.Instance.ConnectedPeers.Values.Where(p => p.IsAlive))
                    {
                        if (!string.IsNullOrEmpty(p.DeviceId)) displayPeers[p.DeviceId] = p;
                    }
                }

                // 2. Add recently active or paired devices
                var paired = DevicePairingManager.GetPairedDevices();
                foreach (var d in paired)
                {
                    if (string.IsNullOrEmpty(d.DeviceId)) continue;
                    if (!displayPeers.ContainsKey(d.DeviceId))
                    {
                        bool isRecentlyActive = (DateTime.Now - d.LastSeen).TotalMinutes < 5;
                        bool hasOpenWs = NetworkSyncServer.ActivePeerWebSocketCount > 0 && d.DeviceType == "Mobile";
                        bool isOnline = isRecentlyActive || hasOpenWs;

                        if (isOnline)
                        {
                            displayPeers[d.DeviceId] = new PeerConnection
                            {
                                DeviceId = d.DeviceId,
                                DeviceName = d.DeviceName,
                                DeviceType = d.DeviceType,
                                IsAlive = true,
                                Transport = (!string.IsNullOrEmpty(d.LastKnownIP) && d.LastKnownIP.Contains("cloud")) ? "Cloud" : "LAN"
                            };
                        }
                    }
                }

                var peers = displayPeers.Values.ToList();

                if (peers.Count == 0)
                {
                    NetPanelPeerCount.Text = "0 devices";
                    var emptyChip = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 6, 12, 6),
                        Background = new SolidColorBrush(ThemeColors.NetworkCardBg),
                        BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text = "No devices — pair in Settings → Network",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(ThemeColors.SlateDark)
                        }
                    };
                    NetPanelDeviceList.Children.Add(emptyChip);
                    return;
                }

                NetPanelPeerCount.Text = $"{peers.Count} device{(peers.Count != 1 ? "s" : "")}";

                foreach (var peer in peers)
                {
                    var deviceCard = CreateNetPanelDeviceCard(peer);
                    NetPanelDeviceList.Children.Add(deviceCard);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Device refresh error: {ex.Message}");
            }
        }

        /// <summary>Refreshes the file queue section.</summary>
        public void RefreshQueue()
        {
            try
            {
                if (NetPanelFileList == null) return;
                NetPanelFileList.Children.Clear();

                var queue = NetworkFileQueue.Instance?.StagedFiles;
                if (queue == null || queue.Count == 0)
                {
                    NetPanelQueueStatus.Text = "No files staged";
                    if (NetPanelEmptyState != null) NetPanelEmptyState.Visibility = Visibility.Visible;
                    return;
                }

                if (NetPanelEmptyState != null) NetPanelEmptyState.Visibility = Visibility.Collapsed;
                NetPanelQueueStatus.Text = $"{queue.Count} file{(queue.Count != 1 ? "s" : "")} staged";

                foreach (var file in queue)
                {
                    var fileCard = CreateNetPanelFileCard(file);
                    NetPanelFileList.Children.Add(fileCard);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Queue refresh error: {ex.Message}");
            }
        }

        /// <summary>Refreshes the active + recent transfer session cards in the networking panel.</summary>
        public void RefreshTransfers()
        {
            try
            {
                if (NetPanelActiveTransfers == null || NetPanelRecentTransfers == null) return;
                NetPanelActiveTransfers.Children.Clear();
                NetPanelRecentTransfers.Children.Clear();

                var mgr = LanTransferManager.Instance;
                if (mgr == null) return;

                // Active transfers with section header
                var activeList = mgr.ActiveTransfers.ToList();
                bool rebuildActive = NetPanelActiveTransfers.Children.Count != (activeList.Count == 0 ? 0 : activeList.Count + 1);

                if (rebuildActive)
                {
                    NetPanelActiveTransfers.Children.Clear();
                    if (activeList.Count > 0)
                    {
                        var header = new TextBlock
                        {
                            Text = $"ACTIVE TRANSFERS ({activeList.Count})",
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(ThemeColors.IndigoMid),
                            Margin = new Thickness(4, 8, 0, 4)
                        };
                        NetPanelActiveTransfers.Children.Add(header);
                        foreach (var session in activeList)
                        {
                            var card = CreateTransferSessionCard(session, isActive: true);
                            NetPanelActiveTransfers.Children.Add(card);
                        }
                    }
                }
                else if (activeList.Count > 0)
                {
                    for (int i = 0; i < activeList.Count; i++)
                    {
                        var session = activeList[i];
                        if (NetPanelActiveTransfers.Children[i + 1] is Border card && card.Child is StackPanel outerStack)
                        {
                            if (outerStack.Children[0] is Grid contentGrid && contentGrid.Children[1] is StackPanel infoStack && infoStack.Children.Count > 2 && infoStack.Children[2] is TextBlock statusText)
                            {
                                string statusStr;
                                if (session.IsActive) statusStr = $"{session.ProgressText}  •  {session.SpeedText}  •  {session.EtaText}";
                                else if (session.IsPaused) statusStr = $"⏸ Paused at {LanTransferSession.FormatBytes(session.BytesTransferred)} / {LanTransferSession.FormatBytes(session.FileSize)}";
                                else if (session.IsFailed) statusStr = $"❌ {session.ErrorMessage ?? "Failed"} — {LanTransferSession.FormatBytes(session.BytesTransferred)} saved";
                                else if (session.IsCompleted) statusStr = $"✅ {LanTransferSession.FormatBytes(session.FileSize)} — {session.PeakSpeedText} peak";
                                else statusStr = session.StateDisplayText;

                                statusText.Text = statusStr;
                            }
                            if (outerStack.Children.Count > 1 && outerStack.Children[1] is ProgressBar pb)
                            {
                                pb.Value = session.ProgressPercent;
                            }
                        }
                    }
                }

                // Recent/completed transfers with section header
                var recentList = mgr.CompletedTransfers.Take(10).ToList();
                bool rebuildRecent = NetPanelRecentTransfers.Children.Count != (recentList.Count == 0 ? 0 : recentList.Count + 1);

                if (rebuildRecent)
                {
                    NetPanelRecentTransfers.Children.Clear();
                    if (recentList.Count > 0)
                    {
                        var header = new TextBlock
                        {
                            Text = "RECENT TRANSFERS",
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(ThemeColors.SlateDark),
                            Margin = new Thickness(4, 8, 0, 4)
                        };
                        NetPanelRecentTransfers.Children.Add(header);
                        foreach (var session in recentList)
                        {
                            var card = CreateTransferSessionCard(session, isActive: false);
                            NetPanelRecentTransfers.Children.Add(card);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Transfer refresh error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // PRIVATE — CARD BUILDERS
        // ═══════════════════════════════════════════════════════════

        /// <summary>Creates a compact horizontal chip for the devices strip.</summary>
        private Border CreateNetPanelDeviceCard(PeerConnection peer)
        {
            var aliveDot = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ThemeColors.SuccessGreen),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var nameText = new TextBlock
            {
                Text = peer.DeviceName ?? peer.DeviceId,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var transportLabel = new TextBlock
            {
                Text = peer.Transport == "LAN" ? "LAN" : "CF",
                FontSize = 9,
                Foreground = new SolidColorBrush(ThemeColors.IndigoLight),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                FontWeight = FontWeights.Bold
            };

            var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
            chipContent.Children.Add(aliveDot);
            chipContent.Children.Add(nameText);
            chipContent.Children.Add(transportLabel);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(ThemeColors.NetworkCardBg),
                BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = chipContent,
                Tag = peer
            };

            chip.MouseEnter += (s, e) =>
            {
                chip.Background = new SolidColorBrush(ThemeColors.NavyDark);
                chip.BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep);
            };
            chip.MouseLeave += (s, e) =>
            {
                chip.Background = new SolidColorBrush(ThemeColors.NetworkCardBg);
                chip.BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder);
            };

            return chip;
        }

        private Border CreateTransferSessionCard(LanTransferSession session, bool isActive)
        {
            // Direction icon + file name
            var directionIcon = new TextBlock
            {
                Text = session.StateIcon,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var nameText = new TextBlock
            {
                Text = session.FileName,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(ThemeColors.LightSlate),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // Status line: progress + speed or error
            string statusStr;
            if (isActive && session.IsActive)
                statusStr = $"{session.ProgressText}  •  {session.SpeedText}  •  {session.EtaText}";
            else if (session.IsPaused)
                statusStr = $"⏸ Paused at {LanTransferSession.FormatBytes(session.BytesTransferred)} / {LanTransferSession.FormatBytes(session.FileSize)}";
            else if (session.IsFailed)
                statusStr = $"❌ {session.ErrorMessage ?? "Failed"} — {LanTransferSession.FormatBytes(session.BytesTransferred)} saved";
            else if (session.IsCompleted)
                statusStr = $"✅ {LanTransferSession.FormatBytes(session.FileSize)} — {session.PeakSpeedText} peak";
            else
                statusStr = session.StateDisplayText;

            var statusText = new TextBlock
            {
                Text = statusStr,
                FontSize = 10,
                Foreground = new SolidColorBrush(session.IsFailed
                    ? Color.FromRgb(0xF8, 0x71, 0x71)
                    : ThemeColors.SlateGray),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var peerText = new TextBlock
            {
                Text = $"{session.DirectionText} {session.PeerDeviceName}",
                FontSize = 10,
                Foreground = new SolidColorBrush(ThemeColors.SlateDark)
            };

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(nameText);
            infoStack.Children.Add(peerText);
            infoStack.Children.Add(statusText);

            // Action buttons
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (session.CanPause)
            {
                var pauseBtn = CreateTransferActionButton("⏸", "Pause", ThemeColors.WarningAmber);
                pauseBtn.MouseLeftButtonDown += async (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        var mgr = LanTransferManager.Instance;
                        if (mgr == null) return;
                        await mgr.PauseTransfer(session.TransferId);
                        RefreshTransfers();
                    }
                    catch (Exception ex) { Logger.LogAction("NETWORK", $"Pause error: {ex.Message}"); }
                };
                buttonStack.Children.Add(pauseBtn);
            }

            if (session.CanResume)
            {
                var resumeBtn = CreateTransferActionButton("▶", "Resume", ThemeColors.SuccessGreen);
                resumeBtn.MouseLeftButtonDown += async (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        var mgr = LanTransferManager.Instance;
                        if (mgr == null) return;
                        await mgr.ResumeTransfer(session.TransferId);
                        RefreshTransfers();
                    }
                    catch (Exception ex) { Logger.LogAction("NETWORK", $"Resume error: {ex.Message}"); }
                };
                buttonStack.Children.Add(resumeBtn);
            }

            if (session.CanRetry)
            {
                var retryBtn = CreateTransferActionButton("↺", "Retry", ThemeColors.IndigoLight);
                retryBtn.MouseLeftButtonDown += async (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        var mgr = LanTransferManager.Instance;
                        if (mgr == null) return;
                        await mgr.RetryTransfer(session.TransferId);
                        RefreshTransfers();
                    }
                    catch (Exception ex) { Logger.LogAction("NETWORK", $"Retry error: {ex.Message}"); }
                };
                buttonStack.Children.Add(retryBtn);
            }

            if (session.CanCancel)
            {
                var cancelBtn = CreateTransferActionButton("✕", "Cancel", ThemeColors.ErrorRed);
                cancelBtn.MouseLeftButtonDown += async (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        var mgr = LanTransferManager.Instance;
                        if (mgr == null) return;
                        await mgr.CancelTransfer(session.TransferId);
                        RefreshTransfers();
                    }
                    catch (Exception ex) { Logger.LogAction("NETWORK", $"Cancel error: {ex.Message}"); }
                };
                buttonStack.Children.Add(cancelBtn);
            }

            // Layout
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(directionIcon, 0);
            Grid.SetColumn(infoStack, 1);
            Grid.SetColumn(buttonStack, 2);
            contentGrid.Children.Add(directionIcon);
            contentGrid.Children.Add(infoStack);
            contentGrid.Children.Add(buttonStack);

            // Progress bar for active transfers
            var outerStack = new StackPanel();
            outerStack.Children.Add(contentGrid);

            if (isActive && (session.IsActive || session.IsPaused))
            {
                var progressBar = new ProgressBar
                {
                    Value = session.ProgressPercent,
                    Minimum = 0,
                    Maximum = 100,
                    Height = 3,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = new SolidColorBrush(session.IsPaused
                        ? ThemeColors.WarningAmber
                        : ThemeColors.IndigoMid),
                    Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0)
                };
                outerStack.Children.Add(progressBar);
            }

            // Card border color based on state
            Color borderColor = session.IsFailed
                ? Color.FromRgb(0x7F, 0x1D, 0x1D) // Red border for failed
                : session.IsPaused
                    ? Color.FromRgb(0x78, 0x35, 0x0F) // Amber border for paused
                    : ThemeColors.NetworkCardBorder; // Default

            Color bgColor = session.IsFailed
                ? Color.FromRgb(0x1A, 0x0A, 0x0A) // Dark red bg for failed
                : ThemeColors.NetworkCardBg; // Default

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Background = new SolidColorBrush(bgColor),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1),
                Child = outerStack
            };

            card.MouseEnter += (s, e) =>
            {
                card.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x33));
                card.BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep);
            };
            card.MouseLeave += (s, e) =>
            {
                card.Background = new SolidColorBrush(bgColor);
                card.BorderBrush = new SolidColorBrush(borderColor);
            };

            return card;
        }

        private Border CreateTransferActionButton(string icon, string tooltip, Color color)
        {
            var btn = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x20, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = new TextBlock
                {
                    Text = icon,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };

            btn.MouseEnter += (s, e) =>
                btn.Background = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
            btn.MouseLeave += (s, e) =>
                btn.Background = new SolidColorBrush(Color.FromArgb(0x20, color.R, color.G, color.B));

            return btn;
        }

        private Border CreateNetPanelFileCard(StagedFile file)
        {
            var nameText = new TextBlock
            {
                Text = file.FileName ?? Path.GetFileName(file.FilePath),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(ThemeColors.LightSlate),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var sizeText = new TextBlock
            {
                Text = file.FileSizeText ?? $"{file.FileSize / 1024.0 / 1024.0:F1} MB",
                FontSize = 10,
                Foreground = new SolidColorBrush(ThemeColors.SlateGray)
            };

            var statusText = new TextBlock
            {
                Text = file.StatusIcon ?? "⏳",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var removeBtn = new TextBlock
            {
                Text = "✕",
                FontSize = 11,
                Foreground = new SolidColorBrush(ThemeColors.SlateGray),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            removeBtn.MouseLeftButtonDown += (s, e) =>
            {
                NetworkFileQueue.Instance?.Remove(file);
                RefreshQueue();
            };

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(nameText);
            infoStack.Children.Add(sizeText);

            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(statusText, 0);
            Grid.SetColumn(infoStack, 1);
            Grid.SetColumn(removeBtn, 2);
            contentGrid.Children.Add(statusText);
            contentGrid.Children.Add(infoStack);
            contentGrid.Children.Add(removeBtn);

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Background = new SolidColorBrush(ThemeColors.NetworkCardBg),
                BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                BorderThickness = new Thickness(1),
                Child = contentGrid
            };

            card.MouseEnter += (s, e) =>
            {
                card.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x33));
                card.BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep);
            };
            card.MouseLeave += (s, e) =>
            {
                card.Background = new SolidColorBrush(ThemeColors.NetworkCardBg);
                card.BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder);
            };

            return card;
        }

        private Border CreateNearbyDeviceChip(NearbyDeviceInfo device)
        {
            var statusDot = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(device.IsConnected
                    ? ThemeColors.SuccessGreen
                    : ThemeColors.WarningAmber),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var nameText = new TextBlock
            {
                Text = device.DeviceName,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 100,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var latencyText = new TextBlock
            {
                Text = device.LatencyMs > 0 ? $"{device.LatencyMs}ms" : "?",
                FontSize = 9,
                Foreground = new SolidColorBrush(ThemeColors.SlateGray),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var connectBtn = new TextBlock
            {
                Text = device.IsConnected ? "✓" : "＋",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(device.IsConnected
                    ? ThemeColors.SuccessGreen
                    : ThemeColors.IndigoLight),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };

            if (!device.IsConnected)
            {
                connectBtn.MouseLeftButtonDown += async (s, e) =>
                {
                    e.Handled = true;
                    connectBtn.Text = "";
                    try
                    {
                        await (NearbyDiscovery.Instance?.ConnectToDevice(device) ?? Task.CompletedTask);
                        if (device.IsConnected)
                        {
                            connectBtn.Text = "✓";
                            connectBtn.Foreground = new SolidColorBrush(ThemeColors.SuccessGreen);
                            statusDot.Background = new SolidColorBrush(ThemeColors.SuccessGreen);
                            ToastWindow.ShowToast($"Connected to {device.DeviceName}");
                            RefreshDevices();
                        }
                        else
                        {
                            connectBtn.Text = "✕";
                            connectBtn.Foreground = new SolidColorBrush(ThemeColors.ErrorRed);
                            ToastWindow.ShowToast($"Could not reach {device.DeviceName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        connectBtn.Text = "✕";
                        Logger.LogAction("NETWORK", $"Connect nearby error: {ex.Message}");
                    }
                };
            }

            var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
            chipContent.Children.Add(statusDot);
            chipContent.Children.Add(nameText);
            chipContent.Children.Add(latencyText);
            chipContent.Children.Add(connectBtn);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(ThemeColors.NetworkCardBg),
                BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = chipContent,
                Tag = device
            };

            chip.MouseEnter += (s, e) =>
            {
                chip.Background = new SolidColorBrush(ThemeColors.NavyDark);
                chip.BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep);
            };
            chip.MouseLeave += (s, e) =>
            {
                chip.Background = new SolidColorBrush(ThemeColors.NetworkCardBg);
                chip.BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder);
            };

            return chip;
        }

        // ═══════════════════════════════════════════════════════════
        // PRIVATE — NEARBY DEVICES REFRESH
        // ═══════════════════════════════════════════════════════════

        private void RefreshNearbyDevices()
        {
            try
            {
                if (NetPanelNearbyList == null) return;
                NetPanelNearbyList.Children.Clear();

                var nearby = NearbyDiscovery.Instance?.DiscoveredDevices?.ToList();
                if (nearby == null || nearby.Count == 0)
                {
                    var emptyChip = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 6, 12, 6),
                        Background = new SolidColorBrush(ThemeColors.NetworkCardBg),
                        BorderBrush = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text = "No nearby devices found — click Scan",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(ThemeColors.SlateDark)
                        }
                    };
                    NetPanelNearbyList.Children.Add(emptyChip);
                    return;
                }

                foreach (var dev in nearby)
                {
                    var chip = CreateNearbyDeviceChip(dev);
                    NetPanelNearbyList.Children.Add(chip);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Nearby refresh error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // EVENT HANDLERS — wired from XAML
        // ═══════════════════════════════════════════════════════════

        private void ResearchBack_Click(object sender, MouseButtonEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void NetPanel_AddFiles_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Multiselect = true,
                    Title = "Select files to send"
                };
                if (dialog.ShowDialog() == true)
                {
                    NetworkFileQueue.Instance?.StageFiles(dialog.FileNames);
                    RefreshQueue();
                    ToastWindow.ShowToast($"{dialog.FileNames.Length} file(s) added to queue");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Add files error: {ex.Message}");
            }
        }

        private async void NetPanel_SendAll_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                if (NetworkFileQueue.Instance == null || NetworkFileQueue.Instance.StagedFiles.Count == 0)
                {
                    ToastWindow.ShowToast("No files in queue to send");
                    return;
                }

                int peerCount = PeerManager.Instance?.AliveCount ?? 0;
                if (peerCount == 0)
                {
                    ToastWindow.ShowToast("No connected devices to send to");
                    return;
                }

                ToastWindow.ShowToast($"Sending {NetworkFileQueue.Instance.StagedFiles.Count} file(s) to {peerCount} device(s)...");
                await NetworkFileQueue.Instance.SendAllToAll();
                RefreshQueue();
                ToastWindow.ShowToast("All files sent!");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Send all error: {ex.Message}");
                ToastWindow.ShowToast($"Send failed: {ex.Message}");
            }
        }

        private void NetPanel_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"DragOver error: {ex.Message}");
            }
        }

        private void NetPanel_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        NetworkFileQueue.Instance?.StageFiles(files);
                        RefreshQueue();
                        ToastWindow.ShowToast($"{files.Length} file(s) added to queue");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Drop error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // FIX 1: ⋯ MENU LEFT-CLICK HANDLER
        // ═══════════════════════════════════════════════════════════

        private void NetPanel_MoreMenu_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                fe.ContextMenu.IsOpen = true;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // CONTEXT MENU HANDLERS (MenuItem Click uses RoutedEventArgs)
        // ═══════════════════════════════════════════════════════════

        private void NetPanel_RefreshDevices_MenuClick(object sender, RoutedEventArgs e)
        {
            RefreshDevices();
            ToastWindow.ShowToast("Devices refreshed");
        }

        private void NetPanel_OpenTransferMgr_MenuClick(object sender, RoutedEventArgs e)
        {
            OpenTransferManagerRequested?.Invoke(this, EventArgs.Empty);
        }

        private void NetPanel_ClearQueue_MenuClick(object sender, RoutedEventArgs e)
        {
            NetworkFileQueue.Instance?.ClearAll();
            RefreshQueue();
            ToastWindow.ShowToast("Queue cleared");
        }

        // ═══════════════════════════════════════════════════════════
        // FIX 4: NEARBY DEVICES + SCAN
        // ═══════════════════════════════════════════════════════════

        private async void NetPanel_Scan_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToastWindow.ShowToast("Scanning for nearby devices...");
            try
            {
                NearbyDiscovery.Instance?.PruneStale();
                await (NearbyDiscovery.Instance?.BroadcastProbe() ?? Task.CompletedTask);

                // Also register alive PeerManager peers as nearby (they may be
                // connected via HTTP polling or WebSocket but not UDP broadcast)
                if (PeerManager.Instance != null && NearbyDiscovery.Instance != null)
                {
                    foreach (var peer in PeerManager.Instance.ConnectedPeers.Values.Where(p => p.IsAlive))
                    {
                        string peerIp = "";
                        if (!string.IsNullOrEmpty(peer.LanUrl))
                        {
                            try { peerIp = new Uri(peer.LanUrl).Host; } catch { }
                        }
                        if (!string.IsNullOrEmpty(peerIp))
                        {
                            NearbyDiscovery.Instance.RecordHttpDiscovery(
                                peer.DeviceId, peer.DeviceName, peerIp, peer.TransferPort, "PC");
                        }
                    }
                }

                // Wait a moment for responses to arrive
                await Task.Delay(1500);
                RefreshNearbyDevices();
                var count = NearbyDiscovery.Instance?.DiscoveredDevices?.Count ?? 0;
                ToastWindow.ShowToast($"Found {count} nearby device(s)");
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Scan error: {ex.Message}");
                ToastWindow.ShowToast($"Scan failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // FIX 5: PAIRING UI HANDLERS
        // ═══════════════════════════════════════════════════════════

        private async void NetPanel_Pair_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                // Generate pairing code if needed
                string code = DevicePairingManager.CurrentPairingCode;
                if (string.IsNullOrEmpty(code))
                {
                    ToastWindow.ShowToast("Generating pairing code...");
                    await DevicePairingManager.PublishPairingCode();
                    code = DevicePairingManager.CurrentPairingCode;
                }

                if (string.IsNullOrEmpty(code))
                {
                    ToastWindow.ShowToast("Could not generate pairing code");
                    return;
                }

                // Show pairing code inline in the header bar
                if (NetPanelPairingInfo != null && NetPanelPairingCodeText != null)
                {
                    NetPanelPairingCodeText.Text = code;
                    NetPanelPairingInfo.Visibility = Visibility.Visible;
                }

                // Build a WPF pairing dialog to enter ANOTHER device's code
                var parentWindow = Window.GetWindow(this);
                string? enteredCode = ShowPairingDialog(code, parentWindow);

                if (!string.IsNullOrEmpty(enteredCode) && enteredCode != code)
                {
                    ToastWindow.ShowToast($"Connecting with code {enteredCode}...");
                    var (success, deviceName) = await DevicePairingManager.ConnectByCode(enteredCode);
                    if (success)
                    {
                        ToastWindow.ShowToast($"Paired with {deviceName}!");
                        await Task.Delay(2000);
                        RefreshDevices();
                        // Hide pairing info after successful pair
                        if (NetPanelPairingInfo != null) NetPanelPairingInfo.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ToastWindow.ShowToast("Invalid code or device unreachable");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NETWORK", $"Pair error: {ex.Message}");
                ToastWindow.ShowToast($"Pairing failed: {ex.Message}");
            }
        }

        private void NetPanel_DismissPairing_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (NetPanelPairingInfo != null) NetPanelPairingInfo.Visibility = Visibility.Collapsed;
        }

        private string? ShowPairingDialog(string myCode, Window? owner)
        {
            var dlg = new Window
            {
                Title = "Device Pairing",
                Width = 380, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0F, 0x1A)),
                Foreground = Brushes.White
            };

            var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            // My Code label
            stack.Children.Add(new TextBlock
            {
                Text = "YOUR PAIRING CODE",
                FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ThemeColors.IndigoLight),
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Big code display
            var codeBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Background = new SolidColorBrush(ThemeColors.NavyDark),
                BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 16),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            codeBorder.Child = new TextBlock
            {
                Text = myCode,
                FontSize = 28, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xB4, 0xFC)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(codeBorder);

            // Share instruction
            stack.Children.Add(new TextBlock
            {
                Text = "Share this code with the other device",
                FontSize = 11, TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(ThemeColors.SlateGray),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Divider
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(ThemeColors.NetworkCardBorder),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Enter other code
            stack.Children.Add(new TextBlock
            {
                Text = "OR ENTER THE OTHER DEVICE'S CODE",
                FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ThemeColors.SuccessGreen),
                Margin = new Thickness(0, 0, 0, 6)
            });

            var inputBox = new TextBox
            {
                FontSize = 18, FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(ThemeColors.NetworkCardBg),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(ThemeColors.IndigoDeep),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                MaxLength = 10,
                Margin = new Thickness(0, 0, 0, 14)
            };
            stack.Children.Add(inputBox);

            // Connect button
            var connectBtn = new Button
            {
                Content = "Connect",
                FontSize = 13, FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 8, 20, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(ThemeColors.IndigoDeep),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };

            string? result = null;
            connectBtn.Click += (s, ev) =>
            {
                result = inputBox.Text?.Trim();
                dlg.DialogResult = true;
                dlg.Close();
            };
            stack.Children.Add(connectBtn);

            dlg.Content = stack;
            WindowHelper.ShowDialogInForeground(dlg, owner);
            return result;
        }
    }
}
