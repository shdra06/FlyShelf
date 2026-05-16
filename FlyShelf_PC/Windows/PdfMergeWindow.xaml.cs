using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AdvanceClip.ViewModels;
using MicaWPF.Controls;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Microsoft.Win32;

namespace AdvanceClip.Windows
{
    // ═══════════════════════════════════════════════════════════════
    // Drag adorner — shows a ghost of the dragged card
    // ═══════════════════════════════════════════════════════════════
    public class DragAdorner : Adorner
    {
        private readonly VisualBrush _brush;
        private readonly Size _size;
        private Point _location;

        public DragAdorner(UIElement adornedElement, UIElement draggedElement, Point startPoint) : base(adornedElement)
        {
            _size = new Size(draggedElement.RenderSize.Width, draggedElement.RenderSize.Height);
            _brush = new VisualBrush(draggedElement)
            {
                Opacity = 0.8,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            _location = startPoint;
            IsHitTestVisible = false;
        }

        public void UpdatePosition(Point position)
        {
            _location = position;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(_brush, null,
                new Rect(new Point(_location.X - _size.Width / 2, _location.Y - 10), _size));
        }
    }

    public partial class PdfMergeWindow : MicaWindow
    {
        public ObservableCollection<PdfMergeItem> MergeItems { get; set; }
        private FlyShelfViewModel _viewModel;

        // Drag state
        private Point _dragStartPoint;
        private PdfMergeItem _draggedItem;
        private DragAdorner _dragAdorner;
        private bool _isDragging;
        private int _dragSourceIndex = -1;

        public PdfMergeWindow(List<ClipboardItem> pdfsToMerge, FlyShelfViewModel vm)
        {
            InitializeComponent();
            _viewModel = vm;
            MergeItems = new ObservableCollection<PdfMergeItem>(
                pdfsToMerge.Select(p => new PdfMergeItem(p.FilePath))
            );
            PdfItemsList.ItemsSource = MergeItems;
            OutputFileName.Text = $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}";
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int totalFiles = MergeItems.Count;
            int validFiles = MergeItems.Count(m => m.IsValid);
            int totalPages = MergeItems.Where(m => m.IsValid).Sum(m => m.GetSelectedPageIndices().Count);
            int totalAllPages = MergeItems.Where(m => m.IsValid).Sum(m => m.TotalPages);

            SummaryText.Text = totalPages == totalAllPages
                ? $"\ud83d\udcc4 {validFiles} PDFs \u2022 {totalPages} total pages to merge"
                : $"\ud83d\udcc4 {validFiles} PDFs \u2022 {totalPages} of {totalAllPages} pages selected";

            if (totalFiles > validFiles)
                SummaryText.Text += $" \u2022 \u26a0 {totalFiles - validFiles} unreadable";
        }

        // ═══════════════════════════════════════════════════════════════
        // DRAG-TO-REORDER with visual adorner + live reordering
        // ═══════════════════════════════════════════════════════════════

        private void PdfList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
            _draggedItem = listBoxItem?.DataContext as PdfMergeItem;
            _dragSourceIndex = _draggedItem != null ? MergeItems.IndexOf(_draggedItem) : -1;
        }

