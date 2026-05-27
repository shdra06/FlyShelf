// ---------------------------------------------------------------
// MainWindow — Quick Notes Panel
// Toggle, navigation, bullet CRUD, freeform mode, search, images.
// Split from MainWindow.Search.cs for modularity.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private bool _isNotesActive = false;
        private bool _isNotesLoaded = false;
        private NoteDay? _selectedNoteDay = null;
        private bool _isNotesSearchActive = false;

        // ═══════════════════════════════════════════════════════════
        // TOGGLE NOTES PANEL
        // ═══════════════════════════════════════════════════════════

        private void NotesToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isNotesActive)
                CloseNotesPanel();
            else
                OpenNotesPanel();
        }

        private void OpenNotesPanel()
        {
            // Close other modes
            if (_isSearchActive) CloseSearch();
            if (_isFilterBarActive) ToggleFilterBar(false);
            if (_isUtilsBarActive) ToggleUtilsBar(false);
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;

            // Lazy-load notes data on first open
            if (!_isNotesLoaded)
            {
                NoteManager.Load();
                _isNotesLoaded = true;
            }

            // Ensure today exists and select it
            var today = NoteManager.EnsureToday();

            // Bind days list
            NotesDaySidebar.ItemsSource = NoteManager.Days;

            _isNotesActive = true;

            // Activate window for keyboard input
            this.Activate();

            // Hide clipboard, show notes
            ShelfListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            NotesPanel.Visibility = Visibility.Visible;

            // Highlight the notes button
            NotesToggleBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));

            // Animate in
            var slideAnim = new DoubleAnimation(-12, 0, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)));
            if (NotesPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);

            SelectNoteDay(today);
        }

        private void CloseNotesPanel()
        {
            _isNotesActive = false;

            // Save before closing
            NoteManager.SaveNow();

            // Reset button color
            NotesToggleBtn.Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");

            // Animate out
            var fadeAnim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150)));
            fadeAnim.Completed += (s, a) =>
            {
                if (!_isNotesActive)
                {
                    NotesPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    // Restore empty state if needed
                    if (_viewModel.DroppedItems.Count == 0)
                        EmptyStatePanel.Visibility = Visibility.Visible;
                }
            };
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private void NotesBack_Click(object sender, MouseButtonEventArgs e)
        {
            CloseNotesPanel();
        }

        // ═══════════════════════════════════════════════════════════
        // DAY SELECTION (SIDEBAR)
        // ═══════════════════════════════════════════════════════════

        private void SelectNoteDay(NoteDay day)
        {
            _selectedNoteDay = day;

            // Clear search if active
            if (_isNotesSearchActive)
            {
                _isNotesSearchActive = false;
                NotesSearchBar.Visibility = Visibility.Collapsed;
                NotesSearchBox.Text = "";
                NotesSearchResults.Visibility = Visibility.Collapsed;
                NotesContentArea.Visibility = Visibility.Visible;
            }

            // Update sidebar selection highlight
            UpdateDaySidebarSelection();

            // Bind content
            NotesBulletList.ItemsSource = day.Bullets;
            NotesFreeformBox.Text = day.FreeformContent ?? "";

            // Show correct mode
            if (day.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformBox.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformBox.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";
            }

            // Update day label
            NotesCurrentDayLabel.Text = day.DisplayDate;
        }

        private void UpdateDaySidebarSelection()
        {
            // Handled via data binding — IsToday and selection state
        }

        private void NotesDayItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteDay day)
            {
                SelectNoteDay(day);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // BULLET CRUD
        // ═══════════════════════════════════════════════════════════

        private void AddNewBulletAndFocus()
        {
            if (_selectedNoteDay == null) return;

            var bullet = NoteManager.AddBullet(_selectedNoteDay);

            // Focus the new bullet's TextBox after render
            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(bullet);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp);
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NotesAddBullet_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;

            var bullet = NoteManager.AddBullet(_selectedNoteDay);

            // Focus the new bullet's TextBox after render
            Dispatcher.InvokeAsync(() =>
            {
                // Find the last item container and focus its TextBox
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(bullet);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp);
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NoteBulletText_TextChanged(object sender, TextChangedEventArgs e)
        {
            NoteManager.MarkDirty();
        }

        private void NoteBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet)
            {
                // Shift+Enter → insert newline (AcceptsReturn handles this when true)
                // Enter without Shift → add new bullet below
                if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    AddNewBulletAndFocus();
                }
            }
        }

        private void NoteBulletCollapse_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                bullet.IsCollapsed = !bullet.IsCollapsed;
                NoteManager.MarkDirty();
            }
        }

        private void NoteBulletDelete_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                NoteManager.RemoveBullet(_selectedNoteDay, bullet);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // IMAGE PASTE & DROP ON BULLETS
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletText_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                // Check for image data on clipboard
                if (Clipboard.ContainsImage())
                {
                    e.CancelCommand(); // Cancel text paste

                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        string path = NoteManager.SaveImage(img);
                        bullet.ImagePath = path;
                        bullet.ImageDisplayWidth = Math.Min(img.PixelWidth, 280); // Fit within panel
                        NoteManager.MarkDirty();
                    }
                }
                // Check for image file path
                else if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    foreach (string? f in files)
                    {
                        if (f != null && IsImageFile(f))
                        {
                            e.CancelCommand();
                            // Copy image to notes directory
                            string destDir = NoteManager.GetImagesDirectory();
                            string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                            try
                            {
                                File.Copy(f, destFile, overwrite: true);
                                bullet.ImagePath = destFile;
                                NoteManager.MarkDirty();
                            }
                            catch { }
                            break; // Only first image
                        }
                    }
                }
            }
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
        }

        private void NoteImageResize_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                double newWidth = Math.Clamp(bullet.ImageDisplayWidth + delta, 60, 600);
                bullet.ImageDisplayWidth = newWidth;
                NoteManager.MarkDirty();
                e.Handled = true;
            }
        }

        private void NoteImageRemove_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                if (bullet.HasImage)
                {
                    try { File.Delete(bullet.ImagePath); } catch { }
                }
                bullet.ImagePath = "";
                NoteManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MODE TOGGLE (BULLETS ↔ FREEFORM)
        // ═══════════════════════════════════════════════════════════

        private void NotesModeToggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;

            _selectedNoteDay.IsFreeformMode = !_selectedNoteDay.IsFreeformMode;
            NoteManager.MarkDirty();

            if (_selectedNoteDay.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformBox.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";
                // Focus freeform
                Dispatcher.InvokeAsync(() =>
                {
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformBox.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";
            }
        }

        private void NotesFreeformBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedNoteDay != null && sender is TextBox tb)
            {
                _selectedNoteDay.FreeformContent = tb.Text;
                NoteManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES SEARCH
        // ═══════════════════════════════════════════════════════════

        private void NotesSearchToggle_Click(object sender, MouseButtonEventArgs e)
        {
            _isNotesSearchActive = !_isNotesSearchActive;
            if (_isNotesSearchActive)
            {
                this.Activate();
                NotesSearchBar.Visibility = Visibility.Visible;

                // Animate in
                var fadeAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));
                NotesSearchBar.BeginAnimation(OpacityProperty, fadeAnim);

                Dispatcher.InvokeAsync(() =>
                {
                    NotesSearchBox.Focus();
                    Keyboard.Focus(NotesSearchBox);
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                CloseNotesSearch();
            }
        }

        private void CloseNotesSearch()
        {
            _isNotesSearchActive = false;
            NotesSearchBox.Text = "";
            NotesSearchBar.Visibility = Visibility.Collapsed;
            NotesSearchResults.Visibility = Visibility.Collapsed;
            NotesContentArea.Visibility = Visibility.Visible;
        }

        private void NotesSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = NotesSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query))
            {
                NotesSearchResults.Visibility = Visibility.Collapsed;
                NotesContentArea.Visibility = Visibility.Visible;
                return;
            }

            var results = NoteManager.Search(query);

            // Build display items
            var displayItems = results.Select(r => new NoteSearchResult
            {
                DateLabel = r.Day.DisplayDate,
                Content = r.Bullet.Content,
                Day = r.Day,
                Bullet = r.Bullet
            }).ToList();

            NotesSearchResultsList.ItemsSource = displayItems;
            NotesSearchResults.Visibility = Visibility.Visible;
            NotesContentArea.Visibility = Visibility.Collapsed;
        }

        private void NotesSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseNotesSearch();
                e.Handled = true;
            }
        }

        private void NotesSearchResult_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteSearchResult result)
            {
                CloseNotesSearch();
                SelectNoteDay(result.Day);
            }
        }

    }

    /// <summary>ViewModel for search results display.</summary>
    public class NoteSearchResult
    {
        public string DateLabel { get; set; } = "";
        public string Content { get; set; } = "";
        public NoteDay Day { get; set; } = null!;
        public NoteBullet Bullet { get; set; } = null!;
    }
}
