// ---------------------------------------------------------------
// MainWindow � Mouse Interactions & Advanced Features
// MouseClick, DragDrop, ForceSend, OpenApp, Selection,
// PDF Merge, Card Hover Preview
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using AdvanceClip.ViewModels;
using AdvanceClip.Classes;
using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdvanceClip
{
    public partial class MainWindow
    {
        private async void ShelfListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_didDragOut)
            {
                _didDragOut = false;
                e.Handled = true;
                return;
            }

            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 300)
            {
                e.Handled = true;
                return;
            }

            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                return;
            }

            if (e.OriginalSource is DependencyObject sourceElement)
            {
                // PDF merge toggle: already handled in PreviewMouseLeftButtonDown
                if (HasAncestorTag(sourceElement, "PdfMergeToggle"))
                {
                    e.Handled = true;
                    return;
                }

                // Other buttons (delete, pin, etc.)
                if (sourceElement is System.Windows.Controls.Primitives.ButtonBase ||
                    FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement) != null)
                {
                    return; 
                }
            }

            var listView = sender as System.Windows.Controls.ListView;
            if (listView == null) return;
            var itemContainer2 = System.Windows.Controls.ItemsControl.ContainerFromElement(listView, e.OriginalSource as DependencyObject) as System.Windows.Controls.ListViewItem;
            
            if (itemContainer2 != null)
            {
                var clipboardObj = itemContainer2.DataContext as ClipboardItem;
                if (clipboardObj != null)
                {
                    _ = CopyItemAndPaste(clipboardObj, hideWindow: true);
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Copies a ClipboardItem to the system clipboard, optionally hides the shelf,
        /// restores focus to the previous window, and simulates Ctrl+V.
        /// Reused by: mouse click, Enter key, and Alt+N global hotkeys.
        /// </summary>
        private async System.Threading.Tasks.Task CopyItemAndPaste(ClipboardItem clipboardObj, bool hideWindow)
        {
            try
            {
                // Use the safety-timer version so the flag stays true until the clipboard
                // change notification has been processed (prevents duplicate-to-top reorder)
                SetWritingClipboard(true);

                if (!string.IsNullOrEmpty(clipboardObj.FilePath))
                {
                    var dataObj = new DataObject();
                    
                    var dropList = new System.Collections.Specialized.StringCollection();
                    dropList.Add(clipboardObj.FilePath);
                    dataObj.SetFileDropList(dropList);
                    dataObj.SetData(DataFormats.StringFormat, clipboardObj.FilePath);
                    dataObj.SetData(DataFormats.Text, clipboardObj.FilePath);
                    dataObj.SetData("FileNameW", new string[] { clipboardObj.FilePath });
                    dataObj.SetData("FileName", new string[] { clipboardObj.FilePath });
                    try { dataObj.SetData("text/uri-list", "file:///" + clipboardObj.FilePath.Replace("\\", "/")); } catch { }
                    
                    if (clipboardObj.ItemType == ClipboardItemType.Image)
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(clipboardObj.FilePath);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            dataObj.SetImage(bmp);
                        }
                        catch { }
                    }
                    
                    byte[] moveEffect = new byte[] { 5, 0, 0, 0 };
                    System.IO.MemoryStream dropEffect = new System.IO.MemoryStream();
                    dropEffect.Write(moveEffect, 0, moveEffect.Length);
                    dataObj.SetData("Preferred DropEffect", dropEffect);

                    for(int retry=0; retry<3; retry++) {
                        try { System.Windows.Clipboard.SetDataObject(dataObj, true); break; }
                        catch { await System.Threading.Tasks.Task.Delay(15); }
                    }
                }
                else if (!string.IsNullOrEmpty(clipboardObj.RawContent))
                {
                    for(int retry=0; retry<3; retry++) {
                        try { System.Windows.Clipboard.SetText(clipboardObj.RawContent); break; }
                        catch { await System.Threading.Tasks.Task.Delay(15); }
                    }
                }
            }
            catch { }

            if (hideWindow)
            {
                AnimateAndHide();
                _isDragHovering = false;
                IsDragHovering = false;
            }

            // Minimal delay — just enough for the target window to receive focus
            await System.Threading.Tasks.Task.Delay(hideWindow ? 80 : 30);

            if (_previousForegroundWindow != IntPtr.Zero)
            {
                var sbTitle = new System.Text.StringBuilder(256);
                GetWindowText(_previousForegroundWindow, sbTitle, 256);
                string contextTitle = sbTitle.ToString();
                
                if (!string.IsNullOrWhiteSpace(contextTitle))
                {
                    clipboardObj.AssociatedContextTitle = contextTitle;
                }
                
                SetForegroundWindow(_previousForegroundWindow);
                await System.Threading.Tasks.Task.Delay(50);
                
                if (GetForegroundWindow() != _previousForegroundWindow)
                {
                    SetForegroundWindow(_previousForegroundWindow);
                    await System.Threading.Tasks.Task.Delay(30);
                }
            }

            keybd_event(VK_CONTROL, 0, 0, 0);
            keybd_event(VK_V, 0, 0, 0);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
        }

        private void ShelfListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _didDragOut = false;

            if (e.OriginalSource is DependencyObject sourceElement)
            {
                // PDF merge toggle: toggle state here and fully consume
                if (HasAncestorTag(sourceElement, "PdfMergeToggle"))
                {
                    // Debounce: ignore rapid-fire from held mouse button
                    if ((DateTime.Now - _lastMergeToggleTime).TotalMilliseconds > 300)
                    {
                        _lastMergeToggleTime = DateTime.Now;
                        var toggleContainer = ItemsControl.ContainerFromElement(ShelfListView, sourceElement) as ListViewItem;
                        if (toggleContainer?.DataContext is ClipboardItem item)
                        {
                            item.IsCheckedForMerge = !item.IsCheckedForMerge;
                            UpdatePdfMergeToolbar();
                        }
                    }
                    e.Handled = true;
                    return;
                }

                // Don't interfere with other button clicks
                if (sourceElement is System.Windows.Controls.Primitives.ButtonBase ||
                    FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement) != null)
                    return;

                var itemContainer = ItemsControl.ContainerFromElement(ShelfListView, sourceElement) as ListViewItem;
                if (itemContainer != null && itemContainer.DataContext is ClipboardItem)
                {
                    if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl) &&
                        !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
                    {
                        ShelfListView.SelectedItems.Clear();
                    }
                    itemContainer.IsSelected = true;
                }
            }
        }

        private void ShelfListView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point position = e.GetPosition(null);
                Vector diff = _dragStartPoint - position;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (ShelfListView.SelectedItems.Count > 0)
                    {
                        var firstItem = ShelfListView.SelectedItems.Cast<ClipboardItem>().FirstOrDefault();
                        if (firstItem == null) return;

                        DataObject dataObj = new DataObject();
                        if (!string.IsNullOrEmpty(firstItem.FilePath))
                        {
                            dataObj.SetData(DataFormats.FileDrop, new string[] { firstItem.FilePath });
                            dataObj.SetData("FileNameW", new string[] { firstItem.FilePath });
                            dataObj.SetData("FileName", new string[] { firstItem.FilePath });
                            try { dataObj.SetData("text/uri-list", "file:///" + firstItem.FilePath.Replace("\\", "/")); } catch { }

                            if (firstItem.ItemType == ClipboardItemType.Image)
                            {
                                try
                                {
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.UriSource = new Uri(firstItem.FilePath);
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.EndInit();
                                    dataObj.SetImage(bmp);
                                }
                                catch { }
                            }

                            // Explicit Win32 Shell 'Copy' Effect override (Required for Windows Explorer Drag Drop)
                            byte[] moveEffect = new byte[] { 5, 0, 0, 0 }; // DragDropEffects.Copy
                            System.IO.MemoryStream dropEffect = new System.IO.MemoryStream();
                            dropEffect.Write(moveEffect, 0, moveEffect.Length);
                            dataObj.SetData("Preferred DropEffect", dropEffect);
                        }
                        else 
                        {
                            dataObj.SetData(DataFormats.UnicodeText, firstItem.RawContent);
                        }
                        
                        _isInternalDragSource = true;
                        _didDragOut = true;
                        try
                        {
                            DragDropEffects result = DragDrop.DoDragDrop(ShelfListView, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
                            
                            // Items remain persistent on the shelf after drag-out
                        }
                        catch (Exception ex)
                        {
                            AdvanceClip.Classes.Logger.LogAction("DRAG OUT FAULT", $"Failed UI Export: {ex.Message}");
                        }
                        finally
                        {
                            _isInternalDragSource = false;
                        }
                    }
                }
            }
        }

        private void ShelfListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ShelfListView.SelectedItem is ClipboardItem item)
            {
                item.Execute();
            }
        }

        private Windows.HubWindow? _hubWindowInstance;

        private async void ForceSendItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as System.Windows.Controls.MenuItem;
                var clipItem = menuItem?.Tag as ClipboardItem;
                if (clipItem == null) return;

                // Fetch active devices
                var devices = await AdvanceClip.Classes.FirebaseSyncManager.GetActiveDevices();
                if (devices.Count == 0)
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast("No other devices found online ⚠️");
                    return;
                }

                // Build device picker dialog
                var dialog = new System.Windows.Window
                {
                    Title = "⚡ Force Send To",
                    Width = 340, Height = 300,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 30, 38)),
                    Foreground = System.Windows.Media.Brushes.White,
                };

                var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
                stack.Children.Add(new System.Windows.Controls.TextBlock 
                { 
                    Text = $"Send \"{(clipItem.FileName ?? clipItem.RawContent ?? "item").Substring(0, Math.Min(40, (clipItem.FileName ?? clipItem.RawContent ?? "item").Length))}\" to:",
                    FontWeight = FontWeights.Bold, FontSize = 14, Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
                });

                // Send to ALL button
                var allBtn = new System.Windows.Controls.Button
                {
                    Content = $"Send to ALL Devices ({devices.Count})",
                    Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 98, 235)),
                    Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0),
                };
                allBtn.Click += async (s2, e2) =>
                {
                    dialog.Close();
                    var allIds = devices.Select(d => d.Id).ToList();
                    int count = await AdvanceClip.Classes.FirebaseSyncManager.ForceSendToDevices(
                        new List<ClipboardItem> { clipItem }, allIds);
                    AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ Force sent to {count} device(s)");
                };
                stack.Children.Add(allBtn);

                // Individual device buttons
                foreach (var dev in devices)
                {
                    string emoji = dev.Type == "PC" ? "💻" : "📱";
                    var btn = new System.Windows.Controls.Button
                    {
                        Content = $"{emoji} {dev.Name} ({(dev.IsOnline ? "Online" : "Offline")})",
                        Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4),
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 47, 58)),
                        Foreground = System.Windows.Media.Brushes.White,
                        BorderThickness = new Thickness(0),
                        Tag = dev.Id
                    };
                    btn.Click += async (s3, e3) =>
                    {
                        dialog.Close();
                        string targetId = (s3 as System.Windows.Controls.Button)?.Tag?.ToString() ?? "";
                        int count = await AdvanceClip.Classes.FirebaseSyncManager.ForceSendToDevices(
                            new List<ClipboardItem> { clipItem }, new List<string> { targetId });
                        AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ Force sent ({count} item)");
                    };
                    stack.Children.Add(btn);
                }

                var scroll = new System.Windows.Controls.ScrollViewer { Content = stack, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
                dialog.Content = scroll;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast($"Force Send Error: {ex.Message}");
            }
        }

        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_hubWindowInstance != null && _hubWindowInstance.IsLoaded)
                {
                    // If the window is on another virtual desktop, close and recreate it
                    // on the current desktop instead of letting Windows switch desktops
                    bool needsRecreate = false;
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(_hubWindowInstance).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            var vdm = (AdvanceClip.Classes.NativeMethods.IVirtualDesktopManager)new AdvanceClip.Classes.NativeMethods.VirtualDesktopManager();
                            int hr = vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out bool onCurrent);
                            if (hr == 0 && !onCurrent)
                                needsRecreate = true;
                        }
                    }
                    catch { /* COM not available on older Windows — skip desktop check */ }

                    if (needsRecreate)
                    {
                        _hubWindowInstance.ForceShutdownRelease();
                        _hubWindowInstance = null;
                    }
                }

                if (_hubWindowInstance == null || !_hubWindowInstance.IsLoaded)
                {
                    _hubWindowInstance = new Windows.HubWindow(_viewModel);
                    _hubWindowInstance.Closed += (s, args) => _hubWindowInstance = null;
                    _hubWindowInstance.Show();
                }
                else
                {
                    if (_hubWindowInstance.WindowState == WindowState.Minimized)
                        _hubWindowInstance.WindowState = WindowState.Normal;
                    _hubWindowInstance.Show();
                }
                _hubWindowInstance.Activate();
                _hubWindowInstance.Focus();
                AnimateAndHide();
            }
            catch (Exception ex)
            {
                _hubWindowInstance = null;
                var fullMsg = ex.ToString();
                var inner = ex.InnerException;
                while (inner != null) { fullMsg += "\n--- INNER: " + inner.Message; inner = inner.InnerException; }
                AdvanceClip.Classes.Logger.LogAction("HUBWINDOW_FAIL", fullMsg);
                AdvanceClip.Windows.ToastWindow.ShowToast($"Hub Error: {(ex.InnerException?.Message ?? ex.Message)}");
            }
        }

        private void ShelfListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only manage the Unpin button here. Merge bar is controlled by checkbox toggles.
            if (ShelfListView.SelectedItems.Count > 1)
            {
                int pinnedCount = 0;
                foreach (var item in ShelfListView.SelectedItems)
                {
                    if (item is ClipboardItem clipItem && clipItem.IsPinned)
                        pinnedCount++;
                }

                if (pinnedCount > 0)
                {
                    UnpinSelectedText.Text = pinnedCount == 1 ? "Unpin 1 Item" : $"Unpin {pinnedCount} Items";
                    UnpinSelectedBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    UnpinSelectedBtn.Visibility = Visibility.Collapsed;
                }

                // Shift/Ctrl-select PDF/DOC merge: auto-check selected files for merge/convert
                var selectedMergeable = ShelfListView.SelectedItems.Cast<ClipboardItem>()
                    .Where(i => (i.IsPdfPreview || i.IsDocPreview) && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                    .ToList();

                if (selectedMergeable.Count >= 2 || (selectedMergeable.Count == 1 && selectedMergeable[0].IsDocPreview))
                {
                    foreach (var item in selectedMergeable)
                        item.IsCheckedForMerge = true;
                    UpdatePdfMergeToolbar();
                }
            }
            else
            {
                UnpinSelectedBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void UnpinSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            var pinnedSelected = ShelfListView.SelectedItems
                .Cast<ClipboardItem>()
                .Where(i => i.IsPinned)
                .ToList();
            
            foreach (var item in pinnedSelected)
            {
                item.IsPinned = false;
            }
            
            _viewModel.SavePinnedItems();
            _viewModel.PersistHistoryPublic();
            UnpinSelectedBtn.Visibility = Visibility.Collapsed;
            ShelfListView.SelectedItems.Clear();
        }

        private async void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            var checkedPdfs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            var checkedDocs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            // Convert DOC/DOCX files to PDF first
            var convertedPdfPaths = new List<string>();
            if (checkedDocs.Count > 0)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast($"📄 Converting {checkedDocs.Count} DOC file(s) to PDF...");

                foreach (var doc in checkedDocs)
                {
                    string pdfPath = await ConvertDocToPdfAsync(doc.FilePath);
                    if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
                    {
                        convertedPdfPaths.Add(pdfPath);
                    }
                    else
                    {
                        AdvanceClip.Windows.ToastWindow.ShowToast($"❌ Failed to convert: {doc.FileName}");
                    }
                }

                // If only DOCs selected (no merge needed), just convert and add to shelf
                if (checkedPdfs.Count == 0 && convertedPdfPaths.Count > 0 && checkedDocs.Count == convertedPdfPaths.Count)
                {
                    foreach (string path in convertedPdfPaths)
                    {
                        var newItem = new ClipboardItem(path);
                        _viewModel.DroppedItems.Insert(0, newItem);
                    }
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    DismissMergeState();
                    AdvanceClip.Windows.ToastWindow.ShowToast($"✅ Converted {convertedPdfPaths.Count} file(s) to PDF");
                    return;
                }
            }

            // Build the final list of PDF items for the merge window
            var allPdfs = new List<ClipboardItem>();
            allPdfs.AddRange(checkedPdfs);

            // Add converted docs as ClipboardItems
            foreach (string path in convertedPdfPaths)
            {
                allPdfs.Add(new ClipboardItem(path));
            }

            if (allPdfs.Count > 1)
            {
                DismissMergeState();
                var win = new AdvanceClip.Windows.PdfMergeWindow(allPdfs, _viewModel);
                App.ActiveMergeWindow = win;
                win.Closed += (_, __) => { App.ActiveMergeWindow = null; this.Show(); this.Activate(); };
                win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
                win.Topmost = true;
                win.Show();
                win.Activate();
                win.Focus();
                win.Topmost = false;
                AnimateAndHide();
            }
            else if (allPdfs.Count == 1)
            {
                // Single converted PDF — just add to shelf
                DismissMergeState();
                AdvanceClip.Windows.ToastWindow.ShowToast("✅ PDF added to clipboard");
            }
            else
            {
                AdvanceClip.Windows.ToastWindow.ShowToast("Select 2+ PDFs/DOCs to merge, or 1 DOC to convert.");
            }
        }

        /// <summary>Converts a DOC/DOCX file to PDF using Word COM via PowerShell. Returns the output path or null.</summary>
        private async System.Threading.Tasks.Task<string> ConvertDocToPdfAsync(string docPath)
        {
            string outputDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Converted");
            System.IO.Directory.CreateDirectory(outputDir);

            string pdfPath = System.IO.Path.Combine(outputDir,
                System.IO.Path.GetFileNameWithoutExtension(docPath) + ".pdf");

            bool success = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // wdFormatPDF = 17
                    string script = $@"
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$doc = $word.Documents.Open('{docPath.Replace("'", "''")}')
$doc.SaveAs([ref]'{pdfPath.Replace("'", "''")}', [ref]17)
$doc.Close()
$word.Quit()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(120000); // 2 min timeout
                    return proc?.ExitCode == 0;
                }
                catch (Exception ex)
                {
                    AdvanceClip.Classes.Logger.LogAction("DOC2PDF", $"Conversion error: {ex.Message}");
                    return false;
                }
            });

            return (success && System.IO.File.Exists(pdfPath)) ? pdfPath : null;
        }

        private void PdfMergeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                item.IsCheckedForMerge = !item.IsCheckedForMerge;
                UpdatePdfMergeToolbar();
            }
        }

        private void UpdatePdfMergeToolbar()
        {
            var checkedPdfs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            var checkedDocs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            int totalChecked = checkedPdfs.Count + checkedDocs.Count;

            if (totalChecked >= 2 || (checkedDocs.Count == 1 && checkedPdfs.Count == 0))
            {
                if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedDocs.Count == 1)
                {
                    // Single DOC — show Convert to PDF
                    MergeSelectedPdfsText.Text = "Convert to PDF";
                    MergePdfToolbarBtn.ToolTip = "Convert DOC/DOCX to PDF";
                }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0)
                {
                    // Multiple DOCs — show Convert All
                    MergeSelectedPdfsText.Text = $"Convert {checkedDocs.Count} to PDF";
                    MergePdfToolbarBtn.ToolTip = $"Convert {checkedDocs.Count} DOC files to PDF";
                }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count > 0)
                {
                    // Mixed — show Merge with auto-convert
                    MergeSelectedPdfsText.Text = $"Merge {totalChecked} Files";
                    MergePdfToolbarBtn.ToolTip = $"Convert DOC→PDF & merge all {totalChecked} files";
                }
                else
                {
                    // PDF-only
                    MergeSelectedPdfsText.Text = $"Merge {checkedPdfs.Count} PDFs";
                    MergePdfToolbarBtn.ToolTip = $"Merge {checkedPdfs.Count} PDFs";
                }

                MergeSelectedPdfsBtn.Visibility = Visibility.Visible;
                EmojiBtn.Visibility = Visibility.Collapsed;
                MergePdfToolbarBtn.Visibility = Visibility.Visible;
            }
            else
            {
                MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
                EmojiBtn.Visibility = Visibility.Visible;
                MergePdfToolbarBtn.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Hides the merge floating bar, restores emoji btn, and unchecks all PDFs.</summary>
        internal void DismissMergeState()
        {
            MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
            EmojiBtn.Visibility = Visibility.Visible;
            MergePdfToolbarBtn.Visibility = Visibility.Collapsed;

            // Uncheck all IsCheckedForMerge
            foreach (var item in _viewModel.DroppedItems)
            {
                if (item.IsCheckedForMerge) item.IsCheckedForMerge = false;
            }
        }

        private async void ConvertPdfToWord_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as System.Windows.Controls.MenuItem;
                var clipItem = menuItem?.Tag as ClipboardItem;
                if (clipItem == null || string.IsNullOrEmpty(clipItem.FilePath)) return;

                AdvanceClip.Windows.ToastWindow.ShowToast("📄 Converting PDF to Word...");

                string outputPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(clipItem.FilePath) ?? System.IO.Path.GetTempPath(),
                    System.IO.Path.GetFileNameWithoutExtension(clipItem.FilePath) + "_Converted.docx");

                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // Use Word COM to open PDF and save as DOCX (Word 2013+ supports this natively)
                        string script = $@"
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$doc = $word.Documents.Open('{clipItem.FilePath.Replace("'", "''")}')
$doc.SaveAs([ref]'{outputPath.Replace("'", "''")}', [ref]16)
$doc.Close()
$word.Quit()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
";
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        System.Diagnostics.Process.Start(psi)?.WaitForExit(60000);
                    }
                    catch (Exception ex)
                    {
                        AdvanceClip.Classes.Logger.LogAction("PDF2WORD", $"Conversion error: {ex.Message}");
                    }
                });

                if (System.IO.File.Exists(outputPath))
                {
                    // Add converted file to shelf
                    var newItem = new ClipboardItem(outputPath);
                    _viewModel.DroppedItems.Insert(0, newItem);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                    // Open containing folder with the file selected
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\"");
                    AdvanceClip.Windows.ToastWindow.ShowToast($"✅ Converted: {System.IO.Path.GetFileName(outputPath)}");
                }
                else
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast("❌ Conversion failed — Microsoft Word required");
                }
            }
            catch (Exception ex)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast($"❌ PDF to Word error: {ex.Message}");
            }
        }

        // ═══ Feature 4: Hover Preview Popup ═══

        internal void CardBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _activePreviewPopup?.ClosePreview();
            _activePreviewPopup = null;

            var border = sender as System.Windows.FrameworkElement;
            var item = border?.DataContext as ClipboardItem;
            if (item == null) return;

            // Only show preview for long text items (>100 chars)
            bool isLongText = !string.IsNullOrEmpty(item.RawContent) && item.RawContent.Length > 100 
                              && (item.ItemType == ClipboardItemType.Text || item.ItemType == ClipboardItemType.Code);
            if (!isLongText) return;

            _hoveredItem = item;
            if (_hoverPreviewTimer == null)
            {
                _hoverPreviewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _hoverPreviewTimer.Tick += HoverPreviewTimer_Tick;
            }
            _hoverPreviewTimer.Stop();
            _hoverPreviewTimer.Start();
        }

        internal void CardBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _hoverPreviewTimer?.Stop();
            _hoveredItem = null;
            _activePreviewPopup?.ClosePreview();
            _activePreviewPopup = null;
        }

        private void HoverPreviewTimer_Tick(object? sender, EventArgs e)
        {
            _hoverPreviewTimer?.Stop();
            if (_hoveredItem == null || string.IsNullOrEmpty(_hoveredItem.RawContent)) return;

            Classes.NativeMethods.GetCursorPos(out var pt);
            var source = PresentationSource.FromVisual(this);
            double dpiScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
            double x = pt.X * dpiScaleX + 20;
            double y = pt.Y * dpiScaleY - 40;

            _activePreviewPopup = new Windows.PreviewPopup(_hoveredItem.RawContent, x, y);
            _activePreviewPopup.Show();
        }
    }
}
