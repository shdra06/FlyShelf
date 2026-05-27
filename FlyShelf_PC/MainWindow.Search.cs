// ---------------------------------------------------------------
// MainWindow — Search & Filter
// SearchToggle, TextChanged, PreviewKeyDown, CloseSearch,
// ApplySearchFilter
// Split from MainWindow.EventHandlers.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using FlyShelf.Classes;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isSearchActive = false;
        private bool _isFilterBarActive = false;
        private bool _isUtilsBarActive = false;
        private DateTime _overflowPopupLastClosed = DateTime.MinValue;

        private void SearchToggle_Click(object sender, RoutedEventArgs e)
        {
            _isSearchActive = !_isSearchActive;
            if (_isSearchActive)
            {
                if (_isFilterBarActive) ToggleFilterBar(false);
                // Activate the window so it receives keyboard input (normally it's a non-activating overlay)
                this.Activate();
                SearchBarContainer.Visibility = Visibility.Visible;
                if (SearchToggleBtn != null)
                {
                    SearchToggleBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0xB8, 0xA6));
                }
                
                // Smooth slide-down + fade-in animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(-8, 0, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));
                SearchBarContainer.RenderTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
                
                // Delay focus — the TextBox needs to be visible and rendered first
                Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                    SearchTextBox.CaretIndex = 0;
                }, System.Windows.Threading.DispatcherPriority.Input);

                // Trigger mascot search animation
                try { Classes.AnimationTriggerService.Instance.OnSearchToggle(true); } catch { }
            }
            else
            {
                CloseSearch();
            }
        }

        private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string query = SearchTextBox.Text;
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _searchDebounceTimer.Tick += (s, args) =>
                {
                    _searchDebounceTimer.Stop();
                    ApplySearchFilter(SearchTextBox.Text);
                };
            }
            else
            {
                _searchDebounceTimer.Stop();
            }

            _searchDebounceTimer.Start();
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ShelfListView.Items.Count > 0)
            {
                // Select first visible result and paste it
                ShelfListView.SelectedIndex = 0;
                if (ShelfListView.SelectedItem is ClipboardItem selected)
                {
                    CloseSearch();
                    _ = CopyItemAndPaste(selected, hideWindow: true);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down && ShelfListView.Items.Count > 0)
            {
                // Move focus to the list so user can arrow-navigate results
                ShelfListView.SelectedIndex = 0;
                var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ListViewItem;
                container?.Focus();
                e.Handled = true;
            }
        }

        private void SearchBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Activate window and focus the textbox when clicking anywhere on the search bar
            this.Activate();
            Dispatcher.InvokeAsync(() =>
            {
                SearchTextBox.Focus();
                Keyboard.Focus(SearchTextBox);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            CloseSearch();
        }

        private void CloseSearch()
        {
            _isSearchActive = false;
            _searchDebounceTimer?.Stop();
            SearchTextBox.Text = "";
            SearchBarContainer.Visibility = Visibility.Collapsed;
            if (SearchToggleBtn != null)
            {
                SearchToggleBtn.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");
            }
            
            // Clear the CollectionView filter only if no category filter is active
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
            if (view != null)
            {
                if (_activeCategoryFilter == null)
                {
                    view.Filter = null;
                }
                else
                {
                    // Reapply the active category filter to maintain persistence
                    ReapplyActiveFilters();
                }
                view.CustomSort = null;
            }
            _viewModel.IsSearchActive = false;
            
            // Also close utilities bar!
            if (_isUtilsBarActive) ToggleUtilsBar(false);

            // Move focus back to the list view
            ShelfListView.Focus();

            // Stop mascot search animation
            try { Classes.AnimationTriggerService.Instance.OnSearchToggle(false); } catch { }

            // Render newly visible thumbnails immediately
            RenderVisibleThumbnails();
        }

        private void ApplySearchFilter(string query)
        {
            if (_isUtilsBarActive) ToggleUtilsBar(false);

            string queryClean = (query ?? "").Trim();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
            if (view == null) return;

            if (string.IsNullOrWhiteSpace(queryClean))
            {
                view.Filter = null;
                view.CustomSort = null;
                _viewModel.IsSearchActive = false;
            }
            else
            {
                string q = queryClean.ToLowerInvariant();
                _viewModel.IsSearchActive = true;
                
                // Filter logic: Match name, content, extension, or type name
                view.Filter = obj =>
                {
                    if (obj is FlyShelf.ViewModels.ClipboardItem item)
                    {
                        // 1. Check substring match in text content or name
                        if (!string.IsNullOrEmpty(item.RawContent) && item.RawContent.ToLowerInvariant().Contains(q))
                            return true;
                        if (!string.IsNullOrEmpty(item.FileName) && item.FileName.ToLowerInvariant().Contains(q))
                            return true;

                        // 2. Check exact extension match (direct property or via FilePath)
                        if (!string.IsNullOrEmpty(item.Extension) && item.Extension.Replace(".", "").Trim().ToLowerInvariant() == q)
                            return true;
                        if (!string.IsNullOrEmpty(item.FilePath))
                        {
                            try
                            {
                                string ext = System.IO.Path.GetExtension(item.FilePath).Replace(".", "").Trim().ToLowerInvariant();
                                if (ext == q) return true;
                            }
                            catch { }
                        }

                        // 3. Check exact match with the item type string
                        if (item.ItemType.ToString().ToLowerInvariant() == q)
                            return true;
                    }
                    return false;
                };

                // Apply custom priority sorter
                view.CustomSort = new SearchResultComparer(q);
            }

            // Render newly visible thumbnails immediately
            RenderVisibleThumbnails();
        }

        // ═══════════════════════════════════════════════════════════════════
        // CATEGORY FILTER — Inline Responsive Bar
        // ═══════════════════════════════════════════════════════════════════

        private string? _activeCategoryFilter = null;

        private void SortFilter_Click(object sender, RoutedEventArgs e)
        {
            ToggleFilterBar(!_isFilterBarActive);
        }

        private void ToggleFilterBar(bool show)
        {
            if (SortFilterInlineBar == null) return;

            _isFilterBarActive = show;

            if (show)
            {
                // Close search and utilities if active
                if (_isSearchActive) CloseSearch();
                if (_isUtilsBarActive) ToggleUtilsBar(false);

                // Highlight buttons based on category
                UpdateFilterButtonHighlight(FilterBtn_Images, "Images", "#F472B6");
                UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned", "#FBBF24");
                UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF", "#EF4444");
                UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs", "#60A5FA");

                SortFilterInlineBar.Visibility = Visibility.Visible;

                // Smooth slide-down + fade-in animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(-8, 0, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));

                if (SortFilterInlineBar.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                SortFilterInlineBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            else
            {
                // Smooth slide-up + fade-out animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(0, -8, new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(120)));

                fadeAnim.Completed += (s, args) =>
                {
                    if (!_isFilterBarActive)
                    {
                        SortFilterInlineBar.Visibility = Visibility.Collapsed;
                    }
                };

                if (SortFilterInlineBar.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                SortFilterInlineBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        }

        private void UpdateFilterButtonHighlight(System.Windows.Controls.Border btn, string category, string accentHex)
        {
            if (btn == null) return;
            bool isActive = _activeCategoryFilter == category;
            var accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentHex);
            if (isActive)
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, accent.R, accent.G, accent.B));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x70, accent.R, accent.G, accent.B));
            }
            else
            {
                btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, accent.R, accent.G, accent.B));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, accent.R, accent.G, accent.B));
            }
        }

        private void FilterCategory_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Border border && border.Tag is string category)
            {
                // If clicking the same category again, toggle it off
                if (_activeCategoryFilter == category)
                {
                    ClearCategoryFilter();
                    ToggleFilterBar(false);
                    return;
                }

                _activeCategoryFilter = category;

                // Close any active text search first
                if (_isSearchActive) CloseSearch();

                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
                if (view == null) return;

                view.Filter = obj =>
                {
                    if (obj is FlyShelf.ViewModels.ClipboardItem item)
                    {
                        return category switch
                        {
                            "Images" => item.IsImagePreview,
                            "Pinned" => item.IsPinned,
                            "PDF" => item.IsPdfPreview,
                            "Docs" => item.IsDocPreview,
                            _ => true
                        };
                    }
                    return false;
                };

                _viewModel.IsSearchActive = true;

                // Highlight the filter button to indicate active filter
                SortFilterBtn.Foreground = new System.Windows.Media.SolidColorBrush(
                    category switch
                    {
                        "Images" => System.Windows.Media.Color.FromRgb(0xF4, 0x72, 0xB6),
                        "Pinned" => System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24),
                        "PDF" => System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44),
                        "Docs" => System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA),
                        _ => System.Windows.Media.Color.FromRgb(0x14, 0xB8, 0xA6)
                    });

                // Update active state highlight on each button
                UpdateFilterButtonHighlight(FilterBtn_Images, "Images", "#F472B6");
                UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned", "#FBBF24");
                UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF", "#EF4444");
                UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs", "#60A5FA");

                // Render newly visible thumbnails immediately
                RenderVisibleThumbnails();
            }
        }

        private void FilterClear_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ClearCategoryFilter();
            ToggleFilterBar(false);
        }

        private void ClearCategoryFilter()
        {
            _activeCategoryFilter = null;

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
            if (view != null) view.Filter = null;
            _viewModel.IsSearchActive = false;

            // Reset button color
            SortFilterBtn.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");

            // Update active state highlight on each button (clearing active colors)
            UpdateFilterButtonHighlight(FilterBtn_Images, "Images", "#F472B6");
            UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned", "#FBBF24");
            UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF", "#EF4444");
            UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs", "#60A5FA");

            // Render newly visible thumbnails immediately
            RenderVisibleThumbnails();
        }

        internal void ReapplyActiveFilters()
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
            if (view == null) return;

            if (_activeCategoryFilter != null)
            {
                string category = _activeCategoryFilter;
                view.Filter = obj =>
                {
                    if (obj is FlyShelf.ViewModels.ClipboardItem item)
                    {
                        return category switch
                        {
                            "Images" => item.IsImagePreview,
                            "Pinned" => item.IsPinned,
                            "PDF" => item.IsPdfPreview,
                            "Docs" => item.IsDocPreview,
                            _ => true
                        };
                    }
                    return false;
                };
                view.Refresh();
            }
            else if (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                string q = SearchTextBox.Text.Trim().ToLowerInvariant();
                view.Filter = obj =>
                {
                    if (obj is FlyShelf.ViewModels.ClipboardItem item)
                    {
                        if (!string.IsNullOrEmpty(item.RawContent) && item.RawContent.ToLowerInvariant().Contains(q))
                            return true;
                        if (!string.IsNullOrEmpty(item.FileName) && item.FileName.ToLowerInvariant().Contains(q))
                            return true;
                    }
                    return false;
                };
                view.Refresh();
            }
        }

        private void OverflowPopup_Closed(object sender, EventArgs e)
        {
            _overflowPopupLastClosed = DateTime.Now;
        }

        private void MoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null)
            {
                // If the popup was closed very recently (within 200ms), it means
                // the user clicked the MoreBtn to close it, and the StaysOpen="False"
                // behavior triggered a close before this click handler fired.
                // In that case, we want it to stay closed.
                if ((DateTime.Now - _overflowPopupLastClosed).TotalMilliseconds < 200)
                {
                    return;
                }

                OverflowPopup.PlacementTarget = MoreBtn;
                OverflowPopup.IsOpen = true;
            }
        }

        private void UtilsToolbar_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            ToggleUtilsBar(!_isUtilsBarActive);
        }

        private void ToggleUtilsBar(bool show)
        {
            if (UtilsInlineBar == null) return;

            _isUtilsBarActive = show;

            if (show)
            {
                // Close search and category filters
                if (_isSearchActive) CloseSearch();
                if (_isFilterBarActive) ToggleFilterBar(false);

                UtilsInlineBar.Visibility = Visibility.Visible;

                // Smooth slide-down + fade-in animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(-8, 0, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));

                if (UtilsInlineBar.RenderTransform is TranslateTransform translate)
                {
                    translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }
                UtilsInlineBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            else
            {
                // Smooth slide-up + fade-out animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation(0, -8, new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(120)));

                fadeAnim.Completed += (s, args) =>
                {
                    if (!_isUtilsBarActive)
                    {
                        UtilsInlineBar.Visibility = Visibility.Collapsed;
                    }
                };

                if (UtilsInlineBar.RenderTransform is TranslateTransform translate)
                {
                    translate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }
                UtilsInlineBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        }

        private void AddNoteToClipboard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                string noteText = QuickNoteInput.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(noteText)) return;

                // Set system clipboard — FlyShelf listener will automatically capture it,
                // create a card, evaluate smart actions, and slide it to the top!
                System.Windows.Clipboard.SetText(noteText);

                // Clear note scratchpad and close utilities bar
                QuickNoteInput.Text = "";
                ToggleUtilsBar(false);

                Windows.ToastWindow.ShowToast("Note added to clipboard! 📋");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to add note to clipboard: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShortcutTimer_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                ToggleUtilsBar(false);
                // Launch the new Dome Timer for 5 minutes
                var tw = new Windows.TimerWindow("5 minutes");
                tw.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch timer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShortcutClear_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                ToggleUtilsBar(false);
                
                var result = MessageBox.Show(
                    "Are you sure you want to clear all unpinned items from your clipboard shelf?",
                    "Clear Shelf",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _viewModel.ClearShelf();
                    Windows.ToastWindow.ShowToast("Shelf cleared! 🧹");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear shelf: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearAllToolbar_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            ToggleClearConfirmPanel(true);
        }
    }
}
