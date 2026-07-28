// ---------------------------------------------------------------
// NotesPanelControl.Freeform.cs — Freeform sections CRUD,
// text/image handlers, inline bullet mode, image paste/resize,
// freeform reminder, expand window, AI improve, and undo.
// Partial class split from NotesPanelControl.xaml.cs.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using FlyShelf.Models;
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
        // FREEFORM SECTION CRUD
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Add a new freeform section card and focus it.
        /// </summary>
        private void AddNewFreeformSection()
        {
            if (_selectedNoteDay == null) return;

            var section = new FreeformSection();
            _selectedNoteDay.FreeformSections.Add(section);
            NoteManager.MarkDirty();

            // Focus the new section after layout update
            Dispatcher.InvokeAsync(() =>
            {
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(section);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded); // Loaded priority: layout pass has completed, containers are realized
        }

        /// <summary>
        /// Remove a freeform section card. Prevents removing the last section.
        /// </summary>
        private void FreeformSectionRemove_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            e.Handled = true;

            if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
            {
                // Don't allow removing the last section
                if (_selectedNoteDay.FreeformSections.Count <= 1)
                {
                    Windows.ToastWindow.ShowToast("Cannot remove the only section");
                    return;
                }

                _selectedNoteDay.FreeformSections.Remove(section);
                NoteManager.MarkDirty();
            }
        }

        /// <summary>
        /// Focus the last freeform section's TextBox.
        /// </summary>
        private void FocusFreeformLastSection()
        {
            if (_selectedNoteDay == null || _selectedNoteDay.FreeformSections.Count == 0) return;

            // Use Loaded priority so the layout pass completes before we look up the container
            Dispatcher.InvokeAsync(() =>
            {
                if (_selectedNoteDay == null || _selectedNoteDay.FreeformSections.Count == 0) return;
                var lastSection = _selectedNoteDay.FreeformSections.Last();
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(lastSection);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null)
                    {
                        tb.Focus();
                        Keyboard.Focus(tb);
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Get the currently focused freeform section TextBox, or the last one if none focused.
        /// </summary>
        private TextBox? GetActiveFreeformTextBox()
        {
            // Check if any section TextBox currently has focus
            if (_selectedNoteDay == null) return null;
            foreach (var section in _selectedNoteDay.FreeformSections)
            {
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(section);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null && tb.IsFocused) return tb;
                }
            }
            // Fallback: return the last section's TextBox
            if (_selectedNoteDay.FreeformSections.Count > 0)
            {
                var lastSection = _selectedNoteDay.FreeformSections.Last();
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(lastSection);
                if (container is ContentPresenter cp)
                {
                    return FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                }
            }
            return null;
        }

        /// <summary>
        /// Get the FreeformSection whose TextBox currently has keyboard focus, or the last section as fallback.
        /// </summary>
        private FreeformSection? GetActiveFreeformSection()
        {
            if (_selectedNoteDay == null) return null;
            foreach (var section in _selectedNoteDay.FreeformSections)
            {
                var container = NotesFreeformSectionsList.ItemContainerGenerator.ContainerFromItem(section);
                if (container is ContentPresenter cp)
                {
                    var tb = FindVisualChild<TextBox>(cp, "FreeformSectionTextBox");
                    if (tb != null && tb.IsFocused) return section;
                }
            }
            // Fallback: return the last section
            return _selectedNoteDay.FreeformSections.LastOrDefault();
        }

        /// <summary>
        /// Check if the given section can accept another image (respects Free/Pro limits).
        /// Shows a toast if the limit is reached.
        /// </summary>
        private bool CanAddImageToSection(FreeformSection section)
        {
            int maxImages = LicenseManager.IsPro
                ? LicenseManager.PRO_NOTE_IMAGES_PER_CARD
                : LicenseManager.FREE_NOTE_IMAGES_PER_CARD;

            if (section.Images.Count >= maxImages)
            {
                if (!LicenseManager.IsPro)
                    UpgradePrompt.ShowNoteImageLimit();
                else
                    Windows.ToastWindow.ShowToast($"Max {LicenseManager.PRO_NOTE_IMAGES_PER_CARD} images per card");
                return false;
            }
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM TEXT HANDLERS & INLINE BULLET MODE
        // ═══════════════════════════════════════════════════════════

        private void NotesFreeformBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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
                        HandleImagePasteForFreeform_Async();
                        e.Handled = true;
                        return;
                    }
                }
                catch { } // Fall through to default paste
            }

            // ── Inline bullet list mode (Shift+Enter to start/stop) ─────────────────────────
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                if (sender is TextBox tb)
                {
                    bool isBulletMode = tb.Tag is bool b && b;
                    if (!isBulletMode)
                    {
                        // ─ Enable inline bullet mode ─
                        tb.Tag = true;
                        // If not at start of an empty line, break to a new line first
                        int caret = tb.CaretIndex;
                        string prefix = (caret > 0 && tb.Text.Length > 0 && tb.Text[caret - 1] != '\n')
                                        ? "\n\u2022 " : "\u2022 ";
                        tb.SelectedText = prefix;
                        tb.CaretIndex = caret + prefix.Length;
                    }
                    else
                    {
                        // ─ Disable inline bullet mode: remove • from current line, cursor stays ─
                        tb.Tag = false;
                        int caret = tb.CaretIndex;
                        string text = tb.Text;

                        // Find the start of the current line
                        int lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1));
                        lineStart = (lineStart < 0) ? 0 : lineStart + 1;

                        // Check if this line starts with "• " and remove it
                        if (lineStart + 2 <= text.Length && text.AsSpan(lineStart, 2).SequenceEqual("\u2022 ".AsSpan()))
                        {
                            tb.Text = text.Remove(lineStart, 2);
                            tb.CaretIndex = Math.Max(lineStart, caret - 2);
                        }
                    }
                }
                return;
            }

            // While in bullet mode, Enter continues the list with a new bullet
            if (e.Key == Key.Enter && sender is TextBox tbEnter && tbEnter.Tag is bool isBul && isBul)
            {
                e.Handled = true;
                int caret = tbEnter.CaretIndex;
                const string bullet = "\n\u2022 ";
                tbEnter.SelectedText = bullet;
                tbEnter.CaretIndex = caret + bullet.Length;
            }
        }

        private void NotesFreeformBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedNoteDay != null && sender is TextBox tb)
            {
                // Hard cap: truncate beyond 10K characters per section
                if (tb.Text.Length > NOTES_HARD_LIMIT)
                {
                    int caretPos = tb.CaretIndex;
                    tb.Text = tb.Text[..NOTES_HARD_LIMIT];
                    tb.CaretIndex = Math.Min(caretPos, NOTES_HARD_LIMIT);
                    Windows.ToastWindow.ShowToast("⚠️ Section limit reached (10,000 chars max)");
                }
                // Soft warning at 5K characters (once per session per note)
                else if (tb.Text.Length > NOTES_SOFT_LIMIT && !_notesCharLimitWarned)
                {
                    _notesCharLimitWarned = true;
                    Windows.ToastWindow.ShowToast("📝 Section is getting long (5,000+ chars) — limit is 10,000");
                }

                // Content is synced via TwoWay binding to FreeformSection.Content
                NoteManager.MarkDirty();
            }
        }

        /// <summary>
        /// When freeform TextBox gets focus, force-activate the window.
        /// </summary>
        private void NotesFreeformBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ActivateWithoutStealingFocusRequested?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM IMAGE PASTE
        // ═══════════════════════════════════════════════════════════

        private async void HandleImagePasteForFreeform_Async()
        {
            if (_selectedNoteDay == null) return;
            var section = GetActiveFreeformSection();
            if (section == null) return;
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
                        if (!CanAddImageToSection(section)) return; // block paste
                        string path = await NoteManager.SaveImage(img);
                        var freeformImg = new FreeformImage
                        {
                            ImagePath = path,
                            DisplayWidth = Math.Min(img.PixelWidth, 140)
                        };
                        section.Images.Add(freeformImg);
                        NoteManager.MarkDirty();
                        return;
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
                                if (!CanAddImageToSection(section)) return; // block paste
                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir, $"note_img_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}_{Path.GetFileName(f)}");
                                await Task.Run(() => File.Copy(f, destFile, overwrite: true));
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140
                                };
                                section.Images.Add(freeformImg);
                                NoteManager.MarkDirty();
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"HandleImagePasteForFreeform error: {ex.Message}");
            }
        }

        /// <summary>
        /// Intercept paste in freeform TextBox — if clipboard has an image, save it and add
        /// to the day's FreeformImages list instead of pasting text.
        /// </summary>
        private async void NotesFreeformBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (_selectedNoteDay == null) return;
            var dataObject = e.DataObject;
            if (dataObject == null) return;

            // Find the FreeformSection that owns this TextBox
            FreeformSection? section = null;
            if (sender is TextBox tb)
            {
                DependencyObject? walk = VisualTreeHelper.GetParent(tb);
                while (walk != null)
                {
                    if (walk is FrameworkElement fe && fe.DataContext is FreeformSection fs)
                    {
                        section = fs;
                        break;
                    }
                    walk = VisualTreeHelper.GetParent(walk);
                }
            }
            if (section == null) section = GetActiveFreeformSection();
            if (section == null) return;

            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                e.CancelCommand();

                if (!CanAddImageToSection(section)) return;

                var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                if (img != null)
                {
                    string path = await NoteManager.SaveImage(img);
                    var freeformImg = new FreeformImage
                    {
                        ImagePath = path,
                        DisplayWidth = Math.Min(img.PixelWidth, 140)
                    };
                    section.Images.Add(freeformImg);
                    NoteManager.MarkDirty();
                }
            }
            else if (dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    foreach (string? f in files)
                    {
                        if (f != null && IsImageFile(f))
                        {
                            e.CancelCommand();
                            if (!CanAddImageToSection(section)) break;
                            string destDir = NoteManager.GetImagesDirectory();
                            string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                            try
                            {
                                await Task.Run(() => File.Copy(f, destFile, overwrite: true));
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140
                                };
                                section.Images.Add(freeformImg);
                                NoteManager.MarkDirty();
                            }
                            catch { } // Best-effort: failure is acceptable
                            break;
                        }
                    }
                }
            }
            });
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM IMAGE CLICK / RESIZE / REMOVE
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Click on a freeform image → open in default system viewer.
        /// </summary>
        private void FreeformImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FreeformImage fi && fi.HasImage)
            {
                var virtualItem = new FlyShelf.ViewModels.ClipboardItem
                {
                    FilePath = fi.ImagePath,
                    ItemType = FlyShelf.ViewModels.ClipboardItemType.Image
                };
                GetMainWindow()?.ShowQuickLookForItem(virtualItem);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Mouse wheel on freeform image → resize.
        /// </summary>
        private void FreeformImageResize_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FreeformImage fi)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                fi.DisplayWidth = Math.Clamp(fi.DisplayWidth + delta, 60, 600);
                NoteManager.MarkDirty();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Remove a freeform image.
        /// </summary>
        private void FreeformImageRemove_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            if (sender is FrameworkElement fe && fe.DataContext is FreeformImage fi)
            {
                // Walk up the visual tree to find the parent FreeformSection
                FreeformSection? section = null;
                DependencyObject? walk = VisualTreeHelper.GetParent(fe);
                while (walk != null)
                {
                    if (walk is FrameworkElement parent && parent.DataContext is FreeformSection fs)
                    {
                        section = fs;
                        break;
                    }
                    walk = VisualTreeHelper.GetParent(walk);
                }

                if (fi.HasImage) { _ = Task.Run(() => { try { File.Delete(fi.ImagePath); } catch { } }); /* Best-effort off UI thread */ }

                if (section != null)
                    section.Images.Remove(fi);
                else
                    _selectedNoteDay.FreeformImages.Remove(fi); // Fallback for legacy day-level images

                NoteManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM REMINDER & EXPAND
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Freeform notes reminder button — parses the selected text (or full freeform content)
        /// using NLP to extract a clean title and auto-calculated due date.
        /// </summary>
        private void NotesFreeformReminder_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;

            // Prefer selected text if the user highlighted a specific line/phrase; otherwise use entire content
            string noteText = "";
            var activeFreeformTb = GetActiveFreeformTextBox();
            if (activeFreeformTb != null && !string.IsNullOrWhiteSpace(activeFreeformTb.SelectedText))
            {
                noteText = activeFreeformTb.SelectedText.Trim();
            }
            else if (activeFreeformTb != null && !string.IsNullOrWhiteSpace(activeFreeformTb.Text))
            {
                // Use the full freeform text, capped at a reasonable length for parsing
                noteText = activeFreeformTb.Text.Trim();
                if (noteText.Length > 200) noteText = noteText[..200];
            }

            if (string.IsNullOrWhiteSpace(noteText))
            {
                // Nothing to parse — open with defaults
                var defaultDue = DateTime.Today.AddDays(1).AddHours(9);
                try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
                var reminderWindow = new FlyShelf.Windows.ReminderCreateWindow("Note Reminder", defaultDue);
                WindowHelper.ShowInForeground(reminderWindow);
                _activeReminderCreateWindow = reminderWindow;
                return;
            }

            // Use the NLP parser to extract a clean title and calculated due date
            var (parsedTitle, calculatedDue) = Classes.NaturalLanguageReminderParser.Parse(noteText, DateTime.Now);

            // If the note belongs to a future date, use that date's 9 AM as minimum
            if (_selectedNoteDay.Date.Date > DateTime.Today && calculatedDue < _selectedNoteDay.Date.Date.AddHours(9))
            {
                calculatedDue = _selectedNoteDay.Date.Date.AddHours(9);
            }

            try { _activeReminderCreateWindow?.Close(); } catch { } // Best-effort: failure is acceptable
            var window = new FlyShelf.Windows.ReminderCreateWindow(parsedTitle, calculatedDue);
            WindowHelper.ShowInForeground(window);
            _activeReminderCreateWindow = window;
        }

        private void NotesFreeformExpand_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_selectedNoteDay == null) return;
            try
            {
                // Get the FreeformSection from the clicked button's DataContext
                if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
                {
                    string dayLabel = $"📝 {_selectedNoteDay.DisplayDate}";
                    var expandWindow = new FlyShelf.Windows.NoteExpandWindow(section, dayLabel);
                    WindowHelper.ShowInForeground(expandWindow);
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("NOTES", $"Failed to open expand window: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM AI IMPROVE & UNDO
        // ═══════════════════════════════════════════════════════════

        private void NotesFreeformImprove_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is FreeformSection section)
            {
                if (string.IsNullOrWhiteSpace(section.Content))
                {
                    Windows.ToastWindow.ShowToast("⚠️ Note is empty. Type something first!");
                    return;
                }

                bool hasCloudKey = AiProviderService.Instance.HasCloudApiKey;
                if (!LicenseManager.IsPro && !hasCloudKey)
                {
                    UpgradePrompt.ShowNotesAILimit(GetMainWindow());
                    return;
                }

                // Snapshot for undo before AI modifies the text
                _notesUndoText = section.Content;
                _notesUndoSection = section;

                var aiWindow = new FlyShelf.Windows.NotesAIDiffWindow(section.Content);
                aiWindow.Owner = GetMainWindow();
                if (aiWindow.ShowDialog() == true && aiWindow.IsApplied)
                {
                    section.Content = aiWindow.ImprovedText;
                    NoteManager.MarkDirty();

                    // Show the undo button now that AI has modified text
                    NotesUndoBtn.Visibility = Visibility.Visible;
                }
            }
        }

        private void NotesUndo_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_notesUndoSection != null && _notesUndoText != null)
            {
                _notesUndoSection.Content = _notesUndoText;
                NoteManager.MarkDirty();
                _notesUndoText = null;
                _notesUndoSection = null;
                NotesUndoBtn.Visibility = Visibility.Collapsed;
                Windows.ToastWindow.ShowToast("↩️ Undo applied");
            }
        }
    }
}
