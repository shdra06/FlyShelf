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
using System.Windows.Media.Animation;
using FlyShelf.Classes;
using FlyShelf.Helpers;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isSearchActive = false;
        public bool IsSearchActive => _isSearchActive;
        private bool _isFilterBarActive = false;
        private bool _isClosingSearch = false;   // re-entrancy guard for CloseSearch
        private bool _isApplyingFilter = false;  // PERF: guard to prevent triple filter reapplication during category switch
        private DateTime _overflowPopupLastClosed = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _reapplyFilterDebounce; // PERF: coalesce rapid ReapplyActiveFilters calls
        private DateTime _lastFilterApplyTime = DateTime.MinValue; // PERF: throttle filter re-evaluation

        private void SearchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSearchActive)
            {
                // Close search — let CloseSearch() handle setting _isSearchActive = false
                CloseSearch();
            }
            else
            {
                // If Todo is active, close it first — search only works on clipboard/notes
                if (_isTodoActive) CloseTodoPanel(immediate: true);

                _isSearchActive = true;
                if (_isFilterBarActive) ToggleFilterBar(false);

                // Remove WS_EX_NOACTIVATE dynamically so the window can receive focus/keyboard input
                UpdateWindowActivationStyle();

                // Suppress DWM accent border before Activate() triggers it
                SuppressDwmBorder();

                // Activate the window so it receives keyboard input
                this.Activate();

                // Hide toolbar buttons so the search bar gets full width (keep filter + close visible)
                SearchToggleBtn.Visibility = Visibility.Collapsed;
                NotesToggleBtn.Visibility = Visibility.Collapsed;
                TodoToggleBtn.Visibility = Visibility.Collapsed;
                ResearchToggleBtn.Visibility = Visibility.Collapsed; // Replace networking with filter during search
                // SortFilterBtn stays visible — users can filter search results by category

                // ── Adjust search bar sizing for mini clipboard mode ──
                bool isMini = _viewModel?.CurrentMode == 0;
                if (isMini)
                {
                    // Mini mode: narrower search bar, hide more buttons to save space
                    SearchBarContainer.MinWidth = 80;
                    SearchBarContainer.Margin = new Thickness(0, 0, 4, 0);
                    if (MoreBtn != null) MoreBtn.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SearchBarContainer.MinWidth = 160;
                    SearchBarContainer.Margin = new Thickness(0, 0, 6, 0);
                }

                // ── Clear any stale animations and force-reset transform + opacity ──
                var scaleTransform = SearchBarContainer.RenderTransform as ScaleTransform;
                if (scaleTransform != null)
                {
                    scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scaleTransform.ScaleX = 0.0;
                }
                SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, null);
                SearchBarContainer.Opacity = 0.0;

                // Show container then animate in smoothly
                SearchBarContainer.Visibility = Visibility.Visible;

                var easeIn = new CubicEase { EasingMode = EasingMode.EaseOut };
                var scaleAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn };
                var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn };

                if (scaleTransform != null)
                    scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

                // Delay focus — the TextBox needs to be visible and rendered first
                Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                    SearchTextBox.CaretIndex = 0;

                    // ═══ Contextual Tip: First search ═══
                    Windows.TipBadge.Show("search_first_use", "Search text, files, or use / for commands", SearchBarContainer);
                }, System.Windows.Threading.DispatcherPriority.Input);

                // Trigger mascot search animation
                try { Classes.AnimationTriggerService.Instance.OnSearchToggle(true); } catch { } // Best-effort: failure is acceptable
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
                if (_notesSearchDebounce == null)
                {
                    _notesSearchDebounce = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    _notesSearchDebounce.Tick += (s, args) =>
                    {
                        _notesSearchDebounce.Stop();
                        ApplyNotesSearch(SearchTextBox.Text);
                    };
                }
                else
                {
                    _notesSearchDebounce.Stop();
                }
                _notesSearchDebounce.Start();
                return;
            }

            if (_isTodoActive)
            {
                if (_todoSearchDebounce == null)
                {
                    _todoSearchDebounce = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    _todoSearchDebounce.Tick += (s, args) =>
                    {
                        _todoSearchDebounce.Stop();
                        ApplyTodoSearch(SearchTextBox.Text);
                    };
                }
                else
                {
                    _todoSearchDebounce.Stop();
                }
                _todoSearchDebounce.Start();
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
            SuppressDwmBorder();
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
        internal void CloseSearch(bool switchingPanel = false)
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

                // ── Restore toolbar buttons respecting current mode ──
                UpdateToolbarButtonsVisibility();

                // ── Reset search bar sizing (may have been reduced for mini mode) ──
                SearchBarContainer.MinWidth = 160;
                SearchBarContainer.Margin = new Thickness(0, 0, 6, 0);

                // Smooth collapse animation
                var easeOut = new CubicEase { EasingMode = EasingMode.EaseIn };
                var scaleAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = easeOut };
                var opacityAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = easeOut };

                opacityAnim.Completed += (s, _) =>
                {
                    // After animation completes, collapse and reset transforms
                    SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, null);
                    SearchBarContainer.Opacity = 1.0;
                    var st = SearchBarContainer.RenderTransform as ScaleTransform;
                    if (st != null)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        st.ScaleX = 1.0;
                    }
                    SearchBarContainer.Visibility = Visibility.Collapsed;
                };

                var scaleTransform = SearchBarContainer.RenderTransform as ScaleTransform;
                if (scaleTransform != null)
                    scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                SearchBarContainer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

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
                        using (view.DeferRefresh())
                        {
                            if (_activeCategoryFilter == null)
                                view.Filter = null;
                            view.CustomSort = null;
                        }
                    }
                    return;
                }

                if (_isNotesActive)
                {
                    ApplyNotesSearch(""); // Clears search results in the UserControl
                    FocusNotesActiveTextBox();
                }
                else
                {
                    // Clear the CollectionView filter only if no category filter is active
                    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
                    if (view != null)
                    {
                        using (view.DeferRefresh())
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
                    using (view.DeferRefresh())
                    {
                        view.Filter = null;
                        if (ShelfListView != null && ShelfListView.Items.CanFilter) ShelfListView.Items.Filter = null;
                        if (AltShelfListView != null && AltShelfListView.Items.CanFilter) AltShelfListView.Items.Filter = null;
                        view.CustomSort = null;
                    }
                }
                _viewModel.IsSearchActive = _activeCategoryFilter != null;
            }
            else
            {
                string q = queryClean;

                _viewModel.IsSearchActive = true;
                
                // PERF: Apply filter synchronously (fast boolean check) but defer sorting.
                // The filter predicate is cheap — just string matching.
                _isApplyingFilter = true;
                try
                {
                    view.Filter = obj =>
                    {
                        if (obj is FlyShelf.ViewModels.ClipboardItem item)
                        {
                            // 1. Fuzzy match in text content or name (handles typos + word-order)
                            if (Classes.FuzzyMatcher.IsMatchAny(q, item.LowerFileName, item.LowerContent, item.FileName, item.RawContent))
                                return true;

                            // 2. Check exact extension match (direct property or via FilePath)
                            if (!string.IsNullOrEmpty(item.Extension) && item.Extension.Replace(".", "").Trim().Equals(q, StringComparison.OrdinalIgnoreCase))
                                return true;
                            if (!string.IsNullOrEmpty(item.FilePath))
                            {
                                try
                                {
                                    string ext = System.IO.Path.GetExtension(item.FilePath).Replace(".", "").Trim();
                                    if (ext.Equals(q, StringComparison.OrdinalIgnoreCase)) return true;
                                }
                                catch { } // Best-effort: failure is acceptable
                            }

                            // 3. Check exact match with the item type string
                            if (item.ItemType.ToString().Equals(q, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        return false;
                    };
                }
                finally
                {
                    _isApplyingFilter = false;
                }

                // PERF: Score and sort on background thread to avoid UI freeze with 100+ items.
                // Collect filtered items snapshot, score off-thread, then apply sort on UI thread.
                var filteredItems = new System.Collections.Generic.List<ViewModels.ClipboardItem>();
                foreach (var obj in view)
                {
                    if (obj is ViewModels.ClipboardItem ci)
                        filteredItems.Add(ci);
                }

                // Only sort if there are enough items to warrant the cost
                if (filteredItems.Count > 1)
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        var scoreCache = new Dictionary<ViewModels.ClipboardItem, double>(filteredItems.Count);
                        foreach (var ci in filteredItems)
                        {
                            scoreCache[ci] = Classes.FuzzyMatcher.ScoreBest(q, ci.LowerFileName, ci.LowerContent, ci.FileName, ci.RawContent);
                        }

                        Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                if (!_isSearchActive) return; // Search was closed while scoring

                                var currentView = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems) as ListCollectionView;
                                if (currentView == null) return;

                                currentView.CustomSort = Comparer<object>.Create((a, b) =>
                                {
                                    var sa = a is ViewModels.ClipboardItem ca && scoreCache.TryGetValue(ca, out var va) ? va : 0.0;
                                    var sb = b is ViewModels.ClipboardItem cb && scoreCache.TryGetValue(cb, out var vb) ? vb : 0.0;
                                    return sb.CompareTo(sa);
                                });
                            }
                            catch { } // Best-effort: failure is acceptable
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    });
                }
            }

            // PERF: Render thumbnails at ContextIdle — let layout complete first
            Dispatcher.InvokeAsync(() => RenderVisibleThumbnails(),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
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
                "PDF"    => FlyShelf.Helpers.ThemeColors.ErrorRed, // #EF4444 red
                "Docs"   => System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA), // #60A5FA blue
                _        => TryFindResource("SystemAccentColor") is System.Windows.Media.Color c ? c : System.Windows.Media.Colors.DodgerBlue
            };

            if (isActive)
            {
                // Strong tinted background + prominent border for the active chip
                var bgBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x40, categoryColor.R, categoryColor.G, categoryColor.B));
                bgBrush.Freeze();
                btn.Background = bgBrush;
                var borderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x60, categoryColor.R, categoryColor.G, categoryColor.B));
                borderBrush.Freeze();
                btn.BorderBrush = borderBrush;
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
                    ? (TryFindResource(bgKey) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent)
                    : System.Windows.Media.Brushes.Transparent;
                btn.BorderBrush = borderKey != null
                    ? (TryFindResource(borderKey) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent)
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

                // PERF: Throttle rapid category switches — ignore clicks within 100ms of last apply
                if ((DateTime.UtcNow - _lastFilterApplyTime).TotalMilliseconds < 100)
                    return;

                _activeCategoryFilter = category;

                // Close any active text search first
                if (_isSearchActive) CloseSearch();

                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
                if (view == null) return;

                // PERF: Set guard flag to prevent CollectionChanged handler from
                // re-firing ReapplyActiveFilters during this filter assignment.
                // Without this, every category click caused 5-6 redundant filter passes.
                _isApplyingFilter = true;
                try
                {
                    // PERF: Pre-build the category predicate ONCE, then assign.
                    // This avoids the switch expression running per-item inside DeferRefresh.
                    Predicate<object> categoryPredicate = category switch
                    {
                        "Images" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsImagePreview,
                        "Pinned" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsPinned,
                        "PDF" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsPdfPreview,
                        "Docs" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsDocPreview,
                        "Password" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsPassword,
                        _ => obj => obj is FlyShelf.ViewModels.ClipboardItem
                    };

                    // PERF: DeferRefresh batches the filter assignment into a single
                    // view refresh, preventing WPF from re-evaluating the filter and
                    // re-materializing containers multiple times.
                    using (view.DeferRefresh())
                    {
                        view.Filter = categoryPredicate;
                    }
                    _lastFilterApplyTime = DateTime.UtcNow;
                }
                finally
                {
                    _isApplyingFilter = false;
                }

                _viewModel.IsSearchActive = true;

                // Reduce bottom padding for filtered views — prevents excessive empty overscroll area
                // that makes the list feel "stuck" when only a few items remain.
                if (ShelfListView != null)
                    ShelfListView.Padding = new Thickness(0, 0, 0, 80);

                // Highlight the filter button to indicate active filter (use theme accent)
                SortFilterBtn.Foreground = TryFindResource("SystemAccentColorLight1Brush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DodgerBlue;

                // Update active state highlight on each button
                UpdateFilterButtonHighlight(FilterBtn_Images, "Images");
                UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned");
                UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF");
                UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs");
                UpdateFilterButtonHighlight(FilterBtn_Password, "Password");

                // PERF: Delay thumbnail rendering 300ms to let WPF finish container
                // virtualization. Immediate calls were iterating containers that
                // haven't been materialized yet, causing wasted layout passes.
                Dispatcher.InvokeAsync(() => RenderVisibleThumbnails(),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }

        private void FilterClear_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ClearCategoryFilter();
            ToggleFilterBar(false);
        }

        /// <summary>
        /// Redirects vertical mouse-wheel / touchpad scroll to horizontal offset on the
        /// category chip ScrollViewer. The scrollbar stays hidden; the gesture still works.
        /// </summary>
        private void CategoryFilterScroller_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (CategoryFilterScroller == null) return;

            // Delta >0 = scroll up (wheel forward) → shift chips left (scroll right on bar)
            // Use a 40px step per notch, same feel as Windows Explorer breadcrumb scroll.
            double scrollAmount = e.Delta > 0 ? -40 : 40;
            CategoryFilterScroller.ScrollToHorizontalOffset(
                CategoryFilterScroller.HorizontalOffset + scrollAmount);
            e.Handled = true; // Prevent the list behind from scrolling
        }

        private void ClearCategoryFilter()
        {
            _activeCategoryFilter = null;

            // PERF: Set guard flag to prevent CollectionChanged re-firing during clear
            _isApplyingFilter = true;
            try
            {
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
                if (view != null)
                {
                    // PERF: DeferRefresh batches the filter clear into a single view refresh
                    using (view.DeferRefresh())
                    {
                        view.Filter = null;
                    }
                }
                if (ShelfListView != null && ShelfListView.Items.CanFilter)
                {
                    ShelfListView.Items.Filter = null;
                }
                if (AltShelfListView != null && AltShelfListView.Items.CanFilter)
                {
                    AltShelfListView.Items.Filter = null;
                }
            }
            finally
            {
                _isApplyingFilter = false;
            }
            _viewModel.IsSearchActive = false;

            // Restore full bottom padding for unfiltered clipboard view
            if (ShelfListView != null)
                ShelfListView.Padding = new Thickness(0, 0, 0, 250);

            // Reset button color
            SortFilterBtn.Foreground = TryFindResource("MicaWPF.Brushes.TextFillColorSecondary") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

            // Update active state highlight on each button (clearing active colors)
            UpdateFilterButtonHighlight(FilterBtn_Images, "Images");
            UpdateFilterButtonHighlight(FilterBtn_Pinned, "Pinned");
            UpdateFilterButtonHighlight(FilterBtn_Pdf, "PDF");
            UpdateFilterButtonHighlight(FilterBtn_Docs, "Docs");
            UpdateFilterButtonHighlight(FilterBtn_Password, "Password");

            // PERF: Render thumbnails at Background priority after clear
            Dispatcher.InvokeAsync(() => RenderVisibleThumbnails(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        internal void ReapplyActiveFilters()
        {
            try
            {
                // PERF: Throttle — skip if we applied a filter very recently (< 50ms ago)
                // This prevents cascading re-evaluations when CollectionChanged fires
                // multiple times in rapid succession (e.g., drag-drop + clipboard copy).
                if ((DateTime.UtcNow - _lastFilterApplyTime).TotalMilliseconds < 50)
                    return;

                var listView = ShelfListView?.Items;
                var altListView = AltShelfListView?.Items;

                Predicate<object>? filterPredicate = null;

                if (_activeCategoryFilter != null)
                {
                    // PERF: Pre-build predicate with captured category string
                    // instead of evaluating switch per-item.
                    string category = _activeCategoryFilter;
                    filterPredicate = category switch
                    {
                        "Images" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsImagePreview,
                        "Pinned" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsPinned,
                        "PDF" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsPdfPreview,
                        "Docs" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsDocPreview,
                        "Password" => obj => obj is FlyShelf.ViewModels.ClipboardItem item && item.IsPassword,
                        _ => obj => obj is FlyShelf.ViewModels.ClipboardItem
                    };
                }
                else if (_isSearchActive)
                {
                    // Use the correct search box based on active UI mode
                    string searchText = _isAltUIActive ? AltSearchTextBox?.Text : SearchTextBox?.Text;
                    string q = searchText?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(q))
                    filterPredicate = obj =>
                    {
                        if (obj is FlyShelf.ViewModels.ClipboardItem item)
                        {
                            return Classes.FuzzyMatcher.IsMatchAny(q, item.LowerFileName, item.LowerContent, item.FileName, item.RawContent);
                        }
                        return false;
                    };
                }

                // PERF: Only set Filter on the currently ACTIVE ListView.
                // The hidden ListView doesn't need filtering — it wastes an entire
                // pass over all items. Filter it lazily when the UI mode switches.
                var activeListView = _isAltUIActive ? altListView : listView;
                if (activeListView != null && activeListView.CanFilter)
                {
                    using (activeListView.DeferRefresh())
                    {
                        activeListView.Filter = filterPredicate;
                    }
                }
                _lastFilterApplyTime = DateTime.UtcNow;
            }
            catch { } // Best-effort: failure is acceptable
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
                            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }


        private void ShortcutsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            // Lazy-load shortcuts data on first access
            if (Classes.ShortcutManager.Shortcuts.Count == 0)
            {
                Classes.ShortcutManager.Load();
            }

            var win = new Windows.ShortcutsWindow();
            WindowHelper.ShowInForeground(win);
        }

        private void ClearAllToolbar_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            ToggleClearConfirmPanel(true);
        }
    }
}
