// ---------------------------------------------------------------
// MainWindow � Mouse Interactions & Advanced Features
// MouseClick, DragDrop, ForceSend, OpenApp, Selection,
// PDF Merge, Card Hover Preview
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using FlyShelf.Classes;
using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyShelf
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
                            bmp.DecodePixelWidth = 1024; // Cap decode size to reduce UI thread stall
                            bmp.EndInit();
                            bmp.Freeze();
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
            // --- Physical mouse movement detection for scroll hover optimization ---
            Point currentPos = e.GetPosition(this);
            if (Math.Abs(currentPos.X - _lastPhysicalMousePosition.X) > 0.5 ||
                Math.Abs(currentPos.Y - _lastPhysicalMousePosition.Y) > 0.5)
            {
                _lastPhysicalMousePosition = currentPos;

                // Only allow summoning the hover action buttons when scrolling is not active,
                // or if scroll speed is slow (e.g. less than 0.25 pixels per ms).
                if (!_viewModel.IsScrolling || _scrollVelocity < 0.25)
                {
                    _viewModel.AllowHover = true;
                }
            }

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
                                    // Load a tiny thumbnail instead of full image — avoids 1-2s freeze on large files
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.UriSource = new Uri(firstItem.FilePath);
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.DecodePixelWidth = 128; // Lightweight thumbnail for drag preview
                                    bmp.EndInit();
                                    bmp.Freeze();
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
                            FlyShelf.Classes.Logger.LogAction("DRAG OUT FAULT", $"Failed UI Export: {ex.Message}");
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
            // Guard: If the double-click originated inside any Button (action pills,
            // expand chevron, PDF merge toggle, etc.), do NOT execute the item.
            // Without this, clicking buttons rapidly triggers item.Execute() which
            // opens files/PDFs unexpectedly.
            if (e.OriginalSource is DependencyObject sourceElement)
            {
                if (sourceElement is System.Windows.Controls.Primitives.ButtonBase ||
                    FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement) != null)
                {
                    e.Handled = true;
                    return;
                }
            }

            if (ShelfListView.SelectedItem is ClipboardItem item)
            {
                item.Execute();
            }
        }

        private Windows.HubWindow? _hubWindowInstance;



        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CloseEmojiPicker();
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
                            var vdm = (FlyShelf.Classes.NativeMethods.IVirtualDesktopManager)new FlyShelf.Classes.NativeMethods.VirtualDesktopManager();
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

                if (_hubWindowInstance == null)
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
                FlyShelf.Classes.Logger.LogAction("HUBWINDOW_FAIL", fullMsg);
                FlyShelf.Windows.ToastWindow.ShowToast($"Hub Error: {(ex.InnerException?.Message ?? ex.Message)}");
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

                // Shift/Ctrl-select PDF/DOC/Image merge: auto-check selected files for merge/convert
                var selectedMergeable = ShelfListView.SelectedItems.Cast<ClipboardItem>()
                    .Where(i => (i.IsPdfPreview || i.IsDocPreview || i.ItemType == ClipboardItemType.Image) && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
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
            
            System.Threading.Tasks.Task.Run(() =>
            {
                _viewModel.SavePinnedItems();
                _viewModel.PersistHistoryPublic();
            });
            
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
            var checkedImages = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            var convertedPdfPaths = new List<string>();

            // Convert DOC/DOCX files to PDF first
            if (checkedDocs.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"📄 Converting {checkedDocs.Count} DOC file(s) to PDF...");

                foreach (var doc in checkedDocs)
                {
                    string pdfPath = await ConversionUtils.ConvertDocToPdfAsync(doc.FilePath);
                    if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
                    {
                        convertedPdfPaths.Add(pdfPath);
                    }
                    else
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ Failed to convert: {doc.FileName}");
                    }
                }
            }

            // Convert Images to PDF next
            if (checkedImages.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"🖼️ Formatting {checkedImages.Count} image(s) to PDF...");

                foreach (var img in checkedImages)
                {
                    try
                    {
                        string pdfPath = await System.Threading.Tasks.Task.Run(() => ConversionUtils.ConvertImageToPdf(img.FilePath));
                        if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
                        {
                            convertedPdfPaths.Add(pdfPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ Failed to format: {img.FileName}");
                        FlyShelf.Classes.Logger.LogAction("IMAGE2PDF_ERR", ex.ToString());
                    }
                }
            }

            // If only DOCs/Images selected and no merge needed (only 1 output item)
            if (checkedPdfs.Count == 0 && checkedDocs.Count + checkedImages.Count == convertedPdfPaths.Count && convertedPdfPaths.Count == 1)
            {
                DismissMergeState();
                var newItem = new ClipboardItem(convertedPdfPaths[0]);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                FlyShelf.Windows.ToastWindow.ShowToast("✅ Converted to PDF");
                return;
            }

            // Build the final list of PDF items for the merge window
            var allPdfs = new List<ClipboardItem>();
            allPdfs.AddRange(checkedPdfs);

            // Add converted items as ClipboardItems
            foreach (string path in convertedPdfPaths)
            {
                allPdfs.Add(new ClipboardItem(path));
            }

            if (allPdfs.Count > 1)
            {
                DismissMergeState();
                var win = new FlyShelf.Windows.PdfMergeWindow(allPdfs, _viewModel);
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
                var newItem = new ClipboardItem(allPdfs[0].FilePath);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                FlyShelf.Windows.ToastWindow.ShowToast("✅ PDF added to clipboard");
            }
            else
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Select 2+ files to merge, or 1 image/doc to convert.");
            }
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
            var checkedImages = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            int totalChecked = checkedPdfs.Count + checkedDocs.Count + checkedImages.Count;

            if (totalChecked >= 2 || (checkedDocs.Count == 1 && checkedPdfs.Count == 0 && checkedImages.Count == 0))
            {
                if (checkedImages.Count > 0 && checkedPdfs.Count == 0 && checkedDocs.Count == 0)
                {
                    MergeSelectedPdfsText.Text = $"Merge {checkedImages.Count} Images";
                    MergePdfToolbarBtn.ToolTip = $"Merge {checkedImages.Count} images into a single PDF";
                }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedImages.Count == 0 && checkedDocs.Count == 1)
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
                else
                {
                    // Mixed
                    MergeSelectedPdfsText.Text = $"Merge {totalChecked} Files";
                    MergePdfToolbarBtn.ToolTip = $"Convert & merge all {totalChecked} files";
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

        private ClipboardItem GetClipItemFromSender(object sender)
        {
            if (sender is System.Windows.FrameworkElement fe)
            {
                if (fe.Tag is ClipboardItem tagItem) return tagItem;
                if (fe.DataContext is ClipboardItem dcItem) return dcItem;
            }
            return null;
        }

        private void GoogleSearch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clipItem = GetClipItemFromSender(sender);
                if (clipItem == null || string.IsNullOrEmpty(clipItem.RawContent)) return;

                string query = Uri.EscapeDataString(clipItem.RawContent);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://www.google.com/search?q={query}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Search Error: {ex.Message}");
            }
        }


        private async void ConvertPdfToWord_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clipItem = GetClipItemFromSender(sender);
                if (clipItem == null || string.IsNullOrEmpty(clipItem.FilePath)) return;

                FlyShelf.Windows.ToastWindow.ShowToast("📄 Converting PDF to Word...");

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
                        FlyShelf.Classes.Logger.LogAction("PDF2WORD", $"Conversion error: {ex.Message}");
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
                    FlyShelf.Windows.ToastWindow.ShowToast($"✅ Converted: {System.IO.Path.GetFileName(outputPath)}");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("❌ Conversion failed — Microsoft Word required");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"❌ PDF to Word error: {ex.Message}");
            }
        }

        // ═══ Feature 4: Hover Preview Popup (DISABLED — replaced by expand/collapse chevron button) ═══

        internal void CardBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // No-op: hover preview removed in favor of expand/collapse chevron button
        }

        internal void CardBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // No-op: hover preview removed in favor of expand/collapse chevron button
        }

        private void HoverPreviewTimer_Tick(object? sender, EventArgs e)
        {
            _hoverPreviewTimer?.Stop();
        }
    }
}
