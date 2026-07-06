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
        /// <summary>
        /// Floating drag preview card window — shown during drag-out.
        /// Created once per drag, destroyed when drag ends.
        /// </summary>
        private Windows.DragPreviewWindow? _dragPreviewWindow;

        /// <summary>
        /// True while DragDrop.DoDragDrop() is executing its nested message loop.
        /// Used to suppress WM_CLIPBOARDUPDATE handling during drag operations,
        /// preventing race conditions where the clipboard monitor fires inside
        /// the OLE drag-drop message pump.
        /// </summary>
        internal static bool _isDragging = false;

        private async void ShelfListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_justDeletedAnItem)
            {
                _justDeletedAnItem = false;
                e.Handled = true;
                return;
            }

            if (_didDragOut)
            {
                _didDragOut = false;
                e.Handled = true;
                return;
            }

            // Guard: if the Down handler flagged a special element (PdfMergeToggle, etc.), don't paste
            if (_shouldPreventDrag)
            {
                _shouldPreventDrag = false; // Always reset to avoid sticking
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
                if (HasAncestorTag(sourceElement, "PdfMergeToggle") || HasAncestorTag(sourceElement, "AltPdfMergeToggle"))
                {
                    e.Handled = true;
                    return;
                }

                // Aero expand/collapse toggle: already handled
                if (HasAncestorTag(sourceElement, "AeroExpandToggle"))
                {
                    e.Handled = true;
                    return;
                }

                // Aero hover actions: already handled / prevent paste-and-hide
                if (HasAncestorTag(sourceElement, "AeroHoverActions"))
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

            try
            {
                var listView = sender as System.Windows.Controls.ListView;
                if (listView == null) return;
                var itemContainer2 = System.Windows.Controls.ItemsControl.ContainerFromElement(listView, e.OriginalSource as DependencyObject) as System.Windows.Controls.ListViewItem;
            
                if (itemContainer2 != null)
                {
                    var clipboardObj = itemContainer2.DataContext as ClipboardItem;
                    if (clipboardObj != null)
                    {
                        await CopyItemAndPaste(clipboardObj, hideWindow: true);
                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("PASTE_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// Copies a ClipboardItem to the system clipboard, optionally hides the shelf,
        /// restores focus to the previous window, and simulates Ctrl+V.
        /// Reused by: mouse click, Enter key, and Alt+N global hotkeys.
        /// </summary>
        private async System.Threading.Tasks.Task CopyItemAndPaste(ClipboardItem clipboardObj, bool hideWindow)
        {

            bool clipboardDataSet = false;
            try
            {
                if (!string.IsNullOrEmpty(clipboardObj.FilePath))
                {
                    var dataObj = new DataObject();
                    
                    var dropList = new System.Collections.Specialized.StringCollection();
                    dropList.Add(clipboardObj.FilePath);
                    dataObj.SetFileDropList(dropList);
                    // For executable/script files (.bat, .cmd, .ps1, .exe), text fields should receive
                    // the file PATH, not the script content — that's what users expect when pasting.
                    bool shouldPastePath = clipboardObj.IsTerminalPreview ||
                        (!string.IsNullOrEmpty(clipboardObj.Extension) && (
                            clipboardObj.Extension == ".EXE" || clipboardObj.Extension == ".MSI" ||
                            clipboardObj.Extension == ".LNK"));

                    if (shouldPastePath || string.IsNullOrEmpty(clipboardObj.RawContent))
                    {
                        dataObj.SetData(DataFormats.StringFormat, clipboardObj.FilePath);
                        dataObj.SetData(DataFormats.Text, clipboardObj.FilePath);
                    }
                    else if (!string.IsNullOrEmpty(clipboardObj.RawContent))
                    {
                        dataObj.SetData(DataFormats.Text, clipboardObj.RawContent);
                        dataObj.SetData(DataFormats.UnicodeText, clipboardObj.RawContent);
                    }
                    dataObj.SetData("FileNameW", new string[] { clipboardObj.FilePath });
                    dataObj.SetData("FileName", new string[] { clipboardObj.FilePath });
                    try { dataObj.SetData("text/uri-list", "file:///" + clipboardObj.FilePath.Replace("\\", "/")); } catch (Exception ex) { Classes.Logger.LogAction("URI_LIST_ERROR", ex.Message); }
                    
                    if (clipboardObj.ItemType == ClipboardItemType.Image)
                    {
                        try
                        {
                            var bmp = await System.Threading.Tasks.Task.Run(() =>
                            {
                                var bytes = System.IO.File.ReadAllBytes(clipboardObj.FilePath);
                                var bi = new BitmapImage();
                                bi.BeginInit();
                                bi.StreamSource = new System.IO.MemoryStream(bytes);
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.DecodePixelWidth = 1024; // Cap decode size to reduce UI thread stall
                                bi.EndInit();
                                bi.Freeze();
                                return bi;
                            });
                            dataObj.SetImage(bmp);
                        }
                        catch (Exception ex) { Classes.Logger.LogAction("IMAGE_CLIPBOARD_ERROR", ex.Message); }
                    }
                    
                    byte[] moveEffect = new byte[] { 5, 0, 0, 0 };
                    using (var dropEffect = new System.IO.MemoryStream())
                    {
                        dropEffect.Write(moveEffect, 0, moveEffect.Length);
                        dataObj.SetData("Preferred DropEffect", dropEffect);
                    }

                    Classes.ClipboardHelper.SafeSetDataObject(dataObj, true, suppressEcho: true, echoDelayMs: 2000);
                    clipboardDataSet = true;
                }
                else if (!string.IsNullOrEmpty(clipboardObj.RawContent))
                {
                    Classes.ClipboardHelper.SafeSetText(clipboardObj.RawContent, suppressEcho: true, echoDelayMs: 2000);
                    clipboardDataSet = true;
                }
            }
            catch (Exception ex) { Classes.Logger.LogAction("CLIPBOARD_SET_ERROR", ex.Message); }

            if (hideWindow)
            {
                AnimateAndHide();
                _isDragHovering = false;
                IsDragHovering = false;
            }

            if (_spawnedWithoutFocus)
            {
                // ═══ NO-FOCUS PASTE PATH ═══
                // The clipboard was shown with WS_EX_NOACTIVATE — the target app never lost focus.
                // DO NOT call SetForegroundWindow: it's unnecessary and can cause focus fighting,
                // caret resets, or paste failures. Just set clipboard data and simulate Ctrl+V.
                // This matches how Windows native clipboard (Win+V) works.
                await System.Threading.Tasks.Task.Delay(50); // Brief settle for clipboard data propagation

                // Capture context title for the item (non-blocking)
                if (_previousForegroundWindow != IntPtr.Zero)
                {
                    try
                    {
                        var sbTitle = new System.Text.StringBuilder(256);
                        GetWindowText(_previousForegroundWindow, sbTitle, 256);
                        string contextTitle = sbTitle.ToString();
                        if (!string.IsNullOrWhiteSpace(contextTitle))
                            clipboardObj.AssociatedContextTitle = contextTitle;
                    }
                    catch (Exception ex) { Classes.Logger.LogAction("CONTEXT_TITLE_ERROR", ex.Message); }
                }
            }
            else
            {
                // ═══ FOCUS-STEAL PASTE PATH ═══
                // The clipboard was shown activated (stealFocus=true) — need to restore focus
                // to the previous window before simulating Ctrl+V.
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
            }

            if (clipboardDataSet)
            {
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            // PERF FIX: Defer MoveItemToTop to Background priority so it never blocks
            // the paste-and-dismiss flow. ObservableCollection.Move() triggers expensive
            // WPF VirtualizingStackPanel re-layout (de-virtualizing all containers between
            // oldIndex and 0). By running it after the window is hidden and Ctrl+V has fired,
            // the user sees zero lag — the collection mutation happens invisibly.
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _viewModel.MoveItemToTop(clipboardObj);
                }
                catch (Exception ex) { Classes.Logger.LogAction("MOVE_TO_TOP_ERROR", ex.Message); }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShelfListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _didDragOut = false;
            _shouldPreventDrag = false;
            _justDeletedAnItem = false;

            var listView = sender as System.Windows.Controls.ListView;
            if (listView == null) return;

            // GUARD: Don't select items when clicking/dragging the scrollbar
            if (e.OriginalSource is DependencyObject src &&
                FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(src) != null)
            {
                _shouldPreventDrag = true;
                return;
            }

            if (e.OriginalSource is DependencyObject sourceElement)
            {
                // PDF merge toggle: toggle state here and fully consume
                if (HasAncestorTag(sourceElement, "PdfMergeToggle") || HasAncestorTag(sourceElement, "AltPdfMergeToggle"))
                {
                    _shouldPreventDrag = true;
                    // Debounce: ignore rapid-fire from held mouse button
                    if ((DateTime.Now - _lastMergeToggleTime).TotalMilliseconds > 300)
                    {
                        _lastMergeToggleTime = DateTime.Now;
                        var toggleContainer = ItemsControl.ContainerFromElement(listView, sourceElement) as ListViewItem;
                        if (toggleContainer?.DataContext is ClipboardItem item)
                        {
                            item.IsCheckedForMerge = !item.IsCheckedForMerge;
                            UpdatePdfMergeToolbar();

                            // Select this item in the ListView
                            listView.SelectedItem = item;

                            // Focus the container
                            toggleContainer.Focus();
                            Keyboard.Focus(toggleContainer);
                        }
                    }
                    e.Handled = true;
                    return;
                }

                // Aero expand/collapse toggle: don't paste-and-hide
                if (HasAncestorTag(sourceElement, "AeroExpandToggle"))
                {
                    _shouldPreventDrag = true;
                    e.Handled = true;
                    return;
                }

                // Aero hover actions: don't start dragging on them
                if (HasAncestorTag(sourceElement, "AeroHoverActions"))
                {
                    _shouldPreventDrag = true;
                    return;
                }

                // Don't interfere with other button clicks
                if (sourceElement is System.Windows.Controls.Primitives.ButtonBase ||
                    FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement) != null)
                {
                    _shouldPreventDrag = true;
                    return;
                }

                var itemContainer = ItemsControl.ContainerFromElement(listView, sourceElement) as ListViewItem;
                if (itemContainer != null && itemContainer.DataContext is ClipboardItem)
                {
                    if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl) &&
                        !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
                    {
                        listView.SelectedItems.Clear();
                    }
                    itemContainer.IsSelected = true;
                }
            }
        }

        private async void ShelfListView_MouseMove(object sender, MouseEventArgs e)
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
                if (_shouldPreventDrag)
                    return;

                // GUARD: Don't start drag-out when dragging the scrollbar
                if (e.OriginalSource is DependencyObject dragSrc &&
                    FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(dragSrc) != null)
                    return;
                Point position = e.GetPosition(null);
                Vector diff = _dragStartPoint - position;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var listView = sender as System.Windows.Controls.ListView;
                    if (listView == null) return;

                    if (listView.SelectedItems.Count > 0)
                    {
                        var firstItem = listView.SelectedItems.Cast<ClipboardItem>().FirstOrDefault();
                        if (firstItem == null) return;

                        DataObject dataObj = new DataObject();
                        if (!string.IsNullOrEmpty(firstItem.FilePath))
                        {
                            bool isTextDrag = !string.IsNullOrEmpty(firstItem.RawContent) && (firstItem.Extension == ".MD" || firstItem.Extension == ".TXT");

                            if (isTextDrag)
                            {
                                dataObj.SetData(DataFormats.UnicodeText, firstItem.RawContent);
                                dataObj.SetData(DataFormats.Text, firstItem.RawContent);
                                dataObj.SetData(DataFormats.StringFormat, firstItem.RawContent);
                            }
                            else
                            {
                                dataObj.SetData(DataFormats.FileDrop, new string[] { firstItem.FilePath });
                                dataObj.SetData("FileNameW", new string[] { firstItem.FilePath });
                                dataObj.SetData("FileName", new string[] { firstItem.FilePath });
                                try { dataObj.SetData("text/uri-list", "file:///" + firstItem.FilePath.Replace("\\", "/")); } catch (Exception ex) { Classes.Logger.LogAction("DRAG_URI_LIST_ERROR", ex.Message); }

                                if (!string.IsNullOrEmpty(firstItem.RawContent))
                                {
                                    dataObj.SetData(DataFormats.UnicodeText, firstItem.RawContent);
                                    dataObj.SetData(DataFormats.Text, firstItem.RawContent);
                                }
                            }

                            if (firstItem.ItemType == ClipboardItemType.Image)
                            {
                                try
                                {
                                    // Load a tiny thumbnail instead of full image — avoids 1-2s freeze on large files
                                    var bmp = await System.Threading.Tasks.Task.Run(() =>
                                    {
                                        var bytes = System.IO.File.ReadAllBytes(firstItem.FilePath);
                                        var bi = new BitmapImage();
                                        bi.BeginInit();
                                        bi.StreamSource = new System.IO.MemoryStream(bytes);
                                        bi.CacheOption = BitmapCacheOption.OnLoad;
                                        bi.DecodePixelWidth = 128; // Lightweight thumbnail for drag preview
                                        bi.EndInit();
                                        bi.Freeze();
                                        return bi;
                                    });
                                    dataObj.SetImage(bmp);
                                }
                                catch (Exception ex) { Classes.Logger.LogAction("DRAG_IMAGE_ERROR", ex.Message); }
                            }

                            // Explicit Win32 Shell 'Copy' Effect override (Required for Windows Explorer Drag Drop)
                            byte[] moveEffect = new byte[] { 5, 0, 0, 0 }; // DragDropEffects.Copy
                            var dropEffect = new System.IO.MemoryStream();
                            dropEffect.Write(moveEffect, 0, moveEffect.Length);
                            dataObj.SetData("Preferred DropEffect", dropEffect);
                        }
                        else 
                        {
                            // Text-only item (no file path) — set all text formats for
                            // maximum compatibility with text fields, browsers, and editors.
                            dataObj.SetData(DataFormats.UnicodeText, firstItem.RawContent);
                            dataObj.SetData(DataFormats.Text, firstItem.RawContent);
                            dataObj.SetData(DataFormats.StringFormat, firstItem.RawContent);
                        }
                        
                        _isInternalDragSource = true;
                        _didDragOut = true;
                        System.IO.MemoryStream? dragDropEffect = null;

                        // ═══ Drag Preview Card ═══
                        // Show a floating mini card (icon + filename) that follows the cursor
                        // during drag-out, similar to Windows File Explorer.
                        ListViewItem? dragSourceContainer = null;
                        try
                        {
                            // Close any stale preview that wasn't cleaned up
                            if (_dragPreviewWindow != null)
                            {
                                try { _dragPreviewWindow.SafeClose(); } catch { } // Best-effort: failure is acceptable
                                _dragPreviewWindow = null;
                            }

                            int selectedCount = listView.SelectedItems.Count;
                            _dragPreviewWindow = new Windows.DragPreviewWindow(firstItem, selectedCount);
                            _dragPreviewWindow.Show();
                            _dragPreviewWindow.StartSafetyTimer(); // Auto-close after 8s if cleanup never fires

                            // Position at current cursor
                            if (Classes.NativeMethods.GetCursorPos(out var cursorPt))
                                _dragPreviewWindow.UpdatePosition(cursorPt.X, cursorPt.Y);

                            // Attach GiveFeedback to track cursor during drag
                            listView.GiveFeedback += DragPreview_GiveFeedback;

                            // Attach QueryContinueDrag to detect cancelled drags (Escape, focus loss)
                            listView.QueryContinueDrag += DragPreview_QueryContinueDrag;

                            // Dim the source card to 40% opacity for visual feedback
                            dragSourceContainer = ItemsControl.ContainerFromElement(listView, 
                                e.OriginalSource as DependencyObject) as ListViewItem;
                            if (dragSourceContainer == null)
                            {
                                // Fallback: find by data context
                                dragSourceContainer = listView.ItemContainerGenerator
                                    .ContainerFromItem(firstItem) as ListViewItem;
                            }
                            if (dragSourceContainer != null)
                                dragSourceContainer.Opacity = 0.35;
                        }
                        catch (Exception previewEx)
                        {
                            // Drag preview is non-critical — log and proceed without it
                            Classes.Logger.LogAction("DRAG_PREVIEW", $"Preview creation failed: {previewEx.Message}");
                        }

                        try
                        {
                            // Retrieve the MemoryStream so we can dispose it after DoDragDrop returns
                            dragDropEffect = dataObj.GetData("Preferred DropEffect") as System.IO.MemoryStream;

                            // Record cursor position BEFORE drag starts — we'll use it for fallback paste
                            _isDragging = true;
                            DragDropEffects result = DragDrop.DoDragDrop(listView, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
                            
                            // ═══ FALLBACK PASTE ═══
                            // When the OLE drop is rejected by the target (result == None), fall back
                            // to clipboard paste: copy text to clipboard → focus the window under cursor
                            // → Ctrl+V. This makes drag-and-drop work like Windows clipboard, even on
                            // targets that don't support OLE drops (browser text fields, Electron apps, etc.)
                            if (result == DragDropEffects.None && !string.IsNullOrEmpty(firstItem.RawContent))
                            {
                                // Get the window under the current cursor position
                                if (Classes.NativeMethods.GetCursorPos(out var dropPt))
                                {
                                    IntPtr targetHwnd = WindowFromPhysicalPoint(new Classes.NativeMethods.POINT { X = dropPt.X, Y = dropPt.Y });
                                    if (targetHwnd != IntPtr.Zero)
                                    {
                                        // Get the top-level ancestor window
                                        IntPtr rootHwnd = GetAncestor(targetHwnd, 2 /* GA_ROOT */);
                                        if (rootHwnd == IntPtr.Zero) rootHwnd = targetHwnd;
                                        
                                        // Don't paste back into our own window
                                        var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                                        if (rootHwnd != selfHwnd)
                                        {
                                            // Copy text to clipboard
                                            Classes.ClipboardHelper.SafeSetText(firstItem.RawContent, suppressEcho: true, echoDelayMs: 2000);
                                            
                                            // Click the target to focus the text field, then paste
                                            SetForegroundWindow(rootHwnd);

                                            // Click the exact cursor position to place caret in the text field
                                            SendClickAt(dropPt.X, dropPt.Y);

                                            // Brief delay for focus + click to settle
                                            await System.Threading.Tasks.Task.Delay(120);
                                            
                                            // Simulate Ctrl+V
                                            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                                            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                                            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                                            // Hide the FlyShelf window after paste
                                            AnimateAndHide();
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            FlyShelf.Classes.Logger.LogAction("DRAG OUT FAULT", $"Failed UI Export: {ex.Message}");
                        }
                        finally
                        {
                            _isInternalDragSource = false;
                            _isDragging = false;
                            dragDropEffect?.Dispose();

                            // ═══ Cleanup Drag Preview ═══
                            listView.GiveFeedback -= DragPreview_GiveFeedback;
                            listView.QueryContinueDrag -= DragPreview_QueryContinueDrag;
                            try
                            {
                                _dragPreviewWindow?.SafeClose();
                            }
                            catch { /* Window may already be disposed */ }
                            _dragPreviewWindow = null;

                            // Restore source card opacity
                            if (dragSourceContainer != null)
                                dragSourceContainer.Opacity = 1.0;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// GiveFeedback handler — fires continuously during drag to track cursor position.
        /// Updates the DragPreviewWindow to follow the mouse with zero lag.
        /// </summary>
        private void DragPreview_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_dragPreviewWindow != null &&
                Classes.NativeMethods.GetCursorPos(out var pt))
            {
                _dragPreviewWindow.UpdatePosition(pt.X, pt.Y);
            }

            // Keep the default system cursors (copy/move arrows, no-drop circle)
            e.UseDefaultCursors = true;
            e.Handled = true;
        }

        /// <summary>
        /// QueryContinueDrag handler — fires when drag state changes.
        /// If drag is cancelled (Escape, focus loss), immediately close the preview
        /// so it doesn't linger as a ghost artifact on screen.
        /// </summary>
        private void DragPreview_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.Action == DragAction.Cancel || e.Action == DragAction.Drop)
            {
                // Drag is ending — close preview immediately, don't wait for finally block
                try { _dragPreviewWindow?.SafeClose(); } catch { } // Best-effort: failure is acceptable
                _dragPreviewWindow = null;
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

            var listView = sender as System.Windows.Controls.ListView;
            if (listView?.SelectedItem is ClipboardItem item)
            {
                item.Execute();
            }
        }

        private Windows.HubWindow? _hubWindowInstance;

        public bool IsHubWindowOpen => _hubWindowInstance != null && _hubWindowInstance.IsVisible;
        /// <summary>
        /// Toggles or summons the Main Clipboard overlay (MainWindow in Medium Mode/Mode 1) at the cursor position.
        /// </summary>
        public void ToggleMainClipboard()
        {
            // ═══ DESKTOP SWITCH DETECTION ═══
            // Primary: cached GUID comparison (zero COM calls)
            bool isOnOtherDesktop = _summonedDesktopId != Guid.Empty &&
                                   _currentDesktopId != Guid.Empty &&
                                   _currentDesktopId != _summonedDesktopId;

            // Fallback 1: Use _desktopSwitchedSinceLastDismiss flag (set by callback)
            if (!isOnOtherDesktop && _desktopSwitchedSinceLastDismiss)
            {
                isOnOtherDesktop = true;
                Classes.Logger.LogAction("VD_TOGGLE", "FALLBACK1: deskSwitchFlag=true → isOnOtherDesktop=true");
            }

            Classes.Logger.LogAction("VD_TOGGLE", $"ENTER | summoned={_isCurrentlySummoned} animHide={_isAnimatingHide} " +
                $"notes={_isNotesActive} todo={_isTodoActive} isOtherDesktop={isOnOtherDesktop} " +
                $"summonedId={_summonedDesktopId:N} currentId={_currentDesktopId:N} " +
                $"deskSwitchFlag={_desktopSwitchedSinceLastDismiss} lastPanel={_lastPanelBeforeDismiss ?? "null"} " +
                $"Left={this.Left:F0} Opacity={this.Opacity:F2}");

            // ═══ NOTES/TODO DISMISS ═══
            if ((_isNotesActive || _isTodoActive) && _isCurrentlySummoned && !_isAnimatingHide)
            {
                if (isOnOtherDesktop)
                {
                    Classes.Logger.LogAction("VD_TOGGLE", "PANEL_DISMISS: Other desktop → EnsureClipboardMode + fall through");
                    EnsureClipboardMode();
                    _lastPanelBeforeDismiss = null;
                    _desktopSwitchedSinceLastDismiss = true;
                    _isCurrentlySummoned = false;
                    UninstallKeyboardHook();
                    _isAnimatingHide = false;
                    StopPanelAutoRevertTimer();
                    this.Opacity = 0;
                    this.BeginAnimation(OpacityProperty, null);
                    // JITTER FIX: Hide via Win32 instead of moving to -20000
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                            Classes.NativeMethods.ShowWindow(hwnd, 0 /*SW_HIDE*/);
                    }
                    catch (Exception ex) { Classes.Logger.LogAction("VD_HIDE_ERROR", ex.Message); }
                    // Don't return — fall through to clipboard show path
                }
                else
                {
                    Classes.Logger.LogAction("VD_TOGGLE", "PANEL_DISMISS: Same desktop → AnimateAndHide + return");
                    AnimateAndHide();
                    return;
                }
            }

            // ═══ VIRTUAL DESKTOP CLEANUP ═══
            if (isOnOtherDesktop)
            {
                Classes.Logger.LogAction("VD_TOGGLE", "VD_CLEANUP: Window on other desktop, resetting state");
                EnsureClipboardMode();
                _lastPanelBeforeDismiss = null;
                _isCurrentlySummoned = false;
                UninstallKeyboardHook();
                _isAnimatingHide = false;
            }

            // Capture whether this is a desktop-switch resummon BEFORE zombie check clears the flag.
            bool wasDesktopSwitch = _desktopSwitchedSinceLastDismiss || isOnOtherDesktop;
            _desktopSwitchedSinceLastDismiss = false; // CONSUME immediately — prevents stale flag on rapid re-entry
            Classes.Logger.LogAction("VD_TOGGLE", $"wasDesktopSwitch={wasDesktopSwitch} (deskSwitchFlag={_desktopSwitchedSinceLastDismiss} || isOther={isOnOtherDesktop})");

            // ═══ ZOMBIE STATE DETECTOR ═══
            // Window is offscreen and not summoned — just reset state.
            // No Hide+Show cycle needed since window is always pinned to all desktops.
            bool zombieRecovered = false;
            if (!_isCurrentlySummoned && !this.IsVisible)
            {
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);

                EnsureClipboardMode();
                _desktopSwitchedSinceLastDismiss = false;
                _isAnimatingHide = false;
                _isCurrentlySummoned = false;
                UninstallKeyboardHook();
                zombieRecovered = true;
            }

            // On desktop switch, clear panel memory — new desktop always starts with clipboard
            if (wasDesktopSwitch)
            {
                Classes.Logger.LogAction("VD_TOGGLE", "Clearing panel memory (desktop switch)");
                _lastPanelBeforeDismiss = null;
            }

            if (!zombieRecovered && _isCurrentlySummoned && (_viewModel.CurrentMode == 1 || _viewModel.CurrentMode == 0) && !_isAnimatingHide)
            {
                Classes.Logger.LogAction("VD_TOGGLE", $"TOGGLE_OFF: Already visible (Opacity={this.Opacity:F2}) → AnimateAndHide");
                AnimateAndHide();
            }
            else
            {
                // If the show animation is still in progress (or within its 150ms duration),
                // we CANNOT trigger a show/summon. It can only be used for dismiss.
                if (DateTime.UtcNow < _showAnimationEndTime)
                {
                    Classes.Logger.LogAction("VD_TOGGLE", "SHOW_PATH IGNORED: Show animation is still in progress.");
                    return;
                }

                Classes.Logger.LogAction("VD_TOGGLE", $"SHOW_PATH: zombieRecovered={zombieRecovered} summoned={_isCurrentlySummoned}");

                // ═══ RESET FILTERS ON RESUMMON ═══
                if (_activeCategoryFilter != null)
                    ClearCategoryFilter();
                if (_isFilterBarActive)
                    ToggleFilterBar(false);
                if (_isSearchActive)
                    CloseSearch();

                double targetX = -1;
                double targetY = -1;
                bool positionFound = false;

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
                    var workArea = SystemParameters.WorkArea;
                    double safeWidth = double.IsNaN(this.Width) ? 360 : this.Width;
                    if (safeWidth <= 0) safeWidth = 320;

                    // Always spawn bottom-left — consistent with the widget's default position
                    targetX = workArea.Left + 16 + (safeWidth / 2);
                    targetY = workArea.Top + workArea.Height;
                    Classes.Logger.LogAction("SUMMON", $"Spawn fallback (bottom-left) at logical X={targetX}, Y={targetY}");
                }

                Classes.Logger.LogAction("VD_TOGGLE", $"SHOW: Calling ShowNearPosition at ({targetX:F0}, {targetY:F0}), knownOnOther={wasDesktopSwitch}");
                ShowNearPosition(targetX, targetY, 1, false, false, knownOnOtherDesktop: wasDesktopSwitch);

                // ═══ SAME-DESKTOP PANEL RESTORE ═══
                // If the last panel was Notes/Todo and this is NOT a desktop switch,
                // re-open the panel after the clipboard finishes appearing.
                if (!wasDesktopSwitch && _lastPanelBeforeDismiss != null)
                {
                    string panelToRestore = _lastPanelBeforeDismiss;
                    _lastPanelBeforeDismiss = null; // Consume — only restore once
                    Classes.Logger.LogAction("VD_TOGGLE", $"PANEL_RESTORE: Restoring '{panelToRestore}' (same desktop)");

                    // Defer panel open to after the show animation completes
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isCurrentlySummoned) return; // Cancelled before we got here

                        if (panelToRestore == "notes")
                            OpenNotesPanel();
                        else if (panelToRestore == "todo")
                            OpenTodoPanel();
                        else if (panelToRestore == "research")
                            OpenResearchPanel();
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    Classes.Logger.LogAction("VD_TOGGLE", $"NO_RESTORE: wasDesktopSwitch={wasDesktopSwitch} lastPanel={_lastPanelBeforeDismiss ?? "null"}");
                }
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
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            try
            {
                // Dismiss the clipboard before opening Hub — prevents the clipboard from
                // briefly disappearing behind the Topmost HubWindow and then reappearing
                // when HubWindow's Topmost is set back to false (both windows are HWND_TOPMOST).
                if (_isCurrentlySummoned && !_isAnimatingHide)
                    AnimateAndHide();

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
                            if (!IsWindowOnCurrentVirtualDesktop(hwnd))
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
                    _hubWindowInstance.Topmost = true;
                    _hubWindowInstance.Show();
                }
                else
                {
                    if (_hubWindowInstance.WindowState == WindowState.Minimized)
                        _hubWindowInstance.WindowState = WindowState.Normal;
                    _hubWindowInstance.Topmost = true;
                    _hubWindowInstance.Show();
                }
                _hubWindowInstance.Activate();
                _hubWindowInstance.Focus();
                _hubWindowInstance.Topmost = false;
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

        /// <summary>
        /// Internal hub window opener — doesn't hide clipboard.
        /// Called from tray menu handlers.
        /// </summary>
        private void OpenApp_Click_Internal()
        {
            try
            {
                CloseEmojiPicker();
                if (_hubWindowInstance != null && _hubWindowInstance.IsLoaded)
                {
                    bool needsRecreate = false;
                    try
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(_hubWindowInstance).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            if (!IsWindowOnCurrentVirtualDesktop(hwnd))
                                needsRecreate = true;
                        }
                    }
                    catch (Exception ex) { Classes.Logger.LogAction("HUB_VD_CHECK_ERROR", ex.Message); }

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
                    _hubWindowInstance.Topmost = true;
                    _hubWindowInstance.Show();
                }
                else
                {
                    if (_hubWindowInstance.WindowState == WindowState.Minimized)
                        _hubWindowInstance.WindowState = WindowState.Normal;
                    _hubWindowInstance.Topmost = true;
                    _hubWindowInstance.Show();
                }
                _hubWindowInstance.Activate();
                _hubWindowInstance.Focus();
                _hubWindowInstance.Topmost = false;
            }
            catch (Exception ex)
            {
                _hubWindowInstance = null;
                FlyShelf.Classes.Logger.LogAction("HUBWINDOW_FAIL", ex.ToString());
            }
        }

        private void ShelfListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsDeletingItem) return;

            var listView = sender as System.Windows.Controls.ListView;
            if (listView == null) return;

            // Only manage the Unpin button here. Merge bar is controlled by checkbox toggles.
            if (listView.SelectedItems.Count > 1)
            {
                int pinnedCount = 0;
                foreach (var item in listView.SelectedItems)
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
            }
            else
            {
                UnpinSelectedBtn.Visibility = Visibility.Collapsed;
                UpdateToolbarButtonsVisibility();
            }
        }

        private void UnpinSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            var listView = _isAltUIActive ? AltShelfListView : ShelfListView;
            var pinnedSelected = listView.SelectedItems
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
            listView.SelectedItems.Clear();
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

            // ── When search is active, these buttons must stay collapsed so the
            //    search bar gets full width.  SearchToggle_Click already hides them;
            //    honour that state here to prevent a deferred call from restoring them.
            if (SearchToggleBtn != null)
            {
                SearchToggleBtn.Visibility = _isSearchActive ? Visibility.Collapsed : Visibility.Visible;
            }

            if (OpenSettingsBtn != null)
            {
                OpenSettingsBtn.Visibility = (isMini || _isSearchActive) ? Visibility.Collapsed : Visibility.Visible;
            }

            if (NotesToggleBtn != null)
            {
                NotesToggleBtn.Visibility = _isSearchActive ? Visibility.Collapsed
                    : ((_isTodoActive || !isMini) ? Visibility.Visible : Visibility.Collapsed);
            }

            if (TodoToggleBtn != null)
            {
                TodoToggleBtn.Visibility = _isSearchActive ? Visibility.Collapsed
                    : ((_isTodoActive || !isMini) ? Visibility.Visible : Visibility.Collapsed);
            }

            if (TodoStopwatchBtn != null)
            {
                TodoStopwatchBtn.Visibility = (_isTodoActive && !_isSearchActive) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SortFilterBtn != null)
            {
                SortFilterBtn.Visibility = (_isTodoActive || _isSearchActive) ? Visibility.Collapsed : Visibility.Visible;
            }

            if (MoreBtn != null)
            {
                MoreBtn.Visibility = (isMini || _isSearchActive) ? Visibility.Collapsed : Visibility.Visible;
            }

            if (ShelfListView != null)
            {
                ScrollViewer.SetVerticalScrollBarVisibility(ShelfListView, isMini ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
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

        /// <summary>
        /// Shows/hides the Alt+C watermark hint at the bottom of the clipboard.
        /// Visible until the view has enough items to be considered "filled" (≥5 items).
        /// </summary>
        private void UpdateAltCWatermarkVisibility()
        {
            if (AltCWatermarkHint == null) return;
            bool showHint = _viewModel.DroppedItems.Count < 5;
            AltCWatermarkHint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
