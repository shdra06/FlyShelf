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
        private bool _isClosingSearch = false;   // re-entrancy guard for CloseSearch
        private DateTime _overflowPopupLastClosed = DateTime.MinValue;

        private void SearchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSearchActive)
            {
                // Close search — let CloseSearch() handle setting _isSearchActive = false
                CloseSearch();
            }
            else
            {
                _isSearchActive = true;
                if (_isFilterBarActive) ToggleFilterBar(false);

                // Remove WS_EX_NOACTIVATE dynamically so the window can receive focus/keyboard input
                UpdateWindowActivationStyle();

                // Activate the window so it receives keyboard input
                this.Activate();

                // Hide the search button so the search bar covers its area too
                SearchToggleBtn.Visibility = Visibility.Collapsed;
                SearchBarContainer.Visibility = Visibility.Visible;

                // Smooth scale-in animation from right (ScaleX 0→1) + fade in
                var scaleTransform = SearchBarContainer.RenderTransform as System.Windows.Media.ScaleTransform;
                if (scaleTransform != null)
                {
                    var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    };
                    scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
                }
                var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
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
        }

        private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Guard: skip re-entrant calls fired while CloseSearch() is clearing the text box
            if (_isClosingSearch) return;

            string query = SearchTextBox.Text;
            // Placeholder visibility is now handled by ControlTemplate triggers

            if (_isNotesActive)
            {
                ApplyNotesSearch(query);
                return;
            }

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

        /// <summary>
        /// Closes the search bar and resets filters.
        /// When <paramref name="switchingPanel"/> is true, we skip heavy clipboard-specific
        /// cleanup (CollectionView refresh, RenderVisibleThumbnails, focus restore) because
        /// the caller (OpenNotesPanel / OpenTodoPanel) is about to collapse ShelfListView anyway.
        /// This prevents the UI-thread freeze caused by cascading re-entrant events.
        /// </summary>
        private void CloseSearch(bool switchingPanel = false)
        {
            if (_isClosingSearch) return;   // prevent re-entrant calls
            if (!_isSearchActive) return;   // PERF: fast-path — nothing to close
            _isClosingSearch = true;
            try
            {
                _isSearchActive = false;
                _searchDebounceTimer?.Stop();
                SearchTextBox.Text = "";           // fires TextChanged, but the guard above blocks it

                // Restore WS_EX_NOACTIVATE dynamically immediately
                UpdateWindowActivationStyle();

                // Smooth scale-out + fade-out, then collapse
                var scaleTransform = SearchBarContainer.RenderTransform as System.Windows.Media.ScaleTransform;
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                fadeOut.Completed += (s, ev) =>
                {
                    SearchBarContainer.Visibility = Visibility.Collapsed;
                    // Reset transforms for next open
                    if (scaleTransform != null)
                        scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                    SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, null);
                    SearchBarContainer.Opacity = 1.0;
                    if (scaleTransform != null) scaleTransform.ScaleX = 1.0;

                    // Restore the search toggle button
                    SearchToggleBtn.Visibility = Visibility.Visible;
                };
                if (scaleTransform != null)
                {
                    var scaleOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                    };
                    scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleOut);
                }
                SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                // Stop mascot search animation
                try { Classes.AnimationTriggerService.Instance.OnSearchToggle(false); } catch { }

                // ── When switching to Notes/Todo, skip all clipboard-specific work ──
                if (switchingPanel)
                {
                    // Still clear ViewModel search state and filters so returning
                    // to clipboard later doesn't show stale results
                    _viewModel.IsSearchActive = false;
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
                    if (view != null)
                    {
                        if (_activeCategoryFilter == null)
                            view.Filter = null;
                        view.CustomSort = null;
                    }
                    return;
                }

                if (_isNotesActive)
                {
                    NotesSearchResults.Visibility = Visibility.Collapsed;
                    NotesContentArea.Visibility = Visibility.Visible;
                    FocusNotesActiveTextBox();
                }
                else
                {
                    // Clear the CollectionView filter only if no category filter is active
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
                    if (view != null)
                    {
                        if (_activeCategoryFilter == null)
                        {
                            view.Filter = null;
                            if (ShelfListView != null && ShelfListView.Items.CanFilter) ShelfListView.Items.Filter = null;
                            if (AltShelfListView != null && AltShelfListView.Items.CanFilter) AltShelfListView.Items.Filter = null;
                        }
                        else
                        {
                            // Reapply the active category filter to maintain persistence
                            ReapplyActiveFilters();
                        }
                        view.CustomSort = null;
                    }
                    _viewModel.IsSearchActive = false;
                    ShelfListView.Focus();

                    // Render newly visible thumbnails immediately
                    RenderVisibleThumbnails();
                }
            }
            finally
            {
                _isClosingSearch = false;
            }
        }

        private void ApplySearchFilter(string query)
        {
            string queryClean = (query ?? "").Trim();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
            if (view == null) return;

            if (string.IsNullOrWhiteSpace(queryClean))
            {
                // CRITICAL: If a category filter is active, DON'T clear the CollectionView filter!
                // Restore the category predicate instead. Otherwise the filter bar shows
                // "Pinned/PDF/etc" but all items appear unfiltered.
                if (_activeCategoryFilter != null)
                {
                    ReapplyActiveFilters();
                }
                else
                {
                    view.Filter = null;
                    if (ShelfListView != null && ShelfListView.Items.CanFilter) ShelfListView.Items.Filter = null;
                    if (AltShelfListView != null && AltShelfListView.Items.CanFilter) AltShelfListView.Items.Filter = null;
                }
                view.CustomSort = null;
                _viewModel.IsSearchActive = _activeCategoryFilter != null;
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
            // In Notes mode, this button acts as the Reminder button
            if (_isNotesActive)
            {
                ReminderBtn_Click(sender, e);
                return;
            }

            if (_isFilterBarActive)
            {
                // Clicking the filter button while active → clear filter and close bar
                if (_activeCategoryFilter != null)
                    ClearCategoryFilter();
                ToggleFilterBar(false);
            }
            else
            {
                ToggleFilterBar(true);
            }
        }

        private void ToggleFilterBar(bool show)
        {
            if (SortFilterInlineBar == null) return;

            _isFilterBarActive = show;

            if (show)
            {
                // Close search and utilities if active
                if (_isSearchActive) CloseSearch();

                // Highlight buttons based on category
                UpdateFilterButtonHighlight(FilterBtn_Images, "Images");
                UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned");
                UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF");
                UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs");

                SortFilterInlineBar.Visibility = Visibility.Visible;

                // Smooth slide-down + fade-in animation
                var slideAnim = Classes.AnimationHelper.SlideIn();
                var fadeAnim = Classes.AnimationHelper.FadeIn();

                if (SortFilterInlineBar.RenderTransform is System.Windows.Media.TranslateTransform translate)
                {
                    translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slideAnim);
                }
                SortFilterInlineBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            else
            {
                // Smooth slide-up + fade-out animation
                var slideAnim = Classes.AnimationHelper.SlideOut();
                var fadeAnim = Classes.AnimationHelper.FadeOut(120);

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

        private void UpdateFilterButtonHighlight(System.Windows.Controls.Border btn, string category)
        {
            if (btn == null) return;
            bool isActive = _activeCategoryFilter == category;

            // Per-category accent colors for active state highlighting
            System.Windows.Media.Color categoryColor = category switch
            {
                "Images" => System.Windows.Media.Color.FromRgb(0xF4, 0x72, 0xB6), // #F472B6 pink
                "Pinned" => System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24), // #FBBF24 amber
                "PDF"    => System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44), // #EF4444 red
                "Docs"   => System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA), // #60A5FA blue
                _        => (System.Windows.Media.Color)FindResource("SystemAccentColor")
            };

            if (isActive)
            {
                // Strong tinted background + prominent border for the active chip
                btn.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x40, categoryColor.R, categoryColor.G, categoryColor.B));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x60, categoryColor.R, categoryColor.G, categoryColor.B));
                btn.BorderThickness = new Thickness(1.5);
            }
            else
            {
                // Restore default subtle tinted background from resources
                string bgKey = category switch
                {
                    "Images" => "FilterImageBg",
                    "Pinned" => "FilterPinnedBg",
                    "PDF"    => "FilterPdfBg",
                    "Docs"   => "FilterDocsBg",
                    _        => null
                };
                string borderKey = category switch
                {
                    "Images" => "FilterImageBorder",
                    "Pinned" => "FilterPinnedBorder",
                    "PDF"    => "FilterPdfBorder",
                    "Docs"   => "FilterDocsBorder",
                    _        => null
                };

                btn.Background = bgKey != null
                    ? (System.Windows.Media.Brush)FindResource(bgKey)
                    : System.Windows.Media.Brushes.Transparent;
                btn.BorderBrush = borderKey != null
                    ? (System.Windows.Media.Brush)FindResource(borderKey)
                    : System.Windows.Media.Brushes.Transparent;
                btn.BorderThickness = new Thickness(1);
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

                // Reduce bottom padding for filtered views — prevents excessive empty overscroll area
                // that makes the list feel "stuck" when only a few items remain.
                if (ShelfListView != null)
                    ShelfListView.Padding = new Thickness(0, 0, 0, 80);

                // Highlight the filter button to indicate active filter (use theme accent)
                SortFilterBtn.Foreground = (System.Windows.Media.Brush)FindResource("SystemAccentColorLight1Brush");

                // Update active state highlight on each button
                UpdateFilterButtonHighlight(FilterBtn_Images, "Images");
                UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned");
                UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF");
                UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs");

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
            if (ShelfListView != null && ShelfListView.Items.CanFilter)
            {
                ShelfListView.Items.Filter = null;
            }
            if (AltShelfListView != null && AltShelfListView.Items.CanFilter)
            {
                AltShelfListView.Items.Filter = null;
            }
            _viewModel.IsSearchActive = false;

            // Restore full bottom padding for unfiltered clipboard view
            if (ShelfListView != null)
                ShelfListView.Padding = new Thickness(0, 0, 0, 250);

            // Reset button color
            SortFilterBtn.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");

            // Update active state highlight on each button (clearing active colors)
            UpdateFilterButtonHighlight(FilterBtn_Images, "Images");
            UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned");
            UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF");
            UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs");

            // Render newly visible thumbnails immediately
            RenderVisibleThumbnails();
        }

        internal void ReapplyActiveFilters()
        {
            try
            {
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
                var listView = ShelfListView?.Items;
                var altListView = AltShelfListView?.Items;

                Predicate<object>? filterPredicate = null;

                if (_activeCategoryFilter != null)
                {
                    string category = _activeCategoryFilter;
                    filterPredicate = obj =>
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
                }
                else if (_isSearchActive && !string.IsNullOrWhiteSpace(SearchTextBox.Text))
                {
                    string q = SearchTextBox.Text.Trim().ToLowerInvariant();
                    filterPredicate = obj =>
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
                }

                if (view != null)
                {
                    view.Filter = filterPredicate;
                }

                if (listView != null && listView.CanFilter)
                {
                    listView.Filter = filterPredicate;
                }

                if (altListView != null && altListView.CanFilter)
                {
                    altListView.Filter = filterPredicate;
                }
            }
            catch { }
        }

        private void OverflowPopup_Closed(object sender, EventArgs e)
        {
            _overflowPopupLastClosed = DateTime.Now;
        }

        private void MoreBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null)
            {
                // If already open, close it
                if (OverflowPopup.IsOpen)
                {
                    OverflowPopup.IsOpen = false;
                    return;
                }

                // If the popup was closed very recently (within 350ms), it means
                // the user clicked the MoreBtn to close it, and the StaysOpen="False"
                // behavior triggered a close before this click handler fired.
                // In that case, we want it to stay closed (toggle OFF).
                if ((DateTime.Now - _overflowPopupLastClosed).TotalMilliseconds < 350)
                {
                    return;
                }

                OverflowPopup.PlacementTarget = MoreBtn;
                OverflowPopup.IsOpen = true;

                // Force popup HWND to be topmost — WPF popups in topmost windows
                // sometimes render behind the parent window's content area.
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var source = (System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(OverflowPopup.Child);
                        if (source != null)
                        {
                            var hwnd = source.Handle;
                            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                        }
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // Win32 interop for popup z-order fix
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);


        private void ShortcutsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            // Lazy-load shortcuts data on first access
            if (Classes.ShortcutManager.Shortcuts.Count == 0)
            {
                Classes.ShortcutManager.Load();
            }

            var win = new Windows.ShortcutsWindow();
            win.Show();
        }

        private void ClearAllToolbar_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            ToggleClearConfirmPanel(true);
        }
    }
}
