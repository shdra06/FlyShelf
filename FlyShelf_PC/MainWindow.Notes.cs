// ---------------------------------------------------------------
// MainWindow — Quick Notes Panel
// Toggle, navigation, bullet CRUD, freeform mode, search, images.
// Split from MainWindow.Search.cs for modularity.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        private System.Windows.Threading.DispatcherTimer? _panelAutoRevertTimer;
        private bool _isNotesLoaded = false;
        private NoteDay? _selectedNoteDay = null;
        private Brush? _originalHeaderBg = null;
        private static readonly SolidColorBrush _notesHeaderBrush = new(Color.FromRgb(0x1A, 0x1A, 0x2E));
        private TextBox? _lastFocusedBulletTextBox = null;
        private DateTime _lastBulletAddedTime = DateTime.MinValue;

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
            if (_isTodoActive) CloseTodoPanel(immediate: true);
            if (_isSearchActive) CloseSearch(switchingPanel: true);
            if (_isFilterBarActive) ToggleFilterBar(false);
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
            StartPanelAutoRevertTimer();

            // Update taskbar/alt-tab title
            Title = "Notes";

            // Update window activation style dynamically so clicking it works
            UpdateWindowActivationStyle();

            // ─── FOCUS FIX: Force-activate and topmost-cycle to grab OS focus ───
            ActivateNotesWindow();

            // Hide clipboard, show notes
            ShelfListView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            NotesPanel.Visibility = Visibility.Visible;

            // ─── HEADER: Match the opaque notes dark theme ───
            if (_originalHeaderBg == null)
                _originalHeaderBg = HeaderAndFiltersStack.Background;
            HeaderAndFiltersStack.Background = _notesHeaderBrush;
            // Also apply ClearType hints to the header while notes are active
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(HeaderAndFiltersStack, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(HeaderAndFiltersStack, ClearTypeHint.Enabled);

            // Swap notes button to clipboard icon (acts as "go back" button)
            NotesToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Clipboard24 };
            NotesToggleBtn.ToolTip = "Back to Clipboard";

            // Animate in
            var slideAnim = Classes.AnimationHelper.SlideIn(fromY: -12, durationMs: 200);
            var fadeAnim = Classes.AnimationHelper.FadeIn(durationMs: 200);
            if (NotesPanel.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);

            SelectNoteDay(today);
        }

        /// <summary>
        /// Force the MainWindow to become the active foreground window.
        /// This is critical because FlyShelf uses ShowActivated="False" and is normally
        /// a non-activating overlay. Without this, typing may go to the previously focused app.
        /// </summary>
        private void ActivateNotesWindow()
        {
            // Step 1: Activate the WPF window (requests OS focus)
            this.Activate();

            // Step 2: Temporarily toggle Topmost to force Win32 SetForegroundWindow
            if (!this.Topmost)
            {
                this.Topmost = true;
                this.Topmost = false;
            }

            // Step 3: Set keyboard focus to the notes panel itself
            this.Focus();
        }

        /// <summary>
        /// Activates the WPF window and brings it to the foreground without stealing keyboard
        /// focus from child text elements that the user is trying to click on.
        /// </summary>
        private void ActivateWindowWithoutStealingFocus()
        {
            if (!this.IsActive)
            {
                this.Activate();
                if (!this.Topmost)
                {
                    this.Topmost = true;
                    this.Topmost = false;
                }
            }
        }

        /// <summary>
        /// Updates the WS_EX_NOACTIVATE style dynamically based on the notes panel state.
        /// When in notes mode, we remove WS_EX_NOACTIVATE so clicking the window activates it.
        /// When not in notes mode, we add it back so it stays a non-activating overlay.
        /// </summary>
        private void UpdateWindowActivationStyle()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                if (_isNotesActive || _isTodoActive)
                {
                    // Remove WS_EX_NOACTIVATE and WS_EX_TOOLWINDOW so the window can be activated
                    // Add WS_EX_APPWINDOW so it appears in the taskbar and alt+tab with its proper app icon
                    exStyle = exStyle & ~WS_EX_NOACTIVATE & ~WS_EX_TOOLWINDOW;
                    exStyle = exStyle | WS_EX_APPWINDOW;
                    SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
                }
                else
                {
                    // Remove WS_EX_APPWINDOW and add WS_EX_NOACTIVATE back for clipboard overlay mode
                    exStyle = exStyle & ~WS_EX_APPWINDOW;
                    exStyle = exStyle | WS_EX_NOACTIVATE;
                    SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
                }

                // Force frame to update style changes immediately
                Classes.NativeMethods.SetWindowPos(
                    helper.Handle,
                    0, 0, 0, 0, 0,
                    Classes.NativeMethods.SWP_NOMOVE |
                    Classes.NativeMethods.SWP_NOSIZE |
                    Classes.NativeMethods.SWP_NOZORDER |
                    Classes.NativeMethods.SWP_NOACTIVATE |
                    0x0020 // SWP_FRAMECHANGED
                );

                // CRITICAL: Re-pin to all virtual desktops SYNCHRONOUSLY after WS_EX_APPWINDOW
                // style changes. Toggling WS_EX_APPWINDOW causes Windows Shell to IMMEDIATELY
                // unpin the window. Re-pinning must be synchronous — deferring to Background
                // creates a race condition where the window gets stuck on the old desktop.
                EnsureVirtualDesktopPinned();
            }
        }

        /// <summary>
        /// Restores keyboard focus to the active text field inside the notes panel.
        /// </summary>
        private void FocusNotesActiveTextBox()
        {
            if (_selectedNoteDay == null) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (_selectedNoteDay.IsFreeformMode)
                {
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
                }
                else
                {
                    // Focus last focused bullet TextBox if it's still valid
                    if (_lastFocusedBulletTextBox != null && _lastFocusedBulletTextBox.IsLoaded && _lastFocusedBulletTextBox.IsVisible)
                    {
                        _lastFocusedBulletTextBox.Focus();
                        Keyboard.Focus(_lastFocusedBulletTextBox);
                    }
                    else if (_selectedNoteDay.Bullets.Count > 0)
                    {
                        // Fallback: focus first bullet's TextBox
                        var firstBullet = _selectedNoteDay.Bullets.First();
                        NotesBulletList.UpdateLayout(); // Force container generation!
                        var container = NotesBulletList.ItemContainerGenerator.ContainerFromItem(firstBullet);
                        if (container is ContentPresenter cp)
                        {
                            var tb = FindVisualChild<TextBox>(cp, "NoteBulletContentBox");
                            if (tb != null)
                            {
                                tb.Focus();
                                Keyboard.Focus(tb);
                            }
                        }
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CloseNotesPanel(bool immediate = false)
        {
            _isNotesActive = false;

            // Restore taskbar/alt-tab title
            Title = "FlyShelf";

            // Restore non-activating window style
            UpdateWindowActivationStyle();

            // Clear last focused bullet textbox reference
            _lastFocusedBulletTextBox = null;


            // Restore notes button icon and tooltip
            NotesToggleBtn.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.List24 };
            NotesToggleBtn.ToolTip = "Quick Notes";
            NotesToggleBtn.ClearValue(ForegroundProperty);

            // ─── HEADER: Restore original transparent/Mica background ───
            HeaderAndFiltersStack.Background = _originalHeaderBg ?? Brushes.Transparent;
            TextOptions.SetTextFormattingMode(HeaderAndFiltersStack, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(HeaderAndFiltersStack, TextRenderingMode.Auto);
            RenderOptions.SetClearTypeHint(HeaderAndFiltersStack, ClearTypeHint.Auto);

            if (immediate)
            {
                // Instant close — no animation (used when switching to another panel)
                NotesPanel.BeginAnimation(OpacityProperty, null);
                NotesPanel.Opacity = 0;
                NotesPanel.Visibility = Visibility.Collapsed;
                ShelfListView.Visibility = Visibility.Visible;
                // Let the XAML DataTrigger on DroppedItems.Count control visibility
                EmptyStatePanel.ClearValue(VisibilityProperty);

                // PERF: Defer save to Background priority so it doesn't block the summon pipeline.
                Dispatcher.InvokeAsync(() => NoteManager.SaveNow(),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            // Normal close path: save synchronously (no spawn pipeline follows)
            NoteManager.SaveNow();

            // Animate out
            var fadeAnim = Classes.AnimationHelper.FadeOut();
            fadeAnim.Completed += (s, a) =>
            {
                if (!_isNotesActive)
                {
                    NotesPanel.Visibility = Visibility.Collapsed;
                    ShelfListView.Visibility = Visibility.Visible;
                    // Restore empty state if needed
                    // Let the XAML DataTrigger on DroppedItems.Count control visibility
                    EmptyStatePanel.ClearValue(VisibilityProperty);
                }
            };
            NotesPanel.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private void NotesBack_Click(object sender, MouseButtonEventArgs e)
        {
            CloseNotesPanel();
        }

        // ═══════════════════════════════════════════════════════════
        // FOCUS CAPTURE: Clicking anywhere in notes panel activates window
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// PreviewMouseDown on the entire NotesPanel grid.
        /// Ensures the window captures OS focus when user clicks ANYWHERE inside notes.
        /// Without this, keyboard input may still go to the previously focused app.
        /// </summary>
        private void NotesPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            ActivateWindowWithoutStealingFocus();
        }

        // ═══════════════════════════════════════════════════════════
        // DAY SELECTION (SIDEBAR)
        // ═══════════════════════════════════════════════════════════

        private void SelectNoteDay(NoteDay day)
        {
            _selectedNoteDay = day;

            // Clear search if active
            if (_isSearchActive)
            {
                CloseSearch();
            }

            // Update sidebar selection highlight
            UpdateDaySidebarSelection();

            // Bind content
            NotesBulletList.ItemsSource = day.Bullets;
            NotesFreeformBox.Text = day.FreeformContent ?? "";

            // Bind freeform images
            NotesFreeformImageList.ItemsSource = day.FreeformImages;

            // Show correct mode
            if (day.IsFreeformMode)
            {
                NotesBulletList.Visibility = Visibility.Collapsed;
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";
                // Defer focus to freeform text box
                Dispatcher.InvokeAsync(() =>
                {
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformArea.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";

                // Auto-create a first bullet if the day is empty so user can start typing immediately
                if (day.Bullets.Count == 0)
                {
                    _lastBulletAddedTime = DateTime.MinValue; // Reset cooldown
                    AddNewBulletAndFocus();
                }
                else
                {
                    // Auto-focus the last bullet's content text box
                    FocusNotesActiveTextBox();
                }
            }

            // Update day label
            NotesCurrentDayLabel.Text = "Notes · " + day.DisplayDate;
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

            // Spam proof check: enforce 1 second cooldown
            if ((DateTime.Now - _lastBulletAddedTime).TotalMilliseconds < 1000)
            {
                return;
            }
            _lastBulletAddedTime = DateTime.Now;

            var bullet = NoteManager.AddBullet(_selectedNoteDay);

            // Focus the new bullet's TextBox after render
            Dispatcher.InvokeAsync(() =>
            {
                NotesBulletList.UpdateLayout(); // Force container generation!
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
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NotesAddBullet_Click(object sender, MouseButtonEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            AddNewBulletAndFocus();
        }

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
            if (sender is TextBox tb && tb.IsFocused && tb.DataContext is NoteBullet bullet)
            {
                bullet.LastEdited = DateTime.Now;
            }
            NoteManager.MarkDirty();
        }

        private void NoteBulletText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is NoteBullet bullet)
            {
                // Intercept Ctrl+V to handle image/file paste manually
                if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (HandleImagePasteForBullet(bullet))
                    {
                        e.Handled = true;
                        return;
                    }
                }

                // Shift+Enter → insert newline (AcceptsReturn handles this when true)
                // Enter without Shift → add new bullet below
                if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    AddNewBulletAndFocus();
                }
            }
        }

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
                if (!LicenseManager.IsPro)
                {
                    System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                        Windows.ToastWindow.ShowToast("⚠️ Embedding 2 images per bullet is a Pro feature."));
                    return false;
                }

                bullet.ImagePath2 = path;
                bullet.ImageDisplayWidth2 = width;
                NoteManager.MarkDirty();
                return true;
            }
            return false;
        }

        private bool HandleImagePasteForBullet(NoteBullet bullet)
        {
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return false;

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

                    if (img != null)
                    {
                        string path = NoteManager.SaveImage(img);
                        double width = Math.Min(img.PixelWidth, 140);
                        return AssignImageToBullet(bullet, path, width);
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
                                string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                                File.Copy(f, destFile, overwrite: true);
                                return AssignImageToBullet(bullet, destFile, 140);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"HandleImagePasteForBullet error: {ex.Message}");
            }
            return false;
        }

        private void NotesFreeformBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Intercept Ctrl+V to handle image/file paste manually
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (HandleImagePasteForFreeform())
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private bool HandleImagePasteForFreeform()
        {
            if (_selectedNoteDay == null) return false;
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return false;

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

                    if (img != null)
                    {
                        string path = NoteManager.SaveImage(img);
                        var freeformImg = new FreeformImage
                        {
                            ImagePath = path,
                            DisplayWidth = Math.Min(img.PixelWidth, 140)
                        };
                        _selectedNoteDay.FreeformImages.Add(freeformImg);
                        NoteManager.MarkDirty();
                        return true;
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
                                string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                                File.Copy(f, destFile, overwrite: true);
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140
                                };
                                _selectedNoteDay.FreeformImages.Add(freeformImg);
                                NoteManager.MarkDirty();
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES", $"HandleImagePasteForFreeform error: {ex.Message}");
            }
            return false;
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
            ActivateWindowWithoutStealingFocus();
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
                var dataObject = e.DataObject;
                if (dataObject == null) return;

                // Check for image data on clipboard
                if (dataObject.GetDataPresent(DataFormats.Bitmap))
                {
                    var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                    if (img != null)
                    {
                        string path = NoteManager.SaveImage(img);
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
                                    File.Copy(f, destFile, overwrite: true);
                                    if (AssignImageToBullet(bullet, destFile, 140))
                                    {
                                        e.CancelCommand(); // Cancel text paste
                                    }
                                }
                                catch { }
                                break; // Only first image
                            }
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

        private void NoteImageResize2_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                double delta = e.Delta > 0 ? 20 : -20;
                double newWidth = Math.Clamp(bullet.ImageDisplayWidth2 + delta, 60, 600);
                bullet.ImageDisplayWidth2 = newWidth;
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

        private void NoteImageRemove2_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteBullet bullet)
            {
                if (bullet.HasImage2)
                {
                    try { File.Delete(bullet.ImagePath2); } catch { }
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
                ShowQuickLookForItem(virtualItem);
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
                ShowQuickLookForItem(virtualItem);
                e.Handled = true;
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
                NotesFreeformArea.Visibility = Visibility.Visible;
                NotesModeToggleText.Text = "● Bullets";

                // ─── FOCUS FIX: Activate window, then focus freeform box ───
                ActivateNotesWindow();
                Dispatcher.InvokeAsync(() =>
                {
                    NotesFreeformBox.Focus();
                    Keyboard.Focus(NotesFreeformBox);
                    NotesFreeformBox.CaretIndex = NotesFreeformBox.Text.Length;
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                NotesBulletList.Visibility = Visibility.Visible;
                NotesFreeformArea.Visibility = Visibility.Collapsed;
                NotesModeToggleText.Text = "📄 Freeform";

                // ─── FOCUS FIX: Activate window, then auto-create or focus bullet ───
                ActivateNotesWindow();
                if (_selectedNoteDay.Bullets.Count == 0)
                {
                    _lastBulletAddedTime = DateTime.MinValue; // Reset cooldown
                    AddNewBulletAndFocus();
                }
                else
                {
                    FocusNotesActiveTextBox();
                }
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

        /// <summary>
        /// When freeform TextBox gets focus, force-activate the window.
        /// </summary>
        private void NotesFreeformBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ActivateWindowWithoutStealingFocus();
        }

        // ═══════════════════════════════════════════════════════════
        // FREEFORM IMAGE PASTE
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Intercept paste in freeform TextBox — if clipboard has an image, save it and add
        /// to the day's FreeformImages list instead of pasting text.
        /// </summary>
        private void NotesFreeformBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (_selectedNoteDay == null) return;
            var dataObject = e.DataObject;
            if (dataObject == null) return;

            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                e.CancelCommand();

                var img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                if (img != null)
                {
                    string path = NoteManager.SaveImage(img);
                    var freeformImg = new FreeformImage
                    {
                        ImagePath = path,
                        DisplayWidth = Math.Min(img.PixelWidth, 140) // Nice and small default size
                    };
                    _selectedNoteDay.FreeformImages.Add(freeformImg);
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
                            string destDir = NoteManager.GetImagesDirectory();
                            string destFile = Path.Combine(destDir, $"note_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(f)}");
                            try
                            {
                                File.Copy(f, destFile, overwrite: true);
                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 140 // Nice and small default size
                                };
                                _selectedNoteDay.FreeformImages.Add(freeformImg);
                                NoteManager.MarkDirty();
                            }
                            catch { }
                            break;
                        }
                    }
                }
            }
        }

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
                ShowQuickLookForItem(virtualItem);
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
                if (fi.HasImage) { try { File.Delete(fi.ImagePath); } catch { } }
                _selectedNoteDay.FreeformImages.Remove(fi);
                NoteManager.MarkDirty();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // NOTES SEARCH
        // ═══════════════════════════════════════════════════════════

        private void ApplyNotesSearch(string query)
        {
            string queryClean = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(queryClean))
            {
                NotesSearchResults.Visibility = Visibility.Collapsed;
                NotesContentArea.Visibility = Visibility.Visible;
                return;
            }

            var results = NoteManager.Search(queryClean);

            // Build display items
            var displayItems = results.Select(r => new NoteSearchResult
            {
                DateLabel = r.Day.DisplayDate,
                Content = !string.IsNullOrEmpty(r.Bullet.Header) ? $"[{r.Bullet.Header}] {r.Bullet.Content}" : r.Bullet.Content,
                Day = r.Day,
                Bullet = r.Bullet
            }).ToList();

            NotesSearchResultsList.ItemsSource = displayItems;
            NotesSearchResults.Visibility = Visibility.Visible;
            NotesContentArea.Visibility = Visibility.Collapsed;
        }

        private void NotesSearchResult_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NoteSearchResult result)
            {
                CloseSearch();
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
