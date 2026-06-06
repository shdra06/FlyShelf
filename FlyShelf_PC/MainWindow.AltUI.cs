using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    }
}
