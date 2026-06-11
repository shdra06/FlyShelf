// ---------------------------------------------------------------
// MainWindow � Event Handlers
// Drag/Drop, Search, Item Actions (Pin/Delete/Open/QuickLook),
// Scroll, KeyDown, NotifyIcon, ContextMenu
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlyShelf
{
    public partial class MainWindow
    {
        internal static bool _isInternalDragSource = false;
        private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            IsDragHovering = false;
            _spawnToken++; 

            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _viewModel.HandleDrop(e.Data, true);
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            IsDragHovering = false;
            _spawnToken++; 

            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _viewModel.HandleDrop(e.Data, true);
            e.Handled = true;
        }

    

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            _isDragHovering = true;
            IsDragHovering = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || 
                e.Data.GetDataPresent("FileNameW") ||
                e.Data.GetDataPresent("FileName") ||
                e.Data.GetDataPresent("text/uri-list") ||
                e.Data.GetDataPresent("application/vnd.code.tree.workspaceFiles") ||
                e.Data.GetDataPresent(DataFormats.Bitmap) || 
                e.Data.GetDataPresent(DataFormats.Dib) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) || 
                e.Data.GetDataPresent(DataFormats.StringFormat) ||
                e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Performance Fix: Do NOT query 'e.Data.GetDataPresent' across cross-process COM COM-wrappers 
            // inside 'DragOver' because this fires hundreds of times a second and completely hangs the UI thread!
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            IsDragHovering = false;
            // The user explicitly requested an impenetrable UI overlay without funky Hide bugs on child-element hovers.
            // Leaving the physical window drag-space now does NOT force kill the app interface!
        }


        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isSuppressingSizeSync) return;
            // Don't persist the shrunken height caused by card deletion — it would corrupt
            // the stored MiniFormHeight, making future summons spawn at the wrong size.
            if (IsDeletingItem) return;

            if (e.NewSize.Width > 100 && e.NewSize.Height > 100)
            {
                // Only persist size changes for the CURRENT mode — prevents mode 1
                // content-driven height from corrupting mode 0 stored dimensions
                if (_viewModel.CurrentMode == 0)
                {
                    Classes.SettingsManager.Current.MiniFormWidth = (int)e.NewSize.Width;
                    Classes.SettingsManager.Current.MiniFormHeight = (int)e.NewSize.Height;
                }
                // Mode 2 (Full) is always screen-relative, no persistence needed
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source)
            {
                var parentBtn = FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source);
                if (parentBtn != null) return; // Ignore drag if the user explicitly clicked a child button!
            }

            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2)
                {
                    return; // Never maximize the FlyShelf
                }

                _isEdgeLocked = false;
                try
                {
                    this.DragMove();
                }
                catch { } 
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // SYNC DIRECTION CONTROLS — Inline Responsive Bar
        // ═══════════════════════════════════════════════════════════════════

        private bool _isSyncBarActive = false;

        private void ToggleGlobalSync_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            ToggleSyncBar(!_isSyncBarActive);
        }

        private void ToggleSyncBar(bool show)
        {
            if (SyncDirectionBar == null) return;

            _isSyncBarActive = show;

            if (show)
            {
                // Close other bars if active
                if (_isFilterBarActive) ToggleFilterBar(false);

                // Update pill highlights to match current state
                UpdateSyncButtonHighlights();

                SyncDirectionBar.Visibility = Visibility.Visible;

                // Smooth slide-down + fade-in animation (same pattern as SortFilterInlineBar)
                var slideAnim = Classes.AnimationHelper.SlideIn();
                var fadeAnim = Classes.AnimationHelper.FadeIn();

                if (SyncDirectionBar.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                SyncDirectionBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            else
            {
                // Smooth slide-up + fade-out animation
                var slideAnim = Classes.AnimationHelper.SlideOut();
                var fadeAnim = Classes.AnimationHelper.FadeOut(120);

                fadeAnim.Completed += (s, args) =>
                {
                    if (!_isSyncBarActive)
                    {
                        SyncDirectionBar.Visibility = Visibility.Collapsed;
                    }
                };

                if (SyncDirectionBar.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                SyncDirectionBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        }

        private void SyncIncoming_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var settings = FlyShelf.Classes.SettingsManager.Current;
            settings.EnableIncomingSync = !settings.EnableIncomingSync;

            // IMPORTANT: Toggling incoming/outgoing individually NEVER kills connections.
            // Connections (Cloudflare, LAN, PeerManager) stay alive at all times.
            // The toggle is a pure data-flow gate checked at the moment content arrives.
            // Only the "Both" toggle (full offline) kills connections.
            if (settings.EnableIncomingSync && !settings.EnableCloudDiscovery)
            {
                // Re-enable connections if they were killed by the "Both" off toggle
                settings.EnableCloudDiscovery = true;
                settings.EnableGlobalCloudflare = true;
                settings.EnableLocalLAN = true;
                TriggerInstantResync();
            }

            FlyShelf.Classes.SettingsManager.Save();
            UpdateSyncButtonHighlights();

            FlyShelf.Classes.Logger.LogAction("SYNC_TOGGLE", $"Incoming sync: {(settings.EnableIncomingSync ? "ON" : "OFF")} (connections preserved)");
        }

        private void SyncOutgoing_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var settings = FlyShelf.Classes.SettingsManager.Current;
            settings.EnableOutgoingSync = !settings.EnableOutgoingSync;

            // IMPORTANT: Toggling outgoing NEVER kills connections.
            // Cloudflare tunnel, LAN server, and PeerManager stay alive.
            // EnableOutgoingSync is a pure soft-gate checked in HandleDropInternal
            // (DropHandler.cs lines 236, 485, 712) before any content is pushed.
            if (settings.EnableOutgoingSync && !settings.EnableCloudDiscovery)
            {
                // Re-enable connections if they were killed by the "Both" off toggle
                settings.EnableCloudDiscovery = true;
                settings.EnableGlobalCloudflare = true;
                settings.EnableLocalLAN = true;
                TriggerInstantResync();
            }

            FlyShelf.Classes.SettingsManager.Save();
            UpdateSyncButtonHighlights();

            FlyShelf.Classes.Logger.LogAction("SYNC_TOGGLE", $"Outgoing sync: {(settings.EnableOutgoingSync ? "ON" : "OFF")} (connections preserved)");
        }

        private void SyncBoth_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var settings = FlyShelf.Classes.SettingsManager.Current;
            bool bothOn = settings.EnableIncomingSync && settings.EnableOutgoingSync && settings.EnableCloudDiscovery;

            if (bothOn)
            {
                // Turn everything OFF
                settings.EnableCloudDiscovery = false;
                settings.EnableGlobalCloudflare = false;
                settings.EnableLocalLAN = false;
                settings.EnableIncomingSync = false;
                settings.EnableOutgoingSync = false;
            }
            else
            {
                // Turn everything ON
                settings.EnableCloudDiscovery = true;
                settings.EnableGlobalCloudflare = true;
                settings.EnableLocalLAN = true;
                settings.EnableIncomingSync = true;
                settings.EnableOutgoingSync = true;
            }
            FlyShelf.Classes.SettingsManager.Save();
            UpdateSyncButtonHighlights();

            if (!bothOn)
            {
                TriggerInstantResync();
            }
        }

        private void TriggerInstantResync()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                // Poll for up to 3 seconds to find a healthy, running PeerManager.Instance
                for (int i = 0; i < 30; i++)
                {
                    var instance = FlyShelf.Classes.PeerManager.Instance;
                    if (instance != null && instance.IsRunning)
                    {
                        FlyShelf.Classes.Logger.LogAction("UI_TRIGGER", "Found active PeerManager.Instance — running ForceResync instantly");
                        await instance.ForceResync();
                        return;
                    }
                    await System.Threading.Tasks.Task.Delay(100);
                }
                FlyShelf.Classes.Logger.LogAction("UI_TRIGGER", "⚠️ Could not find active PeerManager.Instance within timeout");
            });
        }

        private void SyncBarDismiss_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToggleSyncBar(false);
        }

        private void UpdateSyncButtonHighlights()
        {
            var settings = FlyShelf.Classes.SettingsManager.Current;
            bool inOn = settings.EnableIncomingSync && settings.EnableCloudDiscovery;
            bool outOn = settings.EnableOutgoingSync && settings.EnableCloudDiscovery;
            bool bothOn = inOn && outOn;

            // Incoming pill + dot
            UpdateSyncPillHighlight(SyncBtn_Incoming, SyncDot_Incoming, inOn, "#10B981");
            // Outgoing pill + dot
            UpdateSyncPillHighlight(SyncBtn_Outgoing, SyncDot_Outgoing, outOn, "#3B82F6");
            // Both pill + dot
            UpdateSyncPillHighlight(SyncBtn_Both, SyncDot_Both, bothOn, "#8B5CF6");
        }

        private void UpdateSyncPillHighlight(System.Windows.Controls.Border btn, System.Windows.Shapes.Ellipse dot, bool isActive, string accentHex)
        {
            if (btn == null) return;
            var accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentHex);
            if (isActive)
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, accent.R, accent.G, accent.B));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, accent.R, accent.G, accent.B));
            }
            else
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, accent.R, accent.G, accent.B));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, accent.R, accent.G, accent.B));
            }

            // Toggle dot indicator: bright accent glow when ON, dim gray when OFF
            if (dot != null)
            {
                if (isActive)
                {
                    dot.Fill = new System.Windows.Media.SolidColorBrush(accent);
                    dot.Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, accent.R, accent.G, accent.B));
                }
                else
                {
                    var mutedColor = ((System.Windows.Media.SolidColorBrush)FindResource("ThemeTextMuted")).Color;
                    dot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, mutedColor.R, mutedColor.G, mutedColor.B));
                    dot.Stroke = (System.Windows.Media.Brush)FindResource("ThemeOverlayBorder");
                }
            }
        }

        private bool _isClearConfirmActive = false;

        private void ClearShelf_ShowConfirm(object sender, RoutedEventArgs e)
        {
            ToggleClearConfirmPanel(!_isClearConfirmActive);
        }

        private void ClearShelf_Confirm(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToggleClearConfirmPanel(false);
            _viewModel.ClearShelf();
            Windows.ToastWindow.ShowToast("Shelf cleared! 🧹");
        }

        private void ClearShelf_Cancel(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ToggleClearConfirmPanel(false);
        }

        private void ToggleClearConfirmPanel(bool show)
        {
            if (ClearConfirmPanel == null) return;

            _isClearConfirmActive = show;

            if (show)
            {
                // Close filter bar if active
                if (_isFilterBarActive) ToggleFilterBar(false);

                ClearConfirmPanel.Visibility = Visibility.Visible;

                // Slide-down + fade-in (same animation as ToggleFilterBar)
                var slideAnim = Classes.AnimationHelper.SlideIn();
                var fadeAnim = Classes.AnimationHelper.FadeIn();

                if (ClearConfirmPanel.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                ClearConfirmPanel.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            else
            {
                // Slide-up + fade-out
                var slideAnim = Classes.AnimationHelper.SlideOut();
                var fadeAnim = Classes.AnimationHelper.FadeOut(120);

                fadeAnim.Completed += (s, args) =>
                {
                    if (!_isClearConfirmActive)
                    {
                        ClearConfirmPanel.Visibility = Visibility.Collapsed;
                    }
                };

                if (ClearConfirmPanel.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                ClearConfirmPanel.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        }

        private void ReminderBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            var reminderWin = new FlyShelf.Windows.ReminderCreateWindow();
            reminderWin.Left = this.Left + (this.Width - reminderWin.Width) / 2;
            reminderWin.Top = this.Top - reminderWin.Height - 8;
            if (reminderWin.Top < 0) reminderWin.Top = this.Top + this.Height + 8;
            reminderWin.Topmost = true;
            reminderWin.Show();
            reminderWin.Activate();
        }

        private FlyShelf.Windows.EmojiPickerWindow? _emojiPickerInstance;

        private void EmojiPicker_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            // Close any existing emoji picker first
            CloseEmojiPicker();

            var picker = new FlyShelf.Windows.EmojiPickerWindow(_previousForegroundWindow);
            picker.Left = this.Left + (this.Width - picker.Width) / 2;
            picker.Top = this.Top - picker.Height - 8;
            if (picker.Top < 0) picker.Top = this.Top + this.Height + 8;
            picker.Closed += (s, args) => { if (_emojiPickerInstance == picker) _emojiPickerInstance = null; };
            _emojiPickerInstance = picker;
            picker.Topmost = true;
            picker.Show();
            picker.Activate();
            picker.Focus();
        }

        /// <summary>Close the emoji picker if it's open (called when other windows are summoned).</summary>
        internal void CloseEmojiPicker()
        {
            try
            {
                if (_emojiPickerInstance != null && _emojiPickerInstance.IsLoaded)
                {
                    _emojiPickerInstance.Close();
                }
            }
            catch { }
            _emojiPickerInstance = null;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            AnimateAndHide();
            _isDragHovering = false;
            IsDragHovering = false;
        }


        private void MakePasswordSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                item.IsPassword = true;
                item.Extension = "PASSWORD";
                if (string.IsNullOrEmpty(item.FileName) || item.FileName == item.RawContent)
                {
                    item.FileName = "Protected Password";
                }
                item.GeneratePasswordIcon();
                FlyShelf.Windows.ToastWindow.ShowToast("Locked as password card! 🔒");

                // Save to history immediately
                _viewModel.PersistHistoryPublic();

                // Open the View/Edit dialog
                OpenPasswordManagerWindow(item, false);
            }
        }

        private void ConvertToZip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (item.HasZipArchive)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("📦 Zip already exists!");
                    return;
                }
                FlyShelf.Windows.ToastWindow.ShowToast("📦 Creating zip archive...");
                item.CreateZipArchive();
            }
        }

        private void SyncZipLan_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (!item.HasZipArchive)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Create a zip first!");
                    return;
                }
                _ = item.SyncZipViaLanAsync();
            }
        }

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                _viewModel.TogglePin(item);
                e.Handled = true;
            }
        }

        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                _justDeletedAnItem = true;
                AnimateAndRemoveItems(new System.Collections.Generic.List<ClipboardItem> { item });
                e.Handled = true;
            }
        }

        // Deletion anchor state — used by the ScrollChanged interceptor
        private bool _isDeletionScrollGuardActive = false;
        private int _deletionAnchorIndex = -1;
        private double _deletionAnchorTargetY = 0;
        // Shared log list so ScrollChanged guard can append entries
        private System.Collections.Generic.List<string>? _deletionLog = null;

        private void AnimateAndRemoveItems(System.Collections.Generic.List<ClipboardItem> items)
        {
            if (items == null || items.Count == 0) return;

            var log = new System.Collections.Generic.List<string>();
            log.Add($"═══ DELETE @ {DateTime.Now:HH:mm:ss.fff} ═══");

            var sv = GetShelfScrollViewer();
            var verticalScrollBar = sv != null ? FindVisualChild<System.Windows.Controls.Primitives.ScrollBar>(sv) : null;
            var originalScrollBarVisibility = verticalScrollBar?.Visibility ?? Visibility.Visible;

            try
            {
                IsDeletingItem = true;
                _isSuppressingSizeSync = true;

                if (verticalScrollBar != null)
                {
                    verticalScrollBar.Visibility = Visibility.Hidden;
                }

                double savedOffset = sv?.VerticalOffset ?? 0;

                log.Add($"  BEFORE: Offset={savedOffset:F2}  Extent={sv?.ExtentHeight:F2}  Viewport={sv?.ViewportHeight:F2}  ScrollableH={sv?.ScrollableHeight:F2}");
                var beforePositions = CaptureVisibleCardPositions(log, "BEFORE");

                // Capture anchor: first visible container at or below header area
                int anchorIndex = -1;
                double anchorOffsetInViewport = 0;
                if (sv != null)
                {
                    for (int i = 0; i < ShelfListView.Items.Count; i++)
                    {
                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem;
                        if (container == null) continue;
                        try
                        {
                            var transform = container.TransformToAncestor(this);
                            var pos = transform.Transform(new Point(0, 0));
                            if (pos.Y >= 50)
                            {
                                anchorIndex = i;
                                anchorOffsetInViewport = pos.Y;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                log.Add($"  Anchor: idx={anchorIndex}  Y={anchorOffsetInViewport:F1}");

                int deletedIndex = items.Count > 0 ? ShelfListView.Items.IndexOf(items[0]) : -1;
                log.Add($"  Deleting: idx={deletedIndex}  count={items.Count}");

                // Calculate the corrected anchor index after deletion
                int correctedAnchorIndex = anchorIndex;
                if (anchorIndex >= 0)
                {
                    if (deletedIndex >= 0 && deletedIndex < anchorIndex)
                        correctedAnchorIndex = anchorIndex - 1;
                    else if (deletedIndex >= 0 && deletedIndex == anchorIndex)
                        correctedAnchorIndex = anchorIndex;
                }

                // REDUCE VIRTUALIZATION CACHE to prevent off-screen re-estimation
                VirtualizingPanel.SetCacheLength(ShelfListView, new VirtualizationCacheLength(0, 0));
                log.Add($"  CACHE → 0,0");

                // Remove the item(s)
                foreach (var item in items)
                {
                    _viewModel.RemoveItem(item);
                }

                // FILTER PERSISTENCE: Reapply category/search filters synchronously after removal.
                // Without this, the filter delegate on the CollectionView can be lost during the
                // subsequent UpdateLayout() + VirtualizationCacheLength manipulation, causing the
                // user to suddenly see all items instead of the filtered subset.
                if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text)))
                {
                    ReapplyActiveFilters();
                }

                log.Add($"  AFTER REMOVE: Offset={sv?.VerticalOffset:F2}  Extent={sv?.ExtentHeight:F2}  ScrollableH={sv?.ScrollableHeight:F2}");

                // Immediate scroll restore — the permanent 250px bottom padding
                // provides enough extra scrollable range to prevent bottom-of-list clamping
                if (sv != null && savedOffset > 0)
                {
                    double clampedOffset = Math.Min(savedOffset, sv.ScrollableHeight);
                    sv.ScrollToVerticalOffset(clampedOffset);
                    log.Add($"  IMMEDIATE RESTORE: {savedOffset:F2} → {clampedOffset:F2}");
                }

                // SYNCHRONOUS CORRECTION while CacheLength=0,0
                // With zero cache, UpdateLayout() won't trigger massive re-estimation,
                // so it's safe to call synchronously without causing cascading jitter.
                if (sv != null && correctedAnchorIndex >= 0)
                {
                    sv.UpdateLayout();
                    log.Add($"  SYNC LAYOUT: Offset={sv.VerticalOffset:F2}  Extent={sv.ExtentHeight:F2}");

                    for (int pass = 0; pass < 3; pass++)
                    {
                        if (correctedAnchorIndex < 0 || correctedAnchorIndex >= ShelfListView.Items.Count) break;
                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(correctedAnchorIndex) as ListViewItem;
                        if (container == null) break;
                        try
                        {
                            var transform = container.TransformToAncestor(this);
                            var currentPos = transform.Transform(new Point(0, 0));
                            double drift = currentPos.Y - anchorOffsetInViewport;
                            log.Add($"  SYNC PASS {pass}: anchorY={currentPos.Y:F1}  drift={drift:+0.0;-0.0}px");
                            if (Math.Abs(drift) <= 0.5) { log.Add($"  SYNC CONVERGED on pass {pass}"); break; }
                            double correctedOffset = sv.VerticalOffset + drift;
                            correctedOffset = Math.Max(0, Math.Min(correctedOffset, sv.ScrollableHeight));
                            sv.ScrollToVerticalOffset(correctedOffset);
                            sv.UpdateLayout();
                        }
                        catch { break; }
                    }
                }

                // RESTORE CACHE SYNCHRONOUSLY — doing this in a deferred callback
                // caused a visible "double refresh" (one frame without hover buttons,
                // then another frame with them). By restoring cache in the same
                // synchronous block, WPF renders only ONE frame with everything settled.
                if (sv != null && correctedAnchorIndex >= 0)
                {
                    int capturedIndex = correctedAnchorIndex;
                    double capturedTargetY = anchorOffsetInViewport;

                    // Restore cache to normal
                    VirtualizingPanel.SetCacheLength(ShelfListView, new VirtualizationCacheLength(3, 3));
                    sv.UpdateLayout();
                    log.Add($"  CACHE → 3,3  Offset={sv.VerticalOffset:F2}  Extent={sv.ExtentHeight:F2}");

                    // FILTER PERSISTENCE: Reapply filters after cache restoration + UpdateLayout,
                    // which is the transition most likely to reset the CollectionView filter.
                    if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text)))
                    {
                        ReapplyActiveFilters();
                    }

                    // Correct any drift from cache restoration
                    if (capturedIndex >= 0 && capturedIndex < ShelfListView.Items.Count)
                    {
                        var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(capturedIndex) as ListViewItem;
                        if (container != null)
                        {
                            try
                            {
                                var transform = container.TransformToAncestor(this);
                                var currentPos = transform.Transform(new Point(0, 0));
                                double drift = currentPos.Y - capturedTargetY;
                                log.Add($"  POST-CACHE: anchorY={currentPos.Y:F1}  drift={drift:+0.0;-0.0}px");
                                if (Math.Abs(drift) > 0.5)
                                {
                                    double correctedOffset = sv.VerticalOffset + drift;
                                    correctedOffset = Math.Max(0, Math.Min(correctedOffset, sv.ScrollableHeight));
                                    sv.ScrollToVerticalOffset(correctedOffset);
                                    sv.UpdateLayout();
                                    log.Add($"  POST-CACHE FIX: → {correctedOffset:F2}");
                                }
                            }
                            catch { }
                        }
                    }

                    // Final drift check
                    CaptureVisibleCardPositions(log, "FINAL");
                    if (beforePositions != null)
                    {
                        var afterPositions = GetVisibleCardPositionMap();
                        log.Add($"  POSITION DRIFT:");
                        bool anyDrift = false;
                        foreach (var kvp in beforePositions)
                        {
                            if (afterPositions.TryGetValue(kvp.Key, out double afterY))
                            {
                                double d = afterY - kvp.Value;
                                if (Math.Abs(d) > 0.1) { log.Add($"    {kvp.Key}: {d:+0.0;-0.0}px"); anyDrift = true; }
                            }
                        }
                        if (!anyDrift) log.Add($"    (none — all cards stayed put!)");
                    }

                    // FORCE MOUSE RE-EVALUATION synchronously — the next card has
                    // already slid into position under the cursor, so re-set the cursor
                    // to its own position to trigger IsMouseOver on the new card.
                    ForceMouseReEvaluation();
                }
                else
                {
                    // No anchor — just restore cache and force mouse re-evaluation
                    VirtualizingPanel.SetCacheLength(ShelfListView, new VirtualizationCacheLength(3, 3));

                    // FILTER PERSISTENCE: Also reapply filters in the no-anchor path
                    if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text)))
                    {
                        ReapplyActiveFilters();
                    }

                    ForceMouseReEvaluation();
                }

                // Write diagnostic log in deferred callback (file I/O only, no layout changes)
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        log.Add("");
                        string logPath = System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                            "deletion_debug.log");
                        System.IO.File.AppendAllLines(logPath, log);
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
            finally
            {
                if (verticalScrollBar != null)
                {
                    verticalScrollBar.Visibility = originalScrollBarVisibility;
                }

                _isDeletionScrollGuardActive = false;
                _deletionLog = null;
                _isSuppressingSizeSync = false;
                IsDeletingItem = false;

                if (_isEdgeLocked && this.ActualHeight > 0)
                {
                    _lockedBottomEdge = this.Top + this.ActualHeight + 20;
                }
            }
        }

        /// <summary>
        /// Injects a synthetic mouse move event at the current cursor position to force WPF
        /// to re-evaluate IsMouseOver on all elements under the cursor. This is needed after
        /// deleting a card because the next card slides into position under the stationary
        /// cursor, but WPF doesn't fire MouseEnter since the mouse didn't physically move.
        /// </summary>
        private void ForceMouseReEvaluation()
        {
            try
            {
                // Get current mouse position in screen coordinates via Win32
                if (Classes.NativeMethods.GetCursorPos(out var pt))
                {
                    // Re-set the cursor to the same position — this forces WPF
                    // to re-process mouse hit testing and fire MouseEnter/MouseLeave events
                    // on the card that slid into position under the stationary cursor
                    Classes.NativeMethods.SetCursorPos(pt.X, pt.Y);
                }
            }
            catch { }
        }

        /// <summary>Logs screen-relative Y positions of all visible ListViewItem containers.</summary>
        private System.Collections.Generic.Dictionary<string, double>? CaptureVisibleCardPositions(
            System.Collections.Generic.List<string> logLines, string label)
        {
            var posMap = new System.Collections.Generic.Dictionary<string, double>();
            try
            {
                int logged = 0;
                for (int i = 0; i < ShelfListView.Items.Count && logged < 20; i++)
                {
                    var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem;
                    if (container == null) continue;

                    var transform = container.TransformToAncestor(this);
                    var pos = transform.Transform(new Point(0, 0));
                    string itemKey = $"idx{i}";
                    if (container.DataContext is ClipboardItem ci)
                    {
                        string preview = ci.RawContent?.Length > 20 ? ci.RawContent.Substring(0, 20) : (ci.RawContent ?? ci.ItemType.ToString());
                        itemKey = $"idx{i}:\"{preview}\"";
                    }
                    posMap[itemKey] = pos.Y;
                    logLines.Add($"    [{label}] {itemKey}  Y={pos.Y:F1}  H={container.ActualHeight:F1}");
                    logged++;
                }
            }
            catch { }
            return posMap;
        }

        /// <summary>Returns a map of visible card keys to their screen-relative Y positions.</summary>
        private System.Collections.Generic.Dictionary<string, double> GetVisibleCardPositionMap()
        {
            var map = new System.Collections.Generic.Dictionary<string, double>();
            try
            {
                for (int i = 0; i < ShelfListView.Items.Count && map.Count < 20; i++)
                {
                    var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem;
                    if (container == null) continue;

                    var transform = container.TransformToAncestor(this);
                    var pos = transform.Transform(new Point(0, 0));
                    string itemKey = $"idx{i}";
                    if (container.DataContext is ClipboardItem ci)
                    {
                        string preview = ci.RawContent?.Length > 20 ? ci.RawContent.Substring(0, 20) : (ci.RawContent ?? ci.ItemType.ToString());
                        itemKey = $"idx{i}:\"{preview}\"";
                    }
                    map[itemKey] = pos.Y;
                }
            }
            catch { }
            return map;
        }

        private void OpenSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                if (!string.IsNullOrEmpty(item.FilePath) && System.IO.File.Exists(item.FilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                }
                else if (item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Url && !string.IsNullOrEmpty(item.RawContent))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.RawContent) { UseShellExecute = true });
                }
                e.Handled = true;
            }
        }

        private FlyShelf.Windows.QuickLookWindow _activeQuickLook;

        internal void ShowQuickLookForItem(FlyShelf.ViewModels.ClipboardItem item, global::Windows.Media.Ocr.OcrResult preLoadedOcr = null)
        {
            try { _activeQuickLook?.Close(); } catch { }
            _activeQuickLook = null;

            var qLook = new FlyShelf.Windows.QuickLookWindow(item, preLoadedOcr);
            qLook.Closed += (s, args) => { if (_activeQuickLook == s) _activeQuickLook = null; };
            _activeQuickLook = qLook;
            qLook.Show();
            try { qLook.Activate(); } catch { }
        }

        private void QuickLookSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                ShowQuickLookForItem(item);
                e.Handled = true;
            }
        }

        private async void RotateImageSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

                try
                {
                    string filePath = item.FilePath;

                    // Find the Image element in the visual tree for animation
                    var listViewItem = ShelfListView.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                    Image targetImage = null;
                    if (listViewItem != null)
                    {
                        targetImage = FindVisualChild<Image>(listViewItem, "ItemIcon");
                    }

                    // Animate the image rotating 90° with smooth easing
                    if (targetImage != null)
                    {
                        var rotateTransform = new System.Windows.Media.RotateTransform(0, targetImage.ActualWidth / 2, targetImage.ActualHeight / 2);
                        targetImage.RenderTransform = rotateTransform;
                        var rotateAnim = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = 0,
                            To = 90,
                            Duration = TimeSpan.FromMilliseconds(300),
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                        };
                        rotateTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rotateAnim);
                    }

                    // Rotate the file on a background thread to keep UI responsive
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        byte[] fileBytes = System.IO.File.ReadAllBytes(filePath);
                        // PERF: BitmapImage with CacheOption.OnLoad + Freeze() is thread-safe
                        // — no need to marshal to UI thread via Dispatcher.Invoke
                        var original = new System.Windows.Media.Imaging.BitmapImage();
                        using (var ms = new System.IO.MemoryStream(fileBytes))
                        {
                            original.BeginInit();
                            original.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            original.StreamSource = ms;
                            original.EndInit();
                            original.Freeze();
                        }

                        var rotated = new System.Windows.Media.Imaging.TransformedBitmap(original, new System.Windows.Media.RotateTransform(90));
                        rotated.Freeze();

                        string ext = System.IO.Path.GetExtension(filePath).ToLower();
                        System.Windows.Media.Imaging.BitmapEncoder encoder;
                        if (ext == ".png") encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        else if (ext == ".bmp") encoder = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                        else encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };

                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rotated));

                        using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            encoder.Save(fs);
                        }
                    });

                    // Wait for animation to finish
                    await System.Threading.Tasks.Task.Delay(320);

                    // Reload the icon from the freshly rotated file
                    byte[] freshBytes = System.IO.File.ReadAllBytes(filePath);
                    var freshBitmap = new System.Windows.Media.Imaging.BitmapImage();
                    using (var ms = new System.IO.MemoryStream(freshBytes))
                    {
                        freshBitmap.BeginInit();
                        freshBitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        freshBitmap.StreamSource = ms;
                        freshBitmap.EndInit();
                        freshBitmap.Freeze();
                    }

                    // Reset the rotation transform on the image
                    if (targetImage != null)
                    {
                        targetImage.RenderTransform = null;
                    }

                    // Update the item's icon with the rotated image
                    item.Icon = freshBitmap;

                    FlyShelf.Classes.Logger.LogAction("ROTATE", "Rotated 90\u00B0 in-place: " + System.IO.Path.GetFileName(filePath));
                }
                catch (Exception ex)
                {
                    FlyShelf.Classes.Logger.LogAction("ROTATE", "Failed: " + ex.Message);
                }
            }
        }

                private void RunTerminalSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                if (item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Code)
                {
                    item.RunInTerminal();
                }
                e.Handled = true;
            }
        }

        private void SmartActionSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (item.SmartActionType == "CompileAndRun")
                {
                    item.CompileAndRunNative();
                }
                else if (item.SmartActionType == "OpenPDF" || item.SmartActionType == "JoinMeeting" || item.SmartActionType == "OpenBrowser")
                {
                    string target = item.SmartActionType == "OpenPDF" ? item.FilePath : item.RawContent;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
                }
                else if (item.SmartActionType == "OpenMap")
                {
                    string target = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(item.RawContent);
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
                }
                else if (item.SmartActionType == "ConvertToPdf")
                {
                    item.ConvertDocumentTask();
                }
                else if (item.SmartActionType == "SetTimer")
                {
                    var tw = new FlyShelf.Windows.TimerWindow(item.RawContent);
                    tw.Show();
                }
                else if (item.SmartActionType == "CopyQRText")
                {
                    if (Classes.ClipboardHelper.SafeSetText(item.RawContent))
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("QR Text Copied!");
                    }
                }

            }
        }
        
        private void GoogleSearchSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            GoogleSearch_Click(sender, new RoutedEventArgs());
        }

        private void SanitizeUrlSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (item.ItemType == FlyShelf.ViewModels.ClipboardItemType.Url && !string.IsNullOrEmpty(item.RawContent))
                {
                    try
                    {
                        string original = item.RawContent;
                        // Compile our robust tracking parameter cleaner regex
                        var rxUtmClean = new System.Text.RegularExpressions.Regex(
                            @"(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?", 
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        string cleanUrl = rxUtmClean.Replace(original, string.Empty).TrimEnd('?', '&');
                        if (cleanUrl != original)
                        {
                            item.RawContent = cleanUrl;
                            item.FileName = cleanUrl;
                            
                            // 1. Write the clean URL to the OS system clipboard safely
                            Classes.ClipboardHelper.SafeSetText(cleanUrl, suppressEcho: true, echoDelayMs: 500);

                            // 2. Persist updated history to disk
                            _viewModel.PersistHistoryPublic();

                            // 3. Show a premium visual toast
                            FlyShelf.Windows.ToastWindow.ShowToast("URL Sanitized & Copied! 🛡️");
                            
                            FlyShelf.Classes.Logger.LogAction("URL_SANITY", $"Successfully stripped tracking metrics from URL. Result: {cleanUrl}");
                        }
                        else
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast("URL is already clean! ✨");
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("URL_SANITY_ERR", $"Sanitization failed: {ex.Message}");
                    }
                }
            }
        }

        private void ExpandToggleSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                item.IsExpanded = !item.IsExpanded;
            }
            e.Handled = true;
        }


        private void ShelfListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ShelfListView.SelectedItems.Count > 0)
            {
                var itemsToRemove = ShelfListView.SelectedItems.Cast<ClipboardItem>().ToList();
                AnimateAndRemoveItems(itemsToRemove);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ShelfListView.SelectedItem is ClipboardItem selected)
            {
                _ = CopyItemAndPaste(selected, hideWindow: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_isSearchActive)
                {
                    CloseSearch();
                }
                else
                {
                    AnimateAndHide();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    IDataObject data = Clipboard.GetDataObject();
                    if (data != null)
                    {
                        _viewModel.HandleDrop(data, true);
                        AnimateAndHide();
                    }
                }
                catch { }
                e.Handled = true;
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // Ctrl+F opens search
                if (!_isSearchActive)
                {
                    SearchToggle_Click(sender, e);
                }
                else
                {
                    SearchTextBox.Focus();
                    SearchTextBox.SelectAll();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                int currentIdx = ShelfListView.SelectedIndex;
                int count = _viewModel.DroppedItems.Count;
                if (count == 0) { e.Handled = true; return; }

                int newIdx;
                if (currentIdx < 0)
                {
                    newIdx = 0; // Nothing selected — start at first item
                }
                else
                {
                    newIdx = e.Key == Key.Down
                        ? Math.Min(currentIdx + 1, count - 1)
                        : Math.Max(currentIdx - 1, 0);
                }

                ShelfListView.SelectedIndex = newIdx;
                // ScrollIntoView MUST come first — it forces the virtualizer to create the container
                ShelfListView.ScrollIntoView(ShelfListView.Items[newIdx]);
                // Dispatch focus to next frame so the container is fully realized
                Dispatcher.InvokeAsync(() =>
                {
                    var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(newIdx) as ListViewItem;
                    container?.Focus();
                }, System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                // Let the TextBox (SearchTextBox) handle its own cursor navigation/Enter/Down keys.
                if (Keyboard.FocusedElement is TextBox)
                {
                    return;
                }

                int count = _viewModel.DroppedItems.Count;
                if (count == 0) { e.Handled = true; return; }

                int currentIdx = ShelfListView.SelectedIndex;
                int newIdx;
                if (currentIdx < 0)
                {
                    newIdx = 0; // Nothing selected — force select first item
                }
                else
                {
                    newIdx = e.Key == Key.Down
                        ? Math.Min(currentIdx + 1, count - 1)
                        : Math.Max(currentIdx - 1, 0);
                }

                ShelfListView.SelectedIndex = newIdx;
                // ScrollIntoView MUST come first — it forces the virtualizer to create the container
                ShelfListView.ScrollIntoView(ShelfListView.Items[newIdx]);
                // Dispatch focus to next frame so the container is fully realized
                Dispatcher.InvokeAsync(() =>
                {
                    var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(newIdx) as ListViewItem;
                    if (container != null)
                    {
                        container.Focus();
                        Keyboard.Focus(container);
                    }
                }, System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
            }
        }



        private void NotifyIconQuit_Click(object sender, RoutedEventArgs e)
        {
            _hubWindowInstance?.ForceShutdownRelease();
            Application.Current.Shutdown();
        }

        private void nIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e)
        {
            if (_isCurrentlySummoned && _viewModel.IsFullMode)
            {
                AnimateAndHide();
            }
            else
            {
                OpenApp_Click(sender, e);
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            if (child == null) return null;
            DependencyObject parentObject = null;
            if (child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            {
                parentObject = VisualTreeHelper.GetParent(child);
            }
            else
            {
                parentObject = LogicalTreeHelper.GetParent(child);
            }

            if (parentObject == null) return null;
            T parent = parentObject as T;
            if (parent != null) return parent;
            else return FindVisualParent<T>(parentObject);
        }

        /// <summary>Walks up the visual tree checking if any ancestor FrameworkElement has the given Tag.</summary>
        private static bool HasAncestorTag(DependencyObject child, string tag)
        {
            DependencyObject current = child;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == tag)
                    return true;
                if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }
            return false;
        }


        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T tChild) return tChild;
                else
                {
                    T? childOfChild = FindVisualChild<T>(child);
                    if (childOfChild != null) return childOfChild;
                }
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild && tChild.Name == name) return tChild;
                T? deeper = FindVisualChild<T>(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>
        /// Handles the Update Badge click — opens the Hub Window so the user can see
        /// update details and trigger the download.
        /// </summary>
        private void UpdateBadge_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenApp_Click(null, null);
        }

    }
}


