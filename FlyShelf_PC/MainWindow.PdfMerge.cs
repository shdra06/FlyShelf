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
using System.IO;
using System.IO.Compression;
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
            try
            {
            if (OverflowPopup != null) OverflowPopup.IsOpen = false;
            // PERF: Single-pass categorization — avoids 3 separate LINQ chains each calling File.Exists (3N → N I/O calls)
            // File.Exists moved off UI thread to avoid blocking clicks
            var itemsSnapshot = _viewModel.DroppedItems.ToList();
            var (checkedPdfs, checkedDocs, checkedImages) = await System.Threading.Tasks.Task.Run(() =>
            {
                var pdfs = new List<ClipboardItem>();
                var docs = new List<ClipboardItem>();
                var images = new List<ClipboardItem>();
                foreach (var i in itemsSnapshot)
                {
                    if (!i.IsCheckedForMerge || string.IsNullOrEmpty(i.FilePath) || !System.IO.File.Exists(i.FilePath))
                        continue;
                    if (i.IsPdfPreview) pdfs.Add(i);
                    else if (i.IsDocPreview) docs.Add(i);
                    else if (i.ItemType == ClipboardItemType.Image) images.Add(i);
                }
                return (pdfs, docs, images);
            });

            var convertedPdfPaths = new List<string>();

            // Convert DOC/DOCX files to PDF first (Batch optimized)
            if (checkedDocs.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"📄 Converting {checkedDocs.Count} DOC file(s) to PDF...");
                string[] docs = checkedDocs.Select(d => d.FilePath).ToArray();
                string[] results = await ConversionUtils.ConvertDocsToPdfsAsync(docs);
                convertedPdfPaths.AddRange(results);

                if (results.Length < docs.Length)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ {docs.Length - results.Length} DOC files failed to convert.");
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
                        if (!_isClosed && this.IsLoaded && _isCurrentlySummoned)
                        {
                            this.Show();
                            this.Activate();
                        }
                    }
                    catch { } // Best-effort: failure is acceptable
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
                Classes.ClipboardHistoryManager.AppendToJournal(newItem);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                FlyShelf.Windows.ToastWindow.ShowToast("✅ PDF added to clipboard");
            }
            else
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Select 2+ files to merge, or 1 image/doc to convert.");
            }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("PDF_MERGE_ERROR", ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast($"Merge failed: {ex.Message}");
            }
        }

        private async void InstantMergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (OverflowPopup != null) OverflowPopup.IsOpen = false;
                var items = _viewModel.DroppedItems.Where(i => i.IsCheckedForMerge).ToList();
                if (items.Count < 2)
                {
                    // Fallback to normal click if somehow called for 1 item
                    MergeSelectedPdfsBtn_Click(sender, e);
                    return;
                }

                FlyShelf.Windows.ToastWindow.ShowToast($"⚡ Instant Merging {items.Count} files...");
                LoadingProgress.Visibility = Visibility.Visible;

                await System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var pdfPaths = new System.Collections.Generic.List<string>();
                        
                        // 1. Convert DOCs in Batch (Fastest)
                        var docItems = items.Where(i => i.IsDocPreview).ToList();
                        if (docItems.Count > 0)
                        {
                            string[] docs = docItems.Select(d => d.FilePath).ToArray();
                            string[] results = await ConversionUtils.ConvertDocsToPdfsAsync(docs);
                            pdfPaths.AddRange(results);
                        }

                        // 2. Handle Images & PDFs
                        foreach (var item in items)
                        {
                            if (item.IsPdfPreview) pdfPaths.Add(item.FilePath);
                            else if (item.ItemType == ClipboardItemType.Image)
                            {
                                string p = ConversionUtils.ConvertImageToPdf(item.FilePath);
                                if (!string.IsNullOrEmpty(p)) pdfPaths.Add(p);
                            }
                        }

                        if (pdfPaths.Count < 2) throw new Exception("Not enough files could be converted for merging.");

                        // Perform the merge using PDFSharp directly (Instant)
                        string outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FlyShelf", "Merged");
                        Directory.CreateDirectory(outputDir);
                        string outputPath = Path.Combine(outputDir, $"InstantMerge_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

                        using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                        {
                            foreach (var path in pdfPaths)
                            {
                                using (var inputDoc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                                {
                                    for (int i = 0; i < inputDoc.PageCount; i++)
                                    {
                                        outputDoc.AddPage(inputDoc.Pages[i]);
                                    }
                                }
                            }
                            outputDoc.Save(outputPath);
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            DismissMergeState();
                            var newItem = new ClipboardItem(outputPath);
                            _viewModel.DroppedItems.Insert(0, newItem);
                            Classes.ClipboardHistoryManager.AppendToJournal(newItem);
                            _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                            FlyShelf.Windows.ToastWindow.ShowToast("✅ Instant Merge complete!");
                        });
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast($"❌ Instant Merge failed: {ex.Message}"));
                    }
                });
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Error: {ex.Message}");
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
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

        private async void UpdatePdfMergeToolbar()
        {
            // PERF: Single-pass categorization — avoids 3 separate LINQ chains each calling File.Exists (3N → N I/O calls)
            // File.Exists moved off UI thread to avoid blocking clicks
            var itemsSnapshot = _viewModel.DroppedItems.ToList();
            var (checkedPdfs, checkedDocs, checkedImages) = await System.Threading.Tasks.Task.Run(() =>
            {
                var pdfs = new List<ClipboardItem>();
                var docs = new List<ClipboardItem>();
                var images = new List<ClipboardItem>();
                foreach (var i in itemsSnapshot)
                {
                    if (!i.IsCheckedForMerge || string.IsNullOrEmpty(i.FilePath) || !System.IO.File.Exists(i.FilePath))
                        continue;
                    if (i.IsPdfPreview) pdfs.Add(i);
                    else if (i.IsDocPreview) docs.Add(i);
                    else if (i.ItemType == ClipboardItemType.Image) images.Add(i);
                }
                return (pdfs, docs, images);
            });

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

                // Show Instant Merge only if 2+ items selected
                var instantVis = totalChecked >= 2 ? Visibility.Visible : Visibility.Collapsed;
                InstantMergeSelectedPdfsBtn.Visibility = instantVis;
                InstantMergePdfToolbarBtn.Visibility = instantVis;

                UpdateToolbarButtonsVisibility();
            }
            else
            {
                MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
                MergePdfToolbarBtn.Visibility = Visibility.Collapsed;
                InstantMergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
                InstantMergePdfToolbarBtn.Visibility = Visibility.Collapsed;
                UpdateToolbarButtonsVisibility();
            }
        }

        /// <summary>Hides the merge floating bar, restores emoji btn, and unchecks all PDFs.</summary>
        internal void DismissMergeState()
        {
            // PERF: Skip the expensive full-collection .Any() + foreach scans when merge toolbar
            // isn't visible. Called from CollectionChanged on every item add — with 5000 items,
            // the two O(n) scans below cost 10K evaluations per clipboard copy for zero benefit.
            bool wasVisible = MergeSelectedPdfsBtn.Visibility == Visibility.Visible ||
                              MergePdfToolbarBtn.Visibility == Visibility.Visible ||
                              InstantMergeSelectedPdfsBtn.Visibility == Visibility.Visible;

            MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
            MergePdfToolbarBtn.Visibility = Visibility.Collapsed;
            InstantMergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
            InstantMergePdfToolbarBtn.Visibility = Visibility.Collapsed;
            UpdateToolbarButtonsVisibility();

            // Uncheck all IsCheckedForMerge only if merge mode was actually active
            if (wasVisible && _viewModel.DroppedItems != null && _viewModel.DroppedItems.Any(i => i.IsCheckedForMerge))
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
                _ = System.Threading.Tasks.Task.Run(() => { try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://www.google.com/search?q={query}",
                    UseShellExecute = true
                });
                } catch { } });
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Search Error: {ex.Message}");
            }
        }

        private async void TranslateContextMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // The sender is a sub-MenuItem whose Tag is the target language string.
                // The ClipboardItem is stored in the parent MenuItem's Tag.
                string targetLanguage = null;
                ClipboardItem clipItem = null;

                if (sender is MenuItem subItem)
                {
                    targetLanguage = subItem.Tag as string;
                    if (subItem.Parent is MenuItem parentItem)
                    {
                        clipItem = parentItem.Tag as ClipboardItem;
                    }
                }

                if (clipItem == null || string.IsNullOrEmpty(clipItem.RawContent) || string.IsNullOrEmpty(targetLanguage))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No text content to translate.");
                    return;
                }

                // Check AI availability
                if (!AiProviderService.Instance.IsAvailable)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ Translate requires an AI API key");
                    return;
                }

                FlyShelf.Windows.ToastWindow.ShowToast($"🌐 Translating to {targetLanguage}...");

                string translated = await AiProviderService.Instance.TranslateAsync(clipItem.RawContent, targetLanguage);

                if (!string.IsNullOrWhiteSpace(translated))
                {
                    if (ClipboardHelper.SafeSetText(translated))
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"🌐 Translated to {targetLanguage} — copied to clipboard!");
                    }
                    else
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                    }
                    Logger.LogAction("TRANSLATE", $"Context menu: translated {clipItem.RawContent.Length} chars to {targetLanguage}");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Translation returned empty result.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("TRANSLATE", $"Context menu failed: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast($"Translation failed: {ex.Message}");
            }
        }


        private async void ConvertPdfToWord_Click(object sender, RoutedEventArgs e)
        {
#if MSIX_STORE
            await System.Threading.Tasks.Task.CompletedTask; // suppress async warning
            FlyShelf.Windows.ToastWindow.ShowToast("⚠️ PDF to Word conversion is not available in the Store version.");
#else
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
                        var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null)
                        {
                            if (!proc.WaitForExit(60000))
                            {
                                try { proc.Kill(); } catch { } // Best-effort: failure is acceptable
                            }
                            proc.Dispose();
                        }
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
                    _ = System.Threading.Tasks.Task.Run(() => { try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outputPath}\""); } catch { } });
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
#endif
        }

        private async void ReorderPdfPages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var clipItem = GetClipItemFromSender(sender);
                if (clipItem == null || string.IsNullOrEmpty(clipItem.FilePath) || !System.IO.File.Exists(clipItem.FilePath))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("⚠️ PDF file not found.");
                    return;
                }

                var mergeItem = new FlyShelf.Windows.PdfMergeItem(clipItem.FilePath);
                if (!mergeItem.IsValid)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"⚠️ Cannot read PDF: {mergeItem.Error}");
                    return;
                }

                var reorderWin = new FlyShelf.Windows.PageReorderWindow(mergeItem);
                WindowHelper.ShowDialogInForeground(reorderWin, this);

                if (reorderWin.WasConfirmed)
                {
                    // Save reordered PDF using PDFsharp
                    string dir = System.IO.Path.GetDirectoryName(clipItem.FilePath) ?? System.IO.Path.GetTempPath();
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(clipItem.FilePath);
                    string outputPath = System.IO.Path.Combine(dir, $"{baseName}_Reordered.pdf");

                    try
                    {
                        bool orderUnchanged = false;
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            if (reorderWin.HasExternalPages)
                            {
                                // Multi-source mode: pages come from multiple PDF files
                                var entries = reorderWin.GetFinalPageEntries();
                                var openDocs = new Dictionary<string, PdfSharp.Pdf.PdfDocument>();

                                try
                                {
                                    using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                                    {
                                        foreach (var entry in entries)
                                        {
                                            // Open each unique source file once (cached)
                                            if (!openDocs.TryGetValue(entry.SourceFile, out var srcDoc))
                                            {
                                                srcDoc = PdfSharp.Pdf.IO.PdfReader.Open(
                                                    entry.SourceFile, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                                                openDocs[entry.SourceFile] = srcDoc;
                                            }
                                            int pageIdx = entry.OriginalPage - 1; // Convert 1-indexed to 0-indexed
                                            if (pageIdx >= 0 && pageIdx < srcDoc.PageCount)
                                            {
                                                outputDoc.AddPage(srcDoc.Pages[pageIdx]);
                                                if (entry.RotationDegrees != 0)
                                                {
                                                    var addedPage = outputDoc.Pages[outputDoc.Pages.Count - 1];
                                                    addedPage.Rotate = (addedPage.Rotate + entry.RotationDegrees) % 360;
                                                }
                                            }
                                        }
                                        outputDoc.Save(outputPath);
                                    }
                                }
                                finally
                                {
                                    // Dispose all opened source documents
                                    foreach (var doc in openDocs.Values)
                                    {
                                        try { doc.Dispose(); } catch { } // Best-effort: failure is acceptable
                                    }
                                }
                            }
                            else
                            {
                                // Single-source mode: all pages from original file
                                var finalOrder = reorderWin.GetFinalPageOrder(); // 0-indexed page indices
                                var finalEntries = reorderWin.GetFinalPageEntries();

                                // Check if the order or rotation actually changed
                                bool orderChanged = false;
                                bool hasRotation = finalEntries.Any(e => e.RotationDegrees != 0);
                                if (finalOrder.Count != mergeItem.TotalPages)
                                    orderChanged = true;
                                else
                                {
                                    for (int i = 0; i < finalOrder.Count; i++)
                                    {
                                        if (finalOrder[i] != i) { orderChanged = true; break; }
                                    }
                                }

                                if (!orderChanged && !hasRotation)
                                {
                                    orderUnchanged = true;
                                    return;
                                }

                                using (var inputDoc = PdfSharp.Pdf.IO.PdfReader.Open(clipItem.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                                using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                                {
                                    for (int i = 0; i < finalOrder.Count; i++)
                                    {
                                        int pageIdx = finalOrder[i];
                                        if (pageIdx >= 0 && pageIdx < inputDoc.PageCount)
                                        {
                                            outputDoc.AddPage(inputDoc.Pages[pageIdx]);
                                            if (finalEntries[i].RotationDegrees != 0)
                                            {
                                                var addedPage = outputDoc.Pages[outputDoc.Pages.Count - 1];
                                                addedPage.Rotate = (addedPage.Rotate + finalEntries[i].RotationDegrees) % 360;
                                            }
                                        }
                                    }
                                    outputDoc.Save(outputPath);
                                }
                            }
                        });

                        if (orderUnchanged)
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast("📄 Page order unchanged.");
                            return;
                        }

                        // Open output location in Explorer
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{outputPath}\"",
                            UseShellExecute = true
                        });
                        FlyShelf.Windows.ToastWindow.ShowToast($"✅ Reordered PDF saved: {System.IO.Path.GetFileName(outputPath)}");
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"❌ Failed to save reordered PDF: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"❌ Reorder error: {ex.Message}");
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

        /// <summary>
        /// Renames the display name of a file-backed item in FlyShelf.
        /// The actual file on disk is NOT modified — only the in-app label changes.
        /// </summary>
        private void RenameItem_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null) return;

            // Extract current display name with extension
            string initialName = item.FileName ?? (string.IsNullOrEmpty(item.FilePath) ? "" : System.IO.Path.GetFileName(item.FilePath));

            // Build a simple inline rename dialog
            var dlg = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize
            };

            var outerBorder = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16, 18, 16),
                Background = (System.Windows.Media.Brush)FindResource("ThemeOverflowBg"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("ThemeOverflowBorder"),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 25, ShadowDepth = 4, Opacity = 0.25, Color = System.Windows.Media.Colors.Black
                }
            };

            var stack = new System.Windows.Controls.StackPanel();

            // Title
            var title = new System.Windows.Controls.TextBlock
            {
                Text = "Rename File",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("ThemeTextPrimary"),
                Margin = new Thickness(2, 0, 0, 8)
            };
            stack.Children.Add(title);

            // TextBox template overrides OS default styles to guarantee theming consistency
            var tbTemplate = new ControlTemplate(typeof(System.Windows.Controls.TextBox));
            var borderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            borderFactory.Name = "Border";
            borderFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.BackgroundProperty));
            borderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.BorderBrushProperty));
            borderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.BorderThicknessProperty));
            borderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.PaddingProperty));

            var scrollFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ScrollViewer));
            scrollFactory.Name = "PART_ContentHost";
            borderFactory.AppendChild(scrollFactory);
            tbTemplate.VisualTree = borderFactory;

            // TextBox
            var tb = new System.Windows.Controls.TextBox
            {
                Text = initialName,
                FontSize = 12.5,
                Padding = new Thickness(10, 8, 10, 8),
                Background = (System.Windows.Media.Brush)FindResource("ThemeOverlayBg"),
                Foreground = (System.Windows.Media.Brush)FindResource("ThemeTextPrimary"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("ThemeOverlayBorder"),
                BorderThickness = new Thickness(1),
                CaretBrush = (System.Windows.Media.Brush)FindResource("ThemeTextPrimary"),
                SelectionBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0xF5, 0x9E, 0x0B)),
                Template = tbTemplate
            };
            stack.Children.Add(tb);

            // File Path display
            if (!string.IsNullOrEmpty(item.FilePath))
            {
                var pathLabel = new System.Windows.Controls.TextBlock
                {
                    Text = item.FilePath,
                    FontSize = 10,
                    Foreground = (System.Windows.Media.Brush)FindResource("ThemeTextMuted"),
                    Margin = new Thickness(2, 6, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.FilePath
                };
                stack.Children.Add(pathLabel);
            }

            // Buttons row
            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            // Button ControlTemplate (eliminates standard Windows border/shading defaults)
            var btnTemplate = new ControlTemplate(typeof(System.Windows.Controls.Button));
            var btnBorderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            btnBorderFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            btnBorderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Button.BackgroundProperty));
            btnBorderFactory.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Button.BorderBrushProperty));
            btnBorderFactory.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Button.BorderThicknessProperty));
            btnBorderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Button.PaddingProperty));

            var contentFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            contentFactory.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            btnBorderFactory.AppendChild(contentFactory);
            btnTemplate.VisualTree = btnBorderFactory;

            var triggerHover = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
            triggerHover.Setters.Add(new Setter(System.Windows.Controls.Button.BackgroundProperty, (System.Windows.Media.Brush)FindResource("ThemeOverlayBgHover")));
            btnTemplate.Triggers.Add(triggerHover);

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Padding = new Thickness(16, 6, 16, 6),
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("ThemeTextSecondary"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("ThemeOverlayBorder"),
                BorderThickness = new Thickness(1),
                Template = btnTemplate
            };
            cancelBtn.Click += (_, __) => dlg.Close();

            var saveBtnTemplate = new ControlTemplate(typeof(System.Windows.Controls.Button));
            var saveBorderFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            saveBorderFactory.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            saveBorderFactory.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Button.BackgroundProperty));
            saveBorderFactory.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Button.PaddingProperty));
            
            var saveContentFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            saveContentFactory.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            saveContentFactory.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            saveBorderFactory.AppendChild(saveContentFactory);
            saveBtnTemplate.VisualTree = saveBorderFactory;

            var saveBtn = new System.Windows.Controls.Button
            {
                Content = "Save",
                Padding = new Thickness(20, 6, 20, 6),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = (System.Windows.Media.Brush)FindResource("ThemeAccentBg"),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                Template = saveBtnTemplate
            };

            bool saved = false;
            saveBtn.Click += (_, __) => { saved = true; dlg.Close(); };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;

            // Allow Enter to save, Escape to cancel
            dlg.PreviewKeyDown += (_, ke) =>
            {
                if (ke.Key == System.Windows.Input.Key.Enter) { saved = true; dlg.Close(); ke.Handled = true; }
                else if (ke.Key == System.Windows.Input.Key.Escape) { dlg.Close(); ke.Handled = true; }
            };

            // Allow dragging the dialog
            outerBorder.MouseLeftButtonDown += (_, me) => { try { dlg.DragMove(); } catch { } /* Best-effort: failure is acceptable */ };

            // Focus and select only the filename part (ignoring extension) on load
            dlg.ContentRendered += (_, __) =>
            {
                tb.Focus();
                string text = tb.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    int lastDot = text.LastIndexOf('.');
                    if (lastDot > 0 && lastDot < text.Length - 1)
                    {
                        tb.Select(0, lastDot);
                    }
                    else
                    {
                        tb.SelectAll();
                    }
                }
            };

            WindowHelper.ShowDialogInForeground(dlg, this);

            if (saved)
            {
                string newName = tb.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(newName))
                {
                    item.FileName = newName;
                    FlyShelf.Windows.ToastWindow.ShowToast($"Renamed to \"{newName}\" ✏️");
                }
            }
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
            item.FileName = (item.RawContent?.Length ?? 0) > 800 ? string.Concat(item.RawContent.AsSpan(0, 800), "...") : item.RawContent;
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
            WindowHelper.ShowDialogInForeground(win, this);
        }

        /// <summary>
        /// Temporarily reveals the password for 5 seconds in the card's FileName field,
        /// then restores the masked "Protected Password" label.
        /// </summary>
        private void PeekPasswordSpecific_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            var item = GetClipItemFromSender(sender);
            if (item == null || !item.IsPassword || string.IsNullOrEmpty(item.RawContent)) return;

            // Prevent double-peek — if already peeking, ignore
            string masked = item.FileName;
            string raw = item.RawContent;
            if (masked == raw) return; // Already showing

            // Reveal password
            item.FileName = raw;

            // Auto-hide after 5 seconds
            var savedLabel = masked;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(5000);
                if (Application.Current != null && !Application.Current.Dispatcher.HasShutdownStarted)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        // Only revert if still showing the raw password (user didn't change it)
                        if (item.FileName == raw && item.IsPassword)
                        {
                            item.FileName = savedLabel;
                        }
                    });
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // SPLIT PDF — Save each page as a separate PDF file
        // ═══════════════════════════════════════════════════════════════

        private void SplitPdf_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                    string outputDir = System.IO.Path.Combine(dir, $"{baseName}_pages");
                    System.IO.Directory.CreateDirectory(outputDir);

                    using (var inputDoc = PdfSharp.Pdf.IO.PdfReader.Open(item.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    {
                        int total = inputDoc.PageCount;
                        for (int i = 0; i < total; i++)
                        {
                            using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                            {
                                outputDoc.AddPage(inputDoc.Pages[i]);
                                string pagePath = System.IO.Path.Combine(outputDir, $"{baseName}_page{i + 1}.pdf");
                                outputDoc.Save(pagePath);
                            }
                        }

                        Dispatcher.InvokeAsync(() =>
                        {
                            FlyShelf.Windows.ToastWindow.ShowToast($"✂️ Split into {total} pages → {outputDir}");
                            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = outputDir, UseShellExecute = true }); } catch { }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast($"❌ Split failed: {ex.Message}"));
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // PASSWORD PROTECT PDF
        // ═══════════════════════════════════════════════════════════════

        private void PasswordProtectPdf_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            // Show simple password input dialog
            var dialog = new Window
            {
                Title = "Password Protect PDF",
                Width = 400, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = FindResource("ThemeWindowFallback") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Enter password to protect the PDF:", FontSize = 13, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var passwordBox = new PasswordBox { FontSize = 14, MaxLength = 50, Padding = new Thickness(8, 6, 8, 6) };
            Grid.SetRow(passwordBox, 1);
            grid.Children.Add(passwordBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
            cancelBtn.Click += (s, ev) => dialog.Close();
            var okBtn = new Button { Content = "Protect", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            string password = null;
            okBtn.Click += (s, ev) =>
            {
                password = passwordBox.Password;
                dialog.Close();
            };

            dialog.Content = grid;
            WindowHelper.ShowDialogInForeground(dialog, this);

            if (string.IsNullOrEmpty(password)) return;

            string pwd = password;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                    string outputPath = System.IO.Path.Combine(dir, $"{baseName}_protected.pdf");

                    using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(item.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                    {
                        foreach (var page in doc.Pages)
                            outputDoc.AddPage(page);

                        outputDoc.SecuritySettings.UserPassword = pwd;
                        outputDoc.SecuritySettings.OwnerPassword = pwd + "_owner";
                        outputDoc.Save(outputPath);
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"🔒 Protected PDF saved: {System.IO.Path.GetFileName(outputPath)}");
                        _viewModel.HandleDrop(new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { outputPath }), true);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast($"❌ Password protect failed: {ex.Message}"));
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // WATERMARK PDF — Overlay text on each page
        // ═══════════════════════════════════════════════════════════════

        private void WatermarkPdf_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            // Show watermark text input
            var dialog = new Window
            {
                Title = "Watermark PDF",
                Width = 400, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = FindResource("ThemeWindowFallback") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Enter watermark text:", FontSize = 13, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var textBox = new TextBox { FontSize = 14, Text = "CONFIDENTIAL", Padding = new Thickness(8, 6, 8, 6) };
            Grid.SetRow(textBox, 1);
            grid.Children.Add(textBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
            cancelBtn.Click += (s, ev) => dialog.Close();
            var okBtn = new Button { Content = "Apply Watermark", Padding = new Thickness(16, 6, 16, 6), FontWeight = FontWeights.SemiBold };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            string watermarkText = null;
            okBtn.Click += (s, ev) =>
            {
                watermarkText = textBox.Text;
                dialog.Close();
            };

            dialog.Content = grid;
            WindowHelper.ShowDialogInForeground(dialog, this);

            if (string.IsNullOrEmpty(watermarkText)) return;

            string wmText = watermarkText;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                    string outputPath = System.IO.Path.Combine(dir, $"{baseName}_watermarked.pdf");

                    using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(item.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
                    {
                        var font = new PdfSharp.Drawing.XFont("Arial", 48);
                        var brush = new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(40, 128, 128, 128));

                        foreach (PdfSharp.Pdf.PdfPage page in doc.Pages)
                        {
                            using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page, PdfSharp.Drawing.XGraphicsPdfPageOptions.Append))
                            {
                                var state = gfx.Save();
                                // Center the watermark diagonally
                                double cx = page.Width.Point / 2;
                                double cy = page.Height.Point / 2;
                                gfx.TranslateTransform(cx, cy);
                                gfx.RotateTransform(-45);
                                var size = gfx.MeasureString(wmText, font);
                                gfx.DrawString(wmText, font, brush,
                                    new PdfSharp.Drawing.XPoint(-size.Width / 2, size.Height / 2));
                                gfx.Restore(state);
                            }
                        }

                        doc.Save(outputPath);
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        FlyShelf.Windows.ToastWindow.ShowToast($"💧 Watermarked PDF saved: {System.IO.Path.GetFileName(outputPath)}");
                        _viewModel.HandleDrop(new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { outputPath }), true);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast($"❌ Watermark failed: {ex.Message}"));
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════
        // PDF → IMAGES — Export each page as PNG
        // ═══════════════════════════════════════════════════════════════

        private async void PdfToImages_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                string outputDir = System.IO.Path.Combine(dir, $"{baseName}_images");
                System.IO.Directory.CreateDirectory(outputDir);

                // [FIX BTN-20]: Offload byte I/O to background thread, yield UI between pages
                var storageFile = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(item.FilePath);
                var pdfDoc = await global::Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(storageFile);

                int total = (int)pdfDoc.PageCount;
                for (uint i = 0; i < pdfDoc.PageCount; i++)
                {
                    using (var page = pdfDoc.GetPage(i))
                    {
                        string pngPath = System.IO.Path.Combine(outputDir, $"{baseName}_page{i + 1}.png");
                        using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                        {
                            var options = new global::Windows.Data.Pdf.PdfPageRenderOptions();
                            options.DestinationWidth = (uint)(page.Size.Width * 2); // 2x for quality
                            await page.RenderToStreamAsync(stream, options);
                            stream.Seek(0);

                            // Move byte reading and file writing off the UI thread
                            uint size = (uint)stream.Size;
                            var reader = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
                            await reader.LoadAsync(size);
                            var buffer = new byte[size];
                            reader.ReadBytes(buffer);
                            await System.Threading.Tasks.Task.Run(() => System.IO.File.WriteAllBytes(pngPath, buffer));
                        }
                    }

                    // Yield to UI thread periodically so the app stays responsive
                    if ((i + 1) % 5 == 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                            FlyShelf.Windows.ToastWindow.ShowToast($"📝 Exported {i + 1}/{total} pages..."),
                            System.Windows.Threading.DispatcherPriority.Background);
                    }
                }

                FlyShelf.Windows.ToastWindow.ShowToast($"📝 Exported {total} pages as PNG → {outputDir}");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = outputDir, UseShellExecute = true }); } catch { }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"❌ PDF→Images failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PDF → TEXT — Extract text from text-based PDFs
        // ═══════════════════════════════════════════════════════════════

        private void PdfToText_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !System.IO.File.Exists(item.FilePath)) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder();

                    using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(item.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    {
                        foreach (PdfSharp.Pdf.PdfPage page in doc.Pages)
                        {
                            // PdfSharp 6.x: extract text from content streams
                            var content = page.Contents;
                            for (int i = 0; i < content.Elements.Count; i++)
                            {
                                var stream = content.Elements.GetObject(i) as PdfSharp.Pdf.PdfDictionary;
                                if (stream?.Stream?.Value != null)
                                {
                                    string raw = System.Text.Encoding.UTF8.GetString(stream.Stream.Value);
                                    // Extract text between Tj/TJ operators (simple text extraction)
                                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, @"\(([^)]*)\)\s*Tj"))
                                    {
                                        sb.Append(m.Groups[1].Value);
                                    }
                                    // Also handle TJ arrays
                                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, @"\(([^)]*)\)"))
                                    {
                                        if (raw.Contains("TJ", StringComparison.Ordinal))
                                        {
                                            // Already handled above in most cases
                                        }
                                    }
                                }
                            }
                            sb.AppendLine();
                        }
                    }

                    string text = sb.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast("⚠️ No extractable text found. Try OCR instead for scanned PDFs."));
                        return;
                    }

                    // Save as .txt file
                    string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                    string txtPath = System.IO.Path.Combine(dir, $"{baseName}.txt");
                    System.IO.File.WriteAllText(txtPath, text, System.Text.Encoding.UTF8);

                    // Also copy to clipboard
                    Dispatcher.InvokeAsync(() =>
                    {
                        try { ClipboardHelper.SafeSetText(text); } catch { }
                        FlyShelf.Windows.ToastWindow.ShowToast($"📝 Text extracted ({text.Length} chars) and copied to clipboard");
                        _viewModel.HandleDrop(new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { txtPath }), true);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => FlyShelf.Windows.ToastWindow.ShowToast($"❌ Text extraction failed: {ex.Message}"));
                }
            });
        }


        private void ConvertCsvToXlsx_Click(object sender, RoutedEventArgs e)
        {
            var item = GetClipItemFromSender(sender);
            item?.ConvertCsvToXlsx();
        }

        // ═══════════════════════════════════════════════════════════════
        // IMAGE FORMAT CONVERSION — Convert to PNG / JPG
        // ═══════════════════════════════════════════════════════════════

        private void ConvertImageToPng_Click(object sender, RoutedEventArgs e) => GetClipItemFromSender(sender)?.ConvertImageFormat("png");
        private void ConvertImageToJpg_Click(object sender, RoutedEventArgs e) => GetClipItemFromSender(sender)?.ConvertImageFormat("jpg");
    }
}
