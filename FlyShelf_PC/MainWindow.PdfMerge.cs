// ---------------------------------------------------------------
// MainWindow — PDF Merge, Convert & Smart Actions
// MergeSelectedPdfs, PdfMergeToggle, UpdatePdfMergeToolbar,
// DismissMergeState, GoogleSearch, ConvertPdfToWord
// Split from MainWindow.Interactions.cs for modularity
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using FlyShelf.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlyShelf
{
    public partial class MainWindow
    {
        private async void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            var checkedPdfs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            var checkedDocs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            var checkedImages = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            var convertedPdfPaths = new List<string>();

            // Convert DOC/DOCX files to PDF first
            if (checkedDocs.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"📄 Converting {checkedDocs.Count} DOC file(s) to PDF...");

                foreach (var doc in checkedDocs)
                {
                    string pdfPath = await ConversionUtils.ConvertDocToPdfAsync(doc.FilePath);
                    if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
                    {
                        convertedPdfPaths.Add(pdfPath);
                    }
                    else
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ Failed to convert: {doc.FileName}");
                    }
                }
            }

            // Convert Images to PDF next
            if (checkedImages.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"🖼️ Formatting {checkedImages.Count} image(s) to PDF...");

                foreach (var img in checkedImages)
                {
                    try
                    {
                        string pdfPath = await System.Threading.Tasks.Task.Run(() => ConversionUtils.ConvertImageToPdf(img.FilePath));
                        if (!string.IsNullOrEmpty(pdfPath) && System.IO.File.Exists(pdfPath))
                        {
                            convertedPdfPaths.Add(pdfPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ Failed to format: {img.FileName}");
                        FlyShelf.Classes.Logger.LogAction("IMAGE2PDF_ERR", ex.ToString());
                    }
                }
            }

            // If only DOCs/Images selected and no merge needed (only 1 output item)
            if (checkedPdfs.Count == 0 && checkedDocs.Count + checkedImages.Count == convertedPdfPaths.Count && convertedPdfPaths.Count == 1)
            {
                DismissMergeState();
                var newItem = new ClipboardItem(convertedPdfPaths[0]);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                FlyShelf.Windows.ToastWindow.ShowToast("✅ Converted to PDF");
                return;
            }

            // Build the final list of PDF items for the merge window
            var allPdfs = new List<ClipboardItem>();
            allPdfs.AddRange(checkedPdfs);

            // Add converted items as ClipboardItems
            foreach (string path in convertedPdfPaths)
            {
                allPdfs.Add(new ClipboardItem(path));
            }

            if (allPdfs.Count > 1)
            {
                DismissMergeState();
                var win = new FlyShelf.Windows.PdfMergeWindow(allPdfs, _viewModel);
                App.ActiveMergeWindow = win;
                win.Closed += (_, __) =>
                {
                    App.ActiveMergeWindow = null;
                    try
                    {
                        if (!_isClosed && this.IsLoaded)
                        {
                            this.Show();
                            this.Activate();
                        }
                    }
                    catch { }
                };
                win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
                win.Topmost = true;
                win.Show();
                win.Activate();
                win.Focus();
                win.Topmost = false;
                AnimateAndHide();
            }
            else if (allPdfs.Count == 1)
            {
                // Single converted PDF — just add to shelf
                DismissMergeState();
                var newItem = new ClipboardItem(allPdfs[0].FilePath);
                _viewModel.DroppedItems.Insert(0, newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                FlyShelf.Windows.ToastWindow.ShowToast("✅ PDF added to clipboard");
            }
            else
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Select 2+ files to merge, or 1 image/doc to convert.");
            }
        }

        private void PdfMergeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                item.IsCheckedForMerge = !item.IsCheckedForMerge;
                UpdatePdfMergeToolbar();
            }
        }

        private void UpdatePdfMergeToolbar()
        {
            var checkedPdfs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsPdfPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            var checkedDocs = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.IsDocPreview && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();
            var checkedImages = _viewModel.DroppedItems
                .Where(i => i.IsCheckedForMerge && i.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(i.FilePath) && System.IO.File.Exists(i.FilePath))
                .ToList();

            int totalChecked = checkedPdfs.Count + checkedDocs.Count + checkedImages.Count;

            if (totalChecked >= 2 || (checkedDocs.Count == 1 && checkedPdfs.Count == 0 && checkedImages.Count == 0))
            {
                if (checkedImages.Count > 0 && checkedPdfs.Count == 0 && checkedDocs.Count == 0)
                {
                    MergeSelectedPdfsBtn.Content = $"Merge {checkedImages.Count} Images";
                    MergePdfToolbarBtn.ToolTip = $"Merge {checkedImages.Count} images into a single PDF";
                }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0 && checkedImages.Count == 0 && checkedDocs.Count == 1)
                {
                    // Single DOC — show Convert to PDF
                    MergeSelectedPdfsBtn.Content = "Convert to PDF";
                    MergePdfToolbarBtn.ToolTip = "Convert DOC/DOCX to PDF";
                }
                else if (checkedDocs.Count > 0 && checkedPdfs.Count == 0)
                {
                    // Multiple DOCs — show Convert All
                    MergeSelectedPdfsBtn.Content = $"Convert {checkedDocs.Count} to PDF";
                    MergePdfToolbarBtn.ToolTip = $"Convert {checkedDocs.Count} DOC files to PDF";
                }
                else
                {
                    // Mixed
                    MergeSelectedPdfsBtn.Content = $"Merge {totalChecked} Files";
                    MergePdfToolbarBtn.ToolTip = $"Convert & merge all {totalChecked} files";
                }

                MergeSelectedPdfsBtn.Visibility = Visibility.Visible;
                MergePdfToolbarBtn.Visibility = Visibility.Visible;
                UpdateToolbarButtonsVisibility();
            }
            else
            {
                MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
                MergePdfToolbarBtn.Visibility = Visibility.Collapsed;
                UpdateToolbarButtonsVisibility();
            }
        }

        /// <summary>Hides the merge floating bar, restores emoji btn, and unchecks all PDFs.</summary>
        internal void DismissMergeState()
        {
            MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
            MergePdfToolbarBtn.Visibility = Visibility.Collapsed;
            UpdateToolbarButtonsVisibility();

            // Uncheck all IsCheckedForMerge (fast-path optimization to avoid triggering PropertyChanged notify loops)
            if (_viewModel.DroppedItems != null && _viewModel.DroppedItems.Any(i => i.IsCheckedForMerge))
            {
                foreach (var item in _viewModel.DroppedItems)
                {
                    if (item.IsCheckedForMerge) item.IsCheckedForMerge = false;
                }
            }
        }

        private ClipboardItem GetClipItemFromSender(object sender)
        {
            if (sender is System.Windows.FrameworkElement fe)
            {
                if (fe.Tag is ClipboardItem tagItem) return tagItem;
                if (fe.DataContext is ClipboardItem dcItem) return dcItem;
            }
            return null;
        }

        private void GoogleSearch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clipItem = GetClipItemFromSender(sender);
                if (clipItem == null || string.IsNullOrEmpty(clipItem.RawContent)) return;

                string query = Uri.EscapeDataString(clipItem.RawContent);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://www.google.com/search?q={query}",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Search Error: {ex.Message}");
            }
        }


        private async void ConvertPdfToWord_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clipItem = GetClipItemFromSender(sender);
                if (clipItem == null || string.IsNullOrEmpty(clipItem.FilePath)) return;

                FlyShelf.Windows.ToastWindow.ShowToast("📄 Converting PDF to Word...");

                string outputPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(clipItem.FilePath) ?? System.IO.Path.GetTempPath(),
                    System.IO.Path.GetFileNameWithoutExtension(clipItem.FilePath) + "_Converted.docx");

                await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // Use Word COM to open PDF and save as DOCX (Word 2013+ supports this natively)
                        // [SECURITY FIX]: Use -EncodedCommand (Base64) instead of inline -Command
                        // to prevent PowerShell injection via crafted filenames (CWE-78).
                        string script = $@"
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$doc = $word.Documents.Open('{clipItem.FilePath.Replace("'", "''")}')
$doc.SaveAs([ref]'{outputPath.Replace("'", "''")}', [ref]16)
$doc.Close()
$word.Quit()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
";
                        string encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy RemoteSigned -EncodedCommand {encodedScript}",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        System.Diagnostics.Process.Start(psi)?.WaitForExit(60000);
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("PDF2WORD", $"Conversion error: {ex.Message}");
                    }
                });

                if (System.IO.File.Exists(outputPath))
                {
                    // Add converted file to shelf
                    var newItem = new ClipboardItem(outputPath);
                    _viewModel.DroppedItems.Insert(0, newItem);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                    // Open containing folder with the file selected
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\"");
                    FlyShelf.Windows.ToastWindow.ShowToast($"✅ Converted: {System.IO.Path.GetFileName(outputPath)}");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("❌ Conversion failed — Microsoft Word required");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"❌ PDF to Word error: {ex.Message}");
            }
        }

        private void MarkAsPassword_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null) return;
            item.IsPassword = true;
            item.Extension = "PASSWORD";
            if (string.IsNullOrEmpty(item.FileName) || item.FileName == item.RawContent)
            {
                item.FileName = "Protected Password";
            }
            item.GeneratePasswordIcon();
            FlyShelf.Windows.ToastWindow.ShowToast("Locked as password card! 🔒");

            // Open the View/Edit dialog
            OpenPasswordManagerWindow(item, false);
        }

        private void ViewEditPassword_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null) return;
            OpenPasswordManagerWindow(item, false);
        }

        private void RenamePasswordLabel_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null) return;
            OpenPasswordManagerWindow(item, true);
        }

        private void RevertToText_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null) return;
            item.IsPassword = false;
            item.Extension = "TEXT";
            item.FileName = item.RawContent.Length > 800 ? item.RawContent.Substring(0, 800) + "..." : item.RawContent;
            item.Icon = null; // Reverts back to standard text template
            FlyShelf.Windows.ToastWindow.ShowToast("Reverted to normal text card! 📋");
        }

        private void RenamePasswordSpecific_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null) return;
            OpenPasswordManagerWindow(item, true);
        }

        private void OpenPasswordManagerWindow(ClipboardItem item, bool focusLabel)
        {
            var win = new FlyShelf.Windows.PasswordWindow(item, focusLabel);
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Topmost = true;
            win.ShowDialog();
        }
    }
}
