using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicaWPF.Controls;
using WinPdf = global::Windows.Data.Pdf;
using global::Windows.Storage;

namespace AdvanceClip.Windows
{
    public partial class PageReorderWindow : MicaWindow
    {
        private readonly PdfMergeItem _item;
        private const int COLUMNS = 5;
        private const double CELL_W = 120;
        private const double CELL_H = 155;
        private const double CELL_MARGIN = 5;

        // Current page order: each entry is the original 1-indexed page number
        private List<int> _pageOrder;
        // Currently selected indices in _pageOrder
        private HashSet<int> _selectedIndices = new();
        // Thumbnails keyed by original 1-indexed page number
        private Dictionary<int, BitmapImage> _thumbnails = new();

        // Drag state
        private int _dragStartIndex = -1;
        private bool _isDragging;
        private Point _dragStartPoint;
        private DragAdorner _dragAdorner;
        private Border _dragSourceTile;

        // Auto-scroll timer
        private DispatcherTimer _scrollTimer;
        private double _scrollSpeed;
        private const double SCROLL_ZONE = 50; // pixels from edge to trigger scroll
        private const double SCROLL_MAX_SPEED = 20;

        // Live reorder tracking
        private int _currentDragOverIndex = -1;

        public bool WasConfirmed { get; private set; }

        public PageReorderWindow(PdfMergeItem item)
        {
            InitializeComponent();
            _item = item;

            _pageOrder = item.GetSelectedPageIndices().Select(i => i + 1).ToList();
            if (_pageOrder.Count == 0)
                _pageOrder = Enumerable.Range(1, item.TotalPages).ToList();

            HeaderText.Text = $"Reorder Pages — {item.FileName}";

            // Setup auto-scroll timer
            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
            _scrollTimer.Tick += ScrollTimer_Tick;

            RebuildGrid(false);
            LoadThumbnailsAsync();
        }

        public List<int> GetFinalPageOrder() => _pageOrder.Select(p => p - 1).ToList();

        // ═══════════════════════════════════════════════════════════════
        // AUTO-SCROLL during drag
        // ═══════════════════════════════════════════════════════════════

        private void ScrollTimer_Tick(object sender, EventArgs e)
        {
            if (_scrollSpeed == 0) return;
            PageScrollViewer.ScrollToVerticalOffset(
                PageScrollViewer.VerticalOffset + _scrollSpeed);
        }

        private void UpdateAutoScroll(DragEventArgs e)
        {
            var pos = e.GetPosition(PageScrollViewer);
            double viewHeight = PageScrollViewer.ActualHeight;

            if (pos.Y < SCROLL_ZONE)
            {
                // Near top — scroll up
                double factor = 1.0 - (pos.Y / SCROLL_ZONE);
                _scrollSpeed = -SCROLL_MAX_SPEED * factor;
                if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
            }
            else if (pos.Y > viewHeight - SCROLL_ZONE)
            {
                // Near bottom — scroll down
                double factor = 1.0 - ((viewHeight - pos.Y) / SCROLL_ZONE);
                _scrollSpeed = SCROLL_MAX_SPEED * factor;
                if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
            }
            else
            {
                _scrollSpeed = 0;
                _scrollTimer.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // THUMBNAIL LOADING
        // ═══════════════════════════════════════════════════════════════

        private async void LoadThumbnailsAsync()
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_item.FilePath);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);

                for (int i = 0; i < Math.Min(_item.TotalPages, (int)pdfDoc.PageCount); i++)
                {
                    using (var page = pdfDoc.GetPage((uint)i))
                    {
                        using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                        {
                            var options = new WinPdf.PdfPageRenderOptions
                            {
                                DestinationWidth = 140,
                                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                            };
                            await page.RenderToStreamAsync(stream, options);

                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream.AsStreamForRead();
                            bitmap.EndInit();
                            bitmap.Freeze();
                            _thumbnails[i + 1] = bitmap;
                        }
                    }

                    int pageNum = i + 1;
                    Dispatcher.Invoke(() => UpdateTileThumbnail(pageNum));

                    // Small yield every 5 pages to keep UI responsive on large PDFs
                    if (i % 5 == 4)
                        await System.Threading.Tasks.Task.Delay(10);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Thumbnail load failed: {ex.Message}");
            }
        }

