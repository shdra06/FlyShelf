// ---------------------------------------------------------------
// NotesPanelControl.Sidebar.cs — Sidebar navigation, day/month
// selection, month picker popup, sidebar collapse/expand toggle,
// search results click handler, and back button.
// Partial class split from NotesPanelControl.xaml.cs.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using FlyShelf.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyShelf.Helpers;

namespace FlyShelf.Controls
{
    public partial class NotesPanelControl : UserControl
    {
        // ═══════════════════════════════════════════════════════════
        // DAY SELECTION (SIDEBAR)
        // ═══════════════════════════════════════════════════════════

        private void SelectNoteDay(NoteDay day)
        {
            // Auto-determine mode based on existing content
            bool hasBullets = day.Bullets.Any(b => !string.IsNullOrWhiteSpace(b.Header) || !string.IsNullOrWhiteSpace(b.Content) || b.HasImage);
            bool hasFreeform = !string.IsNullOrWhiteSpace(day.FreeformContent) || day.FreeformImages.Count > 0;

            if (hasBullets && !hasFreeform)
            {
                day.IsFreeformMode = false;
            }
            else if (hasFreeform && !hasBullets)
            {
                day.IsFreeformMode = true;
            }
            else if (!hasBullets && !hasFreeform)
            {
                // New/empty notes: always open in freeform mode by default
                day.IsFreeformMode = true;
            }

            _selectedNoteDay = day;
            _selectedMonth = -1;
            _selectedYear = -1;
            _notesCharLimitWarned = false; // Reset warning flag for the new note

            // Clear search if active
            var mainWin = GetMainWindow();
            if (mainWin != null && mainWin.IsSearchActive)
            {
                mainWin.CloseSearch();
            }

            // Update sidebar selection highlight
            UpdateSidebarSelectionVisuals();

            // Bind content
            NotesBulletList.ItemsSource = day.Bullets;
            day.MigrateFreeformIfNeeded(); // Ensure at least one section exists
            NotesFreeformSectionsList.ItemsSource = day.FreeformSections;

            // Show correct mode
            if (day.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "Bullets";
                // Defer focus to last freeform section text box
                Dispatcher.InvokeAsync(() =>
                {
                    FocusFreeformLastSection();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformArea.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "Freeform";

                // Auto-create a first bullet if the day is empty so user can start typing immediately
                if (day.Bullets.Count == 0)
                {
                    _lastBulletAddedTime = DateTime.MinValue; // Reset cooldown
                    AddNewBulletAndFocus();
                }
                else
                {
                    // Auto-focus the last bullet's content text box
                    FocusActiveTextBox();
                }
            }

            // Update day label
            NotesCurrentDayLabel.Text = day.DisplayDate;
        }

        private void RebuildSidebar()
        {
            // Direct-bind to NoteManager.Days — same pattern as Todo sidebar
            NotesDaySidebar.ItemsSource = NoteManager.Days;
        }

        private Border? _lastSelectedContainer;

        private void UpdateSidebarSelectionVisuals()
        {
            if (NotesDaySidebar == null) return;

            // Reset previously selected item
            if (_lastSelectedContainer != null)
            {
                _lastSelectedContainer.Background = FrozenBrush(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF));
                _lastSelectedContainer.BorderBrush = FrozenBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
                _lastSelectedContainer = null;
            }

            if (_selectedNoteDay == null) return;

            var container = NotesDaySidebar.ItemContainerGenerator.ContainerFromItem(_selectedNoteDay);
            if (container is ContentPresenter cp)
            {
                var mainBorder = FindVisualChild<Border>(cp, "NotesDayBorder");
                if (mainBorder != null)
                {
                    mainBorder.Background = FrozenBrush(ThemeColors.VioletAccentA2A);
                    mainBorder.BorderBrush = FrozenBrush(ThemeColors.VioletAccentA60);
                    _lastSelectedContainer = mainBorder;
                }
            }
        }

        private void SelectNoteMonth(int month, int year)
        {
            _selectedNoteDay = null;
            _selectedMonth = month;
            _selectedYear = year;

            var mainWin = GetMainWindow();
            if (mainWin != null && mainWin.IsSearchActive)
            {
                mainWin.CloseSearch();
            }

            var monthDate = new DateTime(year, month, 1);
            NotesCurrentDayLabel.Text = monthDate.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

            UpdateSidebarSelectionVisuals();

            var monthDays = NoteManager.Days.Where(d => d.Date.Month == month && d.Date.Year == year).ToList();
            var combinedBullets = new ObservableCollection<NoteBullet>();
            foreach (var d in monthDays)
            {
                foreach (var b in d.Bullets)
                {
                    combinedBullets.Add(b);
                }
            }

            NotesBulletList.ItemsSource = combinedBullets;

            NotesBulletList.Visibility = Visibility.Visible;
            NotesFreeformArea.Visibility = Visibility.Collapsed;
            NotesModeToggleText.Text = "Month View";
        }

        private void CurrentDayLabel_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay != null)
            {
                // Currently viewing a specific day — switch to month view
                SelectNoteMonth(_selectedNoteDay.Date.Month, _selectedNoteDay.Date.Year);
            }
            else if (_selectedMonth != -1 && _selectedYear != -1)
            {
                // Currently in month view — navigate to the most recent day
                var newestDay = NoteManager.Days
                    .Where(d => d.Date.Month == _selectedMonth && d.Date.Year == _selectedYear)
                    .OrderByDescending(d => d.Date)
                    .FirstOrDefault();
                if (newestDay != null)
                {
                    SelectNoteDay(newestDay);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SIDEBAR COLLAPSE / EXPAND
        // ═══════════════════════════════════════════════════════════

        private void CollapseNotesSidebar()
        {
            _isNotesSidebarCollapsed = true;
            NotesSidebarBorder.Visibility = Visibility.Collapsed;
            NotesSidebarColumn.Width = new GridLength(0);
            NotesSidebarCollapseIcon.Text = "▸";
            NotesSidebarExpandBtn.Visibility = Visibility.Visible;
        }

        private void NotesSidebarToggle_Click(object sender, MouseButtonEventArgs e)
        {
            // Cancel auto-collapse timer on manual interaction
            var mainWin = GetMainWindow();
            mainWin?.StopSidebarAutoCollapseTimer();

            _isNotesSidebarCollapsed = !_isNotesSidebarCollapsed;

            if (_isNotesSidebarCollapsed)
            {
                CollapseNotesSidebar();
            }
            else
            {
                // Expand: show sidebar border and restore column width
                NotesSidebarExpandBtn.Visibility = Visibility.Collapsed;
                NotesSidebarBorder.Visibility = Visibility.Visible;
                NotesSidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, null); // Clear any leftover animation
                NotesSidebarBorder.Width = double.NaN;
                NotesSidebarColumn.Width = new GridLength(42);
                NotesSidebarCollapseIcon.Text = "◂";
            }
        }

        private void NotesDayItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteDay day)
            {
                SelectNoteDay(day);
            }
        }