        private void PdfList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null || _isDragging) return;

            Point pos = e.GetPosition(null);
            Vector diff = _dragStartPoint - pos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;

                // Find the ListBoxItem visual to create adorner
                var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
                if (listBoxItem == null) { _isDragging = false; return; }

                // Dim the source item
                listBoxItem.Opacity = 0.3;

                // Create adorner
                var adornerLayer = AdornerLayer.GetAdornerLayer(PdfItemsList);
                if (adornerLayer != null)
                {
                    _dragAdorner = new DragAdorner(PdfItemsList, listBoxItem, e.GetPosition(PdfItemsList));
                    adornerLayer.Add(_dragAdorner);
                }

                // Start drag
                var data = new DataObject("PdfMergeItem", _draggedItem);
                DragDrop.DoDragDrop(PdfItemsList, data, DragDropEffects.Move);

                // Cleanup after drop
                CleanupDrag();
            }
        }

        private void PdfList_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PdfMergeItem"))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Move adorner
            if (_dragAdorner != null)
            {
                _dragAdorner.UpdatePosition(e.GetPosition(PdfItemsList));
            }

            // Live reorder — move the item as you drag
            var targetItem = GetItemAtPosition(e.GetPosition(PdfItemsList));
            if (targetItem != null && _draggedItem != null && targetItem != _draggedItem)
            {
                int oldIdx = MergeItems.IndexOf(_draggedItem);
                int newIdx = MergeItems.IndexOf(targetItem);
                if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
                {
                    MergeItems.Move(oldIdx, newIdx);
                }
            }
        }

        private void PdfList_Drop(object sender, DragEventArgs e)
        {
            // Drop is already handled by live reorder in DragOver
            e.Handled = true;
            CleanupDrag();
        }

        private void PdfList_DragLeave(object sender, DragEventArgs e)
        {
            // If mouse leaves the list area, keep the current order but cleanup visuals
        }

        private void PdfList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            CleanupDrag();
        }

        private void CleanupDrag()
        {
            // Remove adorner
            if (_dragAdorner != null)
            {
                var adornerLayer = AdornerLayer.GetAdornerLayer(PdfItemsList);
                adornerLayer?.Remove(_dragAdorner);
                _dragAdorner = null;
            }

            // Restore all item opacities
            for (int i = 0; i < PdfItemsList.Items.Count; i++)
            {
                var container = PdfItemsList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (container != null)
                    container.Opacity = 1.0;
            }

            _isDragging = false;
            _draggedItem = null;
            _dragSourceIndex = -1;
        }

        private PdfMergeItem GetItemAtPosition(Point pos)
        {
            var element = PdfItemsList.InputHitTest(pos) as DependencyObject;
            if (element == null) return null;
            var item = FindAncestor<ListBoxItem>(element);
            return item?.DataContext as PdfMergeItem;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ═══════════════════════════════════════════════════════════════
        // ACTIONS
        // ═══════════════════════════════════════════════════════════════

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfMergeItem item)
            {
                MergeItems.Remove(item);
                UpdateSummary();
            }
        }

        private void ReorderPages_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfMergeItem item)
            {
                if (!item.IsValid)
                {
                    MessageBox.Show($"Cannot open this PDF:\n{item.Error}", "PDF Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var reorderWin = new PageReorderWindow(item);
                reorderWin.Owner = this;
                reorderWin.ShowDialog();

                if (reorderWin.WasConfirmed)
                {
                    item.SetCustomPageOrder(reorderWin.GetFinalPageOrder());
                    PdfItemsList.Items.Refresh();
                    UpdateSummary();
                }
            }
        }

        private void RangeInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyRangeFromTextBox(sender as System.Windows.Controls.TextBox);
                e.Handled = true;
                System.Windows.Input.Keyboard.ClearFocus();
            }
        }

        private void RangeInput_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyRangeFromTextBox(sender as System.Windows.Controls.TextBox);
        }

        private void RangeInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Toggle placeholder visibility
            if (sender is System.Windows.Controls.TextBox tb && tb.Parent is Grid g)
            {
                foreach (var child in g.Children)
                {
                    if (child is System.Windows.Controls.TextBlock placeholder && !placeholder.IsHitTestVisible)
                    {
                        placeholder.Visibility = string.IsNullOrEmpty(tb.Text)
                            ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    }
                }
            }
        }

        private void RangeInput_Loaded(object sender, RoutedEventArgs e)
        {
            // Set initial text from current range
            if (sender is System.Windows.Controls.TextBox tb && tb.Tag is PdfMergeItem item)
            {
                string range = item.PageRangeText;
                tb.Text = range;
            }
        }

        private void ApplyRangeFromTextBox(System.Windows.Controls.TextBox textBox)
        {
            if (textBox == null) return;
            if (textBox.Tag is not PdfMergeItem item) return;

            string rangeText = textBox.Text?.Trim() ?? "";

            item.ClearCustomPageOrder();

            if (string.IsNullOrEmpty(rangeText))
            {
                item.SelectAll();
            }
            else
            {
                if (!item.SetPageRange(rangeText))
                {
                    textBox.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(239, 68, 68));
                    textBox.ToolTip = "Invalid range! Use: 1-5, 8, 10-12";
                    return;
                }
            }

            textBox.Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");
            textBox.ToolTip = "Page range (e.g. 1-5, 8, 10-12). Leave empty for all pages.";
            PdfItemsList.Items.Refresh();
            UpdateSummary();
        }

        private async void SaveSinglePdf_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not PdfMergeItem item) return;

            if (!item.IsValid)
            {
                MessageBox.Show($"Cannot save this PDF:\n{item.Error}", "PDF Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pageIndices = item.GetSelectedPageIndices();
            if (pageIndices.Count == 0)
            {
                ToastWindow.ShowToast("No pages selected to save.");
                return;
            }

            // If all pages selected and no reorder, just open the original
            if (pageIndices.Count == item.TotalPages && pageIndices.SequenceEqual(Enumerable.Range(0, item.TotalPages)))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
                return;
            }

            string saveDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Extracted");
            Directory.CreateDirectory(saveDir);
            string outputName = Path.GetFileNameWithoutExtension(item.FileName) + $"_pages.pdf";
            string outputPath = Path.Combine(saveDir, outputName);

            bool success = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (PdfDocument outputDoc = new PdfDocument())
                    {
                        using (PdfDocument inputDoc = PdfReader.Open(item.FilePath, PdfDocumentOpenMode.Import))
                        {
                            foreach (int idx in pageIndices)
                            {
                                if (idx >= 0 && idx < inputDoc.PageCount)
                                    outputDoc.AddPage(inputDoc.Pages[idx]);
                            }
                        }
                        outputDoc.Save(outputPath);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    AdvanceClip.Classes.Logger.LogAction("PDF SAVE", $"Error saving: {ex.Message}");
                    return false;
                }
            });

            if (success && File.Exists(outputPath))
            {
                ToastWindow.ShowToast($"✅ Saved {pageIndices.Count} pages → {outputName}");
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\"");
            }
            else
            {
                ToastWindow.ShowToast("❌ Failed to save PDF pages.");
            }
        }

        private void AddPdf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF Files|*.pdf",
                Multiselect = true,
                Title = "Add PDFs to Merge"
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string file in dlg.FileNames)
                {
                    MergeItems.Add(new PdfMergeItem(file));
                }
                UpdateSummary();
            }
        }

        private void ReverseAll_Click(object sender, RoutedEventArgs e)
        {
            if (MergeItems.Count < 2) return;
            var reversed = MergeItems.Reverse().ToList();
            MergeItems.Clear();
            foreach (var item in reversed)
                MergeItems.Add(item);
            UpdateSummary();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        // ═══════════════════════════════════════════════════════════════
        // MERGE — auto-saves to Downloads/FlyShelf/Merged
        // ═══════════════════════════════════════════════════════════════

        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            string baseName = GetOutputFileName();
            string mergeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyShelf", "Merged");
            Directory.CreateDirectory(mergeDir);
            string outputPath = Path.Combine(mergeDir, baseName);

            await DoMerge(outputPath);
        }

        // ═══════════════════════════════════════════════════════════════
        // SAVE AS — pick custom location
        // ═══════════════════════════════════════════════════════════════

        private async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var validItems = MergeItems.Where(m => m.IsValid).ToList();
            int totalSelectedPages = validItems.Sum(m => m.GetSelectedPageIndices().Count);
            if (totalSelectedPages < 1)
            {
                MessageBox.Show("No pages selected to merge.", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string baseName = GetOutputFileName();
            var saveDlg = new SaveFileDialog
            {
                Title = "Save Merged PDF",
                Filter = "PDF Files|*.pdf",
                FileName = baseName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads"
            };

            if (saveDlg.ShowDialog() != true) return;
            await DoMerge(saveDlg.FileName);
        }

        private string GetOutputFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(OutputFileName.Text)
                ? $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}"
                : OutputFileName.Text.Trim();
            if (!baseName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                baseName += ".pdf";
            return baseName;
        }

        // ═══════════════════════════════════════════════════════════════
        // CORE MERGE LOGIC (shared by Merge + Save As)
        // ═══════════════════════════════════════════════════════════════

        private async System.Threading.Tasks.Task DoMerge(string outputPath)
        {
            var validItems = MergeItems.Where(m => m.IsValid).ToList();
            int totalSelectedPages = validItems.Sum(m => m.GetSelectedPageIndices().Count);

            if (totalSelectedPages < 1)
            {
                MessageBox.Show("No pages selected to merge.", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MergeBtn.IsEnabled = false;
            SaveAsBtn.IsEnabled = false;
            MergeBtn.Content = "Merging...";

            bool success = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (PdfDocument outputDocument = new PdfDocument())
                    {
                        int mergedPages = 0;
                        var failedFiles = new List<string>();

                        foreach (var item in validItems)
                        {
                            try
                            {
                                var pageIndices = item.GetSelectedPageIndices();
                                if (pageIndices.Count == 0) continue;

                                using (PdfDocument inputDocument = PdfReader.Open(item.FilePath, PdfDocumentOpenMode.Import))
                                {
                                    foreach (int idx in pageIndices)
                                    {
                                        if (idx >= 0 && idx < inputDocument.PageCount)
                                        {
                                            PdfPage page = inputDocument.Pages[idx];
                                            outputDocument.AddPage(page);
                                            mergedPages++;
                                        }
                                    }
                                }
                            }
                            catch (Exception fileEx)
                            {
                                failedFiles.Add($"{item.FileName}: {fileEx.Message}");
                                AdvanceClip.Classes.Logger.LogAction("PDF MERGE", $"Skipped '{item.FileName}': {fileEx.Message}");
                            }
                        }

                        if (mergedPages == 0)
                        {
                            string allErrors = failedFiles.Count > 0
                                ? string.Join("\n", failedFiles)
                                : "No valid pages found.";
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show($"Could not merge:\n\n{allErrors}", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return false;
                        }

                        outputDocument.Save(outputPath);

                        if (failedFiles.Count > 0)
                        {
                            string skipped = string.Join("\n", failedFiles);
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show($"Merged {mergedPages} pages. Some files were skipped:\n\n{skipped}", "Partial Merge", MessageBoxButton.OK, MessageBoxImage.Warning);
                            });
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Error merging PDFs: {ex.Message}", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return false;
                }
            });

            if (success && File.Exists(outputPath))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dataObj = new DataObject();
                    dataObj.SetData(DataFormats.FileDrop, new string[] { outputPath });
                    _viewModel.HandleDrop(dataObj, true);
                    ToastWindow.ShowToast($"PDF Merged \u2192 {Path.GetFileName(outputPath)} \ud83d\udcc4");
                });
                this.Close();
            }
            else
            {
                MergeBtn.IsEnabled = true;
                SaveAsBtn.IsEnabled = true;
                MergeBtn.Content = "Merge PDFs";
            }
        }
    }
}
