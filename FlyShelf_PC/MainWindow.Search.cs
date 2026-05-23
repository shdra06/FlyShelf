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

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isSearchActive = false;

        private void SearchToggle_Click(object sender, RoutedEventArgs e)
        {
            _isSearchActive = !_isSearchActive;
            if (_isSearchActive)
            {
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
            
            // Clear the CollectionView filter to show all items again
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
            if (view != null) view.Filter = null;
            _viewModel.IsSearchActive = false;
            
            // Move focus back to the list view
            ShelfListView.Focus();

            // Stop mascot search animation
            try { Classes.AnimationTriggerService.Instance.OnSearchToggle(false); } catch { }
        }

        private void ApplySearchFilter(string query)
        {
            string queryClean = (query ?? "").Trim();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.DroppedItems);
            if (view == null) return;

            if (string.IsNullOrWhiteSpace(queryClean))
            {
                view.Filter = null;
                _viewModel.IsSearchActive = false;
            }
            else
            {
                string q = queryClean.ToLowerInvariant();
                _viewModel.IsSearchActive = true;
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
            }
        }
    }
}
