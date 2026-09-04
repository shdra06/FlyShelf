// ---------------------------------------------------------------
// NotesPanelControl.Bullets.cs — Bullet list CRUD, sub-bullets,
// focus management, image paste/drop/resize on bullet cards,
// mode toggle (Bullets ↔ Freeform), and sort helpers.
// Partial class split from NotesPanelControl.xaml.cs.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyShelf.Controls
{
    public partial class NotesPanelControl : UserControl
    {
        // ═══════════════════════════════════════════════════════════
        // BULLET CRUD — Add, Delete, Focus
        // ═══════════════════════════════════════════════════════════

        private void AddNewBulletAndFocus()
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            // ── Empty-card guard ────────────────────────────────────
            // If the last bullet is already completely empty, just focus it
            // instead of stacking another blank card on top of it.
            if (targetDay.Bullets.Count > 0)
            {
                var last = targetDay.Bullets[^1];
                bool lastIsEmpty = string.IsNullOrWhiteSpace(last.Header)
                                && string.IsNullOrWhiteSpace(last.Content)
                                && last.SubBullets.Count == 0
                                && !last.HasImage && !last.HasImage2;
                if (lastIsEmpty)
                {
                    // Focus that existing empty card's content box
                    Dispatcher.InvokeAsync(() =>
                    {
                        var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(last);
                        if (container is ContentPresenter cp)
                        {
                            var tb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                            tb?.Focus();
                            if (tb != null) Keyboard.Focus(tb);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Loaded); // Loaded priority: layout pass has completed, containers are realized
                    return;
                }
            }

            // Spam proof check: enforce 1 second cooldown
            if ((DateTime.Now - _lastBulletAddedTime).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastBulletAddedTime = DateTime.Now;

            var bullet = NoteManager.AddBullet(targetDay);

            if (_selectedNoteDay == null && _selectedMonth != -1)
            {
                RebuildSidebar();
                SelectNoteMonth(_selectedMonth, _selectedYear);
            }

            // Focus the new bullet's TextBox after render
            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(bullet);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded); // Loaded priority: layout pass has completed, containers are realized
        }

        /// <summary>
        /// Adds a new SubBulletItem to the parent NoteBullet that currently has keyboard focus,
        /// then focuses the new sub-bullet's TextBox.
        /// </summary>
        private void AddSubBulletAndFocus(NoteBullet parentBullet)
        {
            if (parentBullet == null) return;

            // Ensure the card is expanded so sub-bullets are visible
            parentBullet.IsCollapsed = false;

            var sub = new FlyShelf.Classes.SubBulletItem();
            parentBullet.SubBullets.Add(sub);
            parentBullet.OnSubBulletsChanged(); // notify HasSubBullets
            NoteManager.MarkDirty();

            // Focus the new sub-bullet TextBox after the ItemsControl renders it
            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                if (container is ContentPresenter cp)
                {
                    var ic = FindVisualChild<ItemsControl>(cp, "SubBulletsItemsControl");
                    if (ic != null)
                    {
                        ic.UpdateLayout(); // Required: nested sub-ItemsControl containers aren't realized by Loaded priority alone
                        var subContainer = ic.ItemContainerGenerator.ContainerFromItem(sub);
                        
                        if (subContainer == null)
                        {
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                            {
                                var delayedContainer = ic.ItemContainerGenerator.ContainerFromItem(sub);
                                if (delayedContainer is ContentPresenter delayedCp)
                                {
                                    var delayedTb = FindVisualChild<TextBox>(delayedCp, "SubBulletTextBox");
                                    delayedTb?.Focus();
                                    if (delayedTb != null) Keyboard.Focus(delayedTb);
                                }
                            }));
                        }
                        else if (subContainer is ContentPresenter subCp)
                        {
                            var tb = FindVisualChild<TextBox>(subCp, "SubBulletTextBox");
                            tb?.Focus();
                            if (tb != null) Keyboard.Focus(tb);
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Key handler for sub-bullet TextBoxes:
        ///   Enter            → create next sub-bullet in same parent
        ///   Shift+Enter      → dismantle (collapse) sub-bullets, return focus to card body
        ///   Backspace(empty) → remove this sub-bullet, focus previous or parent
        /// </summary>
        private void SubBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (tb.Tag is not FlyShelf.Classes.SubBulletItem sub) return;

            // Walk up the visual tree to find the parent NoteBullet via DataContext
            NoteBullet? parentBullet = null;
            DependencyObject? walk = VisualTreeHelper.GetParent(tb);
            while (walk != null)
            {
                if (walk is FrameworkElement fe && fe.DataContext is NoteBullet nb)
                {
                    parentBullet = nb;
                    break;
                }
                walk = VisualTreeHelper.GetParent(walk);
            }
            if (parentBullet == null) return;

            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift+Enter → remove current sub-bullet if empty, return focus to card body
                e.Handled = true;

                // Remove the current sub-bullet if it's empty
                if (string.IsNullOrWhiteSpace(tb.Text))
                {
                    parentBullet.SubBullets.Remove(sub);
                    NoteManager.MarkDirty();
                }

                Dispatcher.InvokeAsync(() =>
                {
                    var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                    if (container is ContentPresenter cp)
                    {
                        var bodyTb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                        if (bodyTb != null)
                        {
                            bodyTb.Focus();
                            Keyboard.Focus(bodyTb);
                            bodyTb.CaretIndex = bodyTb.Text.Length;
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (e.Key == Key.Enter)
            {
                // Enter → create next sub-bullet below
                e.Handled = true;
                AddSubBulletAndFocus(parentBullet);
            }
            else if (e.Key == Key.Back && string.IsNullOrEmpty(tb.Text))
            {
                e.Handled = true;
                int idx = parentBullet.SubBullets.IndexOf(sub);
                parentBullet.SubBullets.RemoveAt(idx);
                parentBullet.OnSubBulletsChanged();
                NoteManager.MarkDirty();

                // Focus the previous sub-bullet or the parent content box
                Dispatcher.InvokeAsync(() =>
                {
                    var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                    if (container is ContentPresenter cp)
                    {
                        if (idx > 0)
                        {
                            var ic = FindVisualChild<ItemsControl>(cp, "SubBulletsItemsControl");
                            if (ic != null)
                            {
                                var prevSub = parentBullet.SubBullets[idx - 1];
                                var subContainer = ic.ItemContainerGenerator.ContainerFromItem(prevSub);
                                if (subContainer is ContentPresenter subCp)
                                {
                                    var prevTb = FindVisualChild<TextBox>(subCp, "SubBulletTextBox");
                                    prevTb?.Focus();
                                    if (prevTb != null) Keyboard.Focus(prevTb);
                                }
                            }
                        }
                        else
                        {
                            // No more sub-bullets — focus parent content box
                            var parentTb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                            parentTb?.Focus();
                            if (parentTb != null) Keyboard.Focus(parentTb);
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Focuses the last sub-bullet TextBox of a parent bullet card.
        /// </summary>
        private void FocusLastSubBullet(NoteBullet parentBullet)
        {
            if (parentBullet.SubBullets.Count == 0) return;
            var lastSub = parentBullet.SubBullets[^1];

            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(parentBullet);
                if (container is ContentPresenter cp)
                {
                    var ic = FindVisualChild<ItemsControl>(cp, "SubBulletsItemsControl");
                    if (ic != null)
                    {
                        ic.UpdateLayout(); // Required: nested sub-ItemsControl containers aren't realized by Loaded priority alone
                        var subContainer = ic.ItemContainerGenerator.ContainerFromItem(lastSub);
                        if (subContainer is ContentPresenter subCp)
                        {
                            var tb = FindVisualChild<TextBox>(subCp, "SubBulletTextBox");
                            tb?.Focus();
                            if (tb != null) Keyboard.Focus(tb);
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NotesAddBullet_Click(object sender, MouseButtonEventArgs e)
        {
            var targetDay = GetTargetDayForAdd();
            if (targetDay == null) return;

            // If currently in freeform mode, add a new freeform section card
            if (_selectedNoteDay != null && _selectedNoteDay.IsFreeformMode)
            {
                AddNewFreeformSection();
                return;
            }

            AddNewBulletAndFocus();
        }

        // ═══════════════════════════════════════════════════════════
        // BULLET TEXT & HEADER HANDLERS
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletHeader_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.IsFocused && tb.DataContext is NoteBullet bullet)
            {
                bullet.LastEdited = DateTime.Now;
            }
            NoteManager.MarkDirty();
        }

        private void NoteBulletHeader_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    // Move focus to the content textbox of the same bullet card
                    tb.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                }
            }
        }

        private void NoteBulletText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                // Hard cap: truncate beyond 10K characters
                if (tb.Text.Length > NOTES_HARD_LIMIT)
                {
                    int caretPos = tb.CaretIndex;
                    tb.Text = tb.Text[..NOTES_HARD_LIMIT];
                    tb.CaretIndex = Math.Min(caretPos, NOTES_HARD_LIMIT);
                    Windows.ToastWindow.ShowToast("Note limit reached (10,000 chars max)");
                }
                // Soft warning at 5K characters
                else if (tb.Text.Length > NOTES_SOFT_LIMIT && !_notesCharLimitWarned)
                {
                    _notesCharLimitWarned = true;
                    Windows.ToastWindow.ShowToast("Note is getting long (5,000+ chars) — limit is 10,000");
                }

                if (tb.IsFocused && tb.DataContext is NoteBullet bullet)
                {
                    bullet.LastEdited = DateTime.Now;
                }
            }
            NoteManager.MarkDirty();
        }

        private void NoteBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                // Ctrl+V → image/file paste
                if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    // Check synchronously if clipboard has image data, then fire async handler
                    try
                    {
                        IDataObject data = Clipboard.GetDataObject();
                        if (data != null && (data.GetDataPresent(DataFormats.Bitmap) ||
                            data.GetDataPresent(typeof(BitmapSource)) ||
                            data.GetDataPresent("DeviceIndependentBitmap") ||
                            (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Any(f => f != null && IsImageFile(f)))))
                        {
                            HandleImagePasteForBullet_Async(bullet);
                            e.Handled = true;
                            return;
                        }
                    }
                    catch { } // Fall through to default paste
                }

                // Shift+Enter → always add a new sub-bullet below (predictable, no toggle surprises)
                // Plain Enter  → AcceptsReturn=True inserts a newline (native WPF)
                if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    bullet.IsCollapsed = false; // ensure sub-bullets area is visible
                    NoteManager.MarkDirty();
                    AddSubBulletAndFocus(bullet);
                }
            }
        }

        /// <summary>
        /// When a bullet TextBox gets focus, make sure the window is activated.
        /// This fixes the ghost-typing issue where text goes to external app.
        /// </summary>
        private void NoteBulletText_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _lastFocusedBulletTextBox = tb;
            }
            ActivateWithoutStealingFocusRequested?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════════════════════
        // BULLET COLLAPSE, PIN, REMINDER, DELETE
        // ═══════════════════════════════════════════════════════════

        private void NoteBulletCollapse_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                bullet.IsCollapsed = !bullet.IsCollapsed;
                NoteManager.MarkDirty();
            }
        }

        /// <summary>
        /// Auto-expand a collapsed bullet card when clicked anywhere on it.
        /// Only expands — does not re-collapse (use the collapse toggle for that).
        /// </summary>
        private void BulletCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Traverse up the visual tree from the original source to see if we clicked an interactive action button
            DependencyObject dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep != sender)
            {
                if (dep is FrameworkElement fe)
                {
                    if (fe.Name == "BulletDeleteBtn" || fe.Name == "BulletReminderBtn" || fe.Name == "BulletMoreBtn" || fe.Name == "BulletCollapseBtn")
                    {
                        // Let the specific button handler deal with it
                        return;
                    }
                }
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (sender is FrameworkElement cardFe && cardFe.DataContext is NoteBullet bullet && bullet.IsCollapsed)
            {
                bullet.IsCollapsed = false;
                NoteManager.MarkDirty();
            }
        }

        private void NoteBulletPin_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is Classes.NoteBullet bullet)
            {
                bullet.IsPinned = !bullet.IsPinned;
                Classes.NoteManager.MarkDirty();
            }
        }

        private void NoteBulletReminder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                // Build the raw text from the bullet's header and/or content
                string noteText = !string.IsNullOrEmpty(bullet.Header) ? bullet.Header :
                                   (!string.IsNullOrEmpty(bullet.Content) ? (bullet.Content.Length > 120 ? bullet.Content[..120] : bullet.Content) : "");

                // Use the NLP parser to extract a clean title and calculated due date
                var (parsedTitle, calculatedDue) = Classes.NaturalLanguageReminderParser.Parse(noteText, DateTime.Now);

                // If the note belongs to a future date, use that date's 9 AM as minimum
                if (_selectedNoteDay != null && _selectedNoteDay.Date.Date > DateTime.Today && calculatedDue < _selectedNoteDay.Date.Date.AddHours(9))
                {
                    calculatedDue = _selectedNoteDay.Date.Date.AddHours(9);
                }

                try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
                WindowHelper.ShowInForeground(reminderWindow);
                _activeReminderCreateWindow = reminderWindow;
            }
        }

        private void NoteBulletDelete_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                // The very first bullet card is permanent — it cannot be deleted.
                if (_selectedNoteDay.Bullets.Count > 0 && _selectedNoteDay.Bullets[0] == bullet)
                    return;

                var result = MessageBox.Show("Are you sure you want to delete this bullet?", "Delete Bullet",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    NoteManager.DeleteBullet(_selectedNoteDay, bullet);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // IMAGE PASTE & DROP ON BULLETS
        // ═══════════════════════════════════════════════════════════

        private bool AssignImageToBullet(NoteBullet bullet, string path, double width)
        {
            if (string.IsNullOrEmpty(bullet.ImagePath))
            {
                bullet.ImagePath = path;
                bullet.ImageDisplayWidth = width;
                NoteManager.MarkDirty();
                return true;
            }
            else if (string.IsNullOrEmpty(bullet.ImagePath2))
            {
                // v7.2 FREE: Unlocked for all users — uncomment to re-enable Pro gate
                // if (!LicenseManager.IsPro)
                // {
                //     System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                //         Windows.ToastWindow.ShowToast("Embedding 2 images per bullet is a Pro feature."));
                //     return false;
                // }
                bullet.ImagePath2 = path;
                bullet.ImageDisplayWidth2 = width;
                NoteManager.MarkDirty();
                return true;
            }
            return false;
        }

        private async void HandleImagePasteForBullet_Async(NoteBullet bullet)
        {
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return;

                if (data.GetDataPresent(DataFormats.Bitmap) || 
                    data.GetDataPresent(typeof(BitmapSource)) ||
                    data.GetDataPresent("DeviceIndependentBitmap"))
                {
                    BitmapSource? img = null;
                    if (data.GetDataPresent(DataFormats.Bitmap))
                        img = data.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (img == null && data.GetDataPresent(typeof(BitmapSource)))
                        img = data.GetData(typeof(BitmapSource)) as BitmapSource;
                    if (img == null && data.GetDataPresent("DeviceIndependentBitmap"))
                        img = Clipboard.GetImage();
                    if (img != null && img.CanFreeze) img.Freeze();

                    if (img != null)
                    {
                        string path = await NoteManager.SaveImage(img);
                        double width = Math.Min(img.PixelWidth, 140);
                        AssignImageToBullet(bullet, path, width);
                    }
                }
                else if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        foreach (string? f in files)
                        {
                            if (f != null && IsImageFile(f))
                            {
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_img_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}_{Path.GetFileName(f)}");
                                await Task.Run(() => File.Copy(f, destFile, overwrite: true));
                                AssignImageToBullet(bullet, destFile, 140);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"HandleImagePasteForBullet error: {ex.Message}");
            }
        }

        private async void NoteBulletText_Paste(object sender, DataObjectPastingEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                var dataObject = e.DataObject;
                if (dataObject == null) return;

                // Check for image data on clipboard
                if (dataObject.GetDataPresent(DataFormats.Bitmap))
                {
                    var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (img != null)
                    {
                        string path = await NoteManager.SaveImage(img);
                        double width = Math.Min(img.PixelWidth, 140);
                        if (AssignImageToBullet(bullet, path, width))
                        {
                            e.CancelCommand(); // Cancel text paste
                        }
                    }
                }
                // Check for image file path
                else if (dataObject.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        foreach (string? f in files)
                        {
                            if (f != null && IsImageFile(f))
                            {
                                // Copy image to notes directory
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                                try
                                {
                                    await Task.Run(() => File.Copy(f, destFile, overwrite: true));
                                    if (AssignImageToBullet(bullet, destFile, 140))
                                    {
                                        e.CancelCommand(); // Cancel text paste
                                    }
                                }
                                catch { } // Best-effort: failure is acceptable
                                break; // Only first image
                            }
                        }
                    }
                }
            }
            });
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
        }

        /// <summary>
        /// Replace all bullets in a NoteDay with a sorted list, minimizing UI churn.
        /// Assigns a new ObservableCollection so the binding fires a single PropertyChanged
        /// Reset notification instead of N individual Add notifications.
        /// </summary>
        private static void ReplaceBullets(NoteDay day, System.Collections.Generic.List<NoteBullet> sorted)
        {
            day.Bullets = new System.Collections.ObjectModel.ObservableCollection<NoteBullet>(sorted);
        }

        // ═══════════════════════════════════════════════════════════
        // BULLET IMAGE RESIZE / REMOVE / CLICK
        // ═══════════════════════════════════════════════════════════

        private void NoteImageResize_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                double newWidth = Math.Clamp(bullet.ImageDisplayWidth + delta, 60, 600);
                bullet.ImageDisplayWidth = Math.Min(newWidth, Math.Max(200, this.ActualWidth - 80));
                NoteManager.MarkDirty();
                e.Handled = true;
            }
        }

        private void NoteImageResize2_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                double newWidth = Math.Clamp(bullet.ImageDisplayWidth2 + delta, 60, 600);
                bullet.ImageDisplayWidth2 = Math.Min(newWidth, Math.Max(200, this.ActualWidth - 80));
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
                    _ = Task.Run(() => { try { File.Delete(bullet.ImagePath); } catch { } }); // Best-effort off UI thread
                }
                bullet.ImagePath = "";
                NoteManager.MarkDirty();
            }
        }

        private void NoteImageRemove2_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                if (bullet.HasImage2)
                {
                    _ = Task.Run(() => { try { File.Delete(bullet.ImagePath2); } catch { } }); // Best-effort off UI thread
                }
                bullet.ImagePath2 = "";
                NoteManager.MarkDirty();
            }
        }

        private void NoteImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet && bullet.HasImage)
            {
                var virtualItem = new FlyShelf.ViewModels.ClipboardItem
                {
                    FilePath = bullet.ImagePath,
                    ItemType = FlyShelf.ViewModels.ClipboardItemType.Image
                };
                GetMainWindow()?.ShowQuickLookForItem(virtualItem);
                e.Handled = true;
            }
        }

        private void NoteImage2_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet && bullet.HasImage2)
            {
                var virtualItem = new FlyShelf.ViewModels.ClipboardItem
                {
                    FilePath = bullet.ImagePath2,
                    ItemType = FlyShelf.ViewModels.ClipboardItemType.Image
                };
                GetMainWindow()?.ShowQuickLookForItem(virtualItem);
                e.Handled = true;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MODE TOGGLE (BULLETS ↔ FREEFORM)
        // ═══════════════════════════════════════════════════════════

        private void NotesModeToggle_Click(object sender, MouseButtonEventArgs e)
        {
            // Bullet mode removed — no-op, notes always use freeform mode
        }

        /// <summary>
        /// Previously flipped between Bullet mode and Freeform mode.
        /// Bullet mode has been removed — notes always use freeform mode now.
        /// </summary>
        private void ToggleNotesMode()
        {
            // No-op: bullet mode removed
        }
    }
}
