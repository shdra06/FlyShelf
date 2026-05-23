using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.Classes;
using Microsoft.Win32;

namespace FlyShelf.Windows
{
    public partial class TableEditorWindow : MicaWPF.Controls.MicaWindow
    {
        private int _rows;
        private int _cols;
        private TextBox[,] _cells;
        private double[,] _confidence;
        private string[,] _tempValues;
        private string _imagePath;
        private string _extractionMethod;
        private bool _imageVisible = false;

        // Selection tracking
        private int _selectedRow = -1;
        private int _selectedCol = -1;

        // Undo stack
        private readonly Stack<UndoState> _undoStack = new Stack<UndoState>();
        private const int MaxUndoStates = 25;

        // ═══════════════════════════════════════════════════════════════════
        // CONSTRUCTORS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Original constructor for backward compatibility
        /// </summary>
        public TableEditorWindow(string input) : this(input, null, null) { }

        /// <summary>
        /// Enhanced constructor with source image and extraction method metadata
        /// </summary>
        public TableEditorWindow(string input, string imagePath, string extractionMethod)
        {
            InitializeComponent();
            NativeMethods.ApplyWindowBackdropAndBackground(this);

            _imagePath = imagePath;
            _extractionMethod = extractionMethod;

            if (IsJsonMatrix(input))
                ParseJsonMatrix(input);
            else
                ParseRawText(input);

            BuildGrid();
            UpdateInfo();
            SetupImagePanel();
            SetupMethodBadge();
            PushUndoState(); // Initial state
        }

        // ═══════════════════════════════════════════════════════════════════
        // IMAGE PANEL
        // ═══════════════════════════════════════════════════════════════════

        private void SetupImagePanel()
        {
            if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath)) return;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                SourceImage.Source = bitmap;

