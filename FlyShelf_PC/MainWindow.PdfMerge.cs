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
            try
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
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("PDF_MERGE_ERROR", ex.Message);
                FlyShelf.Windows.ToastWindow.ShowToast($"Merge failed: {ex.Message}");
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
                                try { proc.Kill(); } catch { }
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
#endif
        }

        private void ReorderPdfPages_Click(object sender, RoutedEventArgs e)
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
                reorderWin.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                reorderWin.Topmost = true;
                reorderWin.ShowDialog();

                if (reorderWin.WasConfirmed)
                {
                    // Save reordered PDF using PDFsharp
                    string dir = System.IO.Path.GetDirectoryName(clipItem.FilePath) ?? System.IO.Path.GetTempPath();
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(clipItem.FilePath);
                    string outputPath = System.IO.Path.Combine(dir, $"{baseName}_Reordered.pdf");

                    try
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
                                        if (!openDocs.ContainsKey(entry.SourceFile))
                                        {
                                            openDocs[entry.SourceFile] = PdfSharp.Pdf.IO.PdfReader.Open(
                                                entry.SourceFile, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                                        }

                                        var srcDoc = openDocs[entry.SourceFile];
                                        int pageIdx = entry.OriginalPage - 1; // Convert 1-indexed to 0-indexed
                                        if (pageIdx >= 0 && pageIdx < srcDoc.PageCount)
                                            outputDoc.AddPage(srcDoc.Pages[pageIdx]);
                                    }
                                    outputDoc.Save(outputPath);
                                }
                            }
                            finally
                            {
                                // Dispose all opened source documents
                                foreach (var doc in openDocs.Values)
                                {
                                    try { doc.Dispose(); } catch { }
                                }
                            }
                        }
                        else
                        {
                            // Single-source mode: all pages from original file
                            var finalOrder = reorderWin.GetFinalPageOrder(); // 0-indexed page indices

                            // Check if the order actually changed
                            bool orderChanged = false;
                            if (finalOrder.Count != mergeItem.TotalPages)
                                orderChanged = true;
                            else
                            {
                                for (int i = 0; i < finalOrder.Count; i++)
                                {
                                    if (finalOrder[i] != i) { orderChanged = true; break; }
                                }
                            }

                            if (!orderChanged)
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast("📄 Page order unchanged.");
                                return;
                            }

                            using (var inputDoc = PdfSharp.Pdf.IO.PdfReader.Open(clipItem.FilePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                            using (var outputDoc = new PdfSharp.Pdf.PdfDocument())
                            {
                                foreach (int pageIdx in finalOrder)
                                {
                                    if (pageIdx >= 0 && pageIdx < inputDoc.PageCount)
                                        outputDoc.AddPage(inputDoc.Pages[pageIdx]);
                                }
                                outputDoc.Save(outputPath);
                            }
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
            outerBorder.MouseLeftButtonDown += (_, me) => { try { dlg.DragMove(); } catch { } };

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

            dlg.ShowDialog();

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
            item.FileName = (item.RawContent?.Length ?? 0) > 800 ? item.RawContent.Substring(0, 800) + "..." : item.RawContent;
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
                    Dispatcher.InvokeAsync(() =>
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
    }
}
