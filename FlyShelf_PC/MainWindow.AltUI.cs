using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FlyShelf
{
    /// <summary>
    /// Alternate "Aero" clipboard UI — clean minimal card layout with accent bars.
    /// Shares the same DroppedItems data source and event handlers as the original UI.
    /// </summary>
    public partial class MainWindow
    {
        private bool _isAltUIActive = false;
        private bool _isAltSearchActive = false;

        /// <summary>
        /// Applies the correct UI mode based on UseAlternateClipboardUI setting.
        /// Called at startup and when the setting changes at runtime.
        /// </summary>
        private void ApplyAlternateUIMode()
        {
            bool useAlt = Classes.SettingsManager.Current.UseAlternateClipboardUI;
            _isAltUIActive = useAlt;

            if (AltClipboardPanel == null) return;

            // Toggle between original and alternate UI
            AltClipboardPanel.Visibility = useAlt ? Visibility.Visible : Visibility.Collapsed;
            ShelfListView.Visibility = useAlt ? Visibility.Collapsed : Visibility.Visible;
            HeaderAndFiltersStack.Visibility = useAlt ? Visibility.Collapsed : Visibility.Visible;

            // Hide original empty state when in alt mode (Aero has its own)
            if (useAlt && EmptyStatePanel != null)
                EmptyStatePanel.Visibility = Visibility.Collapsed;

            // Initialize scroll handler once for the Aero list view
            if (useAlt && _altScrollTimer == null)
                InitAltScrollHandler();

            // Trigger initial thumbnail rendering for visible images
            if (useAlt)
            {
                Dispatcher.InvokeAsync(() => RenderAltVisibleThumbnails(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }

            // Hide floating multi-action bar in alt mode
            // (Merge PDF and Unpin bar only works with original UI)
        }

        // ═══ Alt UI Search ═══
        private void AltSearchToggle_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleAltSearch();
        }

        private void ToggleAltSearch()
        {
            if (AltSearchContainer == null || AltSearchTextBox == null) return;

            _isAltSearchActive = !_isAltSearchActive;
            AltSearchContainer.Visibility = _isAltSearchActive ? Visibility.Visible : Visibility.Collapsed;
            AltSearchPlaceholder.Visibility = _isAltSearchActive ? Visibility.Collapsed : Visibility.Visible;

            if (_isAltSearchActive)
            {
                AltSearchTextBox.Focus();
            }
            else
            {
                AltSearchTextBox.Text = "";
                // Clear filter
                if (AltShelfListView?.ItemsSource != null)
                {
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(AltShelfListView.ItemsSource);
                    if (view != null) view.Filter = null;
                }
            }
        }

        private void AltSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AltShelfListView?.ItemsSource == null || AltSearchTextBox == null) return;

            string query = AltSearchTextBox.Text?.Trim() ?? "";
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(AltShelfListView.ItemsSource);
            if (view == null) return;

            if (string.IsNullOrEmpty(query))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = obj =>
                {
                    if (obj is ViewModels.ClipboardItem item)
                    {
                        return (item.FileName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    return false;
                };
            }
        }

        // ═══ Alt UI Settings ═══
        private void AltSettings_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenHubWindow();
        }

        // ═══ Alt UI Close ═══
        private void AltClose_Click(object sender, RoutedEventArgs e) => CloseWindow_Click(sender, e);

        // ═══ Alt UI card double-click (paste) ═══
        private void AltShelfListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => ShelfListView_MouseDoubleClick(sender, e);

        // ═══ Alt UI card selection changed ═══
        private void AltShelfListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ShelfListView_SelectionChanged(sender, e);

        // ═══ Alt UI drag support ═══
        private void AltShelfListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => ShelfListView_PreviewMouseLeftButtonDown(sender, e);

        private void AltShelfListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            => ShelfListView_PreviewMouseLeftButtonUp(sender, e);

        private void AltShelfListView_MouseMove(object sender, MouseEventArgs e)
            => ShelfListView_MouseMove(sender, e);

        private void AltShelfListView_KeyDown(object sender, KeyEventArgs e)
            => ShelfListView_KeyDown(sender, e);

        // ═══════════════════════════════════════════════════════════════
        // Category Sidebar Filters
        // Sidebar buttons: All, Text, Image, Pinned, PDF, Document.
        // Each is an x:Named Border in AltClipboardPanel XAML that routes
        // through AltCategoryFilter_Click via PreviewMouseLeftButtonDown.
        // ═══════════════════════════════════════════════════════════════

        private string _altActiveCategory = null; // null = show all

        private void AltCategoryFilter_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.Tag is string category)
            {
                ApplyAltCategoryFilter(category == "All" ? null : category);
                UpdateAltSidebarSelection(category);
            }
        }

        private void ApplyAltCategoryFilter(string category)
        {
            _altActiveCategory = category;
            if (AltShelfListView?.ItemsSource == null) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(AltShelfListView.ItemsSource);
            if (view == null) return;

            if (string.IsNullOrEmpty(category))
            {
                view.Filter = null;
                return;
            }

            view.Filter = obj =>
            {
                if (obj is ViewModels.ClipboardItem item)
                {
                    if (category == "Pinned") return item.IsPinned;
                    if (category == "Text") return item.ItemType == ViewModels.ClipboardItemType.Text || item.ItemType == ViewModels.ClipboardItemType.Code;
                    if (category == "Image") return item.ItemType == ViewModels.ClipboardItemType.Image || item.ItemType == ViewModels.ClipboardItemType.QRCode;
                    if (category == "PDF") return item.ItemType == ViewModels.ClipboardItemType.Pdf;
                    if (category == "Document") return item.ItemType == ViewModels.ClipboardItemType.Document || item.ItemType == ViewModels.ClipboardItemType.Presentation;
                    return true;
                }
                return false;
            };
        }

        private void UpdateAltSidebarSelection(string category)
        {
            // Update sidebar button backgrounds to show active state.
            // Each sidebar button is an x:Named Border in AltClipboardPanel XAML.
            // Use FindName so the code compiles before the XAML elements are wired.
            var names = new[] { "AltSidebarAll", "AltSidebarText", "AltSidebarImage", "AltSidebarPinned", "AltSidebarPdf", "AltSidebarDocument" };
            var categories = new[] { "All", "Text", "Image", "Pinned", "PDF", "Document" };
            for (int i = 0; i < names.Length; i++)
            {
                if (FindName(names[i]) is System.Windows.Controls.Border btn)
                {
                    btn.Background = categories[i] == category
                        ? (System.Windows.Media.Brush)FindResource("ThemeAccentBg")
                        : System.Windows.Media.Brushes.Transparent;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Bottom Bar Handlers
        // Sync, Clear, Shortcuts, More Options — wired from AltBottomBar.
        // ═══════════════════════════════════════════════════════════════

        private void AltSync_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // ToggleGlobalSync_Click expects RoutedEventArgs, so wrap the call
            ToggleGlobalSync_Click(sender, new RoutedEventArgs());
        }

        private void AltClear_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ClearShelf_ShowConfirm(sender, new RoutedEventArgs());
        }

        private void AltShortcuts_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                // Singleton: find existing ShortcutsWindow instead of spawning infinitely
                var existing = System.Windows.Application.Current.Windows
                    .OfType<Windows.ShortcutsWindow>()
                    .FirstOrDefault();
                if (existing != null)
                {
                    existing.Activate();
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    return;
                }

                var shortcutsWindow = new Windows.ShortcutsWindow();
                shortcutsWindow.Owner = System.Windows.Application.Current.MainWindow;
                shortcutsWindow.Show();
                shortcutsWindow.Activate();
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("ALT_UI", $"Failed to open shortcuts: {ex.Message}");
            }
        }

        private void AltMoreOptions_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (OverflowPopup != null)
            {
                // If already open, close it
                if (OverflowPopup.IsOpen)
                {
                    OverflowPopup.IsOpen = false;
                    return;
                }

                // Debounce: if StaysOpen="False" just closed the popup from
                // this same click, don't reopen it (toggle OFF behavior)
                if ((DateTime.Now - _overflowPopupLastClosed).TotalMilliseconds < 350)
                {
                    return;
                }

                OverflowPopup.PlacementTarget = sender as UIElement;
                OverflowPopup.Placement = PlacementMode.Top;
                OverflowPopup.HorizontalOffset = 0;
                OverflowPopup.VerticalOffset = -4;
                OverflowPopup.IsOpen = true;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Scroll-Aware Lazy Loading
        // Disables hover actions while the user is actively scrolling the
        // AltShelfListView.  Re-enables after 200 ms of scroll inactivity.
        // ═══════════════════════════════════════════════════════════════

        private System.Windows.Threading.DispatcherTimer _altScrollTimer;
        private System.Windows.Threading.DispatcherTimer _altThumbnailTimer;
        private ScrollViewer _altScrollViewer;
        private bool _isAltScrolling;

        private ScrollViewer GetAltScrollViewer()
        {
            if (_altScrollViewer == null && AltShelfListView != null)
            {
                _altScrollViewer = FindVisualChild<ScrollViewer>(AltShelfListView);
            }
            return _altScrollViewer;
        }

        private void InitAltScrollHandler()
        {
            _altScrollTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _altScrollTimer.Tick += (s, e) =>
            {
                _altScrollTimer.Stop();
                _isAltScrolling = false;
                if (_viewModel != null) _viewModel.AllowHover = true;
            };
        }

        private void AltShelfListView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.VerticalChange) > 0.5)
            {
                _isAltScrolling = true;
                if (_viewModel != null) _viewModel.AllowHover = false;
                _altScrollTimer?.Stop();
                _altScrollTimer?.Start();

                // Trigger thumbnail loading when scroll stops (30ms debounce)
                if (_altThumbnailTimer == null)
                {
                    _altThumbnailTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(30)
                    };
                    _altThumbnailTimer.Tick += (s2, e2) =>
                    {
                        _altThumbnailTimer.Stop();
                        RenderAltVisibleThumbnails();
                    };
                }
                else
                {
                    _altThumbnailTimer.Stop();
                }
                _altThumbnailTimer.Start();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Alt UI Thumbnail Rendering
        // Loads 300px thumbnails for image/QR items visible in the
        // AltShelfListView viewport. Mirrors RenderVisibleThumbnails
        // from MainWindow.Positioning.cs but targets the alt list.
        // ═══════════════════════════════════════════════════════════════

        private void RenderAltVisibleThumbnails()
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (!this.IsVisible || !_isAltUIActive) return;
                    if (AltShelfListView == null) return;
                    if (AltShelfListView.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                        return;

                    var sv = GetAltScrollViewer();
                    if (sv == null) return;

                    double viewportWidth = sv.ViewportWidth;
                    double viewportHeight = sv.ViewportHeight;
                    if (viewportHeight <= 0 || viewportWidth <= 0) return;

                    // Prefetch 800px above and below viewport
                    System.Windows.Rect viewportRect = new System.Windows.Rect(0, -800, viewportWidth, viewportHeight + 1600);
                    int count = AltShelfListView.Items.Count;
                    int imageCount = 0;

                    for (int i = 0; i < count; i++)
                    {
                        var item = AltShelfListView.Items[i] as ViewModels.ClipboardItem;
                        if (item == null) continue;
                        if (item.ItemType != ViewModels.ClipboardItemType.Image && item.ItemType != ViewModels.ClipboardItemType.QRCode) continue;

                        imageCount++;
                        bool isFirst5 = imageCount <= 5;

                        var container = AltShelfListView.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                        bool isVisible = false;

                        if (isFirst5)
                        {
                            isVisible = true;
                        }
                        else if (container != null && container.IsLoaded)
                        {
                            try
                            {
                                GeneralTransform transform = container.TransformToAncestor(sv);
                                System.Windows.Rect bounds = transform.TransformBounds(new System.Windows.Rect(0, 0, container.ActualWidth, container.ActualHeight));
                                isVisible = viewportRect.IntersectsWith(bounds);
                            }
                            catch { }
                        }

                        if (isVisible)
                        {
                            item.LeftViewportTime = null;
                            if (!item.IsLoadedHighQuality && !item.IsLoadingHighQuality)
                            {
                                item.IsLoadingHighQuality = true;
                                string filePath = item.FilePath;
                                int currentIndex = i;

                                _ = System.Threading.Tasks.Task.Run(() =>
                                {
                                    try
                                    {
                                        var bmp = ViewModels.FlyShelfViewModel.LoadImageThumbnail(filePath, 300);
                                        if (bmp != null)
                                        {
                                            Dispatcher.InvokeAsync(() =>
                                            {
                                                item.Icon = bmp;
                                                item.IsLoadedHighQuality = true;
                                                item.IsLoadingHighQuality = false;

                                                // Fade-in animation
                                                var element = AltShelfListView.ItemContainerGenerator.ContainerFromIndex(currentIndex) as FrameworkElement;
                                                if (element != null && element.IsLoaded)
                                                {
                                                    var img = FindVisualChild<System.Windows.Controls.Image>(element, "ItemIcon");
                                                    if (img != null)
                                                    {
                                                        var anim = new System.Windows.Media.Animation.DoubleAnimation
                                                        {
                                                            From = 0.2,
                                                            To = 1.0,
                                                            Duration = TimeSpan.FromMilliseconds(150),
                                                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                                                        };
                                                        img.BeginAnimation(UIElement.OpacityProperty, anim);
                                                    }
                                                }
                                            }, System.Windows.Threading.DispatcherPriority.Normal);
                                        }
                                        else
                                        {
                                            Dispatcher.InvokeAsync(() => { item.IsLoadingHighQuality = false; });
                                        }
                                    }
                                    catch
                                    {
                                        Dispatcher.InvokeAsync(() => { item.IsLoadingHighQuality = false; });
                                    }
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Classes.Logger.LogAction("ALT_THUMB_ERR", ex.Message);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        // ═══════════════════════════════════════════════════════════════
        // Convert to PDF — sidebar action
        // ═══════════════════════════════════════════════════════════════

        private void AltConvertPdf_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (AltShelfListView.SelectedItem is ViewModels.ClipboardItem item)
            {
                if (item.ItemType == ViewModels.ClipboardItemType.Image || item.IsImagePreview)
                {
                    _viewModel.ConvertImageToPdfCommand?.Execute(item);
                }
                else if (item.SmartActionType == "ConvertToPdf")
                {
                    item.ConvertDocumentTask();
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Select an image or document to convert");
                }
            }
            else
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Select an item first");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Alt UI Merge/Convert — toggle checkbox + floating action bar
        // ═══════════════════════════════════════════════════════════════

        private void AltPdfMergeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item)
            {
                item.IsCheckedForMerge = !item.IsCheckedForMerge;
                UpdatePdfMergeToolbar();
            }
        }
    }
}
