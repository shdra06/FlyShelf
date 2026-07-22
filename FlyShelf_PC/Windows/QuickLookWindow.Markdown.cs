// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Markdown.cs — Markdown preview: WebView2 rendering,
// zoom factor control, and Markdown-to-PDF export.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Windows;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        // ═══ Markdown Preview: Zoom & PDF Export ═══

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

        private async void MarkdownToPdf_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_markdownRawContent)) return;

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

                bool success = await FlyShelf.Classes.WebView2Converter.ConvertMarkdownToPdfAsync(_markdownRawContent, outputPdf, _item?.FilePath);

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
    }
}
