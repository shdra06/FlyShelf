// ---------------------------------------------------------------
// HubWindow.Settings — Device Send, Archive, Merge & Selection
// Split from HubWindow.Settings.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Classes;
using FlyShelf.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        internal void SendToDevice_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe) return;
            var item = fe.DataContext as ClipboardItem;
            if (item == null) return;

            // Determine the file to send
            string fileToSend = "";
            if (item.ItemType == ClipboardItemType.Group || item.ItemType == ClipboardItemType.Folder)
            {
                fileToSend = item.ZippedArchivePath;
                if (string.IsNullOrEmpty(fileToSend) || !System.IO.File.Exists(fileToSend))
                {
                    ToastWindow.ShowToast("⏳ Zip is still being prepared, try again in a moment...");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(item.FilePath) && System.IO.File.Exists(item.FilePath))
            {
                fileToSend = item.FilePath;
            }
            else
            {
                ToastWindow.ShowToast("❌ No file available to send");
                return;
            }

            // Build the device picker popup
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = fe,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var popupBorder = new Border
            {
                Background = (Brush)FindResource("MicaWPF.Brushes.ApplicationBackgroundFillColorBase"),
                BorderBrush = (Brush)FindResource("MicaWPF.Brushes.ControlStrokeColorDefault"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8),
                MinWidth = 200,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20, ShadowDepth = 4, Opacity = 0.3, Color = Colors.Black
                }
            };

            var stack = new StackPanel();
            var header = new TextBlock
            {
                Text = "📡 Send to Device",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary"),
                Margin = new Thickness(8, 4, 8, 8)
            };
            stack.Children.Add(header);

            // Get alive peers from PeerManager
            var peerManager = PeerManager.Instance;
            var peers = peerManager?.ConnectedPeers?.Values
                .Where(p => p.IsAlive)
                .ToList() ?? new List<Classes.PeerConnection>();

            if (peers.Count == 0)
            {
                var noPeers = new TextBlock
                {
                    Text = "No devices connected",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary"),
                    Margin = new Thickness(8, 4, 8, 8)
                };
                stack.Children.Add(noPeers);

                // Hint: Firebase is NOT used for content relay — P2P only
                var hintText = new TextBlock
                {
                    Text = "Ensure both devices are online\nfor direct P2P file transfer.",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary"),
                    Margin = new Thickness(8, 0, 8, 8),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic
                };
                stack.Children.Add(hintText);
            }
            else
            {
                foreach (var peer in peers)
                {
                    string deviceId = peer.DeviceId;
                    string deviceName = peer.DeviceName;
                    string transport = peer.Transport;

                    var deviceBtn = new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin = new Thickness(2),
                        Cursor = Cursors.Hand,
                        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
                    };
                    deviceBtn.MouseEnter += (s, args) =>
                        deviceBtn.Background = (Brush)FindResource("MicaWPF.Brushes.SubtleFillColorTertiary");
                    deviceBtn.MouseLeave += (s, args) =>
                        deviceBtn.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

                    var deviceStack = new StackPanel { Orientation = Orientation.Horizontal };

                    string typeEmoji = deviceId.StartsWith("Mobile_", StringComparison.OrdinalIgnoreCase) ? "📱" : "💻";
                    string transportEmoji = transport == "LAN" ? "📡" : "🌐";

                    deviceStack.Children.Add(new TextBlock
                    {
                        Text = typeEmoji,
                        FontSize = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    });

                    var nameStack = new StackPanel();
                    nameStack.Children.Add(new TextBlock
                    {
                        Text = deviceName,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary")
                    });
                    nameStack.Children.Add(new TextBlock
                    {
                        Text = $"{transportEmoji} {transport}",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary")
                    });
                    deviceStack.Children.Add(nameStack);
                    deviceBtn.Child = deviceStack;

                    string capturedDeviceId = deviceId;
                    string capturedDeviceName = deviceName;
                    string capturedFile = fileToSend;
                    var capturedItem = item;

                    deviceBtn.MouseLeftButtonDown += async (s, args) =>
                    {
                        popup.IsOpen = false;
                        ToastWindow.ShowToast($"📡 Sending {capturedItem.FileName} to {capturedDeviceName}...");

                        bool success = false;
                        try
                        {
                            success = await PeerManager.Instance.PushFileToSinglePeer(
                                capturedDeviceId, capturedFile, capturedItem.FileName, "Archive");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogAction("SEND_TO_DEVICE", $"P2P send failed: {ex.Message}");
                        }

                        if (success)
                        {
                            ToastWindow.ShowToast($"✅ Sent to {capturedDeviceName}!");
                        }
                        else
                        {
                            // P2P is the only transfer path — Firebase is never used for content relay
                            ToastWindow.ShowToast($"❌ Send failed — {capturedDeviceName} is unreachable. Ensure both devices are online.");
                        }
                    };

                    stack.Children.Add(deviceBtn);
                }
            }

            popupBorder.Child = stack;
            popup.Child = popupBorder;
            popup.IsOpen = true;
        }

        /// <summary>
        /// Context menu wrapper for Send to Device — extracts the ClipboardItem from MenuItem.DataContext
        /// </summary>
        internal void ContextMenu_SendToDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var item = fe.DataContext as ClipboardItem;
            if (item == null) return;

            // Close the context menu first
            if (fe is System.Windows.Controls.MenuItem mi && mi.Parent is System.Windows.Controls.ContextMenu cm)
                cm.IsOpen = false;

            // Reuse the SendToDevice_Click logic by creating a simulated event
            // We need to show the popup relative to the HubListView
            Dispatcher.InvokeAsync(() =>
            {
                var fakeArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = fe
                };
                SendToDevice_Click(fe, fakeArgs);
            });
        }

        /// <summary>
        /// Extract a .zip archive to a user-chosen folder
        /// </summary>
        internal void ExtractArchive_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var item = fe.DataContext as ClipboardItem;
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            try
            {
                string ext = System.IO.Path.GetExtension(item.FilePath).ToLowerInvariant();
                if (ext != ".zip" && ext != ".apk")
                {
                    ToastWindow.ShowToast("⚠️ Only .zip and .apk extraction is supported");
                    return;
                }

                // Extract to a subfolder next to the archive (or in Downloads if the archive is in a temp path)
                string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                string baseDir = System.IO.Path.GetDirectoryName(item.FilePath) ?? "";
                
                // If zip is in a temp folder, extract to Downloads instead
                string tempDir = System.IO.Path.GetTempPath();
                if (baseDir.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    baseDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                }

                string targetDir = System.IO.Path.Combine(baseDir, baseName);
                System.IO.Directory.CreateDirectory(targetDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(item.FilePath, targetDir, overwriteFiles: true);
                ToastWindow.ShowToast($"✅ Extracted to {targetDir}");
                System.Diagnostics.Process.Start("explorer.exe", targetDir);
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Extract failed: {ex.Message}");
                Logger.LogAction("EXTRACT", $"Archive extraction error: {ex}");
            }
        }

        internal void ExpandToggleSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item) { item.IsExpanded = !item.IsExpanded; }
            e.Handled = true;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool anyUnselected = _viewModel.DroppedItems.Any(i => !i.IsCheckedForMerge);
            foreach (var item in _viewModel.DroppedItems) { item.IsCheckedForMerge = anyUnselected; }
            UpdateMergeButton();
        }

        private void HubListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMergeButton();

        internal void ItemCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => UpdateMergeButton(), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateMergeButton()
        {
            if (_viewModel == null || MergePdfFloatingBar == null) return;
            var checkedPdfs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedDocs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedImages = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            int totalChecked = checkedPdfs.Count + checkedDocs.Count + checkedImages.Count;

            if (totalChecked >= 2 || (checkedDocs.Count == 1 && checkedPdfs.Count == 0 && checkedImages.Count == 0))
            {
                MergePdfFloatingBar.Visibility = Visibility.Visible;
                if (checkedImages.Count > 0 && checkedPdfs.Count == 0 && checkedDocs.Count == 0)
                { MergeBarText.Text = $"{checkedImages.Count} Images selected"; MergeBarBtn.Content = "Merge Images"; MergeBarBtn.ToolTip = $"Merge {checkedImages.Count} images into a single PDF"; }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedImages.Count == 0 && checkedDocs.Count == 1)
                { MergeBarText.Text = "1 DOC selected"; MergeBarBtn.Content = "Convert to PDF"; MergeBarBtn.ToolTip = "Convert DOC/DOCX to PDF"; }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedImages.Count == 0)
                { MergeBarText.Text = $"{checkedDocs.Count} DOCs selected"; MergeBarBtn.Content = "Convert DOCs"; MergeBarBtn.ToolTip = $"Convert {checkedDocs.Count} DOC files to PDF"; }
                else
                { MergeBarText.Text = $"{totalChecked} Files selected"; MergeBarBtn.Content = "Merge Files"; MergeBarBtn.ToolTip = $"Convert & merge all {totalChecked} files"; }
            }
            else { MergePdfFloatingBar.Visibility = Visibility.Collapsed; }
        }

        private async void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            var checkedPdfs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedDocs = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var checkedImages = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath)).ToList();
            var convertedPdfPaths = new List<string>();

            if (checkedDocs.Count > 0)
            {
                ToastWindow.ShowToast($"📄 Converting {checkedDocs.Count} DOC file(s) to PDF...");
                foreach (var doc in checkedDocs)
                {
                    string pdfPath = await ConversionUtils.ConvertDocToPdfAsync(doc.FilePath);
                    if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath)) convertedPdfPaths.Add(pdfPath);
                    else ToastWindow.ShowToast($"❌ Failed to convert: {doc.FileName}");
                }
            }

            if (checkedImages.Count > 0)
            {
                ToastWindow.ShowToast($"🖼️ Formatting {checkedImages.Count} image(s) to PDF...");
                foreach (var img in checkedImages)
                {
                    try
                    {
                        string pdfPath = await System.Threading.Tasks.Task.Run(() => ConversionUtils.ConvertImageToPdf(img.FilePath));
                        if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath)) convertedPdfPaths.Add(pdfPath);
                    }
                    catch (Exception ex) { ToastWindow.ShowToast($"❌ Failed to format: {img.FileName}"); Logger.LogAction("IMAGE2PDF_ERR", ex.ToString()); }
                }
            }

            if (checkedPdfs.Count == 0 && checkedDocs.Count + checkedImages.Count == convertedPdfPaths.Count && convertedPdfPaths.Count == 1)
            {
                foreach (var item in _viewModel.DroppedItems) item.IsCheckedForMerge = false;
                UpdateMergeButton();
                var newItem = new ClipboardItem(convertedPdfPaths[0]);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                ToastWindow.ShowToast("✅ Converted to PDF");
                return;
            }

            var allPdfs = new List<ClipboardItem>();
            allPdfs.AddRange(checkedPdfs);
            foreach (string path in convertedPdfPaths) allPdfs.Add(new ClipboardItem(path));

            if (allPdfs.Count > 1)
            {
                foreach (var item in _viewModel.DroppedItems) item.IsCheckedForMerge = false;
                UpdateMergeButton();
                var win = new PdfMergeWindow(allPdfs, _viewModel);
                App.ActiveMergeWindow = win;
                win.Closed += (_, __) => App.ActiveMergeWindow = null;
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                win.Topmost = true; win.Show(); win.Activate(); win.Focus(); win.Topmost = false;
            }
            else if (allPdfs.Count == 1)
            {
                foreach (var item in _viewModel.DroppedItems) item.IsCheckedForMerge = false;
                UpdateMergeButton();
                var newItem = new ClipboardItem(allPdfs[0].FilePath);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                ToastWindow.ShowToast("✅ PDF added to clipboard");
            }
            else { ToastWindow.ShowToast("Select 2+ files to merge, or 1 image/doc to convert."); }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_coastPrefetchHandler != null) { Classes.SmoothScrollPCApp.CoastPrefetchNeeded -= _coastPrefetchHandler; _coastPrefetchHandler = null; }
            if (_devicePairedHandler != null) { DevicePairingManager.OnDevicePaired -= _devicePairedHandler; _devicePairedHandler = null; }
            if (_viewModel?.DroppedItems != null) { _viewModel.DroppedItems.CollectionChanged -= DroppedItems_CollectionChanged; }
            if (_deviceRefreshTimer != null) { _deviceRefreshTimer.Stop(); _deviceRefreshTimer = null; }
            if (_hubScrollHighQualityTimer != null) { _hubScrollHighQualityTimer.Stop(); _hubScrollHighQualityTimer = null; }
        }
    }
}
