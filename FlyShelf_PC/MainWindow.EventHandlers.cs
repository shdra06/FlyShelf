// ---------------------------------------------------------------
// MainWindow � Event Handlers
// Drag/Drop, Search, Item Actions (Pin/Delete/Open/QuickLook),
// Scroll, KeyDown, NotifyIcon, ContextMenu
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using FlyShelf.Classes;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlyShelf
{
    public partial class MainWindow
    {
        internal static bool _isInternalDragSource = false;
        private System.Windows.Threading.DispatcherTimer? _searchDebounceTimer;
        private System.Windows.Threading.DispatcherTimer? _notesSearchDebounce;
        private System.Windows.Threading.DispatcherTimer? _todoSearchDebounce;
        private Action<bool>? _incognitoStateChangedHandler; // Unsubscribed in MainWindow.OnClosed()

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

            try
            {
                _viewModel.HandleDrop(e.Data, forceClipboardSync: false);
            }
            catch (Exception ex)
            {
                Classes.Logger.LogCrash("Window_PreviewDrop", ex);
            }
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

            // Drop processing is handled exclusively in Window_PreviewDrop to avoid duplicate handling
            e.Handled = true;
        }

    

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            try
            {
                _isDragHovering = true;
                IsDragHovering = !_isNotesActive && !_isTodoActive;
            }
            catch (Exception ex)
            {
                Classes.Logger.LogCrash("Window_DragEnter", ex);
            }
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
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
            // Don't persist size during spawn animation or post-animation cooldown — prevents
            // settings PropertyChanged feedback loop that causes the window to bounce
            if (_isShowAnimating) return;
            if (_showAnimEndTime != DateTime.MinValue &&
                (DateTime.UtcNow - _showAnimEndTime).TotalMilliseconds < 500) return;
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
                catch { } // Best-effort: failure is acceptable 
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
                FlyShelf.Classes.Logger.LogAction("UI_TRIGGER", "Could not find active PeerManager.Instance within timeout");
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
                var bgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, accent.R, accent.G, accent.B));
                bgBrush.Freeze();
                btn.Background = bgBrush;
                var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, accent.R, accent.G, accent.B));
                borderBrush.Freeze();
                btn.BorderBrush = borderBrush;
            }
            else
            {
                var bgBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, accent.R, accent.G, accent.B));
                bgBrush.Freeze();
                btn.Background = bgBrush;
                var borderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, accent.R, accent.G, accent.B));
                borderBrush.Freeze();
                btn.BorderBrush = borderBrush;
            }

            // Toggle dot indicator: bright accent glow when ON, dim gray when OFF
            if (dot != null)
            {
                if (isActive)
                {
                    var fillBrush = new System.Windows.Media.SolidColorBrush(accent);
                    fillBrush.Freeze();
                    dot.Fill = fillBrush;
                    var strokeBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, accent.R, accent.G, accent.B));
                    strokeBrush.Freeze();
                    dot.Stroke = strokeBrush;
                }
                else
                {
                    var mutedBrush = TryFindResource("ThemeTextMuted") as System.Windows.Media.SolidColorBrush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
                    var mutedColor = mutedBrush.Color;
                    var fillBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, mutedColor.R, mutedColor.G, mutedColor.B));
                    fillBrush.Freeze();
                    dot.Fill = fillBrush;
                    var fallbackBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGray);
                    fallbackBrush.Freeze();
                    dot.Stroke = TryFindResource("ThemeOverlayBorder") as System.Windows.Media.Brush ?? fallbackBrush;
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
            Windows.ToastWindow.ShowToast("Shelf cleared!");
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
            var historyWin = new FlyShelf.Windows.ReminderHistoryWindow();
            historyWin.Topmost = true;
            historyWin.Show();
            historyWin.Activate();
            historyWin.Focus();
            historyWin.Topmost = false;
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
            catch { } // Best-effort: failure is acceptable
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
                FlyShelf.Windows.ToastWindow.ShowToast("Locked as password card!");

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
                    FlyShelf.Windows.ToastWindow.ShowToast("Zip already exists!");
                    return;
                }
                FlyShelf.Windows.ToastWindow.ShowToast("Creating zip archive...");
                item.CreateZipArchive();
            }
        }

        private void UngroupItems_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (item.ItemType != ViewModels.ClipboardItemType.Group)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Not a grouped item");
                    return;
                }

                try
                {
                    // Parse file paths from RawContent (newline-separated)
                    var raw = item.RawContent;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("No files to ungroup");
                        return;
                    }

                    var filePaths = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (filePaths.Length == 0)
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("No files to ungroup");
                        return;
                    }

                    // Remove the group item
                    int insertIndex = _viewModel.DroppedItems.IndexOf(item);
                    if (insertIndex < 0) insertIndex = 0;
                    _viewModel.DroppedItems.Remove(item);

                    // Insert individual items at the same position
                    int added = 0;
                    foreach (var filePath in filePaths)
                    {
                        if (!System.IO.File.Exists(filePath) && !System.IO.Directory.Exists(filePath))
                            continue;

                        // Use single-file constructor — handles type classification, icons, size automatically
                        var individual = new ViewModels.ClipboardItem(filePath);

                        _viewModel.DroppedItems.Insert(Math.Min(insertIndex + added, _viewModel.DroppedItems.Count), individual);
                        added++;
                    }

                    FlyShelf.Windows.ToastWindow.ShowToast($"Ungrouped into {added} items");
                    Classes.Logger.LogAction("UNGROUP", $"Split group into {added} individual items from {filePaths.Length} paths");
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("CRASH", $"UngroupItems: {ex}");
                    FlyShelf.Windows.ToastWindow.ShowToast("Failed to ungroup");
                }
            }
        }

        private void SyncZipLan_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (!item.HasZipArchive)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Create a zip first!");
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

            // Cooldown / batch safety: if we delete a massive number of items, do it instantly
            if (items.Count > 5)
            {
                IsDeletingItem = true;
                _isSuppressingSizeSync = true;
                ActivateDeletionScrollGuard(items);
                try
                {
                    foreach (var item in items)
                    {
                        _viewModel.RemoveItem(item);
                    }
                }
                finally
                {
                    _isDeletionScrollGuardActive = false;
                    if (_isEdgeLocked && this.ActualHeight > 0)
                    {
                        _lockedBottomEdge = this.Top + this.ActualHeight + 20;
                    }
                    IsDeletingItem = false;
                    _isSuppressingSizeSync = false;
                }

                if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text)))
                {
                    ReapplyActiveFilters();
                }
                ForceMouseReEvaluation();
                return;
            }

            IsDeletingItem = true;
            _isSuppressingSizeSync = true;

            // Find all loaded container items to animate
            var containersToAnimate = new System.Collections.Generic.List<Tuple<ListViewItem, double>>();

            foreach (var item in items)
            {
                var c1 = ShelfListView.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
                if (c1 != null && c1.IsLoaded)
                {
                    containersToAnimate.Add(new Tuple<ListViewItem, double>(c1, c1.ActualHeight));
                }
                var c2 = AltShelfListView.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
                if (c2 != null && c2.IsLoaded)
                {
                    containersToAnimate.Add(new Tuple<ListViewItem, double>(c2, c2.ActualHeight));
                }
            }

            if (containersToAnimate.Count == 0)
            {
                // No visible containers to animate — remove instantly
                ActivateDeletionScrollGuard(items);
                try
                {
                    foreach (var item in items)
                    {
                        _viewModel.RemoveItem(item);
                    }
                }
                finally
                {
                    _isDeletionScrollGuardActive = false;
                    if (_isEdgeLocked && this.ActualHeight > 0)
                    {
                        _lockedBottomEdge = this.Top + this.ActualHeight + 20;
                    }
                    IsDeletingItem = false;
                    _isSuppressingSizeSync = false;
                }

                if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text)))
                {
                    ReapplyActiveFilters();
                }
                ForceMouseReEvaluation();
                return;
            }

            int animationsRunning = containersToAnimate.Count;
            var duration = TimeSpan.FromMilliseconds(180);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            Action onAnimationCompleted = () =>
            {
                animationsRunning--;
                if (animationsRunning <= 0)
                {
                    // CRITICAL: Reset container properties BEFORE removing items from the
                    // collection. In Release/Store builds, VirtualizingStackPanel recycles
                    // containers immediately on Remove — if we clear animation state AFTER
                    // the Remove, the recycled container starts with Height=0/Opacity=0
                    // from the stale animation, causing a visible flash/jump.
                    foreach (var tuple in containersToAnimate)
                    {
                        var container = tuple.Item1;
                        // [FIX ANIM-3]: Clear ScaleY animation and reset transform for recycling
                        if (container.RenderTransform is ScaleTransform st)
                        {
                            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        }
                        container.BeginAnimation(ListViewItem.OpacityProperty, null);
                        container.RenderTransform = null;
                        container.Height = 0;
                        container.Visibility = Visibility.Collapsed;
                        container.Opacity = 1.0;
                        container.IsHitTestVisible = true;
                    }

                    // Activate deletion scroll guard BEFORE removing items
                    ActivateDeletionScrollGuard(items);

                    // NOW physically remove the items from the view model
                    try
                    {
                        foreach (var it in items)
                        {
                            _viewModel.RemoveItem(it);
                        }
                    }
                    finally
                    {
                        _isDeletionScrollGuardActive = false;
                        if (_isEdgeLocked && this.ActualHeight > 0)
                        {
                            _lockedBottomEdge = this.Top + this.ActualHeight + 20;
                        }
                        IsDeletingItem = false;
                        _isSuppressingSizeSync = false;
                    }

                    // Reapply filters if needed
                    if (_activeCategoryFilter != null || (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text)))
                    {
                        ReapplyActiveFilters();
                    }
                    ForceMouseReEvaluation();
                }
            };

            foreach (var tuple in containersToAnimate)
            {
                var container = tuple.Item1;
                double actualHeight = tuple.Item2;

                container.IsHitTestVisible = false;

                // [FIX ANIM-3]: Use ScaleTransform.ScaleY instead of Height to avoid layout thrashing
                var scaleTransform = new ScaleTransform(1, 1);
                container.RenderTransform = scaleTransform;
                container.RenderTransformOrigin = new Point(0.5, 0);

                var scaleAnim = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                // Smoothly fade out opacity
                var oAnim = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease };

                scaleAnim.Completed += (s, e) => onAnimationCompleted();

                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                container.BeginAnimation(ListViewItem.OpacityProperty, oAnim);
            }
        }

        /// <summary>
        /// Captures an anchor card below the deleted items and activates the deletion scroll guard.
        /// The ScrollChanged handler uses this anchor to correct any scroll drift caused by
        /// VirtualizingStackPanel extent recalculation during item removal.
        /// </summary>
        private void ActivateDeletionScrollGuard(System.Collections.Generic.List<ClipboardItem> itemsToDelete)
        {
            try
            {
                // Find the highest index among items being deleted
                int maxDeletedIndex = -1;
                foreach (var item in itemsToDelete)
                {
                    int idx = ShelfListView.Items.IndexOf(item);
                    if (idx > maxDeletedIndex) maxDeletedIndex = idx;
                }

                // Anchor = first item AFTER the deleted items
                int anchorIndex = maxDeletedIndex + 1;
                if (anchorIndex >= ShelfListView.Items.Count)
                {
                    // Deleting last items — anchor to the item BEFORE the deleted range
                    int minDeletedIndex = int.MaxValue;
                    foreach (var item in itemsToDelete)
                    {
                        int idx = ShelfListView.Items.IndexOf(item);
                        if (idx >= 0 && idx < minDeletedIndex) minDeletedIndex = idx;
                    }
                    anchorIndex = minDeletedIndex - 1;
                }

                if (anchorIndex >= 0 && anchorIndex < ShelfListView.Items.Count)
                {
                    var anchorContainer = ShelfListView.ItemContainerGenerator.ContainerFromIndex(anchorIndex) as ListViewItem;
                    if (anchorContainer != null)
                    {
                        var transform = anchorContainer.TransformToAncestor(this);
                        var pos = transform.Transform(new Point(0, 0));
                        _deletionAnchorIndex = anchorIndex;
                        _deletionAnchorTargetY = pos.Y;
                        _isDeletionScrollGuardActive = true;
                        return;
                    }
                }
            }
            catch { }
            // If we couldn't find a valid anchor, don't activate the guard
            _isDeletionScrollGuardActive = false;
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
            catch { } // Best-effort: failure is acceptable
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
                        string preview = ci.RawContent?.Length > 20 ? ci.RawContent[..20] : (ci.RawContent ?? ci.ItemType.ToString());
                        itemKey = $"idx{i}:\"{preview}\"";
                    }
                    posMap[itemKey] = pos.Y;
                    logLines.Add($"    [{label}] {itemKey}  Y={pos.Y:F1}  H={container.ActualHeight:F1}");
                    logged++;
                }
            }
            catch { } // Best-effort: failure is acceptable
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
                        string preview = ci.RawContent?.Length > 20 ? ci.RawContent[..20] : (ci.RawContent ?? ci.ItemType.ToString());
                        itemKey = $"idx{i}:\"{preview}\"";
                    }
                    map[itemKey] = pos.Y;
                }
            }
            catch { } // Best-effort: failure is acceptable
            return map;
        }

        private void OpenSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                var filePath = item.FilePath;
                var rawContent = item.RawContent;
                var itemType = item.ItemType;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                        {
                            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                            string[] dangerousExts = { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh", ".scr", ".msi", ".com", ".hta", ".dll", ".jar", ".pif", ".reg" };
                            if (System.Array.IndexOf(dangerousExts, ext) >= 0)
                            {
                                // SECURITY: Do not execute untrusted executables/scripts directly on double-click.
                                // Instead, open Windows Explorer with the file selected.
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
                                Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast("Security: Executable file opened in folder instead of running."));
                            }
                            else
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
                            }
                        }
                        else if (itemType == FlyShelf.ViewModels.ClipboardItemType.Url && !string.IsNullOrEmpty(rawContent))
                        {
                            // SECURITY: Validate URL protocol is strictly http or https
                            if (Uri.TryCreate(rawContent, UriKind.Absolute, out var uri) &&
                                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rawContent) { UseShellExecute = true });
                            }
                            else
                            {
                                Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast("Blocked unsafe URL protocol"));
                            }
                        }
                    }
                    catch (Exception)
                    {
                        Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast("Could not open file"));
                    }
                });
            }
        }

        private void OpenExplorer_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                _viewModel.OpenFileLocationCommand.Execute(item);
                e.Handled = true;
            }
        }

        private FlyShelf.Windows.QuickLookWindow? _activeQuickLook;

        internal void ShowQuickLookForItem(FlyShelf.ViewModels.ClipboardItem item, global::Windows.Media.Ocr.OcrResult preLoadedOcr = null, bool autoTriggerOcr = false)
        {
            try { _activeQuickLook?.Close(); } catch { } // Best-effort: failure is acceptable
            _activeQuickLook = null;

            var qLook = new FlyShelf.Windows.QuickLookWindow(item, preLoadedOcr, autoTriggerOcr);
            qLook.Closed += (s, args) => { if (_activeQuickLook == s) _activeQuickLook = null; };
            _activeQuickLook = qLook;
            qLook.Show();
            try { qLook.Activate(); } catch { }

            // Keep the clipboard window visible behind QuickLook
            // (both are Topmost, so re-show ensures the shelf isn't hidden)
            Dispatcher.InvokeAsync(() =>
            {
                try { if (_isCurrentlySummoned) this.Show(); } catch { } // Best-effort: failure is acceptable
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void QuickLookSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                ShowQuickLookForItem(item);
                e.Handled = true;
            }
        }

        private void ContextMenuQuickLook_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FlyShelf.ViewModels.ClipboardItem item)
            {
                ShowQuickLookForItem(item);
            }
        }

        private void ConvertImageToPdfSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                item.ConvertImageToPdf();
            }
        }

        private void ConvertDocToPdfSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                item.ConvertDocumentTask();
            }
        }
        private async void RotateImageSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;


                Image targetImage = null;
                try
                {
                    string filePath = item.FilePath;

                    // Find the Image element in the visual tree for animation
                    var listViewItem = ShelfListView.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
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
                        var _fi = new System.IO.FileInfo(filePath);
                        if (_fi.Length > 100_000_000)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ROTATE] Skipped — file too large ({_fi.Length} bytes): {filePath}");
                            return;
                        }
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

                        string ext = System.IO.Path.GetExtension(filePath).ToLower(System.Globalization.CultureInfo.InvariantCulture);
                        System.Windows.Media.Imaging.BitmapEncoder encoder;
                        if (ext == ".png") encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        else if (ext == ".bmp") encoder = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                        else encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };

                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rotated));

                        if (!FlyShelf.Classes.DiskSpaceHelper.HasSufficientDiskSpace(filePath, 10_000_000))
                        {
                            FlyShelf.Classes.Logger.LogAction("IMAGE_SAVE", "Insufficient disk space");
                            return;
                        }
                        using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            encoder.Save(fs);
                        }
                    });

                    // Wait for animation to finish
                    await System.Threading.Tasks.Task.Delay(320);

                    // Reload the icon from the freshly rotated file
                    var _fi2 = new System.IO.FileInfo(filePath);
                    if (_fi2.Length > 100_000_000)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ROTATE] Skipped reload — file too large ({_fi2.Length} bytes): {filePath}");
                        return;
                    }
                    // PERF [FIX 4]: Decode BitmapImage on background thread (Freeze makes it cross-thread safe)
                    var freshBitmap = await System.Threading.Tasks.Task.Run(() =>
                    {
                        byte[] freshBytes = System.IO.File.ReadAllBytes(filePath);
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        using (var ms = new System.IO.MemoryStream(freshBytes))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                            bmp.Freeze();
                        }
                        return bmp;
                    });

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
                    // Reset the visual rotation transform on failure so the image doesn't appear rotated
                    if (targetImage != null)
                        targetImage.RenderTransform = null;
                    FlyShelf.Classes.Logger.LogAction("ROTATE", "Failed: " + ex.Message);
                }
            }
            });
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
                else if (item.SmartActionType == "OpenPDF")
                {
                    if (!string.IsNullOrEmpty(item.FilePath))
                        _ = System.Threading.Tasks.Task.Run(() => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.FilePath, UseShellExecute = true }); } catch { } });
                }
                else if (item.SmartActionType == "JoinMeeting" || item.SmartActionType == "OpenBrowser")
                {
                    // SECURITY: Validate URL scheme is strictly http or https to prevent arbitrary protocol handler launches
                    string target = item.RawContent;
                    if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        _ = System.Threading.Tasks.Task.Run(() => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { } });
                }
                else if (item.SmartActionType == "OpenMap")
                {
                    string target = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(item.RawContent);
                    _ = System.Threading.Tasks.Task.Run(() => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { } }); // Best-effort: failure is acceptable
                }
                else if (item.SmartActionType == "ConvertToPdf")
                {
                    item.ConvertDocumentTask();
                }
                else if (item.SmartActionType == "SetTimer")
                {
                    var tw = new FlyShelf.Windows.TimerWindow(item.RawContent);
                    tw.Topmost = true;
                    tw.Show();
                    tw.Activate();
                    tw.Focus();
                    tw.Topmost = false;
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
                            _viewModel.SchedulePersistHistoryPublic(); // PERF: throttled — settings change is non-critical

                            // 3. Show a premium visual toast
                            FlyShelf.Windows.ToastWindow.ShowToast("URL Sanitized & Copied!");
                            
                            FlyShelf.Classes.Logger.LogAction("URL_SANITY", $"Successfully stripped tracking metrics from URL. Result: {cleanUrl}");
                        }
                        else
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast("URL is already clean!");
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

        private void ReadMoreSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                item.AdvancePhase();
            }
            e.Handled = true;
        }

        private void ShowAllSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                item.ShowAllPhase();
            }
            e.Handled = true;
        }

        private void CollapseSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FlyShelf.ViewModels.ClipboardItem item)
            {
                item.CollapsePhase();
            }
            e.Handled = true;
        }


        private void ShelfListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (ShelfListView.SelectedItem is ClipboardItem renamingItem && renamingItem.IsRenaming)
            {
                return;
            }

            if (e.Key == Key.F2 && ShelfListView.SelectedItem is ClipboardItem f2Item && f2Item.CanRename)
            {
                StartInlineRename(f2Item);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete && ShelfListView.SelectedItems.Count > 0)
            {
                if (_isNotesActive || _isTodoActive) return; // Prevent deleting clipboard items while in overlay panels
                var itemsToRemove = ShelfListView.SelectedItems.Cast<ClipboardItem>().ToList();
                AnimateAndRemoveItems(itemsToRemove);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ShelfListView.SelectedItem is ClipboardItem selected)
            {
                if (_isNotesActive || _isTodoActive) return; // Prevent pasting clipboard items while in overlay panels
                _ = CopyItemAndPaste(selected, hideWindow: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_isSearchActive)
                {
                    CloseSearch();
                }
                else if (_isAltSearchActive)
                {
                    CloseAltSearch();
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
                        _viewModel.HandleDrop(data, forceClipboardSync: false);
                    }
                }
                catch { } // Best-effort: failure is acceptable
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
            // ── ESC key: dismiss clipboard from anywhere (notes, todo, any panel) ──
            if (e.Key == Key.Escape)
            {
                if (_isSearchActive)
                {
                    CloseSearch();
                }
                else if (_isAltSearchActive)
                {
                    CloseAltSearch();
                }
                else
                {
                    AnimateAndHide();
                }
                e.Handled = true;
                return;
            }

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
            // Left-click tray icon: summon clipboard + open Hub
            TrayOpenClipboard_Click(sender, e);
        }

        /// <summary>
        /// Tray menu: summon the clipboard popup AND open the Hub window.
        /// </summary>
        private void TrayOpenClipboard_Click(object sender, RoutedEventArgs e)
        {
            // Summon the clipboard popup if not already visible
            if (!_isCurrentlySummoned || !_viewModel.IsFullMode)
            {
                try { ToggleMainClipboard(); } catch { } // Best-effort: failure is acceptable
            }

            // Also open the Hub window
            OpenApp_Click_Internal();
        }

        /// <summary>
        /// Tray menu: open the Hub window (and summon clipboard too).
        /// </summary>
        private void TrayOpenHub_Click(object sender, RoutedEventArgs e)
        {
            // Summon the clipboard popup if not already visible
            if (!_isCurrentlySummoned || !_viewModel.IsFullMode)
            {
                try { ToggleMainClipboard(); } catch { } // Best-effort: failure is acceptable
            }

            // Also open the Hub window
            OpenApp_Click_Internal();
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

        // ═══ Incognito Mode ═══

        private System.Windows.Threading.DispatcherTimer _incognitoRefreshTimer;

        /// <summary>Call during startup to initialize incognito state and wire up events.</summary>
        internal void InitializeIncognitoMode()
        {
            Classes.IncognitoManager.Initialize();

            // Subscribe to state changes (stored for unsubscription)
            _incognitoStateChangedHandler = (isActive) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    UpdateIncognitoBadge();
                    // Start/stop refresh timer based on incognito state
                    if (isActive)
                        _incognitoRefreshTimer?.Start();
                    else
                        _incognitoRefreshTimer?.Stop();
                });
            };
            Classes.IncognitoManager.IncognitoStateChanged += _incognitoStateChangedHandler;

            // Refresh timer for countdown text
            _incognitoRefreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _incognitoRefreshTimer.Tick += (s, e) =>
            {
                if (Classes.IncognitoManager.IsIncognito)
                    UpdateIncognitoBadge();
            };
            // Only start if currently in incognito mode
            if (Classes.IncognitoManager.IsIncognito)
                _incognitoRefreshTimer.Start();

            // Initial state
            UpdateIncognitoBadge();
        }

        private void UpdateIncognitoBadge()
        {
            if (IncognitoBadge == null) return;

            if (Classes.IncognitoManager.IsIncognito)
            {
                IncognitoBadge.Visibility = Visibility.Visible;
                string remaining = Classes.IncognitoManager.RemainingTimeText;
                IncognitoBadgeText.Text = string.IsNullOrEmpty(remaining) ? "Incognito" : remaining;

                // Show the popup toggle bar below the header
                if (IncognitoToggleBar != null) IncognitoToggleBar.Visibility = Visibility.Visible;
                if (IncognitoToggleBarText != null)
                    IncognitoToggleBarText.Text = string.IsNullOrEmpty(remaining)
                        ? "Incognito Mode Active" : $"Incognito — {remaining}";
                if (IncognitoToggle != null) IncognitoToggle.IsChecked = true;
            }
            else
            {
                IncognitoBadge.Visibility = Visibility.Collapsed;
                if (IncognitoToggleBar != null) IncognitoToggleBar.Visibility = Visibility.Collapsed;
                if (IncognitoToggle != null) IncognitoToggle.IsChecked = false;
            }
        }

        private void IncognitoBadge_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Classes.IncognitoManager.DisableIncognito();
            Windows.ToastWindow.ShowToast("Clipboard monitoring resumed");
        }

        private void IncognitoToggle_Click(object sender, RoutedEventArgs e)
        {
            if (IncognitoToggle != null && IncognitoToggle.IsChecked == false)
            {
                Classes.IncognitoManager.DisableIncognito();
                Windows.ToastWindow.ShowToast("Clipboard monitoring resumed");
            }
        }

        // [FIX ANIM-8]: Pause GIF animations when items are recycled off-screen by VirtualizingStackPanel
        private void GifItemIcon_Loaded(object sender, RoutedEventArgs e)
        {
            // Defer GIF playback until scroll stops to avoid decoder stutter
            if (_viewModel.IsScrolling) return;

            if (sender is System.Windows.Controls.Image img)
            {
                try
                {
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(img);
                    animator?.Play();
                }
                catch { } // Best-effort: GIF may not be loaded yet
            }
        }

        private void GifItemIcon_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Image img)
            {
                try
                {
                    var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(img);
                    animator?.Pause();
                }
                catch { } // Best-effort
            }
        }

        private void ImagePlaceholderBorder_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item)
            {
                if (item.Icon == null && (item.ItemType == ViewModels.ClipboardItemType.Image || item.IsImagePreview))
                {
                    item.EnsureThumbnailLoadedAsync();
                }
            }
        }

    }
}


