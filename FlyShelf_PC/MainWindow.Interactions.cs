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
        internal static volatile bool _isDragging = false;

        // ═══ Ctrl+Drag Path Mode State ═══
        // Both file data and path text are always prepared at drag start.
        // Pressing Ctrl during drag live-swaps the DataObject from file→path.
        // Releasing Ctrl reverts. No drag cancellation needed.
        private DataObject? _activeDragDataObj;
        private string[]? _dragOriginalFilePaths;
        private string? _dragOriginalText;
        private string? _dragFilePath;        // Always populated with the item's path
        private bool _dragCtrlPathMode = false;
        private string? _dragCtrlPendingPath; // Path text for fallback paste (text-only items)

        private async void ShelfListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
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

                        // ═══ Contextual Tip: Double-click to open ═══
                        if (!string.IsNullOrEmpty(clipboardObj.FilePath))
                            Windows.TipBadge.Show("doubleclick_hint", "🖱️ Double-click to open files directly");

                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("PASTE_ERROR", ex.Message);
            }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("MOUSEUP_FAULT", $"Unhandled: {ex.Message}");
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
                    // For Image items, ALWAYS use file path as text — never OCR text.
                    // OCR text in RawContent would cause text fields to receive the OCR result
                    // instead of the image file, which is almost never what the user wants.
                    bool shouldPastePath = clipboardObj.IsTerminalPreview ||
                        clipboardObj.ItemType == ClipboardItemType.Image ||
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
                                using var ms = new System.IO.MemoryStream(bytes); // PERF: dispose after OnLoad decode
                                bi.StreamSource = ms;
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

            // NOTE: Intentionally NOT calling MoveItemToTop() here.
            // Items should stay in their original position after being pasted/dragged out.
            // Moving to top was disorienting and caused expensive VirtualizingStackPanel re-layout.
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
                else
                {
                    // STABILITY FIX: Click hit empty space (e.g. 250px bottom padding below last item).
                    // Without this guard, the click falls through to WPF's default ListView handling,
                    // which under stress (residual SmoothScroll velocity) causes scroll-to-focus jumps.
                    // Clear selection, kill any residual scroll physics, and consume the event.
                    listView.SelectedItems.Clear();
                    Classes.SmoothScroll.ResetScrollState(GetShelfScrollViewer());
                    _shouldPreventDrag = true;
                }
            }
        }

        private async void ShelfListView_MouseMove(object sender, MouseEventArgs e)
        {
            // [FIX H-2]: Top-level guard for async void — a COM exception from DoDragDrop
            // would otherwise crash the app since there's no caller to catch it.
            try
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

                        // STABILITY: Set drag flag EARLY to suppress clipboard events during
                        // the entire drag preparation (DataObject build, wait-for-FilePath, etc.)
                        // This prevents race conditions where new items are inserted mid-drag.
                        _isDragging = true;

                        DataObject dataObj = new DataObject();
                        
                        // ═══ FIX: Wait for async PNG save to complete for bitmap-only clipboard items ═══
                        // When copying an image from an app (Photos, browser, etc.), only a bitmap
                        // is on the clipboard — no file. FlyShelf saves it to PNG in the background,
                        // but FilePath is empty until that completes. Wait briefly for it.
                        if (string.IsNullOrEmpty(firstItem.FilePath) && firstItem.ItemType == ClipboardItemType.Image)
                        {
                            // [FIX DD-4]: Reduced max pre-drag wait from 2s to 1s for faster drag start
                            for (int waitLoop = 0; waitLoop < 10 && string.IsNullOrEmpty(firstItem.FilePath); waitLoop++)
                                await System.Threading.Tasks.Task.Delay(100); // Wait up to 1 second total
                            
                            // If FilePath is STILL empty but we have an in-memory thumbnail, save it now
                            if (string.IsNullOrEmpty(firstItem.FilePath) && firstItem.Icon is BitmapSource iconBmp)
                            {
                                try
                                {
                                    string tempPath = Classes.ClipboardHistoryManager.GetPersistentImagePath();
                                    var frozenBmp = iconBmp.IsFrozen ? iconBmp : (BitmapSource)iconBmp.GetAsFrozen();
                                    await System.Threading.Tasks.Task.Run(() =>
                                    {
                                        using var fs = new System.IO.FileStream(tempPath, System.IO.FileMode.Create);
                                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frozenBmp));
                                        encoder.Save(fs);
                                    });
                                    firstItem.FilePath = tempPath;
                                    Classes.Logger.LogAction("DRAG_FIX", $"Saved thumbnail as fallback: {tempPath}");
                                }
                                catch (Exception saveEx) { Classes.Logger.LogAction("DRAG_SAVE_ERR", saveEx.Message); }
                            }
                        }

                        if (!string.IsNullOrEmpty(firstItem.FilePath))
                        {
                            // Always provide FileDrop for items with a path
                            dataObj.SetData(DataFormats.FileDrop, new string[] { firstItem.FilePath });
                            dataObj.SetData("FileNameW", new string[] { firstItem.FilePath });
                            dataObj.SetData("FileName", new string[] { firstItem.FilePath });
                            try { dataObj.SetData("text/uri-list", "file:///" + firstItem.FilePath.Replace("\\", "/")); } catch (Exception ex) { Classes.Logger.LogAction("DRAG_URI_LIST_ERROR", ex.Message); }

                            // If we also have text content (Markdown, Code, Txt), provide it as well for text-only drop targets
                            // For Image items, DON'T set OCR text — it would override the file drop in text targets
                            if (!string.IsNullOrEmpty(firstItem.RawContent) && firstItem.ItemType != ClipboardItemType.Image)
                            {
                                dataObj.SetData(DataFormats.UnicodeText, firstItem.RawContent);
                                dataObj.SetData(DataFormats.Text, firstItem.RawContent);
                                dataObj.SetData(DataFormats.StringFormat, firstItem.RawContent);
                            }

                            if (firstItem.ItemType == ClipboardItemType.Image)
                            {
                                try
                                {
                                    // Load a tiny thumbnail for the OLE bitmap format.
                                    // Uses FileShare.ReadWrite to handle files still being written by the save pipeline.
                                    var bmp = await System.Threading.Tasks.Task.Run(() =>
                                    {
                                        byte[] bytes;
                                        using (var readFs = new System.IO.FileStream(firstItem.FilePath, 
                                            System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                                        {
                                            bytes = new byte[readFs.Length];
                                            readFs.Read(bytes, 0, bytes.Length);
                                        }
                                        var bi = new BitmapImage();
                                        bi.BeginInit();
                                        using var ms = new System.IO.MemoryStream(bytes);
                                        bi.StreamSource = ms;
                                        bi.CacheOption = BitmapCacheOption.OnLoad;
                                        bi.DecodePixelWidth = 128; // Lightweight thumbnail for drag preview
                                        bi.EndInit();
                                        bi.Freeze();
                                        return bi;
                                    });

                                    // ═══ FIX: SetImage can crash on transparent PNGs ═══
                                    // WPF DataObject.SetImage() internally creates an HBITMAP which
                                    // doesn't support alpha channels. This can throw COMException or
                                    // OutOfMemoryException with transparent images. Wrap safely.
                                    try
                                    {
                                        dataObj.SetImage(bmp);
                                    }
                                    catch (Exception setImgEx)
                                    {
                                        // SetImage failed (likely transparent PNG) — skip bitmap format.
                                        // FileDrop is already set, so the drag will still work for file targets.
                                        Classes.Logger.LogAction("DRAG_IMAGE", $"SetImage skipped (transparent?): {setImgEx.Message}");
                                    }
                                }
                                catch (Exception ex) { Classes.Logger.LogAction("DRAG_IMAGE_ERROR", ex.Message); }
                            }

                            // Explicit Win32 Shell 'Copy' Effect override (Required for Windows Explorer Drag Drop)
                            byte[] moveEffect = new byte[] { 5, 0, 0, 0 }; // DragDropEffects.Copy
                            // [FIX DD-6]: Added 'using' to prevent MemoryStream leak on every drag
                            using var dropEffect = new System.IO.MemoryStream();
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

                            // ═══ Contextual Tip: Ctrl+Drag path mode ═══
                            // Only show for items with a file path (not text-only), and only the first 3 times
                            if (!string.IsNullOrEmpty(firstItem.FilePath) && _dragPreviewWindow != null)
                            {
                                Windows.TipBadge.ShowLimited("ctrl_drag_path", "💡 Hold Ctrl to paste file path instead", 3, _dragPreviewWindow);
                            }
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

                            // ═══ Ctrl+Drag Path Mode: store originals for live swap ═══
                            // Both file and path are always ready. Ctrl toggles which is active.
                            _activeDragDataObj = dataObj;
                            _dragCtrlPathMode = false;
                            _dragFilePath = firstItem.FilePath ?? firstItem.RawContent;
                            _dragOriginalFilePaths = dataObj.GetDataPresent(DataFormats.FileDrop)
                                ? dataObj.GetData(DataFormats.FileDrop) as string[]
                                : null;
                            _dragOriginalText = dataObj.GetDataPresent(DataFormats.UnicodeText)
                                ? dataObj.GetData(DataFormats.UnicodeText) as string
                                : null;
                            // Pre-compute the path text so it's instant when Ctrl is pressed
                            _dragCtrlPendingPath = !string.IsNullOrEmpty(firstItem.FilePath)
                                ? firstItem.FilePath
                                : null;

                            // Record cursor position BEFORE drag starts — we'll use it for fallback paste
                            DragDropEffects result;
                            try
                            {
                                result = DragDrop.DoDragDrop(listView, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
                            }
                            catch (System.Runtime.InteropServices.COMException comEx)
                            {
                                // OLE drag-drop can throw COMException (e.g. 0x8004005E) when
                                // the target rejects the drop or the bitmap format conversion fails
                                // inside the OLE subsystem. Treat as a cancelled drop.
                                Classes.Logger.LogAction("DRAG_COM", $"OLE COMException: 0x{comEx.ErrorCode:X8} — {comEx.Message}");
                                result = DragDropEffects.None;
                            }
                            // ═══ POST-DRAG PASTE ═══
                            // Two scenarios:
                            // 1. Ctrl+Drag PATH MODE: Always paste the file path as text via
                            //    clipboard+Ctrl+V, regardless of OLE result. OLE DataObject mutation
                            //    doesn't work reliably mid-drag (targets cache formats at DragEnter).
                            // 2. Normal mode, OLE rejected (result == None): Fall back to pasting
                            //    text content via clipboard, covering text fields and browsers.
                            bool shouldPastePath = _dragCtrlPathMode && !string.IsNullOrEmpty(_dragCtrlPendingPath);
                            string fallbackText = shouldPastePath
                                ? _dragCtrlPendingPath!
                                : (result == DragDropEffects.None ? firstItem.RawContent : null);
                            
                            if (!string.IsNullOrEmpty(fallbackText))
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
                                            Classes.ClipboardHelper.SafeSetText(fallbackText, suppressEcho: true, echoDelayMs: 2000);
                                            
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

                            // ═══ POST-DRAG COOLDOWN ═══
                            // Keep WM_CLIPBOARDUPDATE guard active for 500ms after drag completes
                            // to suppress self-capture from target app clipboard writes.
                            _ = Dispatcher.InvokeAsync(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(500);
                                _isDragging = false;
                            });
                            dragDropEffect?.Dispose();

                            // ═══ Deselect after drag-out ═══
                            // Prevents Enter key from re-copying the same content
                            // after user drags an item out and presses Enter to submit
                            listView.SelectedItems.Clear();

                            // ═══ Cleanup Ctrl+Drag state ═══
                            _activeDragDataObj = null;
                            _dragOriginalFilePaths = null;
                            _dragOriginalText = null;
                            _dragFilePath = null;
                            _dragCtrlPathMode = false;
                            _dragCtrlPendingPath = null;

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
            catch (Exception ex)
            {
                _isDragging = false; // Safety reset: inner finally may not have executed
                Classes.Logger.LogAction("MOUSEMOVE_FAULT", $"Unhandled exception in ShelfListView_MouseMove: {ex.Message}");
            }
        }

        /// <summary>
        /// GiveFeedback handler — fires continuously during drag to track cursor position.
        /// Updates the DragPreviewWindow to follow the mouse with zero lag.
        /// </summary>
        private void DragPreview_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            try
            {
                if (_dragPreviewWindow != null &&
                    Classes.NativeMethods.GetCursorPos(out var pt))
                {
                    _dragPreviewWindow.UpdatePosition(pt.X, pt.Y);
                }
            }
            catch { } // STABILITY: Exceptions in GiveFeedback crash the OLE message loop

            // Keep the default system cursors (copy/move arrows, no-drop circle)
            e.UseDefaultCursors = true;
            e.Handled = true;
        }

        /// <summary>
        /// QueryContinueDrag handler — fires continuously when drag state changes.
        /// - Cancel/Drop: close the preview immediately.
        /// - Ctrl held: live-swap DataObject from file → path text (no drag cancel).
        /// - Ctrl released: revert DataObject back to file data.
        /// Both sides are always prepared at drag start — Ctrl just toggles which is active.
        /// </summary>
        private void DragPreview_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.Action == DragAction.Cancel || e.Action == DragAction.Drop)
            {
                // Drag is ending — close preview immediately, don't wait for finally block
                try { _dragPreviewWindow?.SafeClose(); } catch { } // Best-effort: failure is acceptable
                _dragPreviewWindow = null;
                return;
            }

            if (e.Action != DragAction.Continue || _activeDragDataObj == null)
                return;

            bool ctrlPressed = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);

            if (ctrlPressed && !_dragCtrlPathMode)
            {
                // ═══ CTRL PRESSED: Live-swap to path mode ═══
                // Replace DataObject content with path text. The OLE drag continues
                // uninterrupted — the target app receives path text on drop.
                _dragCtrlPathMode = true;

                string pathText = !string.IsNullOrEmpty(_dragFilePath)
                    ? _dragFilePath
                    : _dragOriginalText ?? "";

                try
                {
                    // Clear file drop data so target app gets text, not a file.
                    // NOTE: SetData(null) doesn't remove the format from WPF DataObject —
                    // target apps still see FileDrop and prefer it. Use empty array instead.
                    _activeDragDataObj.SetData(DataFormats.FileDrop, new string[0]);
                    _activeDragDataObj.SetData("Preferred DropEffect", null);
                    // Also clear bitmap data — some apps prefer bitmap over text
                    _activeDragDataObj.SetData(DataFormats.Bitmap, (object)null);
                    _activeDragDataObj.SetData("FileNameW", new string[0]);
                    _activeDragDataObj.SetData("FileName", new string[0]);

                    // Set all text formats to the path
                    _activeDragDataObj.SetData(DataFormats.UnicodeText, pathText);
                    _activeDragDataObj.SetData(DataFormats.Text, pathText);
                    _activeDragDataObj.SetData(DataFormats.StringFormat, pathText);
                }
                catch { } // Best-effort: if swap fails, drag continues with original data

                // Update drag preview to show path mode indicator
                try { _dragPreviewWindow?.SetPathMode(true); } catch { }
            }
            else if (!ctrlPressed && _dragCtrlPathMode)
            {
                // ═══ CTRL RELEASED: Revert to file mode ═══
                // Restore original DataObject content — seamless toggle back.
                _dragCtrlPathMode = false;

                try
                {
                    // Restore file drop data
                    if (_dragOriginalFilePaths != null)
                    {
                        _activeDragDataObj.SetData(DataFormats.FileDrop, _dragOriginalFilePaths);
                        _activeDragDataObj.SetData("FileNameW", _dragOriginalFilePaths);
                        _activeDragDataObj.SetData("FileName", _dragOriginalFilePaths);
                        // Restore copy effect
                        byte[] copyEffect = new byte[] { 5, 0, 0, 0 };
                        using var effectStream = new System.IO.MemoryStream();
                        effectStream.Write(copyEffect, 0, copyEffect.Length);
                        _activeDragDataObj.SetData("Preferred DropEffect", effectStream);
                    }

                    // Restore original text
                    if (_dragOriginalText != null)
                    {
                        _activeDragDataObj.SetData(DataFormats.UnicodeText, _dragOriginalText);
                        _activeDragDataObj.SetData(DataFormats.Text, _dragOriginalText);
                        _activeDragDataObj.SetData(DataFormats.StringFormat, _dragOriginalText);
                    }
                }
                catch { } // Best-effort: if revert fails, drag continues with path data

                // Revert drag preview
                try { _dragPreviewWindow?.SetPathMode(false); } catch { }
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
            OpenApp_Click(this, new RoutedEventArgs());
        }

        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            try
            {
                // Force-hide the clipboard immediately (not animated) before opening Hub —
                // animated hide has a delay during which the Hub's Topmost toggle can cause
                // the clipboard to briefly flash/respawn.
                if (_isCurrentlySummoned && !_isAnimatingHide)
                {
                    _isCurrentlySummoned = false;
                    _isShowAnimating = false;

                    // ═══ FULL CLEANUP: Mirror AnimateAndHide() — PC-1 bug fix ═══
                    // Previously this path only did this.Hide() with no cleanup.
                    // Missing cleanup caused: keyboard hooks intercepting keys globally,
                    // timers firing on hidden windows, stale panel/search/merge state.

                    UninstallKeyboardHook(); // Release arrow-key hook so keys return to other apps
                    StopDragActiveDismissTimer();
                    StopPanelAutoRevertTimer();
                    _mascotDelayTimer?.Stop();

                    // Save panel state so it can restore on re-summon
                    if (_isNotesActive || _isTodoActive || _isResearchActive)
                    {
                        _lastPanelBeforeDismiss = _isNotesActive ? "notes" : _isTodoActive ? "todo" : "research";
                        if (_isNotesActive) CloseNotesPanel(immediate: true);
                        if (_isTodoActive) CloseTodoPanel(immediate: true);
                        if (_isResearchActive) CloseResearchPanel(immediate: true);
                    }

                    DismissMergeState();      // PC-4: Clear PDF merge bar
                    CloseSearch();            // Clear search state
                    if (_isFilterBarActive) ToggleFilterBar(false); // PC-5: Clear filter bar

                    // PC-8: Reset drag hover indicator
                    IsDragHovering = false;

                    // PC-9: Reset scroll/hover state so hover buttons work on re-summon
                    _viewModel.IsScrolling = false;
                    _viewModel.AllowHover = true;

                    // PC-2/PC-3: Stop background timers that shouldn't fire on hidden window
                    _evictionBackgroundTimer?.Stop();
                    _scrollLiveLoadTimer?.Stop();

                    // Grey-box fix: Reset scroll engine state
                    try
                    {
                        var sv = GetShelfScrollViewer();
                        Classes.SmoothScroll.ResetScrollState(sv);
                    }
                    catch { }
                }

                // ═══ ALWAYS hide clipboard before Hub — prevents black-shape ghost ═══
                // Even if the clipboard wasn't _isCurrentlySummoned, the DWM surface may
                // still be alive (SW_HIDE keeps it warm). The Hub's Topmost toggle can
                // cause the OS to briefly make the clipboard visible as a dark rectangle.
                // Force opacity=0 + SW_HIDE to guarantee it stays invisible.
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                        Classes.NativeMethods.ShowWindow(hwnd, 0 /*SW_HIDE*/);
                }
                catch { }


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
                // ═══ ALWAYS hide clipboard before Hub — prevents ghost clipboard ═══
                // Same as OpenApp_Click: force opacity=0 + SW_HIDE to guarantee it stays invisible.
                _isCurrentlySummoned = false;
                _isShowAnimating = false;
                this.Opacity = 0;
                this.BeginAnimation(OpacityProperty, null);
                try
                {
                    var mainHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (mainHwnd != IntPtr.Zero)
                        Classes.NativeMethods.ShowWindow(mainHwnd, 0 /*SW_HIDE*/);
                }
                catch { }

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
                _viewModel.SchedulePersistHistoryPublic(); // PERF: throttled — unpin is non-critical
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
