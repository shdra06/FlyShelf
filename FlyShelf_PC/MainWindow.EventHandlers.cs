// ---------------------------------------------------------------
// MainWindow � Event Handlers
// Drag/Drop, Search, Item Actions (Pin/Delete/Open/QuickLook),
// Scroll, KeyDown, NotifyIcon, ContextMenu
// Split from MainWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using AdvanceClip.ViewModels;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdvanceClip
{
    public partial class MainWindow
    {
        internal static bool _isInternalDragSource = false;

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            IsDragHovering = false;
            _spawnToken++; 

            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _viewModel.HandleDrop(e.Data, true);
            e.Handled = true;

            // Immediately disappear on drop!
            AnimateAndHide();
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            IsDragHovering = false;
            _spawnToken++; 

            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _viewModel.HandleDrop(e.Data, true);
            e.Handled = true;

            // Immediately disappear on drop!
            AnimateAndHide();
        }

    

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            _isDragHovering = true;
            IsDragHovering = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || 
                e.Data.GetDataPresent("FileNameW") ||
                e.Data.GetDataPresent("FileName") ||
                e.Data.GetDataPresent("text/uri-list") ||
                e.Data.GetDataPresent("application/vnd.code.tree.workspaceFiles") ||
                e.Data.GetDataPresent(DataFormats.Bitmap) || 
                e.Data.GetDataPresent(DataFormats.Dib) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) || 
                e.Data.GetDataPresent(DataFormats.StringFormat) ||
                e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Performance Fix: Do NOT query 'e.Data.GetDataPresent' across cross-process COM COM-wrappers 
            // inside 'DragOver' because this fires hundreds of times a second and completely hangs the UI thread!
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            IsDragHovering = false;
            // The user explicitly requested an impenetrable UI overlay without funky Hide bugs on child-element hovers.
            // Leaving the physical window drag-space now does NOT force kill the app interface!
        }


        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 100 && e.NewSize.Height > 100)
            {
                // Only persist size changes for the CURRENT mode — prevents mode 1
                // content-driven height from corrupting mode 0 stored dimensions
                if (_viewModel.CurrentMode == 0)
                {
                    Classes.SettingsManager.Current.MiniFormWidth = (int)e.NewSize.Width;
                    Classes.SettingsManager.Current.MiniFormHeight = (int)e.NewSize.Height;
                }
                else if (_viewModel.CurrentMode == 1)
                {
                    Classes.SettingsManager.Current.MediumFormWidth = (int)e.NewSize.Width;
                    Classes.SettingsManager.Current.MediumFormHeight = (int)e.NewSize.Height;
                }
                // Mode 2 (Full) is always screen-relative, no persistence needed
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source)
            {
                var parentBtn = FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source);
                if (parentBtn != null) return; // Ignore drag if the user explicitly clicked a child button!
            }

            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2)
                {
                    return; // Never maximize the FlyShelf
                }

                _isEdgeLocked = false;
                try
                {
                    this.DragMove();
                }
                catch { } 
            }
        }

        private void ToggleGlobalSync_Click(object sender, RoutedEventArgs e)
        {
            bool newState = !AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync;
            AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync = newState;
            // Toggle ALL sync: Cloudflare + LAN
            // When OFF, no data enters or leaves the device
            AdvanceClip.Classes.SettingsManager.Current.EnableGlobalCloudflare = newState;
            AdvanceClip.Classes.SettingsManager.Current.EnableLocalLAN = newState;
            AdvanceClip.Classes.SettingsManager.Save();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearShelf();
        }

        private void EmojiPicker_Click(object sender, RoutedEventArgs e)
        {
            var picker = new AdvanceClip.Windows.EmojiPickerWindow();
            picker.Left = this.Left + (this.Width - picker.Width) / 2;
            picker.Top = this.Top - picker.Height - 8;
            if (picker.Top < 0) picker.Top = this.Top + this.Height + 8;
            picker.Show();
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            _isDragHovering = false;
            IsDragHovering = false;
        }

        // ═══ SEARCH FEATURE ═══
        private bool _isSearchActive = false;
        private System.ComponentModel.ICollectionView _collectionView;

        private void SearchToggle_Click(object sender, RoutedEventArgs e)
        {
            _isSearchActive = !_isSearchActive;
            if (_isSearchActive)
            {
                // Activate the window so it receives keyboard input (normally it's a non-activating overlay)
                this.Activate();
                SearchBarContainer.Visibility = Visibility.Visible;
                SearchToggleBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0xB8, 0xA6));
                
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
            ApplySearchFilter(query);
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
                var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
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
            SearchTextBox.Text = "";
            SearchBarContainer.Visibility = Visibility.Collapsed;
            SearchToggleBtn.Foreground = (System.Windows.Media.Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");
            // Remove filter and custom sort
            if (_collectionView != null)
            {
                _collectionView.Filter = null;
                if (_collectionView is System.Windows.Data.ListCollectionView listCol)
                {
                    listCol.CustomSort = null;
                }
            }
            // Move focus back to the list view
            ShelfListView.Focus();
        }

        private void ApplySearchFilter(string query)
        {
            if (_collectionView == null)
            {
                _collectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(ShelfListView.ItemsSource);
            }
            if (_collectionView == null) return;

            string queryClean = (query ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(queryClean))
            {
                _collectionView.Filter = null;
                if (_collectionView is System.Windows.Data.ListCollectionView listColReset)
                {
                    listColReset.CustomSort = null;
                }
                return;
            }

            var comparer = new ClipboardItemComparer(queryClean);
            _collectionView.Filter = obj =>
            {
                if (obj is not ClipboardItem item) return false;
                return comparer.CalculateScore(item) > 0;
            };

            if (_collectionView is System.Windows.Data.ListCollectionView listCol)
            {
                listCol.CustomSort = comparer;
            }
        }

        private class ClipboardItemComparer : System.Collections.IComparer
        {
            private readonly string _query;
            private readonly string[] _terms;

            public ClipboardItemComparer(string query)
            {
                _query = query.ToLowerInvariant().Trim();
                _terms = _query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }

            public int Compare(object? x, object? y)
            {
                if (x is not ClipboardItem itemX || y is not ClipboardItem itemY) return 0;

                double scoreX = CalculateScore(itemX);
                double scoreY = CalculateScore(itemY);

                // Sort descending by score (highest relevance first)
                int comp = scoreY.CompareTo(scoreX);
                if (comp != 0) return comp;

                // Fallback to original creation order or ID to keep stable sort
                return 0;
            }

            public double CalculateScore(ClipboardItem item)
            {
                if (string.IsNullOrWhiteSpace(_query)) return 0;

                double score = 0;

                string fileName = (item.FileName ?? "").ToLowerInvariant();
                string rawText = (item.RawContent ?? "").ToLowerInvariant();
                string ext = (item.Extension ?? "").ToLowerInvariant();
                string device = (item.SourceDeviceName ?? "").ToLowerInvariant();

                // Special Keyword Type matching: e.g. type:image, :code, is:pinned
                foreach (var term in _terms)
                {
                    if (term == "is:pinned" || term == ":pinned" || term == "pinned:true")
                    {
                        if (item.IsPinned) score += 1000;
                    }
                    else if (term == "is:image" || term == "type:image" || term == ":image" || term == ":img")
                    {
                        if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Image) score += 1000;
                    }
                    else if (term == "is:pdf" || term == "type:pdf" || term == ":pdf")
                    {
                        if (ext == ".pdf") score += 1000;
                    }
                    else if (term == "is:code" || term == "type:code" || term == ":code")
                    {
                        if (item.IsCodePreview) score += 1000;
                    }
                    else if (term == "is:text" || term == "type:text" || term == ":text")
                    {
                        if (item.ItemType != AdvanceClip.ViewModels.ClipboardItemType.File) score += 500;
                    }
                    else if (term == "is:file" || term == "type:file" || term == ":file")
                    {
                        if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.File) score += 1000;
                    }
                }

                // Exact full-query matches (highest priority)
                if (fileName == _query) score += 2000;
                else if (fileName.StartsWith(_query)) score += 1200;
                else if (fileName.Contains(_query)) score += 800;

                if (rawText == _query) score += 1500;
                else if (rawText.StartsWith(_query)) score += 900;
                else if (rawText.Contains(_query)) score += 600;

                // Word term matching
                foreach (var term in _terms)
                {
                    // Skip type filter terms in text matching
                    if (term.Contains(':')) continue;

                    // Match filename
                    if (fileName.Contains(term))
                    {
                        score += 300;
                        if (fileName.StartsWith(term)) score += 150;
                    }
                    
                    // Match content
                    if (rawText.Contains(term))
                    {
                        score += 150;
                        if (rawText.StartsWith(term)) score += 50;
                    }

                    // Match extension
                    if (ext.Contains(term)) score += 100;

                    // Match device
                    if (device.Contains(term)) score += 80;

                    // Fuzzy / Subsequence matches
                    double subScore;
                    if (IsSubsequenceMatch(fileName, term, out subScore))
                    {
                        score += 50 * subScore;
                    }
                    else if (IsSubsequenceMatch(rawText, term, out subScore))
                    {
                        score += 25 * subScore;
                    }
                }

                return score;
            }

            private bool IsSubsequenceMatch(string text, string pattern, out double score)
            {
                score = 0;
                if (string.IsNullOrEmpty(pattern)) return true;
                if (string.IsNullOrEmpty(text)) return false;

                int textLen = text.Length;
                int patternLen = pattern.Length;
                
                int textIdx = 0;
                int patternIdx = 0;
                
                int firstMatchIdx = -1;
                int lastMatchIdx = -1;

                while (textIdx < textLen && patternIdx < patternLen)
                {
                    if (char.ToLowerInvariant(text[textIdx]) == char.ToLowerInvariant(pattern[patternIdx]))
                    {
                        if (firstMatchIdx == -1) firstMatchIdx = textIdx;
                        lastMatchIdx = textIdx;
                        patternIdx++;
                    }
                    textIdx++;
                }

                if (patternIdx == patternLen)
                {
                    int gap = (lastMatchIdx - firstMatchIdx) + 1;
                    score = (double)patternLen / gap;
                    return true;
                }

                return false;
            }
        }

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                _viewModel.TogglePin(item);
                e.Handled = true;
            }
        }

        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                // Set flag to suppress the subsequent MouseUp paste-and-close
                _didDragOut = true;
                
                // Defer removal to prevent structural DOM shifts from triggering MouseUp events on unrelated ListBox items underneath
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
                {
                    _viewModel.RemoveItem(item);
                }, System.Windows.Threading.DispatcherPriority.Background);
                
                e.Handled = true;
            }
        }

        private void OpenSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                if (!string.IsNullOrEmpty(item.FilePath) && System.IO.File.Exists(item.FilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                }
                else if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Url && !string.IsNullOrEmpty(item.RawContent))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.RawContent) { UseShellExecute = true });
                }
                e.Handled = true;
            }
        }

        private AdvanceClip.Windows.QuickLookWindow _activeQuickLook;

        private void QuickLookSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                // Close existing Quick Look window first
                try { _activeQuickLook?.Close(); } catch { }
                _activeQuickLook = null;

                var qLook = new AdvanceClip.Windows.QuickLookWindow(item);
                qLook.Closed += (s, args) => { if (_activeQuickLook == s) _activeQuickLook = null; };
                _activeQuickLook = qLook;
                qLook.Show();
                e.Handled = true;
            }
        }

        private async void RotateImageSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

                try
                {
                    string filePath = item.FilePath;

                    // Find the Image element in the visual tree for animation
                    var listViewItem = ShelfListView.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                    Image targetImage = null;
                    if (listViewItem != null)
                    {
                        targetImage = FindVisualChild<Image>(listViewItem, "ItemIcon");
                    }

                    // Animate the image rotating 90° with smooth easing
                    if (targetImage != null)
                    {
                        var rotateTransform = new System.Windows.Media.RotateTransform(0, targetImage.ActualWidth / 2, targetImage.ActualHeight / 2);
                        targetImage.RenderTransform = rotateTransform;
                        var rotateAnim = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = 0,
                            To = 90,
                            Duration = TimeSpan.FromMilliseconds(300),
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                        };
                        rotateTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rotateAnim);
                    }

                    // Rotate the file on a background thread to keep UI responsive
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        byte[] fileBytes = System.IO.File.ReadAllBytes(filePath);
                        System.Windows.Media.Imaging.BitmapImage original = null;
                        Dispatcher.Invoke(() =>
                        {
                            original = new System.Windows.Media.Imaging.BitmapImage();
                            using (var ms = new System.IO.MemoryStream(fileBytes))
                            {
                                original.BeginInit();
                                original.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                original.StreamSource = ms;
                                original.EndInit();
                                original.Freeze();
                            }
                        });

                        var rotated = new System.Windows.Media.Imaging.TransformedBitmap(original, new System.Windows.Media.RotateTransform(90));
                        rotated.Freeze();

                        string ext = System.IO.Path.GetExtension(filePath).ToLower();
                        System.Windows.Media.Imaging.BitmapEncoder encoder;
                        if (ext == ".png") encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        else if (ext == ".bmp") encoder = new System.Windows.Media.Imaging.BmpBitmapEncoder();
                        else encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };

                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rotated));

                        using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            encoder.Save(fs);
                        }
                    });

                    // Wait for animation to finish
                    await System.Threading.Tasks.Task.Delay(320);

                    // Reload the icon from the freshly rotated file
                    byte[] freshBytes = System.IO.File.ReadAllBytes(filePath);
                    var freshBitmap = new System.Windows.Media.Imaging.BitmapImage();
                    using (var ms = new System.IO.MemoryStream(freshBytes))
                    {
                        freshBitmap.BeginInit();
                        freshBitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        freshBitmap.StreamSource = ms;
                        freshBitmap.EndInit();
                        freshBitmap.Freeze();
                    }

                    // Reset the rotation transform on the image
                    if (targetImage != null)
                    {
                        targetImage.RenderTransform = null;
                    }

                    // Update the item's icon with the rotated image
                    item.Icon = freshBitmap;

                    // Move to top without triggering clipboard copy or sync
                    _viewModel.MoveItemToTop(item);

                    AdvanceClip.Classes.Logger.LogAction("ROTATE", "Rotated 90u00B0 in-place: " + System.IO.Path.GetFileName(filePath));
                }
                catch (Exception ex)
                {
                    AdvanceClip.Classes.Logger.LogAction("ROTATE", "Failed: " + ex.Message);
                }
            }
        }

                private void RunTerminalSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Code)
                {
                    item.RunInTerminal();
                }
                e.Handled = true;
            }
        }

        private void SmartActionSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (item.SmartActionType == "CompileAndRun")
                {
                    item.CompileAndRunNative();
                }
                else if (item.SmartActionType == "OpenPDF" || item.SmartActionType == "JoinMeeting" || item.SmartActionType == "OpenBrowser")
                {
                    string target = item.SmartActionType == "OpenPDF" ? item.FilePath : item.RawContent;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
                }
                else if (item.SmartActionType == "OpenMap")
                {
                    string target = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(item.RawContent);
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
                }
                else if (item.SmartActionType == "ConvertToPdf")
                {
                    System.Threading.Tasks.Task.Run(() => 
                    {
                        try 
                        {
                            string targetPdf = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.FilePath) ?? System.IO.Path.GetTempPath(), System.IO.Path.GetFileNameWithoutExtension(item.FilePath) + "_Converted.pdf");
                            string script = $"$word = New-Object -ComObject Word.Application; $doc = $word.Documents.Open('{item.FilePath}'); $doc.SaveAs([ref]'{targetPdf}', [ref]17); $doc.Close(); $word.Quit();";
                            var p = new System.Diagnostics.ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"", CreateNoWindow = true, UseShellExecute = false };
                            System.Diagnostics.Process.Start(p)?.WaitForExit();
                            
                            if (System.IO.File.Exists(targetPdf))
                            {
                                Dispatcher.InvokeAsync(() => {
                                    var dropList = new System.Collections.Specialized.StringCollection(); dropList.Add(targetPdf);
                                    System.Windows.Clipboard.SetFileDropList(dropList);
                                });
                            }
                        } catch { } // Sandbox fail softly if Microsoft Word isn't installed
                    });
                }
                else if (item.SmartActionType == "SetTimer")
                {
                    var tw = new AdvanceClip.Windows.TimerWindow(item.RawContent);
                    tw.Show();
                }
                else if (item.SmartActionType == "CopyQRText")
                {
                    try { System.Windows.Clipboard.SetText(item.RawContent); AdvanceClip.Windows.ToastWindow.ShowToast("QR Text Copied!"); } catch { }
                }

            }
        }
        
        private void GoogleSearchSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            GoogleSearch_Click(sender, new RoutedEventArgs());
        }
        
        private void ShelfListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;

            var scrollViewer = FindVisualChild<ScrollViewer>(ShelfListView);
            if (scrollViewer == null) return;

            double scrollAmount = -e.Delta / 120.0 * 48.0;
            double targetOffset = scrollViewer.VerticalOffset + scrollAmount;
            targetOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableHeight));
            scrollViewer.ScrollToVerticalOffset(targetOffset);
        }


        private void ShelfListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ShelfListView.SelectedItems.Count > 0)
            {
                var itemsToRemove = ShelfListView.SelectedItems.Cast<ClipboardItem>().ToList();
                foreach (var item in itemsToRemove)
                {
                    _viewModel.RemoveItem(item);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ShelfListView.SelectedItem is ClipboardItem selected)
            {
                _ = CopyItemAndPaste(selected, hideWindow: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_isSearchActive)
                {
                    CloseSearch();
                }
                else
                {
                    this.Hide();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    IDataObject data = Clipboard.GetDataObject();
                    if (data != null)
                    {
                        _viewModel.HandleDrop(data, true);
                        AnimateAndHide();
                    }
                }
                catch { }
                e.Handled = true;
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // Ctrl+F opens search
                if (!_isSearchActive)
                {
                    SearchToggle_Click(sender, e);
                }
                else
                {
                    SearchTextBox.Focus();
                    SearchTextBox.SelectAll();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                int currentIdx = ShelfListView.SelectedIndex;
                int count = _viewModel.DroppedItems.Count;
                if (count == 0) { e.Handled = true; return; }

                int newIdx;
                if (currentIdx < 0)
                {
                    newIdx = 0; // Nothing selected — start at first item
                }
                else
                {
                    newIdx = e.Key == Key.Down
                        ? Math.Min(currentIdx + 1, count - 1)
                        : Math.Max(currentIdx - 1, 0);
                }

                ShelfListView.SelectedIndex = newIdx;
                // ScrollIntoView MUST come first — it forces the virtualizer to create the container
                ShelfListView.ScrollIntoView(ShelfListView.Items[newIdx]);
                // Dispatch focus to next frame so the container is fully realized
                Dispatcher.InvokeAsync(() =>
                {
                    var container = ShelfListView.ItemContainerGenerator.ContainerFromIndex(newIdx) as ListViewItem;
                    container?.Focus();
                }, System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
            }
        }



        private void NotifyIconQuit_Click(object sender, RoutedEventArgs e)
        {
            _hubWindowInstance?.ForceShutdownRelease();
            Application.Current.Shutdown();
        }

        private void nIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e)
        {
            if (this.IsVisible && _viewModel.IsFullMode)
            {
                this.Hide();
            }
            else
            {
                OpenApp_Click(sender, e);
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            T? parent = parentObject as T;
            if (parent != null) return parent;
            else return FindVisualParent<T>(parentObject);
        }

        /// <summary>Walks up the visual tree checking if any ancestor FrameworkElement has the given Tag.</summary>
        private static bool HasAncestorTag(DependencyObject child, string tag)
        {
            DependencyObject current = child;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == tag)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }


        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T tChild) return tChild;
                else
                {
                    T? childOfChild = FindVisualChild<T>(child);
                    if (childOfChild != null) return childOfChild;
                }
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild && tChild.Name == name) return tChild;
                T? deeper = FindVisualChild<T>(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

    }
}


