using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicaWPF.Controls;
using WpfButton = System.Windows.Controls.Button;
using WinPdf = global::Windows.Data.Pdf;
using global::Windows.Storage;

namespace AdvanceClip.Windows
{
    public partial class PageSelectorWindow : MicaWindow
    {
        private PdfMergeItem _item;
        private const int COLUMNS = 5;
        private Border[] _pageCells;
        private int _lastClickedPage = -1;
        public bool Confirmed { get; private set; } = false;
        private BitmapImage[] _thumbnails;

        public PageSelectorWindow(PdfMergeItem item)
        {
            InitializeComponent();
            AdvanceClip.Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            _item = item;
            HeaderText.Text = $"Select Pages \u2014 {item.FileName}";
            LoadThumbnailsAsync();
            BuildPageGrid();
            UpdateSelectionInfo();
        }

        private async void LoadThumbnailsAsync()
        {
            try
            {
                _thumbnails = new BitmapImage[_item.TotalPages];
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
                                DestinationWidth = 120,
                                BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                            };
                            await page.RenderToStreamAsync(stream, options);

                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream.AsStreamForRead();
                            bitmap.EndInit();
                            bitmap.Freeze();
                            _thumbnails[i] = bitmap;
                        }
                    }

                    // Update the UI with the thumbnail as it loads
                    int idx = i;
                    Dispatcher.Invoke(() => UpdateCellThumbnail(idx));
                }
            }
            catch (Exception ex)
            {
                // Thumbnails are optional — grid still works without them
                System.Diagnostics.Debug.WriteLine($"Thumbnail load failed: {ex.Message}");
            }
        }

        private void UpdateCellThumbnail(int pageIndex)
        {
            if (_pageCells == null || pageIndex >= _pageCells.Length || _pageCells[pageIndex] == null) return;

            var cell = _pageCells[pageIndex];
            if (cell.Child is Grid grid)
            {
                // Find the Image placeholder and set its source
                foreach (var child in grid.Children)
                {
                    if (child is Image img && _thumbnails[pageIndex] != null)
                    {
                        img.Source = _thumbnails[pageIndex];
                        img.Visibility = Visibility.Visible;
                        break;
                    }
                }
            }
        }

        private void BuildPageGrid()
        {
            PageGrid.Children.Clear();
            ColumnHeaders.Children.Clear();

            if (_item.TotalPages == 0) return;

            int rows = (int)Math.Ceiling(_item.TotalPages / (double)COLUMNS);
            _pageCells = new Border[_item.TotalPages];

            // Column header buttons
            var rowSpacer = new Border { Width = 36, Height = 28, Margin = new Thickness(0, 0, 4, 0) };
            ColumnHeaders.Children.Add(rowSpacer);

            for (int col = 0; col < COLUMNS; col++)
            {
                int c = col;
                var colBtn = new WpfButton
                {
                    Content = $"Col {col + 1}",
                    Width = 110,
                    Height = 24,
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 10,
                    Background = new SolidColorBrush(Color.FromArgb(20, 59, 130, 246)),
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 59, 130, 246)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Toggle all pages in column {col + 1}"
                };
                colBtn.Click += (s, e) =>
                {
                    _item.ToggleColumn(c, COLUMNS);
                    RefreshAllCells();
                    UpdateSelectionInfo();
                };
                ColumnHeaders.Children.Add(colBtn);
            }

            // Build page grid with row buttons
            for (int row = 0; row < rows; row++)
            {
                int r = row;
                var rowBtn = new WpfButton
                {
                    Content = $"R{row + 1}",
                    Width = 36,
                    Height = 140,
                    Margin = new Thickness(0, 0, 4, 4),
                    FontSize = 10,
                    Background = new SolidColorBrush(Color.FromArgb(20, 34, 197, 94)),
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 34, 197, 94)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 34, 197, 94)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = $"Toggle all pages in row {row + 1}"
                };
                rowBtn.Click += (s, e) =>
                {
                    _item.ToggleRow(r, COLUMNS);
                    RefreshAllCells();
                    UpdateSelectionInfo();
                };
                PageGrid.Children.Add(rowBtn);

                for (int col = 0; col < COLUMNS; col++)
                {
                    int pageNum = row * COLUMNS + col + 1;
                    if (pageNum > _item.TotalPages)
                    {
                        var spacer = new Border { Width = 110, Height = 140, Margin = new Thickness(0, 0, 4, 4) };
                        PageGrid.Children.Add(spacer);
                        continue;
                    }

                    int pn = pageNum;
                    bool selected = _item.PageSelected[pageNum - 1];

                    // Page cell: thumbnail + page number overlay
                    var cellGrid = new Grid();

                    // Thumbnail image (initially hidden, loaded async)
                    var img = new Image
                    {
                        Width = 106,
                        Height = 110,
                        Stretch = Stretch.Uniform,
                        Visibility = Visibility.Collapsed,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    cellGrid.Children.Add(img);

                    // Page number + label at bottom
                    var labelStack = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Bottom,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    var numText = new TextBlock
                    {
                        Text = pn.ToString(),
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    var pageLabel = new TextBlock
                    {
                        Text = "page",
                        FontSize = 8,
                        Opacity = 0.5,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    labelStack.Children.Add(numText);
                    labelStack.Children.Add(pageLabel);
                    cellGrid.Children.Add(labelStack);

                    var cell = new Border
                    {
                        Width = 110,
                        Height = 140,
                        Margin = new Thickness(0, 0, 4, 4),
                        CornerRadius = new CornerRadius(6),
                        Cursor = Cursors.Hand,
                        Child = cellGrid,
                        ToolTip = $"Page {pn}"
                    };

                    ApplyCellStyle(cell, selected);

                    cell.MouseLeftButtonDown += (s, e) =>
                    {
                        // Shift-click for range selection
                        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                        {
                            if (_lastClickedPage > 0)
                            {
                                int from = Math.Min(_lastClickedPage, pn);
                                int to = Math.Max(_lastClickedPage, pn);
                                bool targetState = !_item.PageSelected[pn - 1];
                                for (int p = from; p <= to; p++)
                                {
                                    if (_item.PageSelected[p - 1] != targetState)
                                        _item.TogglePage(p);
                                }
                                RefreshAllCells();
                                UpdateSelectionInfo();
                                _lastClickedPage = pn;
                                return;
                            }
                        }

                        _item.TogglePage(pn);
                        _lastClickedPage = pn;
                        ApplyCellStyle(cell, _item.PageSelected[pn - 1]);
                        UpdateSelectionInfo();
                    };

                    _pageCells[pageNum - 1] = cell;
                    PageGrid.Children.Add(cell);
                }
            }
        }

        private void ApplyCellStyle(Border cell, bool selected)
        {
            if (selected)
            {
                cell.Background = new SolidColorBrush(Color.FromArgb(35, 59, 130, 246));
                cell.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));
                cell.BorderThickness = new Thickness(2);
            }
            else
            {
                cell.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
                cell.BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                cell.BorderThickness = new Thickness(1);
            }
        }

        private void RefreshAllCells()
        {
            if (_pageCells == null) return;
            for (int i = 0; i < _pageCells.Length; i++)
            {
                if (_pageCells[i] != null)
                    ApplyCellStyle(_pageCells[i], _item.PageSelected[i]);
            }
        }

        private void UpdateSelectionInfo()
        {
            int selected = _item.PageSelected.Count(p => p);
            SelectionInfo.Text = $"{selected} of {_item.TotalPages} pages selected";
            RangeInput.Text = _item.PageRangeText;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _item.SelectAll();
            RefreshAllCells();
            UpdateSelectionInfo();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            _item.DeselectAll();
            RefreshAllCells();
            UpdateSelectionInfo();
        }

        private void SelectOdd_Click(object sender, RoutedEventArgs e)
        {
            _item.DeselectAll();
            for (int p = 1; p <= _item.TotalPages; p += 2)
                _item.TogglePage(p);
            RefreshAllCells();
            UpdateSelectionInfo();
        }

        private void SelectEven_Click(object sender, RoutedEventArgs e)
        {
            _item.DeselectAll();
            for (int p = 2; p <= _item.TotalPages; p += 2)
                _item.TogglePage(p);
            RefreshAllCells();
            UpdateSelectionInfo();
        }

        private void ApplyRange_Click(object sender, RoutedEventArgs e)
        {
            if (!_item.SetPageRange(RangeInput.Text))
            {
                MessageBox.Show("Invalid range format. Use: 1-5, 8, 10-12", "Invalid Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshAllCells();
            UpdateSelectionInfo();
        }

        private void RangeInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyRange_Click(sender, e);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            this.Close();
        }
    }
}
