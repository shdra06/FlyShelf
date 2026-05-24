// ---------------------------------------------------------------
// MainWindow � Mouse Interactions & Advanced Features
// MouseClick, DragDrop, OpenApp, Selection,
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
                    using (var dropEffect = new System.IO.MemoryStream())
                    {
                        dropEffect.Write(moveEffect, 0, moveEffect.Length);
                        dataObj.SetData("Preferred DropEffect", dropEffect);
                    }

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

            // GUARD: Don't select items when clicking/dragging the scrollbar
            if (e.OriginalSource is DependencyObject src &&
                FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(src) != null)
                return;

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
                // GUARD: Don't start drag-out when dragging the scrollbar
                if (e.OriginalSource is DependencyObject dragSrc &&
                    FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(dragSrc) != null)
                    return;
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
                            using (var dropEffect = new System.IO.MemoryStream())
                            {
                                dropEffect.Write(moveEffect, 0, moveEffect.Length);
                                dataObj.SetData("Preferred DropEffect", dropEffect);
                            }
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



        /// <summary>
        /// Toggles or summons the Main Clipboard overlay (MainWindow in Medium Mode/Mode 1) at the cursor position.
        /// </summary>
        public void ToggleMainClipboard()
        {
            // If the overlay is already visible and in Mode 1, hide it
            if (_isCurrentlySummoned && _viewModel.CurrentMode == 1 && !_isAnimatingHide)
            {
                AnimateAndHide();
            }
            else
            {
                double targetX = -1;
                double targetY = -1;
                bool positionFound = false;

                // Try to get the taskbar widget position
                if (_taskbarWidget != null && _taskbarWidget.IsVisible)
                {
                    try
                    {
                        Point widgetPos = _taskbarWidget.GetWidgetScreenPosition();
                        if (widgetPos.X >= 0 && widgetPos.Y >= 0)
                        {
                            targetX = widgetPos.X;
                            targetY = widgetPos.Y;
                            positionFound = true;
                            Classes.Logger.LogAction("SUMMON", $"Spawn at widget center logical X={targetX}, top Y={targetY}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("SUMMON_FAIL", $"Failed to get widget position: {ex.Message}");
                    }
                }

                if (!positionFound)
                {
                    // Fallback to cursor position
                    Classes.NativeMethods.POINT pt;
                    if (Classes.NativeMethods.GetCursorPos(out pt))
                    {
                        // Convert physical cursor to logical pixels using the monitor of the cursor
                        double scaleX = 1.0;
                        double scaleY = 1.0;
                        try
                        {
                            var monitor = Classes.Utils.MonitorUtil.GetMonitorWithCursor();
                            scaleX = monitor.dpiX / 96.0;
                            scaleY = monitor.dpiY / 96.0;
                        }
                        catch { }

                        if (scaleX <= 0) scaleX = 1.0;
                        if (scaleY <= 0) scaleY = 1.0;

                        targetX = pt.X / scaleX;
                        targetY = pt.Y / scaleY;
                        Classes.Logger.LogAction("SUMMON", $"Spawn fallback at cursor logical X={targetX}, Y={targetY}");
                    }
                    else
                    {
                        // Last resort fallback: screen center
                        var workArea = SystemParameters.WorkArea;
                        targetX = workArea.Left + workArea.Width / 2;
                        targetY = workArea.Top + workArea.Height - 50;
                        Classes.Logger.LogAction("SUMMON", $"Spawn fallback at workarea center logical X={targetX}, Y={targetY}");
                    }
                }

                ShowNearPosition(targetX, targetY, 1, false, true); // mode = 1, isPersistent = false, stealFocus = true
            }
        }

        /// <summary>
        /// Public entry point for external callers (widget, hotkey) to open the HubWindow (big clipboard).
        /// Toggles: if Hub is already visible and active, hide it instead.
        /// </summary>
        public void OpenHubWindow()
        {
            // Toggle: if HubWindow is already visible and focused, just hide it
            if (_hubWindowInstance != null && _hubWindowInstance.IsVisible)
            {
                _hubWindowInstance.Hide();
                return;
            }
            OpenApp_Click(null, null);
        }

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
            if (IsDeletingItem) return;

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
                    UnpinSelectedBtn.Content = pinnedCount == 1 ? "Unpin 1 Item" : $"Unpin {pinnedCount} Items";
                    UnpinSelectedBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    UnpinSelectedBtn.Visibility = Visibility.Collapsed;
                }

                // Sync → Unpin swap in toolbar (mirrors emoji → merge pattern)
                UpdateToolbarButtonsVisibility();

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
                UpdateToolbarButtonsVisibility();
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
            UpdateToolbarButtonsVisibility();
            ShelfListView.SelectedItems.Clear();
        }

        // ═══ PDF Merge, Convert & Smart Actions moved to MainWindow.PdfMerge.cs ═══



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

        internal void UpdateToolbarButtonsVisibility()
        {
            if (_viewModel == null) return;
            bool isMini = _viewModel.CurrentMode == 0;
            
            if (SearchToggleBtn != null)
            {
                SearchToggleBtn.Visibility = isMini ? Visibility.Collapsed : Visibility.Visible;
            }
            
            if (ClearShelfBtn != null)
            {
                ClearShelfBtn.Visibility = isMini ? Visibility.Collapsed : Visibility.Visible;
            }
            
            // Emoji → Merge PDF swap (existing behavior)
            bool isMergeActive = MergePdfToolbarBtn != null && MergePdfToolbarBtn.Visibility == Visibility.Visible;
            if (EmojiBtn != null)
            {
                EmojiBtn.Visibility = (isMini || isMergeActive) ? Visibility.Collapsed : Visibility.Visible;
            }

            // Sync → Unpin swap: when pinned items are multi-selected, swap sync button for unpin icon
            bool hasUnpinTarget = UnpinSelectedBtn != null && UnpinSelectedBtn.Visibility == Visibility.Visible;
            if (SyncToolbarBtn != null)
            {
                SyncToolbarBtn.Visibility = hasUnpinTarget ? Visibility.Collapsed : Visibility.Visible;
            }
            if (UnpinToolbarBtn != null)
            {
                UnpinToolbarBtn.Visibility = hasUnpinTarget ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