        private NoteDay? GetTargetDayForAdd()
        {
            if (_selectedNoteDay != null) return _selectedNoteDay;

            if (_selectedMonth != -1 && _selectedYear != -1)
            {
                var today = DateTime.Today;
                if (today.Month == _selectedMonth && today.Year == _selectedYear)
                {
                    return NoteManager.GetOrCreateDay(today);
                }

                var newest = NoteManager.Days
                    .Where(d => d.Date.Month == _selectedMonth && d.Date.Year == _selectedYear)
                    .OrderByDescending(d => d.Date)
                    .FirstOrDefault();

                if (newest != null) return newest;

                return NoteManager.GetOrCreateDay(new DateTime(_selectedYear, _selectedMonth, 1));
            }
            return null;
        }

        // ═══════════════════════════════════════════════════════════
        // MONTH PICKER — Navigate notes by month
        // ═══════════════════════════════════════════════════════════

        private void NotesMonthPicker_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            // Build month list for popup
            var monthsWithContent = NoteManager.Days
                .Where(d => {
                    bool hasBullets = d.Bullets.Any(b => !string.IsNullOrWhiteSpace(b.Header) || !string.IsNullOrWhiteSpace(b.Content) || b.HasImage);
                    bool hasFreeform = !string.IsNullOrWhiteSpace(d.FreeformContent) || d.FreeformImages.Count > 0;
                    return hasBullets || hasFreeform;
                })
                .Select(d => new { d.Date.Month, d.Date.Year })
                .Distinct()
                .OrderByDescending(m => m.Year)
                .ThenByDescending(m => m.Month)
                .ToList();

            var today = DateTime.Today;
            if (!monthsWithContent.Any(m => m.Month == today.Month && m.Year == today.Year))
            {
                monthsWithContent.Insert(0, new { Month = today.Month, Year = today.Year });
            }