        private void UpdateTileThumbnail(int originalPageNum)
        {
            foreach (var child in PageItemsControl.Items)
            {
                if (child is Border tile && tile.Tag is int[] meta && meta[1] == originalPageNum)
                {
                    if (tile.Child is Grid grid)
                    {
                        foreach (var c in grid.Children)
                        {
                            if (c is Image img && _thumbnails.ContainsKey(originalPageNum))
                            {
                                img.Source = _thumbnails[originalPageNum];
                                img.Visibility = Visibility.Visible;
                                break;
                            }
                        }
                    }
                    break;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // BUILD PAGE GRID
        // ═══════════════════════════════════════════════════════════════

        private void RebuildGrid(bool animate)
        {
            // Capture old positions for animation
            var oldPositions = new Dictionary<int, Point>();
            if (animate)
            {
                for (int i = 0; i < PageItemsControl.Items.Count; i++)
                {
                    if (PageItemsControl.Items[i] is Border tile && tile.Tag is int[] meta)
                    {
                        int col = i % COLUMNS;
                        int row = i / COLUMNS;
                        oldPositions[meta[1]] = new Point(
                            col * (CELL_W + CELL_MARGIN * 2),
                            row * (CELL_H + CELL_MARGIN * 2));
                    }
                }
            }

            PageItemsControl.Items.Clear();

            for (int i = 0; i < _pageOrder.Count; i++)
            {
                var tile = CreatePageTile(i, _pageOrder[i]);
                PageItemsControl.Items.Add(tile);
            }

            // Animate from old to new position
            if (animate && oldPositions.Count > 0)
            {
                for (int i = 0; i < PageItemsControl.Items.Count; i++)
                {
                    if (PageItemsControl.Items[i] is Border tile && tile.Tag is int[] meta)
                    {
                        int col = i % COLUMNS;
                        int row = i / COLUMNS;
                        var newPos = new Point(
                            col * (CELL_W + CELL_MARGIN * 2),
                            row * (CELL_H + CELL_MARGIN * 2));

                        if (oldPositions.TryGetValue(meta[1], out var oldPos))
                        {
                            double dx = oldPos.X - newPos.X;
                            double dy = oldPos.Y - newPos.Y;

                            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
                            {
                                var transform = (TranslateTransform)tile.RenderTransform;
                                transform.X = dx;
                                transform.Y = dy;

                                var animX = new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(200))
                                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                                var animY = new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(200))
                                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

                                transform.BeginAnimation(TranslateTransform.XProperty, animX);
                                transform.BeginAnimation(TranslateTransform.YProperty, animY);
                            }
                        }
                    }
                }
            }

            UpdateInfo();
        }

        private Border CreatePageTile(int orderIndex, int originalPage)
        {
            var cellGrid = new Grid();

            var img = new Image
            {
                Width = CELL_W - 14,
                Height = CELL_H - 40,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            };

            if (_thumbnails.ContainsKey(originalPage))
            {
                img.Source = _thumbnails[originalPage];
                img.Visibility = Visibility.Visible;
            }
            cellGrid.Children.Add(img);

            var labelStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var orderBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(139, 92, 246)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            orderBadge.Child = new TextBlock
            {
                Text = (orderIndex + 1).ToString(),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            labelStack.Children.Add(orderBadge);

            var pageLabel = new TextBlock
            {
                Text = $"pg {originalPage}",
                FontSize = 9,
                Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorTertiary")
            };
            labelStack.Children.Add(pageLabel);
            cellGrid.Children.Add(labelStack);

            var tile = new Border
            {
                Width = CELL_W,
                Height = CELL_H,
                Margin = new Thickness(CELL_MARGIN),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = new int[] { orderIndex, originalPage },
                Child = cellGrid,
                ToolTip = $"Position {orderIndex + 1} • Original page {originalPage}",
                AllowDrop = true,
                RenderTransform = new TranslateTransform(),
                SnapsToDevicePixels = true
            };

            tile.MouseLeftButtonDown += Tile_MouseDown;
            tile.MouseMove += Tile_MouseMove;
            tile.MouseLeftButtonUp += Tile_MouseUp;
            tile.DragOver += Tile_DragOver;
            tile.Drop += Tile_Drop;
            tile.MouseEnter += (s, e) =>
            {
                if (!_selectedIndices.Contains(orderIndex))
                    tile.Background = new SolidColorBrush(Color.FromArgb(30, 139, 92, 246));
            };
            tile.MouseLeave += (s, e) =>
            {
                if (!_selectedIndices.Contains(orderIndex))
                    tile.Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
            };

            return tile;
        }

        // ═══════════════════════════════════════════════════════════════
        // SELECTION
        // ═══════════════════════════════════════════════════════════════

        private void ToggleSelection(int index, bool ctrlHeld)
        {
            if (!ctrlHeld)
            {
                _selectedIndices.Clear();
                _selectedIndices.Add(index);
            }
            else
            {
                if (_selectedIndices.Contains(index))
                    _selectedIndices.Remove(index);
                else
                    _selectedIndices.Add(index);
            }
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            for (int i = 0; i < PageItemsControl.Items.Count; i++)
            {
                if (PageItemsControl.Items[i] is Border tile)
                {
                    bool sel = _selectedIndices.Contains(i);
                    tile.Background = sel
                        ? new SolidColorBrush(Color.FromArgb(50, 139, 92, 246))
                        : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
                    tile.BorderBrush = sel
                        ? new SolidColorBrush(Color.FromRgb(139, 92, 246))
                        : new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
                    tile.BorderThickness = sel ? new Thickness(2) : new Thickness(1);
                }
            }
            SelectedCountText.Text = _selectedIndices.Count > 0
                ? $"{_selectedIndices.Count} selected"
                : $"{_pageOrder.Count} pages";
        }

        private void UpdateInfo()
        {
            PageCountInfo.Text = $"{_pageOrder.Count} pages from {_item.FileName}";
            SelectedCountText.Text = _selectedIndices.Count > 0
                ? $"{_selectedIndices.Count} selected"
                : $"{_pageOrder.Count} pages";
        }

        // ═══════════════════════════════════════════════════════════════
        // DRAG TO REORDER — LIVE reorder as you drag
        // ═══════════════════════════════════════════════════════════════

        private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border tile && tile.Tag is int[] meta)
            {
                int idx = meta[0];
                _dragStartPoint = e.GetPosition(null);
                _dragStartIndex = idx;
                bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                ToggleSelection(idx, ctrl);
            }
        }

