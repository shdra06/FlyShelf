using System;
using System.Globalization;
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
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    /// <summary>
    /// Represents a single page entry in the reorder grid.
    /// Supports pages from the original PDF and pages imported from external PDFs.
    /// </summary>
    public class PageEntry
    {
        /// <summary>Original 1-indexed page number within its source file.</summary>
        public int OriginalPage { get; set; }
        /// <summary>Full path to the source PDF file.</summary>
        public string SourceFile { get; set; }
        /// <summary>Short display name for the source file (shown on external pages).</summary>
        public string SourceLabel { get; set; }
        /// <summary>True if this page was imported from an external PDF (not the original).</summary>
        public bool IsExternal { get; set; }
        /// <summary>Rotation to apply when saving (0, 90, 180, 270).</summary>
        public int RotationDegrees { get; set; } = 0;
    }

    public partial class PageReorderWindow : MicaWindow
    {
        private readonly PdfMergeItem _item;
        private const int COLUMNS = 5;
        private const double CELL_W = 120;
        private const double CELL_H = 155;
        private const double CELL_MARGIN = 5;

        // Current page order — each entry tracks source file + page number
        private List<PageEntry> _pageEntries;
        // Currently selected indices in _pageEntries
        private HashSet<int> _selectedIndices = new();
        // Thumbnails keyed by "sourcePath:pageNum" for uniqueness
        private Dictionary<string, BitmapImage> _thumbnails = new();

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
        public bool WasOverwritten { get; private set; }
        /// <summary>True if external pages were added (caller needs to use multi-source save).</summary>
        public bool HasExternalPages => _pageEntries.Any(p => p.IsExternal);

        public PageReorderWindow(PdfMergeItem item)
        {
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            FlyShelf.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            _item = item;

            // Build initial page entries from the original PDF
            var entries = item.GetSelectedPageEntries();
            if (entries.Count == 0)
            {
                _pageEntries = Enumerable.Range(1, item.TotalPages).Select(p => new PageEntry
                {
                    OriginalPage = p,
                    SourceFile = item.FilePath,
                    SourceLabel = item.FileName,
                    IsExternal = false,
                    RotationDegrees = 0
                }).ToList();
            }
            else
            {
                _pageEntries = entries.Select(e => new PageEntry
                {
                    OriginalPage = e.PageIndex + 1,
                    SourceFile = item.FilePath,
                    SourceLabel = item.FileName,
                    IsExternal = false,
                    RotationDegrees = e.Rotation
                }).ToList();
            }

            HeaderText.Text = $"Reorder Pages — {item.FileName}";

            // Setup auto-scroll timer
            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
            _scrollTimer.Tick += ScrollTimer_Tick;

            RebuildGrid(false);
            _ = LoadThumbnailsAsync(_item.FilePath, _item.TotalPages);
            Closed += (s, e) => { _thumbnails = null; FlyShelf.Classes.SmoothScrollFeature.Detach(this); }; // PERF: release thumbnail memory on close
            this.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) this.Close(); };
        }

        /// <summary>
        /// Returns the final page order as a list of PageEntry objects.
        /// The caller uses this to build the output PDF from potentially multiple source files.
        /// </summary>
        public List<PageEntry> GetFinalPageEntries() => _pageEntries.ToList();

        /// <summary>
        /// Legacy: Returns 0-indexed page order for the original file only (no external pages).
        /// Used when HasExternalPages is false.
        /// </summary>
        public List<int> GetFinalPageOrder() => _pageEntries.Select(p => p.OriginalPage - 1).ToList();

        /// <summary>
        /// Returns the final page order with rotation info for the original file context.
        /// </summary>
        public List<PdfMergeItem.PageOrderEntry> GetFinalPageOrderEntries()
        {
            return _pageEntries.Select(p => new PdfMergeItem.PageOrderEntry
            {
                PageIndex = p.OriginalPage - 1,
                Rotation = p.RotationDegrees
            }).ToList();
        }

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

        private static string ThumbKey(string sourcePath, int pageNum) => $"{sourcePath}:{pageNum}";

        private async Task LoadThumbnailsAsync(string pdfPath, int totalPages)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(pdfPath);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);

                for (int i = 0; i < Math.Min(totalPages, (int)pdfDoc.PageCount); i++)
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

                            string key = ThumbKey(pdfPath, i + 1);
                            _thumbnails[key] = bitmap;
                        }
                    }

                    int pageNum = i + 1;
                    string thumbPath = pdfPath;
                    Dispatcher.Invoke(() => UpdateTileThumbnail(thumbPath, pageNum));

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

        private void UpdateTileThumbnail(string sourcePath, int originalPageNum)
        {
            string key = ThumbKey(sourcePath, originalPageNum);
            if (!_thumbnails.ContainsKey(key)) return;

            foreach (var child in PageItemsControl.Items)
            {
                if (child is Border tile && tile.Tag is PageEntry entry
                    && entry.SourceFile == sourcePath && entry.OriginalPage == originalPageNum)
                {
                    if (tile.Child is Grid grid)
                    {
                        foreach (var c in grid.Children)
                        {
                            if (c is Image img)
                            {
                                img.Source = _thumbnails[key];
                                img.Visibility = Visibility.Visible;
                                break;
                            }
                        }
                    }
                    // Don't break — there could be duplicate pages from the same source
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // BUILD PAGE GRID
        // ═══════════════════════════════════════════════════════════════

        private void RebuildGrid(bool animate)
        {
            // Capture old positions for animation
            var oldPositions = new Dictionary<string, Point>();
            if (animate)
            {
                for (int i = 0; i < PageItemsControl.Items.Count; i++)
                {
                    if (PageItemsControl.Items[i] is Border tile && tile.Tag is PageEntry entry)
                    {
                        int col = i % COLUMNS;
                        int row = i / COLUMNS;
                        string posKey = $"{entry.SourceFile}:{entry.OriginalPage}:{i}";
                        oldPositions[posKey] = new Point(
                            col * (CELL_W + CELL_MARGIN * 2),
                            row * (CELL_H + CELL_MARGIN * 2));
                    }
                }
            }

            PageItemsControl.Items.Clear();

            for (int i = 0; i < _pageEntries.Count; i++)
            {
                var tile = CreatePageTile(i, _pageEntries[i]);
                PageItemsControl.Items.Add(tile);
            }

            // Animate from old to new position
            if (animate && oldPositions.Count > 0)
            {
                for (int i = 0; i < PageItemsControl.Items.Count; i++)
                {
                    if (PageItemsControl.Items[i] is Border tile && tile.Tag is PageEntry entry)
                    {
                        int col = i % COLUMNS;
                        int row = i / COLUMNS;
                        var newPos = new Point(
                            col * (CELL_W + CELL_MARGIN * 2),
                            row * (CELL_H + CELL_MARGIN * 2));

                        // Try to find old position by matching the entry
                        string posKey = oldPositions.Keys.FirstOrDefault(k =>
                            k.StartsWith($"{entry.SourceFile}:{entry.OriginalPage}:", StringComparison.Ordinal));

                        if (posKey != null && oldPositions.TryGetValue(posKey, out var oldPos))
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

                            // Remove used key so duplicates get matched to different old positions
                            oldPositions.Remove(posKey);
                        }
                    }
                }
            }

            UpdateInfo();
            if (OverwriteBtn != null)
                OverwriteBtn.Visibility = HasExternalPages ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border CreatePageTile(int orderIndex, PageEntry entry)
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

            string key = ThumbKey(entry.SourceFile, entry.OriginalPage);
            if (_thumbnails.ContainsKey(key))
            {
                img.Source = _thumbnails[key];
                img.Visibility = Visibility.Visible;
            }
            cellGrid.Children.Add(img);

            // Apply existing rotation to the thumbnail
            if (entry.RotationDegrees != 0)
            {
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                img.RenderTransform = new RotateTransform(entry.RotationDegrees);
            }

            // Source badge for external pages (top-left corner)
            if (entry.IsExternal)
            {
                var srcBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(200, 46, 134, 222)),
                    CornerRadius = new CornerRadius(0, 0, 6, 0),
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                string shortName = entry.SourceLabel.Length > 12
                    ? string.Concat(entry.SourceLabel.AsSpan(0, 10), "…")
                    : entry.SourceLabel;
                srcBadge.Child = new TextBlock
                {
                    Text = shortName,
                    FontSize = 8,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = CELL_W - 20
                };
                srcBadge.ToolTip = entry.SourceLabel;
                cellGrid.Children.Add(srcBadge);
            }

            // Rotate button (top-right corner)
            var rotateBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(180, 50, 50, 50)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Opacity = 0.7,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "↻",
                    FontSize = 13,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            rotateBtn.ToolTip = "Rotate 90°";
            rotateBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true; // Prevent tile drag/selection
                entry.RotationDegrees = (entry.RotationDegrees + 90) % 360;
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                img.RenderTransform = new RotateTransform(entry.RotationDegrees);
            };
            cellGrid.Children.Add(rotateBtn);

            var labelStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var orderBadge = new Border
            {
                Background = TryFindResource("ThemeAccent") as Brush ?? new SolidColorBrush(Color.FromRgb(139, 92, 246)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            orderBadge.Child = new TextBlock
            {
                Text = (orderIndex + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            labelStack.Children.Add(orderBadge);

            var pageLabel = new TextBlock
            {
                Text = $"pg {entry.OriginalPage}",
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
                BorderBrush = entry.IsExternal
                    ? new SolidColorBrush(Color.FromArgb(60, 46, 134, 222))
                    : new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = entry,
                Child = cellGrid,
                ToolTip = entry.IsExternal
                    ? $"Position {orderIndex + 1} • Page {entry.OriginalPage} from {entry.SourceLabel}"
                    : $"Position {orderIndex + 1} • Original page {entry.OriginalPage}",
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
                    tile.Background = TryFindResource("ThemeAccentBg") as Brush ?? new SolidColorBrush(Color.FromArgb(30, 139, 92, 246));
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

        private int GetTileIndex(Border tile)
        {
            return PageItemsControl.Items.IndexOf(tile);
        }

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
                        ? (TryFindResource("ThemeAccentBgHover") as Brush ?? new SolidColorBrush(Color.FromArgb(50, 139, 92, 246)))
                        : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
                    tile.BorderBrush = sel
                        ? (TryFindResource("ThemeAccent") as Brush ?? new SolidColorBrush(Color.FromRgb(139, 92, 246)))
                        : (tile.Tag is PageEntry pe && pe.IsExternal
                            ? new SolidColorBrush(Color.FromArgb(60, 46, 134, 222))
                            : new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)));
                    tile.BorderThickness = sel ? new Thickness(2) : new Thickness(1);
                }
            }
            SelectedCountText.Text = _selectedIndices.Count > 0
                ? $"{_selectedIndices.Count} selected"
                : $"{_pageEntries.Count} pages";
        }

        private void UpdateInfo()
        {
            int externalCount = _pageEntries.Count(p => p.IsExternal);
            string info = externalCount > 0
                ? $"{_pageEntries.Count} pages ({externalCount} from other PDFs)"
                : $"{_pageEntries.Count} pages from {_item.FileName}";
            PageCountInfo.Text = info;
            SelectedCountText.Text = _selectedIndices.Count > 0
                ? $"{_selectedIndices.Count} selected"
                : $"{_pageEntries.Count} pages";
        }

        // ═══════════════════════════════════════════════════════════════
        // DRAG TO REORDER — LIVE reorder as you drag
        // ═══════════════════════════════════════════════════════════════

        private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border tile)
            {
                int idx = GetTileIndex(tile);
                if (idx < 0) return;
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

            // LIVE REORDER: move the dragged page(s) as cursor hovers
            if (sender is Border targetTile)
            {
                int targetIndex = GetTileIndex(targetTile);
                if (targetIndex >= 0)
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
            var entry = _pageEntries[fromIndex];
            _pageEntries.RemoveAt(fromIndex);
            int insertAt = Math.Min(targetIndex, _pageEntries.Count);
            _pageEntries.Insert(insertAt, entry);

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
                    // Update badge text
                    if (b.Child is Grid g)
                    {
                        foreach (var child in g.Children)
                        {
                            if (child is StackPanel sp && sp.VerticalAlignment == VerticalAlignment.Bottom)
                            {
                                if (sp.Children.Count > 0 && sp.Children[0] is Border badge && badge.Child is TextBlock tb)
                                    tb.Text = (i + 1).ToString(CultureInfo.InvariantCulture);
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
            _selectedIndices = new HashSet<int>(Enumerable.Range(0, _pageEntries.Count));
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
                (_pageEntries[idx], _pageEntries[newIdx]) = (_pageEntries[newIdx], _pageEntries[idx]);
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
            if (sorted[0] >= _pageEntries.Count - 1) return;

            var newSel = new HashSet<int>();
            foreach (int idx in sorted)
            {
                int newIdx = idx + 1;
                (_pageEntries[idx], _pageEntries[newIdx]) = (_pageEntries[newIdx], _pageEntries[idx]);
                newSel.Add(newIdx);
            }
            _selectedIndices = newSel;
            RebuildGrid(true);
            UpdateVisuals();
        }

        private void Reverse_Click(object sender, RoutedEventArgs e)
        {
            _pageEntries.Reverse();
            RebuildGrid(true);
        }

        private void ResetDefault_Click(object sender, RoutedEventArgs e)
        {
            // Remove external pages and restore original page order (1, 2, 3 ...)
            _pageEntries = _pageEntries
                .Where(p => !p.IsExternal)
                .OrderBy(p => p.OriginalPage)
                .ToList();

            // If all pages were external (edge case), rebuild from original item
            if (_pageEntries.Count == 0)
            {
                _pageEntries = Enumerable.Range(1, _item.TotalPages)
                    .Select(p => new PageEntry
                    {
                        OriginalPage = p,
                        SourceFile = _item.FilePath,
                        SourceLabel = _item.FileName,
                        IsExternal = false
                    }).ToList();
            }

            _selectedIndices.Clear();
            RebuildGrid(true);
            FlyShelf.Windows.ToastWindow.ShowToast("🔄 Page order reset to default");
        }

        // ═══════════════════════════════════════════════════════════════
        // ADD PAGES FROM EXTERNAL PDF
        // ═══════════════════════════════════════════════════════════════

        private async void AddPages_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PDF Files (*.pdf)|*.pdf",
                Title = "Select PDF to add pages from",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            foreach (string filePath in dlg.FileNames)
            {
                try
                {
                    int pageCount = await System.Threading.Tasks.Task.Run(() =>
                    {
                        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                        {
                            return doc.PageCount;
                        }
                    });

                    if (pageCount == 0)
                    {
                        ToastWindow.ShowToast($"⚠️ {Path.GetFileName(filePath)} has no pages.");
                        continue;
                    }

                    string fileName = Path.GetFileName(filePath);

                    // Determine insert position: after last selected, or at the end
                    int insertAt = _selectedIndices.Count > 0
                        ? _selectedIndices.Max() + 1
                        : _pageEntries.Count;

                    // Add all pages from the external PDF
                    for (int p = 1; p <= pageCount; p++)
                    {
                        _pageEntries.Insert(insertAt, new PageEntry
                        {
                            OriginalPage = p,
                            SourceFile = filePath,
                            SourceLabel = fileName,
                            IsExternal = true
                        });
                        insertAt++;
                    }

                    // Load thumbnails for the new file
                    _ = LoadThumbnailsAsync(filePath, pageCount);

                    ToastWindow.ShowToast($"✅ Added {pageCount} pages from {fileName}");
                }
                catch (Exception ex)
                {
                    ToastWindow.ShowToast($"❌ Failed to read {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            _selectedIndices.Clear();
            RebuildGrid(false);
            UpdateVisuals();
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // DELETE SELECTED PAGES
        // ═══════════════════════════════════════════════════════════════

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndices.Count == 0)
            {
                ToastWindow.ShowToast("⚠️ Select pages to delete first.");
                return;
            }

            if (_selectedIndices.Count >= _pageEntries.Count)
            {
                ToastWindow.ShowToast("⚠️ Can't delete all pages — at least one page must remain.");
                return;
            }

            // Remove in reverse order to preserve indices
            foreach (int idx in _selectedIndices.OrderByDescending(i => i))
            {
                _pageEntries.RemoveAt(idx);
            }

            _selectedIndices.Clear();
            RebuildGrid(true);
            UpdateVisuals();
        }

        // ═══════════════════════════════════════════════════════════════
        // FOOTER
        // ═══════════════════════════════════════════════════════════════
 
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
 
        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_pageEntries.Count == 0)
            {
                ToastWindow.ShowToast("⚠️ No pages remaining.");
                return;
            }
            WasConfirmed = true;
            Close();
        }

        private async void Overwrite_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (HasExternalPages)
            {
                ToastWindow.ShowToast("⚠️ Cannot overwrite original file when external pages are added.");
                return;
            }

            if (_pageEntries.Count == 0)
            {
                ToastWindow.ShowToast("⚠️ No pages remaining.");
                return;
            }

            var result = MessageBox.Show(
                $"This will overwrite the original file:\n{_item.FileName}\n\nAre you sure?",
                "Confirm Overwrite",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            OverwriteBtn.IsEnabled = false;
            ConfirmBtn.IsEnabled = false;
            OverwriteBtn.Content = "Saving...";

            string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");
            bool success = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                    {
                        using (var inputDoc = PdfSharp.Pdf.IO.PdfReader.Open(_item.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                        {
                            foreach (var entry in _pageEntries)
                            {
                                int idx = entry.OriginalPage - 1;
                                if (idx >= 0 && idx < inputDoc.PageCount)
                                {
                                    var page = inputDoc.Pages[idx];
                                    if (entry.RotationDegrees != 0)
                                    {
                                        page.Rotate = (page.Rotate + entry.RotationDegrees) % 360;
                                    }
                                    outputDoc.AddPage(page);
                                }
                            }
                        }
                        outputDoc.Save(tempPath);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Overwrite failed: {ex.Message}");
                    return false;
                }
            });

            if (success && File.Exists(tempPath))
            {
                try
                {
                    // Overwrite the original file (offload IO to thread pool)
                    var targetPath = _item.FilePath;
                    await Task.Run(() =>
                    {
                        File.Copy(tempPath, targetPath, true);
                        File.Delete(tempPath);
                    });

                    ToastWindow.ShowToast($"✅ Original file overwritten: {_item.FileName}");
                    
                    WasOverwritten = true;
                    WasConfirmed = true; 
                    Close();
                }
                catch (Exception ex)
                {
                    ToastWindow.ShowToast($"❌ Failed to overwrite: {ex.Message}");
                    OverwriteBtn.IsEnabled = true;
                    ConfirmBtn.IsEnabled = true;
                    OverwriteBtn.Content = "Save & Overwrite";
                }
            }
            else
            {
                ToastWindow.ShowToast("❌ Failed to generate rotated/reordered PDF.");
                OverwriteBtn.IsEnabled = true;
                ConfirmBtn.IsEnabled = true;
                OverwriteBtn.Content = "Save & Overwrite";
            }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _scrollTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