                // Auto-show image panel
                ShowImagePanel();
            }
            catch (Exception ex)
            {
                Logger.LogAction("TABLE_IMAGE", $"Failed to load source image: {ex.Message}");
            }
        }

        private void ShowImagePanel()
        {
            _imageVisible = true;
            ImagePanel.Visibility = Visibility.Visible;
            ImageColumnDef.Width = new GridLength(320);
            SplitterColumnDef.Width = new GridLength(6);
            PanelSplitter.Width = 6;
            PanelSplitter.Visibility = Visibility.Visible;
            ToggleImageBtn.Content = "🖼 Hide Source";
        }

        private void HideImagePanel()
        {
            _imageVisible = false;
            ImagePanel.Visibility = Visibility.Collapsed;
            ImageColumnDef.Width = new GridLength(0);
            SplitterColumnDef.Width = new GridLength(0);
            PanelSplitter.Width = 0;
            PanelSplitter.Visibility = Visibility.Collapsed;
            ToggleImageBtn.Content = "🖼 Source";
        }

        private void ToggleImagePanel_Click(object sender, RoutedEventArgs e)
        {
            if (_imageVisible) HideImagePanel();
            else
            {
                if (SourceImage.Source != null) ShowImagePanel();
                else ToastWindow.ShowToast("No source image available");
            }
        }

        private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Ctrl+Wheel to zoom the source image
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                double scale = e.Delta > 0 ? 1.15 : 0.87;
                var transform = SourceImage.LayoutTransform as ScaleTransform ?? new ScaleTransform(1, 1);
                double newScaleX = Math.Clamp(transform.ScaleX * scale, 0.2, 5.0);
                double newScaleY = Math.Clamp(transform.ScaleY * scale, 0.2, 5.0);
                SourceImage.LayoutTransform = new ScaleTransform(newScaleX, newScaleY);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // METHOD BADGE
        // ═══════════════════════════════════════════════════════════════════

        private void SetupMethodBadge()
        {
            if (string.IsNullOrEmpty(_extractionMethod)) return;

            MethodBadge.Visibility = Visibility.Visible;
            if (_extractionMethod.Contains("OCR", StringComparison.OrdinalIgnoreCase))
            {
                MethodBadgeText.Text = "⚡ Windows OCR";
                MethodBadge.Background = new SolidColorBrush(Color.FromArgb(26, 16, 185, 129));
                MethodBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else if (_extractionMethod.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                MethodBadgeText.Text = "✨ Gemini AI";
                MethodBadge.Background = new SolidColorBrush(Color.FromArgb(26, 139, 92, 246));
                MethodBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(167, 139, 250));
            }
            else
            {
                MethodBadgeText.Text = _extractionMethod;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // UNDO SYSTEM
        // ═══════════════════════════════════════════════════════════════════

        private class UndoState
        {
            public string[,] Values { get; set; }
            public double[,] Confidence { get; set; }
            public int Rows { get; set; }
            public int Cols { get; set; }
        }

        private void PushUndoState()
        {
            if (_cells == null) return;
            var state = new UndoState
            {
                Rows = _rows,
                Cols = _cols,
                Values = new string[_rows, _cols],
                Confidence = new double[_rows, _cols]
            };
            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols; j++)
                {
                    state.Values[i, j] = _cells[i, j]?.Text ?? "";
                    state.Confidence[i, j] = _confidence[i, j];
                }
            _undoStack.Push(state);
            if (_undoStack.Count > MaxUndoStates)
            {
                // Trim excess — convert to array, take last N, rebuild stack
                var items = _undoStack.ToArray();
                _undoStack.Clear();
                for (int i = Math.Min(items.Length - 1, MaxUndoStates - 1); i >= 0; i--)
                    _undoStack.Push(items[i]);
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => PerformUndo();

        private void PerformUndo()
        {
            if (_undoStack.Count <= 1) { ToastWindow.ShowToast("Nothing to undo"); return; }

            _undoStack.Pop(); // Remove current state
            var prev = _undoStack.Peek();

            _rows = prev.Rows;
            _cols = prev.Cols;
            _tempValues = new string[_rows, _cols];
            _confidence = new double[_rows, _cols];
            Array.Copy(prev.Values, _tempValues, prev.Values.Length);
            Array.Copy(prev.Confidence, _confidence, prev.Confidence.Length);

            BuildGrid();
            UpdateInfo();
        }

        // ═══════════════════════════════════════════════════════════════════
        // PARSING — Smart detection of table format
        // ═══════════════════════════════════════════════════════════════════

        private bool IsJsonMatrix(string input)
        {
            var trimmed = input.TrimStart();
            return trimmed.StartsWith("{") && trimmed.Contains("\"text\"");
        }

        private void ParseJsonMatrix(string jsonPayload)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, CellData>>(jsonPayload);
                if (dict == null || dict.Count == 0) { ParseRawText(jsonPayload); return; }

                int maxRow = -1, maxCol = -1;
                foreach (var key in dict.Keys)
                {
                    string cleaned = key.Replace("(", "").Replace(")", "");
                    var parts = cleaned.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int r) && int.TryParse(parts[1].Trim(), out int c))
                    {
                        if (r > maxRow) maxRow = r;
                        if (c > maxCol) maxCol = c;
                    }
                }

                if (maxRow < 0 || maxCol < 0) { ParseRawText(jsonPayload); return; }

                _rows = maxRow + 1;
                _cols = maxCol + 1;
                _cells = new TextBox[_rows, _cols];
                _confidence = new double[_rows, _cols];

                _tempValues = new string[_rows, _cols];
                for (int i = 0; i < _rows; i++)
                    for (int j = 0; j < _cols; j++)
                    {
                        string key = $"({i},{j})";
                        if (dict.ContainsKey(key))
                        {
                            _confidence[i, j] = dict[key].conf;
                            _tempValues[i, j] = dict[key].text;
                        }
                        else
                        {
                            _confidence[i, j] = 1.0;
                            _tempValues[i, j] = "";
                        }
                    }
            }
            catch
            {
                ParseRawText(jsonPayload);
            }
        }

        private void ParseRawText(string text)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                _rows = 1; _cols = 1;
                _tempValues = new string[1, 1];
                _confidence = new double[1, 1];
                _confidence[0, 0] = 1.0;
                _tempValues[0, 0] = text;
                return;
            }

            char separator = DetectSeparator(lines);
            var parsed = new List<string[]>();
            int maxCols = 0;

            foreach (var line in lines)
            {
                string[] cells;
                if (separator == '|')
                {
                    var trimmed = line.Trim().Trim('|');
                    cells = trimmed.Split('|').Select(c => c.Trim()).ToArray();
                }
                else
                {
                    cells = line.Split(separator).Select(c => c.Trim()).ToArray();
                }

                // Skip separator lines like "---+---+---"
                if (cells.All(c => c.All(ch => ch == '-' || ch == '+' || ch == '=' || ch == ' ')))
                    continue;

                parsed.Add(cells);
                if (cells.Length > maxCols) maxCols = cells.Length;
            }

            if (parsed.Count == 0)
            {
                _rows = 1; _cols = 1;
                _tempValues = new string[1, 1];
                _confidence = new double[1, 1];
                _confidence[0, 0] = 1.0;
                _tempValues[0, 0] = text;
                return;
            }

            _rows = parsed.Count;
            _cols = maxCols;
            _tempValues = new string[_rows, _cols];
            _confidence = new double[_rows, _cols];

            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols; j++)
                {
                    _tempValues[i, j] = (j < parsed[i].Length) ? parsed[i][j] : "";
                    _confidence[i, j] = 1.0;
                }
        }

        private char DetectSeparator(string[] lines)
        {
            int tabs = lines.Sum(l => l.Count(c => c == '\t'));
            int pipes = lines.Sum(l => l.Count(c => c == '|'));
            int commas = lines.Sum(l => l.Count(c => c == ','));

            if (tabs > 0 && tabs >= pipes && tabs >= commas) return '\t';
            if (pipes > 0 && pipes >= commas) return '|';
            if (commas > 0) return ',';
            return '\t';
        }

        // ═══════════════════════════════════════════════════════════════════
        // GRID BUILDING — Premium grid with headers, hover effects, and nav
        // ═══════════════════════════════════════════════════════════════════

        private void BuildGrid()
        {
            TableGrid.Children.Clear();
            TableGrid.RowDefinitions.Clear();
            TableGrid.ColumnDefinitions.Clear();

            if (_rows == 0 || _cols == 0) return;

            // Row number column
            TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            for (int j = 0; j < _cols; j++)
                TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 70 });

            // Column header row
            TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Corner cell
            var cornerCell = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(4)
            };
            cornerCell.Child = new TextBlock
            {
                Text = "#",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(cornerCell, 0);
            Grid.SetColumn(cornerCell, 0);
            TableGrid.Children.Add(cornerCell);

            // Column headers
            for (int j = 0; j < _cols; j++)
            {
                var header = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(20, 59, 130, 246)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 7, 8, 7),
                    Cursor = Cursors.Hand
                };
                string colName = j < 26 ? ((char)('A' + j)).ToString() : $"C{j + 1}";
                header.Child = new TextBlock
                {
                    Text = colName,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 96, 165, 250)),
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                // Column header context menu for delete/insert
                int colIndex = j;
                var colMenu = new ContextMenu();
                var insertLeftItem = new MenuItem { Header = $"Insert Column Left of {colName}" };
                insertLeftItem.Click += (s, e) => InsertColumnAt(colIndex);
                var insertRightItem = new MenuItem { Header = $"Insert Column Right of {colName}" };
                insertRightItem.Click += (s, e) => InsertColumnAt(colIndex + 1);
                var deleteColItem = new MenuItem { Header = $"Delete Column {colName}", Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)) };
                deleteColItem.Click += (s, e) => DeleteColumnAt(colIndex);
                colMenu.Items.Add(insertLeftItem);
                colMenu.Items.Add(insertRightItem);
                colMenu.Items.Add(new Separator());
                colMenu.Items.Add(deleteColItem);
                header.ContextMenu = colMenu;

                Grid.SetRow(header, 0);
                Grid.SetColumn(header, j + 1);
                TableGrid.Children.Add(header);
            }

            _cells = new TextBox[_rows, _cols];

            // Data rows
            for (int i = 0; i < _rows; i++)
            {
                TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 36 });

                // Row number cell with context menu
                var rowNumBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(12, 34, 197, 94)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(4),
                    Cursor = Cursors.Hand
                };
                rowNumBorder.Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 34, 197, 94)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Row header context menu
                int rowIndex = i;
                var rowMenu = new ContextMenu();
                var insertAboveItem = new MenuItem { Header = $"Insert Row Above Row {i + 1}" };
                insertAboveItem.Click += (s, e) => InsertRowAt(rowIndex);
                var insertBelowItem = new MenuItem { Header = $"Insert Row Below Row {i + 1}" };
                insertBelowItem.Click += (s, e) => InsertRowAt(rowIndex + 1);
                var deleteRowItem = new MenuItem { Header = $"Delete Row {i + 1}", Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)) };
                deleteRowItem.Click += (s, e) => DeleteRowAt(rowIndex);
                rowMenu.Items.Add(insertAboveItem);
                rowMenu.Items.Add(insertBelowItem);
                rowMenu.Items.Add(new Separator());
                rowMenu.Items.Add(deleteRowItem);
                rowNumBorder.ContextMenu = rowMenu;

                Grid.SetRow(rowNumBorder, i + 1);
                Grid.SetColumn(rowNumBorder, 0);
                TableGrid.Children.Add(rowNumBorder);

                // Data cells
                for (int j = 0; j < _cols; j++)
                {
                    double conf = _confidence[i, j];
                    Color bg;
                    if (conf >= 1.0) bg = Color.FromArgb(8, 255, 255, 255);
                    else if (conf >= 0.95) bg = Color.FromArgb(25, 16, 185, 129);
                    else if (conf >= 0.85) bg = Color.FromArgb(30, 234, 179, 8);
                    else bg = Color.FromArgb(35, 239, 68, 68);

                    var border = new Border
                    {
                        Background = new SolidColorBrush(bg),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(1)
                    };

                    string cellText = _tempValues != null ? _tempValues[i, j] : "";

                    var tb = new TextBox
                    {
                        Text = cellText,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = false,
                        Background = Brushes.Transparent,
                        Foreground = string.IsNullOrEmpty(cellText)
                            ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
                            : Brushes.White,
                        BorderThickness = new Thickness(0),
                        MinHeight = 32,
                        FontSize = 13,
                        Padding = new Thickness(7, 5, 7, 5),
                        FontWeight = (i == 0) ? FontWeights.Bold : FontWeights.Normal,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Tag = $"{i},{j}" // Store row,col for navigation
                    };

                    // Track selection
                    int ri = i, ci = j;
                    tb.GotFocus += (s, e) =>
                    {
                        _selectedRow = ri;
                        _selectedCol = ci;
                        border.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 96, 165, 250));
                        border.BorderThickness = new Thickness(1.5);
                        UpdateSelectionInfo();
                    };
                    tb.LostFocus += (s, e) =>
                    {
                        border.BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                        border.BorderThickness = new Thickness(0, 0, 1, 1);
                    };
                    tb.TextChanged += (s, e) =>
                    {
                        // Update foreground color based on content
                        tb.Foreground = string.IsNullOrEmpty(tb.Text)
                            ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
                            : Brushes.White;
                    };

                    // Tab/Enter navigation
                    tb.PreviewKeyDown += Cell_PreviewKeyDown;

                    // Cell context menu
                    var cellMenu = new ContextMenu();
                    var copyItem = new MenuItem { Header = "Copy Cell", InputGestureText = "Ctrl+C" };
                    copyItem.Click += (s, e) => { if (!string.IsNullOrEmpty(tb.SelectedText)) Clipboard.SetText(tb.SelectedText); else Clipboard.SetText(tb.Text); };
                    var clearItem = new MenuItem { Header = "Clear Cell" };
                    clearItem.Click += (s, e) => { PushUndoState(); tb.Text = ""; };
                    var insertRowAbove = new MenuItem { Header = "Insert Row Above" };
                    insertRowAbove.Click += (s, e) => InsertRowAt(ri);
                    var insertRowBelow = new MenuItem { Header = "Insert Row Below" };
                    insertRowBelow.Click += (s, e) => InsertRowAt(ri + 1);
                    var insertColLeft = new MenuItem { Header = "Insert Column Left" };
                    insertColLeft.Click += (s, e) => InsertColumnAt(ci);
                    var insertColRight = new MenuItem { Header = "Insert Column Right" };
                    insertColRight.Click += (s, e) => InsertColumnAt(ci + 1);
                    var delRow = new MenuItem { Header = $"Delete Row {ri + 1}", Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)) };
                    delRow.Click += (s, e) => DeleteRowAt(ri);
                    var delCol = new MenuItem { Header = $"Delete Column {(ci < 26 ? ((char)('A' + ci)).ToString() : $"C{ci + 1}")}", Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)) };
                    delCol.Click += (s, e) => DeleteColumnAt(ci);

                    cellMenu.Items.Add(copyItem);
                    cellMenu.Items.Add(clearItem);
                    cellMenu.Items.Add(new Separator());
                    cellMenu.Items.Add(insertRowAbove);
                    cellMenu.Items.Add(insertRowBelow);
                    cellMenu.Items.Add(insertColLeft);
                    cellMenu.Items.Add(insertColRight);
                    cellMenu.Items.Add(new Separator());
                    cellMenu.Items.Add(delRow);
                    cellMenu.Items.Add(delCol);
                    tb.ContextMenu = cellMenu;

                    _cells[i, j] = tb;
                    border.Child = tb;
                    Grid.SetRow(border, i + 1);
                    Grid.SetColumn(border, j + 1);
                    TableGrid.Children.Add(border);
                }
            }

            _tempValues = null;
        }

        private void UpdateSelectionInfo()
        {
            if (_selectedRow >= 0 && _selectedCol >= 0)
            {
                string colName = _selectedCol < 26 ? ((char)('A' + _selectedCol)).ToString() : $"C{_selectedCol + 1}";
                SelectionInfo.Text = $"Cell {colName}{_selectedRow + 1}  ·  Tab ↹ navigate  ·  Ctrl+Z undo";
            }
        }

        private void Cell_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var tag = tb.Tag?.ToString()?.Split(',');
            if (tag == null || tag.Length != 2) return;
            int row = int.Parse(tag[0]), col = int.Parse(tag[1]);

            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                PushUndoState();
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Move left/up
                    col--;
                    if (col < 0) { col = _cols - 1; row--; }
                    if (row < 0) row = _rows - 1;
                }
                else
                {
                    // Move right/down
                    col++;
                    if (col >= _cols) { col = 0; row++; }
                    if (row >= _rows) row = 0;
                }
                _cells[row, col]?.Focus();
                _cells[row, col]?.SelectAll();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                PushUndoState();
                row++;
                if (row >= _rows) row = 0;
                _cells[row, col]?.Focus();
                _cells[row, col]?.SelectAll();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
            }
        }

        private void UpdateInfo()
        {
            InfoText.Text = $"{_rows} rows × {_cols} columns";
        }

        // ═══════════════════════════════════════════════════════════════════
        // EXPORT — HTML (Word), CSV, TSV, Markdown, Save to File
        // ═══════════════════════════════════════════════════════════════════

        private void ExportHtml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sbHtml = new StringBuilder();
                sbHtml.Append("<table style=\"border-collapse: collapse; width: 100%; font-family: Calibri, Arial, sans-serif; font-size: 11pt;\">\n");

                for (int i = 0; i < _rows; i++)
                {
                    sbHtml.Append("<tr>\n");
                    for (int j = 0; j < _cols; j++)
                    {
                        string cellVal = _cells[i, j].Text;
                        string tag = (i == 0) ? "th" : "td";
                        string style = (i == 0)
                            ? "border: 1px solid #000; padding: 6px 10px; font-weight: bold; background-color: #D9E2F3; text-align: left;"
                            : "border: 1px solid #999; padding: 6px 10px; text-align: left;";

                        sbHtml.Append($"<{tag} style=\"{style}\">");
                        sbHtml.Append(System.Net.WebUtility.HtmlEncode(cellVal));
                        sbHtml.Append($"</{tag}>\n");
                    }
                    sbHtml.Append("</tr>\n");
                }
                sbHtml.Append("</table>\n");

                string fragment = sbHtml.ToString();

                string header = "Version:0.9\r\nStartHTML:SSSSSSSS\r\nEndHTML:EEEEEEEE\r\nStartFragment:FFFFFFFF\r\nEndFragment:GGGGGGGG\r\n";
                string htmlStart = "<html><body>\r\n<!--StartFragment-->\r\n";
                string htmlEnd = "\r\n<!--EndFragment-->\r\n</body></html>";

                int headerLen = Encoding.UTF8.GetByteCount(header);
                int htmlStartLen = Encoding.UTF8.GetByteCount(htmlStart);
                int fragmentLen = Encoding.UTF8.GetByteCount(fragment);
                int htmlEndLen = Encoding.UTF8.GetByteCount(htmlEnd);

                header = header.Replace("SSSSSSSS", headerLen.ToString("D8"));
                header = header.Replace("EEEEEEEE", (headerLen + htmlStartLen + fragmentLen + htmlEndLen).ToString("D8"));
                header = header.Replace("FFFFFFFF", (headerLen + htmlStartLen).ToString("D8"));
                header = header.Replace("GGGGGGGG", (headerLen + htmlStartLen + fragmentLen).ToString("D8"));

                string cfHtml = header + htmlStart + fragment + htmlEnd;
                string tsv = BuildTsv();

                MainWindow.SetWritingClipboard(true);
                try
                {
                    var dataObj = new DataObject();
                    dataObj.SetData(DataFormats.Html, cfHtml);
                    dataObj.SetData(DataFormats.Text, tsv);
                    Clipboard.SetDataObject(dataObj, true);
                }
                finally { MainWindow.SetWritingClipboard(false); }

                ToastWindow.ShowToast("Table copied! Paste into Word 📋");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Export failed: {ex.Message}");
                Logger.LogAction("TABLE_EXPORT", ex.Message);
            }
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.SetWritingClipboard(true);
            try
            {
                Clipboard.SetText(BuildCsv());
                ToastWindow.ShowToast("Table copied as CSV 📋");
            }
            catch { ToastWindow.ShowToast("Clipboard busy — try again"); }
            finally { MainWindow.SetWritingClipboard(false); }
        }

        private void ExportTsv_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.SetWritingClipboard(true);
            try
            {
                Clipboard.SetText(BuildTsv());
                ToastWindow.ShowToast("Table copied as TSV 📋");
            }
            catch { ToastWindow.ShowToast("Clipboard busy — try again"); }
            finally { MainWindow.SetWritingClipboard(false); }
        }

        private void ExportMarkdown_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.SetWritingClipboard(true);
            try
            {
                Clipboard.SetText(BuildMarkdown());
                ToastWindow.ShowToast("Table copied as Markdown 📋");
            }
            catch { ToastWindow.ShowToast("Clipboard busy — try again"); }
            finally { MainWindow.SetWritingClipboard(false); }
        }

        private void SaveToFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "CSV Files (*.csv)|*.csv|TSV Files (*.tsv)|*.tsv|HTML Files (*.html)|*.html|Markdown Files (*.md)|*.md",
                    DefaultExt = ".csv",
                    FileName = "extracted_table"
                };

                if (dlg.ShowDialog() == true)
                {
                    string content;
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    switch (ext)
                    {
                        case ".tsv": content = BuildTsv(); break;
                        case ".html": content = BuildFullHtml(); break;
                        case ".md": content = BuildMarkdown(); break;
                        default: content = BuildCsv(); break;
                    }

                    File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
                    ToastWindow.ShowToast($"Table saved to {Path.GetFileName(dlg.FileName)} ✅");
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Save failed: {ex.Message}");
            }
        }

        private string BuildCsv()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _rows; i++)
            {
                for (int j = 0; j < _cols; j++)
                {
                    if (j > 0) sb.Append(',');
                    string val = _cells[i, j].Text;
                    if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
                        sb.Append($"\"{val.Replace("\"", "\"\"")}\"");
                    else
                        sb.Append(val);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string BuildTsv()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _rows; i++)
            {
                for (int j = 0; j < _cols; j++)
                {
                    if (j > 0) sb.Append('\t');
                    sb.Append(_cells[i, j].Text);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string BuildMarkdown()
        {
            var sb = new StringBuilder();
            // Calculate column widths for alignment
            var widths = new int[_cols];
            for (int j = 0; j < _cols; j++)
            {
                widths[j] = 3; // minimum
                for (int i = 0; i < _rows; i++)
                {
                    int len = _cells[i, j].Text.Length;
                    if (len > widths[j]) widths[j] = len;
                }
            }

            for (int i = 0; i < _rows; i++)
            {
                sb.Append('|');
                for (int j = 0; j < _cols; j++)
                {
                    string val = _cells[i, j].Text.PadRight(widths[j]);
                    sb.Append($" {val} |");
                }
                sb.AppendLine();

                // Separator after header row
                if (i == 0)
                {
                    sb.Append('|');
                    for (int j = 0; j < _cols; j++)
                        sb.Append($" {new string('-', widths[j])} |");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private string BuildFullHtml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\"/>");
            sb.AppendLine("<style>table{border-collapse:collapse;width:100%;font-family:Calibri,Arial,sans-serif;font-size:11pt}");
            sb.AppendLine("th,td{border:1px solid #ccc;padding:8px 12px;text-align:left}");
            sb.AppendLine("th{background:#D9E2F3;font-weight:bold}");
            sb.AppendLine("tr:nth-child(even){background:#f8f9fa}</style>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<table>");
            for (int i = 0; i < _rows; i++)
            {
                sb.Append("<tr>");
                for (int j = 0; j < _cols; j++)
                {
                    string tag = (i == 0) ? "th" : "td";
                    sb.Append($"<{tag}>{System.Net.WebUtility.HtmlEncode(_cells[i, j].Text)}</{tag}>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table></body></html>");
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════
        // ROW/COLUMN MANIPULATION — Positional insert/delete
        // ═══════════════════════════════════════════════════════════════════

        private void SnapshotCurrentValues()
        {
            _tempValues = new string[_rows, _cols];
            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols; j++)
                    _tempValues[i, j] = _cells[i, j]?.Text ?? "";
        }

        private void AddRow_Click(object sender, RoutedEventArgs e) => InsertRowAt(_rows);
        private void AddCol_Click(object sender, RoutedEventArgs e) => InsertColumnAt(_cols);

        private void InsertRowAbove_Click(object sender, RoutedEventArgs e)
        {
            int pos = _selectedRow >= 0 ? _selectedRow : _rows;
            InsertRowAt(pos);
        }

        private void InsertColLeft_Click(object sender, RoutedEventArgs e)
        {
            int pos = _selectedCol >= 0 ? _selectedCol : _cols;
            InsertColumnAt(pos);
        }

        private void InsertRowAt(int position)
        {
            PushUndoState();
            SnapshotCurrentValues();

            position = Math.Clamp(position, 0, _rows);
            var newValues = new string[_rows + 1, _cols];
            var newConf = new double[_rows + 1, _cols];

            for (int i = 0; i < _rows + 1; i++)
                for (int j = 0; j < _cols; j++)
                {
                    if (i < position)
                    {
                        newValues[i, j] = _tempValues[i, j];
                        newConf[i, j] = _confidence[i, j];
                    }
                    else if (i == position)
                    {
                        newValues[i, j] = "";
                        newConf[i, j] = 1.0;
                    }
                    else
                    {
                        newValues[i, j] = _tempValues[i - 1, j];
                        newConf[i, j] = _confidence[i - 1, j];
                    }
                }

            _rows++;
            _tempValues = newValues;
            _confidence = newConf;
            BuildGrid();
            UpdateInfo();
        }

        private void InsertColumnAt(int position)
        {
            PushUndoState();
            SnapshotCurrentValues();

            position = Math.Clamp(position, 0, _cols);
            var newValues = new string[_rows, _cols + 1];
            var newConf = new double[_rows, _cols + 1];

            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols + 1; j++)
                {
                    if (j < position)
                    {
                        newValues[i, j] = _tempValues[i, j];
                        newConf[i, j] = _confidence[i, j];
                    }
                    else if (j == position)
                    {
                        newValues[i, j] = "";
                        newConf[i, j] = 1.0;
                    }
                    else
                    {
                        newValues[i, j] = _tempValues[i, j - 1];
                        newConf[i, j] = _confidence[i, j - 1];
                    }
                }

            _cols++;
            _tempValues = newValues;
            _confidence = newConf;
            BuildGrid();
            UpdateInfo();
        }

        private void DeleteRowAt(int position)
        {
            if (_rows <= 1) return;
            if (position < 0 || position >= _rows) return;

            PushUndoState();
            SnapshotCurrentValues();

            var newValues = new string[_rows - 1, _cols];
            var newConf = new double[_rows - 1, _cols];
            int dst = 0;
            for (int i = 0; i < _rows; i++)
            {
                if (i == position) continue;
                for (int j = 0; j < _cols; j++)
                {
                    newValues[dst, j] = _tempValues[i, j];
                    newConf[dst, j] = _confidence[i, j];
                }
                dst++;
            }

            _rows--;
            _tempValues = newValues;
            _confidence = newConf;
            _selectedRow = Math.Min(_selectedRow, _rows - 1);
            BuildGrid();
            UpdateInfo();
        }

        private void DeleteColumnAt(int position)
        {
            if (_cols <= 1) return;
            if (position < 0 || position >= _cols) return;

            PushUndoState();
            SnapshotCurrentValues();

            var newValues = new string[_rows, _cols - 1];
            var newConf = new double[_rows, _cols - 1];
            for (int i = 0; i < _rows; i++)
            {
                int dst = 0;
                for (int j = 0; j < _cols; j++)
                {
                    if (j == position) continue;
                    newValues[i, dst] = _tempValues[i, j];
                    newConf[i, dst] = _confidence[i, j];
                    dst++;
                }
            }

            _cols--;
            _tempValues = newValues;
            _confidence = newConf;
            _selectedCol = Math.Min(_selectedCol, _cols - 1);
            BuildGrid();
            UpdateInfo();
        }

        private void DelRow_Click(object sender, RoutedEventArgs e)
        {
            int pos = _selectedRow >= 0 ? _selectedRow : _rows - 1;
            DeleteRowAt(pos);
        }

        private void DelCol_Click(object sender, RoutedEventArgs e)
        {
            int pos = _selectedCol >= 0 ? _selectedCol : _cols - 1;
            DeleteColumnAt(pos);
        }

        // ═══════════════════════════════════════════════════════════════════
        // KEYBOARD SHORTCUTS
        // ═══════════════════════════════════════════════════════════════════

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.Z:
                        e.Handled = true;
                        PerformUndo();
                        break;
                    case Key.S:
                        e.Handled = true;
                        SaveToFile_Click(null, null);
                        break;
                    case Key.A:
                        // Select all text in focused cell
                        if (Keyboard.FocusedElement is TextBox focusedTb)
                        {
                            e.Handled = true;
                            focusedTb.SelectAll();
                        }
                        break;
                }
            }
            else if (e.Key == Key.Delete)
            {
                // Clear focused cell
                if (Keyboard.FocusedElement is TextBox tb && tb.SelectionLength == 0)
                {
                    PushUndoState();
                    tb.Text = "";
                    e.Handled = true;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // INTERNAL TYPES
        // ═══════════════════════════════════════════════════════════════════

        private class CellData
        {
            public string text { get; set; } = string.Empty;
            public double conf { get; set; } = 1.0;
        }
    }
}
