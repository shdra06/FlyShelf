// ═══════════════════════════════════════════════════════════════════════
// HubWindow.History.cs — Clipboard history management: duplicate sweeper,
// timeframe-based cleanup, and retention settings.
// Part of the HubWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FlyShelf.Classes;
using FlyShelf.ViewModels;

namespace FlyShelf.Windows
{
    public partial class HubWindow
    {
        private bool _isRetentionChanging = false;
        private void RetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRetentionChanging) return;

            if (RetentionCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
            {
                if (int.TryParse(selected.Tag.ToString(), out int days))
                {
                    if (days == 0 && !LicenseManager.IsPro)
                    {
                        System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                            Windows.ToastWindow.ShowToast("🔒 Unlock Premium to use this option!"));

                        _isRetentionChanging = true;
                        try
                        {
                            for (int i = 0; i < RetentionCombo.Items.Count; i++)
                            {
                                if (RetentionCombo.Items[i] is ComboBoxItem cbi && cbi.Tag?.ToString() == "7")
                                {
                                    RetentionCombo.SelectedIndex = i;
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            _isRetentionChanging = false;
                        }

                        MessageBox.Show(
                            "Disabling auto-cleanup (Never delete unpinned history) is a Pro feature.\n\nUpgrade to Pro to unlock the Never option!",
                            "FlyShelf — Pro Feature",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        UpgradePrompt.ShowActivationDialog(this);
                        return;
                    }

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
                    ToastWindow.ShowToast("Clipboard history is empty. 📋");
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
                    ToastWindow.ShowToast("No duplicates found! ✿");
                    return;
                }

                // Perform fast bulk removal
                _viewModel.BulkRemoveItems(itemsToDelete);

                ToastWindow.ShowToast($"Successfully swept {itemsToDelete.Count} duplicate(s)! 🧹");
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"Failed to sweep duplicates: {ex.Message}", 3000);
            }
        }

        private void CleanHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CleanupTimeframeCombo.SelectedItem is ComboBoxItem selected && selected.Tag != null)
                {
                    string tag = selected.Tag.ToString();
                    TimeSpan selectedTimeSpan;
                    string timeframeName;

                    switch (tag)
                    {
                        case "1h":
                            selectedTimeSpan = TimeSpan.FromHours(1);
                            timeframeName = "Last 1 Hour";
                            break;
                        case "6h":
                            selectedTimeSpan = TimeSpan.FromHours(6);
                            timeframeName = "Last 6 Hours";
                            break;
                        case "9h":
                            selectedTimeSpan = TimeSpan.FromHours(9);
                            timeframeName = "Last 9 Hours";
                            break;
                        case "24h":
                            selectedTimeSpan = TimeSpan.FromHours(24);
                            timeframeName = "Last 24 Hours";
                            break;
                        case "2d":
                            selectedTimeSpan = TimeSpan.FromDays(2);
                            timeframeName = "Last 2 Days";
                            break;
                        default:
                            return;
                    }

                    var confirm = MessageBox.Show(
                        $"Are you sure you want to permanently delete all unpinned clipboard entries from the {timeframeName}?\n\nPinned entries will remain secure and untouched.",
                        "Smart History Cleanup",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (confirm != MessageBoxResult.Yes) return;

                    DateTime threshold = DateTime.Now.Subtract(selectedTimeSpan);
                    var itemsToDelete = _viewModel.DroppedItems
                        .Where(item => !item.IsPinned && item.DateCopied >= threshold)
                        .ToList();

                    if (itemsToDelete.Count == 0)
                    {
                        ToastWindow.ShowToast($"No entries found from the {timeframeName}. ✿");
                        return;
                    }

                    _viewModel.BulkRemoveItems(itemsToDelete);

                    ToastWindow.ShowToast($"Successfully deleted {itemsToDelete.Count} entry/entries! 🧹");
                }
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"Failed to clean history: {ex.Message}", 3000);
            }
        }
    }
}