            var items = monthsWithContent.Select(m => new NotesMonthPickerItem
            {
                MonthName = new DateTime(m.Year, m.Month, 1).ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture),
                YearText = m.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DayCount = NoteManager.Days.Count(d => d.Date.Month == m.Month && d.Date.Year == m.Year) + " days",
                Month = m.Month,
                Year = m.Year
            }).ToList();

            NotesMonthList.ItemsSource = items;
            NotesMonthPopup.IsOpen = !NotesMonthPopup.IsOpen;
        }

        private void NotesMonthItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NotesMonthPickerItem item)
            {
                NotesMonthPopup.IsOpen = false;

                // Navigate to the first (most recent) day in that month
                var firstDay = NoteManager.Days
                    .Where(d => d.Date.Month == item.Month && d.Date.Year == item.Year)
                    .OrderByDescending(d => d.Date)
                    .FirstOrDefault();

                if (firstDay != null)
                {
                    SelectNoteDay(firstDay);
                }
                else
                {
                    SelectNoteMonth(item.Month, item.Year);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES SEARCH
        // ═══════════════════════════════════════════════════════════

        private void NotesSearchResult_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteSearchResult result)
            {
                GetMainWindow()?.CloseSearch();
                SelectNoteDay(result.Day);
                if (result.Bullet != null)
                {
                    result.Bullet.IsCollapsed = false;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // BACK BUTTON — fires CloseRequested
        // ═══════════════════════════════════════════════════════════

        private void NotesBack_Click(object sender, MouseButtonEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════════════════════
        // VIEW MODE SWITCHING (Folders ↔ Journal)
        // ═══════════════════════════════════════════════════════════

        private void ViewFolders_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SwitchToFolderView();
        }

        private void ViewJournal_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SwitchToJournalView();
        }

        private void SwitchToFolderView()
        {
            _isFolderViewMode = true;

            // Update view switcher visuals — use SetCurrentValue to avoid blocking Style triggers
            ViewFoldersBtn.SetCurrentValue(Border.BackgroundProperty, FrozenBrush(Color.FromArgb(0x18, 0x8B, 0x5C, 0xF6)));
            ViewFoldersLabel.SetCurrentValue(ForegroundProperty, FrozenBrush(Color.FromArgb(0xFF, 0xC4, 0xB5, 0xFD)));
            ViewFoldersLabel.FontWeight = FontWeights.SemiBold;
            ViewJournalBtn.ClearValue(Border.BackgroundProperty); // Let Style handle it (transparent + hover)
            ViewJournalLabel.SetCurrentValue(ForegroundProperty, FrozenBrush(Color.FromArgb(0xFF, 0x8B, 0x8B, 0xA8)));
            ViewJournalLabel.FontWeight = FontWeights.Medium;

            // Switch sidebar panels
            DaySidebarPanel.Visibility = Visibility.Collapsed;
            FolderSidebarPanel.Visibility = Visibility.Visible;

            // Widen sidebar for folder names
            NotesSidebarColumn.Width = new GridLength(120);

            // Show breadcrumb
            NotesBreadcrumb.Visibility = Visibility.Visible;

            // Load folders into sidebar
            RefreshFolderTree();
        }

        private void SwitchToJournalView()
        {
            _isFolderViewMode = false;

            // Update view switcher visuals — use SetCurrentValue to avoid blocking Style triggers
            ViewJournalBtn.SetCurrentValue(Border.BackgroundProperty, FrozenBrush(Color.FromArgb(0x18, 0x8B, 0x5C, 0xF6)));
            ViewJournalLabel.SetCurrentValue(ForegroundProperty, FrozenBrush(Color.FromArgb(0xFF, 0xC4, 0xB5, 0xFD)));
            ViewJournalLabel.FontWeight = FontWeights.SemiBold;
            ViewFoldersBtn.ClearValue(Border.BackgroundProperty); // Let Style handle it (transparent + hover)
            ViewFoldersLabel.SetCurrentValue(ForegroundProperty, FrozenBrush(Color.FromArgb(0xFF, 0x8B, 0x8B, 0xA8)));
            ViewFoldersLabel.FontWeight = FontWeights.Medium;

            // Switch sidebar panels
            FolderSidebarPanel.Visibility = Visibility.Collapsed;
            DaySidebarPanel.Visibility = Visibility.Visible;

            // Restore narrow sidebar
            NotesSidebarColumn.Width = new GridLength(42);

            // Hide breadcrumb
            NotesBreadcrumb.Visibility = Visibility.Collapsed;

            // Rebuild day sidebar
            RebuildSidebar();
        }

        private void RefreshFolderTree()
        {
            var folders = NoteManager.Folders.OrderBy(f => f.SortOrder).ToList();
            NotesFolderTree.ItemsSource = folders;
        }

        // ═══════════════════════════════════════════════════════════
        // FOLDER TREE SIDEBAR HANDLERS
        // ═══════════════════════════════════════════════════════════

        private void FolderTreeItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteFolder folder)
            {
                e.Handled = true;
                _selectedFolder = folder;

                // Update breadcrumb
                BreadcrumbFolder.Visibility = Visibility.Visible;
                BreadcrumbFolderName.Text = folder.Name;
                BreadcrumbSeparator2.Visibility = Visibility.Visible;

                // Show notes in this folder
                var notesInFolder = NoteManager.GetNotesInFolder(folder.Id).ToList();
                if (notesInFolder.Count > 0)
                {
                    SelectNoteDay(notesInFolder.First());
                }
                else
                {
                    // Create a new note day in this folder
                    var today = NoteManager.EnsureToday();
                    NoteManager.MoveNoteToFolder(today, folder.Id);
                    SelectNoteDay(today);
                }
            }
        }

        private void FolderTreeItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteFolder folder)
            {
                e.Handled = true;
                var menu = new ContextMenu();

                // Rename
                var renameItem = new MenuItem { Header = "✏️  Rename" };
                renameItem.Click += async (s, ev) =>
                {
                    var inputBox = new TextBox
                    {
                        Text = folder.Name,
                        FontSize = 13,
                        Margin = new Thickness(0, 8, 0, 0),
                        SelectionStart = 0,
                        SelectionLength = folder.Name.Length,
                        Background = FrozenBrush(Color.FromRgb(0x22, 0x22, 0x40)),
                        Foreground = FrozenBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                        CaretBrush = FrozenBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                        Padding = new Thickness(8, 6, 8, 6),
                        BorderThickness = new Thickness(1),
                        BorderBrush = FrozenBrush(Color.FromRgb(0x3A, 0x3A, 0x5C))
                    };
                    var dialog = new Wpf.Ui.Controls.MessageBox
                    {
                        Title = "Rename Folder",
                        Content = inputBox,
                        PrimaryButtonText = "Rename",
                        CloseButtonText = "Cancel"
                    };
                    var result = await dialog.ShowDialogAsync();
                    if (result == Wpf.Ui.Controls.MessageBoxResult.Primary && !string.IsNullOrWhiteSpace(inputBox.Text))
                    {
                        NoteManager.RenameFolder(folder.Id, inputBox.Text.Trim());
                        RefreshFolderTree();
                    }
                };
                menu.Items.Add(renameItem);

                // New Subfolder
                var subfolderItem = new MenuItem { Header = "📁  New Subfolder" };
                subfolderItem.Click += (s, ev) =>
                {
                    NoteManager.CreateFolder("New Subfolder", folder.Id);
                    RefreshFolderTree();
                };
                menu.Items.Add(subfolderItem);

                menu.Items.Add(new Separator());

                // Delete
                var deleteItem = new MenuItem { Header = "🗑️  Delete Folder", Foreground = FrozenBrush(Color.FromRgb(0xFF, 0x66, 0x66)) };
                deleteItem.Click += (s, ev) =>
                {
                    NoteManager.DeleteFolder(folder.Id);
                    _selectedFolder = null;
                    BreadcrumbFolder.Visibility = Visibility.Collapsed;
                    BreadcrumbSeparator2.Visibility = Visibility.Collapsed;
                    RefreshFolderTree();
                };
                menu.Items.Add(deleteItem);

                menu.PlacementTarget = fe;
                menu.IsOpen = true;
            }
        }

        private void NewFolder_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            NoteManager.CreateFolder("New Folder", _selectedFolder?.Id);
            RefreshFolderTree();
        }

        // ═══════════════════════════════════════════════════════════
        // BREADCRUMB NAVIGATION
        // ═══════════════════════════════════════════════════════════

        private void BreadcrumbRoot_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _selectedFolder = null;
            BreadcrumbFolder.Visibility = Visibility.Collapsed;
            BreadcrumbSeparator2.Visibility = Visibility.Collapsed;
            RefreshFolderTree();
        }

        private void BreadcrumbFolder_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // Already viewing this folder, no-op
        }
    }
}
