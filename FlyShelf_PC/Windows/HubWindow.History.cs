// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// HubWindow.History.cs â€” Clipboard history management: duplicate sweeper,
// advanced date-range + category-filtered cleanup, and retention settings.
// Part of the HubWindow partial class split.
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FlyShelf.Classes;
using FlyShelf.ViewModels;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private bool _isRetentionChanging = false;
        // Suppress recursive updates when Select All programmatically toggles children
        private bool _isCategoryBulkUpdate = false;

        private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRetentionChanging) return;

            if (RetentionCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
            {
                if (int.TryParse(selected.Tag.ToString(), out int days))
                {
                    // v7.2 FREE: Pro gate temporarily bypassed â€” uncomment to re-enable
                    // if (days == 0 && !LicenseManager.IsPro)
                    // {
                    //     System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                    //         Windows.ToastWindow.ShowToast("Unlock Premium to use this option!"));
                    // 
                    //     _isRetentionChanging = true;
                    //     try
                    //     {
                    //         for (int i = 0; i < RetentionCombo.Items.Count; i++)
                    //         {
                    //             if (RetentionCombo.Items[i] is ComboBoxItem cbi && cbi.Tag?.ToString() == "7")
                    //             {
                    //                 RetentionCombo.SelectedIndex = i;
                    //                 break;
                    //             }
                    //         }
                    //     }
                    //     finally
                    //     {
                    //         _isRetentionChanging = false;
                    //     }
                    // 
                    //     MessageBox.Show(
                    //         "Disabling auto-cleanup (Never delete unpinned history) is a Pro feature.\n\nUpgrade to Pro to unlock the Never option!",
                    //         "FlyShelf  Pro Feature",
                    //         MessageBoxButton.OK,
                    //         MessageBoxImage.Information);
                    // 
                    //     UpgradePrompt.ShowActivationDialog(this);
                    //     return;
                    // }

                    SettingsManager.Current.ClipboardRetentionDays = days;
                    SettingsManager.Save();
                }
            }
        }

        private void SweepDuplicates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "This will scan your entire clipboard history, locate duplicate items (exact same text or exact same file path), and delete all older duplicates, keeping only the most recent version.\n\nPinned items are completely safe and will not be touched.\n\nAre you sure you want to proceed?",
                    "Duplicate Sweeper",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                var items = _viewModel.DroppedItems.ToList();
                if (items.Count == 0)
                {
                    ToastWindow.ShowToast("Clipboard history is empty.");
                    return;
                }

                var itemsToDelete = new List<ClipboardItem>();
                
                // Group duplicates using normalized keys (case-insensitive file path, exact text content)
                var groups = items.GroupBy(item => {
                    if (!string.IsNullOrEmpty(item.FilePath))
                        return "F:" + item.FilePath.ToLowerInvariant().Replace('\\', '/');
                    if (!string.IsNullOrEmpty(item.RawContent))
                        return "T:" + item.RawContent;
                    if (!string.IsNullOrEmpty(item.FileName))
                        return "N:" + item.FileName;
                    return string.Empty;
                });

                foreach (var g in groups)
                {
                    if (string.IsNullOrEmpty(g.Key)) continue;

                    // Sort by newest DateCopied descending
                    var sortedGroup = g.OrderByDescending(x => x.DateCopied).ToList();
                    
                    // The first item (index 0) is kept as the most recent.
                    // For the remaining items in the group, we delete them if they are not pinned.
                    for (int i = 1; i < sortedGroup.Count; i++)
                    {
                        var duplicateItem = sortedGroup[i];
                        if (!duplicateItem.IsPinned)
                        {
                            itemsToDelete.Add(duplicateItem);
                        }
                    }
                }

                if (itemsToDelete.Count == 0)
                {
                    ToastWindow.ShowToast("No duplicates found!");
                    return;
                }

                // Perform fast bulk removal
                _viewModel.BulkRemoveItems(itemsToDelete);

                ToastWindow.ShowToast($"Successfully swept {itemsToDelete.Count} duplicate(s)!");
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"Failed to sweep duplicates: {ex.Message}", 3000);
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // Advanced History Cleanup â€” Chip Grid + Inline Date Inputs
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        // Track chip selected state: Tag â†’ bool
        private Dictionary<string, bool> _chipStates = new();
        private bool _isDateSegmentUpdating = false;

        /// <summary>
        /// Initialize cleanup defaults: fill dates to current month, all chips enabled.
        /// Call from HubWindow_Loaded or Dashboard_Loaded.
        /// </summary>
        internal void InitCleanupDefaults()
        {
            try
            {
                var today = DateTime.Today;
                var monthStart = new DateTime(today.Year, today.Month, 1);

                SetDateSegments(FromDay, FromMonth, FromYear, monthStart);
                SetDateSegments(ToDay, ToMonth, ToYear, today);
                UpdateDateRangeSummary();

                // Initialize all chips as selected
                foreach (var chip in GetAllCategoryChips())
                {
                    string tag = chip.Tag?.ToString() ?? "";
                    _chipStates[tag] = true;
                }

                UpdateCleanupMatchCount();
            }
            catch { }
        }

        private void SetDateSegments(TextBox? dayBox, TextBox? monthBox, TextBox? yearBox, DateTime date)
        {
            _isDateSegmentUpdating = true;
            try
            {
                if (dayBox != null) dayBox.Text = date.Day.ToString("D2");
                if (monthBox != null) monthBox.Text = date.Month.ToString("D2");
                if (yearBox != null) yearBox.Text = date.Year.ToString("D4");
            }
            finally { _isDateSegmentUpdating = false; }
        }

        private DateTime? ParseDateFromSegments(TextBox? dayBox, TextBox? monthBox, TextBox? yearBox)
        {
            if (dayBox == null || monthBox == null || yearBox == null) return null;
            if (!int.TryParse(dayBox.Text, out int d) || !int.TryParse(monthBox.Text, out int m) || !int.TryParse(yearBox.Text, out int y))
                return null;
            if (y < 2000 || y > 2099 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
                return null;
            return new DateTime(y, m, d);
        }

        /// <summary>
        /// Returns all category chip borders (excluding the "All" chip).
        /// </summary>
        private Border[] GetAllCategoryChips() => new Border[]
        {
            ChipText, ChipCode, ChipUrl,
            ChipImage, ChipQRCode,
            ChipDocument, ChipPdf, ChipPresentation,
            ChipVideo, ChipAudio,
            ChipFile, ChipArchive, ChipFolder
        };

        private bool IsChipSelected(string tag) => _chipStates.TryGetValue(tag, out var s) && s;

        /// <summary>
        /// Returns the set of ClipboardItemTypes currently selected via chips.
        /// </summary>
        private HashSet<ClipboardItemType> GetSelectedCategories()
        {
            var selected = new HashSet<ClipboardItemType>();
            foreach (var chip in GetAllCategoryChips())
            {
                string tag = chip.Tag?.ToString() ?? "";
                if (IsChipSelected(tag) && Enum.TryParse<ClipboardItemType>(tag, true, out var parsed))
                    selected.Add(parsed);
            }
            return selected;
        }

        /// <summary>
        /// Computes the list of items matching the current date range and category filters.
        /// </summary>
        private List<ClipboardItem> GetFilteredCleanupItems()
        {
            DateTime? fromDate = ParseDateFromSegments(FromDay, FromMonth, FromYear);
            DateTime? toDate = ParseDateFromSegments(ToDay, ToMonth, ToYear);

            if (fromDate == null && toDate == null) return new List<ClipboardItem>();

            var selectedTypes = GetSelectedCategories();
            if (selectedTypes.Count == 0) return new List<ClipboardItem>();

            DateTime rangeStart = fromDate?.Date ?? DateTime.MinValue;
            DateTime rangeEnd = toDate?.Date.AddDays(1).AddTicks(-1) ?? DateTime.MaxValue;

            return _viewModel.DroppedItems
                .Where(item => !item.IsPinned
                    && item.DateCopied >= rangeStart
                    && item.DateCopied <= rangeEnd
                    && selectedTypes.Contains(item.ItemType))
                .ToList();
        }

        /// <summary>
        /// Updates the live match count label based on current filter selections.
        /// </summary>
        private void UpdateCleanupMatchCount()
        {
            if (CleanupMatchCount == null || _viewModel == null) return;

            try
            {
                DateTime? fromDate = ParseDateFromSegments(FromDay, FromMonth, FromYear);
                DateTime? toDate = ParseDateFromSegments(ToDay, ToMonth, ToYear);

                if (fromDate == null && toDate == null)
                {
                    CleanupMatchCount.Text = "Select a date range to preview";
                    CleanupMatchCount.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary");
                    return;
                }

                var selectedTypes = GetSelectedCategories();
                if (selectedTypes.Count == 0)
                {
                    CleanupMatchCount.Text = "No categories selected";
                    CleanupMatchCount.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary");
                    return;
                }

                var matchedItems = GetFilteredCleanupItems();
                int count = matchedItems.Count;

                if (count == 0)
                {
                    CleanupMatchCount.Text = "No matching items found";
                    CleanupMatchCount.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary");
                }
                else
                {
                    var typeCounts = matchedItems
                        .GroupBy(i => i.ItemType)
                        .OrderByDescending(g => g.Count())
                        .Take(3)
                        .Select(g => $"{g.Count()} {g.Key}");
                    string typeSummary = string.Join(", ", typeCounts);
                    if (matchedItems.GroupBy(i => i.ItemType).Count() > 3)
                        typeSummary += "â€¦";

                    CleanupMatchCount.Text = $"{count} item{(count != 1 ? "s" : "")} match ({typeSummary})";
                    CleanupMatchCount.Foreground = new System.Windows.Media.SolidColorBrush(
                        count > 100 ? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)
                                    : System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
                }
            }
            catch
            {
                CleanupMatchCount.Text = "Select a date range to preview";
            }
        }

        private void UpdateDateRangeSummary()
        {
            if (DateRangeSummary == null) return;
            DateTime? from = ParseDateFromSegments(FromDay, FromMonth, FromYear);
            DateTime? to = ParseDateFromSegments(ToDay, ToMonth, ToYear);

            if (from != null && to != null)
            {
                int days = (to.Value - from.Value).Days;
                if (days >= 0)
                    DateRangeSummary.Text = $"{from:MMM d, yyyy} â†’ {to:MMM d, yyyy}  ({days + 1} day{(days != 0 ? "s" : "")})";
                else
                    DateRangeSummary.Text = "âš  'From' must be before 'To'";
            }
            else if (from != null)
                DateRangeSummary.Text = $"From {from:MMM d, yyyy} onwards";
            else if (to != null)
                DateRangeSummary.Text = $"Up to {to:MMM d, yyyy}";
            else
                DateRangeSummary.Text = "";
        }

        // â”€â”€ Event Handlers: Date Inputs â”€â”€

        private void DateSegment_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            string tag = tb.Tag?.ToString() ?? "";

            if (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down)
            {
                int delta = e.Key == System.Windows.Input.Key.Up ? 1 : -1;
                if (int.TryParse(tb.Text, out int val))
                {
                    int newVal = val + delta;

                    if (tag.EndsWith("Day"))
                        newVal = newVal < 1 ? 31 : newVal > 31 ? 1 : newVal;
                    else if (tag.EndsWith("Month"))
                        newVal = newVal < 1 ? 12 : newVal > 12 ? 1 : newVal;
                    else if (tag.EndsWith("Year"))
                        newVal = Math.Clamp(newVal, 2000, 2099);

                    _isDateSegmentUpdating = true;
                    tb.Text = tag.EndsWith("Year") ? newVal.ToString("D4") : newVal.ToString("D2");
                    _isDateSegmentUpdating = false;
                    tb.SelectAll();

                    UpdateDateRangeSummary();
                    UpdateCleanupMatchCount();
                }
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Tab || e.Key == System.Windows.Input.Key.Right)
            {
                // Auto-advance to next segment on Tab or Right arrow at end
                if (tb.CaretIndex >= tb.Text.Length || e.Key == System.Windows.Input.Key.Tab)
                {
                    var next = GetNextDateSegment(tag);
                    if (next != null)
                    {
                        next.Focus();
                        next.SelectAll();
                        if (e.Key == System.Windows.Input.Key.Right) e.Handled = true;
                    }
                }
            }
            else if (e.Key == System.Windows.Input.Key.Left && tb.CaretIndex == 0)
            {
                var prev = GetPrevDateSegment(tag);
                if (prev != null)
                {
                    prev.Focus();
                    prev.SelectAll();
                    e.Handled = true;
                }
            }
        }

        private TextBox? GetNextDateSegment(string tag) => tag switch
        {
            "FromDay" => FromMonth,
            "FromMonth" => FromYear,
            "FromYear" => ToDay,
            "ToDay" => ToMonth,
            "ToMonth" => ToYear,
            _ => null
        };

        private TextBox? GetPrevDateSegment(string tag) => tag switch
        {
            "FromMonth" => FromDay,
            "FromYear" => FromMonth,
            "ToDay" => FromYear,
            "ToMonth" => ToDay,
            "ToYear" => ToMonth,
            _ => null
        };

        private void DateSegment_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void DateSegment_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            string tag = tb.Tag?.ToString() ?? "";

            // Pad with leading zero
            if (int.TryParse(tb.Text, out int val))
            {
                _isDateSegmentUpdating = true;
                tb.Text = tag.EndsWith("Year") ? val.ToString("D4") : val.ToString("D2");
                _isDateSegmentUpdating = false;
            }
        }

        private void DateSegment_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isDateSegmentUpdating) return;
            UpdateDateRangeSummary();
            UpdateCleanupMatchCount();

            // Auto-advance when segment is fully typed (2 digits for day/month, 4 for year)
            if (sender is TextBox tb)
            {
                string tag = tb.Tag?.ToString() ?? "";
                int maxLen = tag.EndsWith("Year") ? 4 : 2;
                if (tb.Text.Length >= maxLen)
                {
                    var next = GetNextDateSegment(tag);
                    if (next != null)
                    {
                        next.Focus();
                        next.SelectAll();
                    }
                }
            }
        }

        // â”€â”€ Event Handlers: Quick Presets â”€â”€

        private void QuickDatePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            string preset = fe.Tag?.ToString() ?? "";

            var today = DateTime.Today;
            DateTime from, to = today;

            switch (preset)
            {
                case "today":
                    from = today;
                    break;
                case "7d":
                    from = today.AddDays(-6);
                    break;
                case "30d":
                    from = today.AddDays(-29);
                    break;
                case "month":
                    from = new DateTime(today.Year, today.Month, 1);
                    break;
                default:
                    return;
            }

            SetDateSegments(FromDay, FromMonth, FromYear, from);
            SetDateSegments(ToDay, ToMonth, ToYear, to);
            UpdateDateRangeSummary();
            UpdateCleanupMatchCount();
        }

        // â”€â”€ Event Handlers: Category Chips â”€â”€

        private void TypeChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border chip) return;
            string tag = chip.Tag?.ToString() ?? "";

            if (tag == "All")
            {
                // Toggle all chips
                bool anyDeselected = GetAllCategoryChips().Any(c => !IsChipSelected(c.Tag?.ToString() ?? ""));
                foreach (var c in GetAllCategoryChips())
                {
                    string t = c.Tag?.ToString() ?? "";
                    _chipStates[t] = anyDeselected;
                    UpdateChipVisual(c, anyDeselected);
                }
                UpdateChipVisual(ChipAll, anyDeselected);
            }
            else
            {
                bool newState = !IsChipSelected(tag);
                _chipStates[tag] = newState;
                UpdateChipVisual(chip, newState);

                // Update "All" chip visual
                bool allSelected = GetAllCategoryChips().All(c => IsChipSelected(c.Tag?.ToString() ?? ""));
                UpdateChipVisual(ChipAll, allSelected);
            }

            UpdateCleanupMatchCount();
        }

        private void UpdateChipVisual(Border chip, bool selected)
        {
            chip.Opacity = selected ? 1.0 : 0.35;
        }

        // â”€â”€ Event Handlers: Cleanup Action â”€â”€

        private void AdvancedCleanup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DateTime? fromDate = ParseDateFromSegments(FromDay, FromMonth, FromYear);
                DateTime? toDate = ParseDateFromSegments(ToDay, ToMonth, ToYear);

                if (fromDate == null && toDate == null)
                {
                    ToastWindow.ShowToast("Please select a date range first.");
                    return;
                }

                if (fromDate != null && toDate != null && fromDate > toDate)
                {
                    ToastWindow.ShowToast("\"From\" date must be before \"To\" date.");
                    return;
                }

                var selectedTypes = GetSelectedCategories();
                if (selectedTypes.Count == 0)
                {
                    ToastWindow.ShowToast("Please select at least one category.");
                    return;
                }

                var itemsToDelete = GetFilteredCleanupItems();

                if (itemsToDelete.Count == 0)
                {
                    ToastWindow.ShowToast("No matching items found in the selected range.");
                    return;
                }

                string dateDesc;
                if (fromDate != null && toDate != null)
                    dateDesc = $"{fromDate:MMM d, yyyy} â†’ {toDate:MMM d, yyyy}";
                else if (fromDate != null)
                    dateDesc = $"from {fromDate:MMM d, yyyy} onwards";
                else
                    dateDesc = $"up to {toDate:MMM d, yyyy}";

                int typeCount = selectedTypes.Count;
                int totalTypes = GetAllCategoryChips().Length;
                string typeDesc = typeCount == totalTypes ? "all types" : $"{typeCount} selected type{(typeCount != 1 ? "s" : "")}";

                var confirm = MessageBox.Show(
                    $"This will permanently delete {itemsToDelete.Count} unpinned clipboard item{(itemsToDelete.Count != 1 ? "s" : "")} from {dateDesc} matching {typeDesc}.\n\nPinned items will remain safe and untouched.\n\nAre you sure you want to proceed?",
                    "History Cleanup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;

                _viewModel.BulkRemoveItems(itemsToDelete);

                ToastWindow.ShowToast($"Successfully deleted {itemsToDelete.Count} item{(itemsToDelete.Count != 1 ? "s" : "")}!");

                UpdateCleanupMatchCount();
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"Failed to clean history: {ex.Message}", 3000);
            }
        }
    }
}
