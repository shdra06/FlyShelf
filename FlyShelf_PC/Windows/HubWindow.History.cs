// ═══════════════════════════════════════════════════════════════════════
// HubWindow.History.cs — Clipboard history management: duplicate sweeper,
// advanced date-range + category-filtered cleanup, and retention settings.
// Part of the HubWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

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
                    // v7.2 FREE: Pro gate temporarily bypassed — uncomment to re-enable
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

        // ═══════════════════════════════════════════════════════════════
        // Advanced History Cleanup — Date Range + Category Filter
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// All category checkboxes in the filter dropdown, used for iteration.
        /// </summary>
        private CheckBox[] GetCategoryCheckboxes() => new[]
        {
            CatText, CatCode, CatUrl,
            CatImage, CatQRCode,
            CatDocument, CatPdf, CatPresentation,
            CatVideo, CatAudio,
            CatFile, CatArchive, CatFolder
        };

        /// <summary>
        /// Returns the set of ClipboardItemTypes currently selected in the category dropdown.
        /// </summary>
        private HashSet<ClipboardItemType> GetSelectedCategories()
        {
            var selected = new HashSet<ClipboardItemType>();
            foreach (var cb in GetCategoryCheckboxes())
            {
                if (cb != null && cb.IsChecked == true && cb.Tag is string tagStr)
                {
                    if (Enum.TryParse<ClipboardItemType>(tagStr, true, out var parsed))
                        selected.Add(parsed);
                }
            }
            return selected;
        }

        /// <summary>
        /// Computes the list of items matching the current date range and category filters.
        /// </summary>
        private List<ClipboardItem> GetFilteredCleanupItems()
        {
            DateTime? fromDate = CleanupFromDate?.SelectedDate;
            DateTime? toDate = CleanupToDate?.SelectedDate;

            // If no date range selected, return empty
            if (fromDate == null && toDate == null) return new List<ClipboardItem>();

            var selectedTypes = GetSelectedCategories();
            if (selectedTypes.Count == 0) return new List<ClipboardItem>();

            // Use start of fromDate and end of toDate for inclusive date range
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
                DateTime? fromDate = CleanupFromDate?.SelectedDate;
                DateTime? toDate = CleanupToDate?.SelectedDate;

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
                    // Build a category summary of matched types
                    var typeCounts = matchedItems
                        .GroupBy(i => i.ItemType)
                        .OrderByDescending(g => g.Count())
                        .Take(3)
                        .Select(g => $"{g.Count()} {g.Key}");
                    string typeSummary = string.Join(", ", typeCounts);
                    if (matchedItems.GroupBy(i => i.ItemType).Count() > 3)
                        typeSummary += "…";

                    CleanupMatchCount.Text = $"{count} item{(count != 1 ? "s" : "")} match ({typeSummary})";
                    CleanupMatchCount.Foreground = new System.Windows.Media.SolidColorBrush(
                        count > 100 ? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)  // Red for large deletions
                                    : System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green for normal
                }
            }
            catch
            {
                CleanupMatchCount.Text = "Select a date range to preview";
            }
        }

        /// <summary>
        /// Updates the category filter button label to reflect current selection.
        /// </summary>
        private void UpdateCategoryFilterLabel()
        {
            if (CategoryFilterLabel == null) return;

            var checkboxes = GetCategoryCheckboxes().Where(cb => cb != null).ToArray();
            int total = checkboxes.Length;
            if (total == 0) return;
            int checkedCount = checkboxes.Count(cb => cb.IsChecked == true);

            if (checkedCount == total)
                CategoryFilterLabel.Text = "All Types";
            else if (checkedCount == 0)
                CategoryFilterLabel.Text = "No Types Selected";
            else
            {
                // Show up to 2 selected type names, then "+N more"
                var names = checkboxes
                    .Where(cb => cb.IsChecked == true)
                    .Select(cb => cb.Content?.ToString() ?? "")
                    .Take(2)
                    .ToList();
                string label = string.Join(", ", names);
                if (checkedCount > 2)
                    label += $" +{checkedCount - 2} more";
                CategoryFilterLabel.Text = label;
            }
        }

        // ── Event Handlers ──

        private void CleanupDateRange_Changed(object? sender, SelectionChangedEventArgs e)
        {
            UpdateCleanupMatchCount();
        }

        private void CategoryFilter_ToggleDropdown(object sender, RoutedEventArgs e)
        {
            if (CategoryFilterPopup != null)
                CategoryFilterPopup.IsOpen = !CategoryFilterPopup.IsOpen;
        }

        private void CategorySelectAll_Changed(object sender, RoutedEventArgs e)
        {
            if (_isCategoryBulkUpdate) return;
            // Guard: This event fires during InitializeComponent() when XAML sets IsChecked,
            // but the individual category checkboxes (CatText, CatCode, etc.) aren't created yet.
            if (CategorySelectAll == null) return;
            _isCategoryBulkUpdate = true;
            try
            {
                bool isChecked = CategorySelectAll.IsChecked == true;
                foreach (var cb in GetCategoryCheckboxes())
                {
                    if (cb != null) cb.IsChecked = isChecked;
                }
            }
            finally
            {
                _isCategoryBulkUpdate = false;
            }
            UpdateCategoryFilterLabel();
            UpdateCleanupMatchCount();
        }

        private void CategoryItem_Changed(object sender, RoutedEventArgs e)
        {
            if (_isCategoryBulkUpdate) return;
            if (CategorySelectAll == null) return;

            // Sync the "Select All" checkbox state
            _isCategoryBulkUpdate = true;
            try
            {
                var checkboxes = GetCategoryCheckboxes().Where(cb => cb != null).ToArray();
                if (checkboxes.Length == 0) return;
                int checkedCount = checkboxes.Count(cb => cb.IsChecked == true);
                if (checkedCount == checkboxes.Length)
                    CategorySelectAll.IsChecked = true;
                else if (checkedCount == 0)
                    CategorySelectAll.IsChecked = false;
                else
                    CategorySelectAll.IsChecked = null; // Indeterminate
            }
            finally
            {
                _isCategoryBulkUpdate = false;
            }
            UpdateCategoryFilterLabel();
            UpdateCleanupMatchCount();
        }

        private void AdvancedCleanup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DateTime? fromDate = CleanupFromDate?.SelectedDate;
                DateTime? toDate = CleanupToDate?.SelectedDate;

                if (fromDate == null && toDate == null)
                {
                    ToastWindow.ShowToast("Please select a date range first.");
                    return;
                }

                // Validate date order
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

                // Build description for confirmation
                string dateDesc;
                if (fromDate != null && toDate != null)
                    dateDesc = $"{fromDate:MMM d, yyyy} → {toDate:MMM d, yyyy}";
                else if (fromDate != null)
                    dateDesc = $"from {fromDate:MMM d, yyyy} onwards";
                else
                    dateDesc = $"up to {toDate:MMM d, yyyy}";

                int typeCount = selectedTypes.Count;
                int totalTypes = GetCategoryCheckboxes().Length;
                string typeDesc = typeCount == totalTypes ? "all types" : $"{typeCount} selected type{(typeCount != 1 ? "s" : "")}";

                var confirm = MessageBox.Show(
                    $"This will permanently delete {itemsToDelete.Count} unpinned clipboard item{(itemsToDelete.Count != 1 ? "s" : "")} from {dateDesc} matching {typeDesc}.\n\nPinned items will remain safe and untouched.\n\nAre you sure you want to proceed?",
                    "Advanced History Cleanup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;

                _viewModel.BulkRemoveItems(itemsToDelete);

                ToastWindow.ShowToast($"Successfully deleted {itemsToDelete.Count} item{(itemsToDelete.Count != 1 ? "s" : "")}!");

                // Refresh the match count after deletion
                UpdateCleanupMatchCount();
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"Failed to clean history: {ex.Message}", 3000);
            }
        }
    }
}
