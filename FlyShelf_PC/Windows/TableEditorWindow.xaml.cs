using System;
using System.Globalization;
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
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            this.Closed += (s, e) => FlyShelf.Classes.SmoothScrollFeature.Detach(this);
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
                bitmap.DecodePixelWidth = 2048;
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
                // [FIX ANIM-9]: Use RenderTransform instead of LayoutTransform to skip layout passes during zoom
                var transform = SourceImage.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
                double newScaleX = Math.Clamp(transform.ScaleX * scale, 0.2, 5.0);
                double newScaleY = Math.Clamp(transform.ScaleY * scale, 0.2, 5.0);
                SourceImage.RenderTransform = new ScaleTransform(newScaleX, newScaleY);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // METHOD BADGE
        // ═══════════════════════════════════════════════════════════════════

        private void SetupMethodBadge()
        {
            if (string.IsNullOrEmpty(_extractionMethod)) return;

            MethodBadge.Visibility = Visibility.Visible;
            MethodBadgeText.Text = "⚡ Powered by FlyShelf";
            var badgeAccent = TryFindResource("ThemeAccent") as SolidColorBrush;
            var badgeColor = badgeAccent?.Color ?? Color.FromRgb(0, 210, 255);
            MethodBadge.Background = new SolidColorBrush(Color.FromArgb(26, badgeColor.R, badgeColor.G, badgeColor.B));
            MethodBadgeText.Foreground = badgeAccent ?? new SolidColorBrush(Color.FromRgb(0, 210, 255));
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
            return trimmed.StartsWith('{') && trimmed.Contains("\"text\"", StringComparison.Ordinal);
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

            // ── Resolve theme resources once for performance ──
            var borderBrush = TryFindResource("ThemeOverlayBorder") as Brush ?? new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            var textPrimary = TryFindResource("ThemeTextPrimary") as Brush ?? Brushes.White;
            var textMuted = TryFindResource("ThemeTextMuted") as Brush ?? new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            var overlayBg = TryFindResource("ThemeOverlayBg") as Brush ?? new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
            var dangerBrush = TryFindResource("DangerColor") as Brush ?? new SolidColorBrush(Color.FromRgb(239, 68, 68));

            // Accent color for column headers and focus highlight
            var accentSCB = TryFindResource("ThemeAccent") as SolidColorBrush;
            Color accentColor = accentSCB?.Color ?? Color.FromRgb(96, 165, 250);
            var colHeaderBg = new SolidColorBrush(Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));
            var colHeaderFg = new SolidColorBrush(Color.FromArgb(200, accentColor.R, accentColor.G, accentColor.B));
            var focusBorder = new SolidColorBrush(Color.FromArgb(100, accentColor.R, accentColor.G, accentColor.B));

            // Success color for row headers
            var successSCB = TryFindResource("SuccessColor") as SolidColorBrush;
            Color successColor = successSCB?.Color ?? Color.FromRgb(34, 197, 94);
            var rowHeaderBg = new SolidColorBrush(Color.FromArgb(12, successColor.R, successColor.G, successColor.B));
            var rowHeaderFg = new SolidColorBrush(Color.FromArgb(160, successColor.R, successColor.G, successColor.B));

            // Empty cell placeholder color
            var emptyFg = TryFindResource("ThemeTextMuted") as SolidColorBrush;
            var emptyFgColor = emptyFg?.Color ?? Color.FromArgb(60, 255, 255, 255);
            var emptyCellBrush = new SolidColorBrush(Color.FromArgb(60, emptyFgColor.R, emptyFgColor.G, emptyFgColor.B));

            // Row number column
            TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            for (int j = 0; j < _cols; j++)
                TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 70 });

            // Column header row
            TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Corner cell
            var cornerCell = new Border
            {
                Background = overlayBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(4)
            };
            cornerCell.Child = new TextBlock
            {
                Text = "#",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = textMuted,
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
                    Background = colHeaderBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 7, 8, 7),
                    Cursor = Cursors.Hand
                };
                string colName = j < 26 ? ((char)('A' + j)).ToString(CultureInfo.InvariantCulture) : $"C{j + 1}";
                header.Child = new TextBlock
                {
                    Text = colName,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = colHeaderFg,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                // Column header context menu for delete/insert
                int colIndex = j;
                var colMenu = new ContextMenu();
                var insertLeftItem = new MenuItem { Header = $"Insert Column Left of {colName}" };
                insertLeftItem.Click += (s, e) => InsertColumnAt(colIndex);
                var insertRightItem = new MenuItem { Header = $"Insert Column Right of {colName}" };
                insertRightItem.Click += (s, e) => InsertColumnAt(colIndex + 1);
                var deleteColItem = new MenuItem { Header = $"Delete Column {colName}", Foreground = dangerBrush };
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
                    Background = rowHeaderBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(4),
                    Cursor = Cursors.Hand
                };
                rowNumBorder.Child = new TextBlock
                {
                    Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = rowHeaderFg,
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
                var deleteRowItem = new MenuItem { Header = $"Delete Row {i + 1}", Foreground = dangerBrush };
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
                        BorderBrush = borderBrush,
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
                        Foreground = string.IsNullOrEmpty(cellText) ? emptyCellBrush : textPrimary,
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
                        border.BorderBrush = focusBorder;
                        border.BorderThickness = new Thickness(1.5);
                        UpdateSelectionInfo();
                    };
                    tb.LostFocus += (s, e) =>
                    {
                        border.BorderBrush = borderBrush;
                        border.BorderThickness = new Thickness(0, 0, 1, 1);
                    };
                    tb.TextChanged += (s, e) =>
                    {
                        // Update foreground color based on content
                        tb.Foreground = string.IsNullOrEmpty(tb.Text) ? emptyCellBrush : textPrimary;
                    };

                    // Tab/Enter navigation
                    tb.PreviewKeyDown += Cell_PreviewKeyDown;

                    // Cell context menu
                    var cellMenu = new ContextMenu();
                    var copyItem = new MenuItem { Header = "Copy Cell", InputGestureText = "Ctrl+C" };
                    copyItem.Click += (s, e) => 
                    { 
                        string textToCopy = !string.IsNullOrEmpty(tb.SelectedText) ? tb.SelectedText : tb.Text;
                        ClipboardHelper.SafeSetText(textToCopy);
                    };
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
                    var delRow = new MenuItem { Header = $"Delete Row {ri + 1}", Foreground = dangerBrush };
                    delRow.Click += (s, e) => DeleteRowAt(ri);
                    var delCol = new MenuItem { Header = $"Delete Column {(ci < 26 ? ((char)('A' + ci)).ToString(CultureInfo.InvariantCulture) : $"C{ci + 1}")}", Foreground = dangerBrush };
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
                string colName = _selectedCol < 26 ? ((char)('A' + _selectedCol)).ToString(CultureInfo.InvariantCulture) : $"C{_selectedCol + 1}";
                SelectionInfo.Text = $"Cell {colName}{_selectedRow + 1}  ·  Tab ↹ navigate  ·  Ctrl+Z undo";
            }
        }

        private void Cell_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var tag = tb.Tag?.ToString()?.Split(',');
            if (tag == null || tag.Length != 2) return;
            int row = int.Parse(tag[0], System.Globalization.CultureInfo.InvariantCulture), col = int.Parse(tag[1], System.Globalization.CultureInfo.InvariantCulture);

            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                // M-32 FIX: Removed PushUndoState() — Tab only navigates, no text change occurs
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

        // --- Export, Row/Col Manipulation, Keyboard Shortcuts moved to TableEditorWindow.IO.cs ---
    }
}