        private void Tile_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStartIndex < 0 || _isDragging) return;
            Point pos = e.GetPosition(null);
            Vector diff = _dragStartPoint - pos;
            if (Math.Abs(diff.X) > 8 || Math.Abs(diff.Y) > 8)
            {
                _isDragging = true;
                _dragSourceTile = (Border)sender;
                _currentDragOverIndex = _dragStartIndex;

                if (!_selectedIndices.Contains(_dragStartIndex))
                {
                    _selectedIndices.Clear();
                    _selectedIndices.Add(_dragStartIndex);
                    UpdateVisuals();
                }

                // Dim source tile
                _dragSourceTile.Opacity = 0.3;

                // Create adorner ghost
                var adornerLayer = AdornerLayer.GetAdornerLayer(PageItemsControl);
                if (adornerLayer != null)
                {
                    _dragAdorner = new DragAdorner(PageItemsControl, _dragSourceTile, e.GetPosition(PageItemsControl));
                    adornerLayer.Add(_dragAdorner);
                }

                var data = new DataObject("PageReorder", _dragStartIndex);
                DragDrop.DoDragDrop(_dragSourceTile, data, DragDropEffects.Move);

                // Cleanup
                CleanupDrag();
            }
        }

        private void Tile_MouseUp(object sender, MouseButtonEventArgs e) => _dragStartIndex = -1;

        private void Tile_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PageReorder"))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Update adorner position
            if (_dragAdorner != null)
                _dragAdorner.UpdatePosition(e.GetPosition(PageItemsControl));

            // Auto-scroll
            UpdateAutoScroll(e);

            // LIVE REORDER: move the dragged page(s) in _pageOrder as cursor hovers
            if (sender is Border targetTile && targetTile.Tag is int[] targetMeta)
            {
                int targetIndex = targetMeta[0];
                LiveReorder(targetIndex);
            }
        }

        private void Tile_Drop(object sender, DragEventArgs e)
        {
            // Drop already handled by live reorder — just finalize
            e.Handled = true;
            CleanupDrag();
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PageReorder"))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (_dragAdorner != null)
                _dragAdorner.UpdatePosition(e.GetPosition(PageItemsControl));

            UpdateAutoScroll(e);
        }

        private void Grid_Drop(object sender, DragEventArgs e)
        {
            // Finalize — order is already set by live reorder
            e.Handled = true;
            CleanupDrag();
        }

        /// <summary>
        /// Moves the dragged page(s) to the target index in real-time.
        /// Uses lightweight Items.Remove/Insert — NO tile recreation.
        /// </summary>
        private void LiveReorder(int targetIndex)
        {
            if (targetIndex == _currentDragOverIndex) return;
            if (_selectedIndices.Count == 0) return;
            if (_selectedIndices.Count != 1) return; // Single-item drag only for perf

            int fromIndex = _selectedIndices.First();
            if (fromIndex == targetIndex) return;
            _currentDragOverIndex = targetIndex;

            // Move in data
            int page = _pageOrder[fromIndex];
            _pageOrder.RemoveAt(fromIndex);
            int insertAt = Math.Min(targetIndex, _pageOrder.Count);
            _pageOrder.Insert(insertAt, page);

            // Move the existing tile in the Items collection (no recreation)
            var tile = PageItemsControl.Items[fromIndex];
            PageItemsControl.Items.RemoveAt(fromIndex);
            PageItemsControl.Items.Insert(insertAt, tile);

            // Update selection
            _selectedIndices.Clear();
            _selectedIndices.Add(insertAt);

            // Update only the badge numbers that changed (lightweight)
            int lo = Math.Min(fromIndex, insertAt);
            int hi = Math.Max(fromIndex, insertAt);
            for (int i = lo; i <= hi; i++)
            {
                if (PageItemsControl.Items[i] is Border b)
                {
                    // Update Tag
                    if (b.Tag is int[] meta)
                        meta[0] = i;

                    // Update badge text
                    if (b.Child is Grid g)
                    {
                        foreach (var child in g.Children)
                        {
                            if (child is StackPanel sp && sp.VerticalAlignment == VerticalAlignment.Bottom)
                            {
                                if (sp.Children.Count > 0 && sp.Children[0] is Border badge && badge.Child is TextBlock tb)
                                    tb.Text = (i + 1).ToString();
                                break;
                            }
                        }
                    }

                    // Dim the dragged one
                    b.Opacity = _selectedIndices.Contains(i) ? 0.3 : 1.0;
                }
            }
        }

        private void CleanupDrag()
        {
            _scrollTimer.Stop();
            _scrollSpeed = 0;

            // Remove adorner
            if (_dragAdorner != null)
            {
                var layer = AdornerLayer.GetAdornerLayer(PageItemsControl);
                layer?.Remove(_dragAdorner);
                _dragAdorner = null;
            }

            // Restore all opacities
            for (int i = 0; i < PageItemsControl.Items.Count; i++)
            {
                if (PageItemsControl.Items[i] is Border tile)
                    tile.Opacity = 1.0;
            }

            _dragSourceTile = null;
            _isDragging = false;
            _dragStartIndex = -1;
            _currentDragOverIndex = -1;

            // Final rebuild to ensure clean state with correct badges
            RebuildGrid(false);
            UpdateVisuals();
        }

        // ═══════════════════════════════════════════════════════════════
        // TOOLBAR ACTIONS
        // ═══════════════════════════════════════════════════════════════

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _selectedIndices = new HashSet<int>(Enumerable.Range(0, _pageOrder.Count));
            UpdateVisuals();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            _selectedIndices.Clear();
            UpdateVisuals();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndices.Count == 0) return;
            var sorted = _selectedIndices.OrderBy(i => i).ToList();
            if (sorted[0] == 0) return;

            var newSel = new HashSet<int>();
            foreach (int idx in sorted)
            {
                int newIdx = idx - 1;
                (_pageOrder[idx], _pageOrder[newIdx]) = (_pageOrder[newIdx], _pageOrder[idx]);
                newSel.Add(newIdx);
            }
            _selectedIndices = newSel;
            RebuildGrid(true);
            UpdateVisuals();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndices.Count == 0) return;
            var sorted = _selectedIndices.OrderByDescending(i => i).ToList();
            if (sorted[0] >= _pageOrder.Count - 1) return;

            var newSel = new HashSet<int>();
            foreach (int idx in sorted)
            {
                int newIdx = idx + 1;
                (_pageOrder[idx], _pageOrder[newIdx]) = (_pageOrder[newIdx], _pageOrder[idx]);
                newSel.Add(newIdx);
            }
            _selectedIndices = newSel;
            RebuildGrid(true);
            UpdateVisuals();
        }

        private void Reverse_Click(object sender, RoutedEventArgs e)
        {
            _pageOrder.Reverse();
            RebuildGrid(true);
        }

        // ═══════════════════════════════════════════════════════════════
        // FOOTER
        // ═══════════════════════════════════════════════════════════════

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            WasConfirmed = true;
            Close();
        }
    }
}
