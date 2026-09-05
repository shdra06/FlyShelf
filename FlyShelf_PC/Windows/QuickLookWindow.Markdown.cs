// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Markdown.cs — Markdown preview: WebView2 rendering,
// live editor mode, HTML export, zoom controls, and Markdown-to-PDF export.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Text;
using System.Windows;
using FlyShelf.Classes;
using ICSharpCode.AvalonEdit.Highlighting;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        private bool _isMarkdownEditMode = false;

        // ═══ Markdown Preview: Zoom & HTML / Raw Copy ═══

        private void MarkdownWebView_ZoomFactorChanged(object sender, EventArgs e)
        {
            try
            {
                int pct = (int)Math.Round(WebPreview.ZoomFactor * 100);
                ZoomLabel.Text = $"{pct}%";
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void MarkdownZoomReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WebPreview.ZoomFactor = 1.0;
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void CopyMarkdownButton_Click(object sender, RoutedEventArgs e)
        {
            string content = _isMarkdownEditMode ? CodePreview.Text : _markdownRawContent;
            if (string.IsNullOrEmpty(content)) return;

            try
            {
                ClipboardHelper.SafeSetText(content);
                FlyShelf.Windows.ToastWindow.ShowToast("Raw Markdown copied to clipboard! 📋");
            }
            catch { }
        }

        private async void CopyHtmlButton_Click(object sender, RoutedEventArgs e)
        {
            string content = _isMarkdownEditMode ? CodePreview.Text : _markdownRawContent;
            if (string.IsNullOrEmpty(content)) return;

            try
            {
                string renderedHtml = "";
                if (WebPreview != null && WebPreview.CoreWebView2 != null && !_isMarkdownEditMode)
                {
                    try
                    {
                        string json = await WebPreview.CoreWebView2.ExecuteScriptAsync("document.getElementById('content') ? document.getElementById('content').innerHTML : ''");
                        if (!string.IsNullOrEmpty(json) && json != "null")
                        {
                            renderedHtml = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(renderedHtml))
                {
                    renderedHtml = FlyShelf.Classes.MarkdownTemplate.GetHtml(content);
                }

                var dataObj = new System.Windows.DataObject();
                dataObj.SetData(DataFormats.Html, FormatHtmlForClipboard(renderedHtml));
                dataObj.SetData(DataFormats.UnicodeText, content);
                Clipboard.SetDataObject(dataObj, true);
                FlyShelf.Windows.ToastWindow.ShowToast("Rendered HTML copied to clipboard! 📋");
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Copy HTML failed: {ex.Message} ❌");
            }
        }

        private async void MarkdownEditToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isMarkdownEditMode)
                {
                    // Enter Markdown Source Editor Mode
                    _isMarkdownEditMode = true;
                    WebPreview.Visibility = Visibility.Collapsed;
                    CodePreview.Visibility = Visibility.Visible;
                    CodePreview.IsReadOnly = false;

                    // Clear any previous highlighting to prevent freezes
                    // (MarkDown/HTML highlighting in AvalonEdit is regex-heavy and crashes on large files)
                    CodePreview.SyntaxHighlighting = null;

                    // Update UI labels first
                    if (MdEditLabel != null) MdEditLabel.Text = "Preview";
                    if (MdEditIcon != null) MdEditIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Eye24;
                    if (MdSaveBtn != null) MdSaveBtn.Visibility = Visibility.Visible;

                    // Defer text loading to let the UI render the editor shell first
                    await System.Threading.Tasks.Task.Delay(50);
                    CodePreview.Text = _markdownRawContent ?? "";

                    FlyShelf.Windows.ToastWindow.ShowToast("Markdown Editor Mode (Ctrl+S to save, Ctrl+E to preview)");
                }
                else
                {
                    // Return to Rendered Markdown Preview Mode
                    _isMarkdownEditMode = false;
                    _markdownRawContent = CodePreview.Text;

                    // Clear editor to release resources
                    CodePreview.SyntaxHighlighting = null;
                    CodePreview.Text = "";
                    CodePreview.Visibility = Visibility.Collapsed;
                    WebPreview.Visibility = Visibility.Visible;

                    string html = !string.IsNullOrEmpty(_item?.FilePath)
                        ? FlyShelf.Classes.MarkdownTemplate.GetHtml(_markdownRawContent, _item.FilePath)
                        : FlyShelf.Classes.MarkdownTemplate.GetHtml(_markdownRawContent);

                    try
                    {
                        WebPreview.NavigateToString(html);
                    }
                    catch { }

                    if (MdEditLabel != null) MdEditLabel.Text = "Edit";
                    if (MdEditIcon != null) MdEditIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24;
                    if (MdSaveBtn != null) MdSaveBtn.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD_EDIT_TOGGLE_ERR", $"Edit toggle failed: {ex.Message}");
            }
        }

        private async void MarkdownSave_Click(object sender, RoutedEventArgs e)
        {
            string contentToSave = _isMarkdownEditMode ? CodePreview.Text : _markdownRawContent;
            if (string.IsNullOrEmpty(contentToSave)) return;

            try
            {
                _markdownRawContent = contentToSave;
                if (_item != null) _item.RawContent = contentToSave;

                if (!string.IsNullOrEmpty(_item?.FilePath) && File.Exists(_item.FilePath))
                {
                    await File.WriteAllTextAsync(_item.FilePath, contentToSave, Encoding.UTF8);
                    FlyShelf.Windows.ToastWindow.ShowToast($"Markdown saved! 💾 {Path.GetFileName(_item.FilePath)}");
                }
                else
                {
                    ClipboardHelper.SafeSetText(contentToSave);
                    FlyShelf.Windows.ToastWindow.ShowToast("Markdown updated & copied to clipboard! 💾");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Failed to save markdown: {ex.Message} ❌");
            }
        }

        private async void MarkdownToPdf_Click(object sender, RoutedEventArgs e)
        {
            string content = _isMarkdownEditMode ? CodePreview.Text : _markdownRawContent;
            if (string.IsNullOrEmpty(content)) return;

            try
            {
                MdToPdfBtn.IsEnabled = false;
                LoadingProgress.Visibility = Visibility.Visible;

                // Save PDF to the same directory as source file, or temp
                string sourceDir = !string.IsNullOrEmpty(_item?.FilePath) && Directory.Exists(Path.GetDirectoryName(_item.FilePath))
                    ? Path.GetDirectoryName(_item.FilePath)!
                    : Path.GetTempPath();
                string baseName = Path.GetFileNameWithoutExtension(_item?.FilePath ?? "document");
                string outputPdf = Path.Combine(sourceDir, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                bool success = await FlyShelf.Classes.WebView2Converter.ConvertMarkdownToPdfAsync(content, outputPdf, _item?.FilePath);
                if (!success || !File.Exists(outputPdf))
                {
                    // Pure C# offline fallback
                    success = FlyShelf.Classes.Utils.MarkdownToPdfConverter.ConvertContent(content, outputPdf, baseName, sourceDir);
                }

                LoadingProgress.Visibility = Visibility.Collapsed;
                MdToPdfBtn.IsEnabled = true;

                if (success && File.Exists(outputPdf))
                {
                    // Drop into clipboard via HandleDrop — same pattern as ConvertDocumentTask
                    var dataObj = new System.Windows.DataObject();
                    dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPdf });
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                    FlyShelf.Windows.ToastWindow.ShowToast($"Markdown → PDF exported! ✅ {Path.GetFileName(outputPdf)}");
                    mainWin?.ScrollClipboardToTop();
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("PDF export failed ❌");
                }
            }
            catch
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                MdToPdfBtn.IsEnabled = true;
            }
        }

        private static string FormatHtmlForClipboard(string html)
        {
            const string header = @"Version:0.9
StartHTML:{0:D8}
EndHTML:{1:D8}
StartFragment:{2:D8}
EndFragment:{3:D8}
";
            const string startFrag = "<!--StartFragment-->";
            const string endFrag = "<!--EndFragment-->";

            string content = $"<html><body>{startFrag}{html}{endFrag}</body></html>";
            int headerLen = string.Format(header, 0, 0, 0, 0).Length;
            int startHtml = headerLen;
            int startFragment = headerLen + $"<html><body>{startFrag}".Length;
            int endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
            int endHtml = startFragment + Encoding.UTF8.GetByteCount(html + endFrag + "</body></html>");

            return string.Format(header, startHtml, endHtml, startFragment, endFragment) + content;
        }
    }
}
