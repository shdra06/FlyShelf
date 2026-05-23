// ---------------------------------------------------------------
// TableEditorWindow — Export & Row/Column Manipulation
// ExportHtml, ExportCsv, ExportTsv, ExportMarkdown, SaveToFile,
// BuildCsv/Tsv/Markdown/FullHtml, Insert/Delete Row/Column,
// Window_KeyDown, CellData type
// Split from TableEditorWindow.xaml.cs for modularity
// ---------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyShelf.Classes;
using Microsoft.Win32;

namespace FlyShelf.Windows
{
    public partial class TableEditorWindow
    {
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
