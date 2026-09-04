// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Pdf.cs — PDF management: page editor grid, thumbnail
// rendering, reorder/rotate/delete pages, add external PDFs, save/export.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlyShelf.Classes;
using FlyShelf.Controls;
using WinPdf = global::Windows.Data.Pdf;
using global::Windows.Storage;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WpfUi = Wpf.Ui.Controls;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        // ═══════════════════════════════════════════════════════════
        // PDF MANAGEMENT LOGIC
        // ═══════════════════════════════════════════════════════════

        private async void PdfManage_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (!_isPdfMode) return;

            if (!_isPdfEditorMode)
            {
                _isPdfEditorMode = true;
                PdfAddBtn.Visibility = Visibility.Visible;
                PdfSaveBtn.Visibility = Visibility.Visible;
                PdfEditorGrid.Visibility = Visibility.Visible;
                WebPreview.Visibility = Visibility.Collapsed;
                PdfManageBtn.Appearance = WpfUi.ControlAppearance.Primary;
                PdfManageBtn.ToolTip = "Back to Browser View";

                if (_pdfPageEntries.Count == 0)
                {
                    await LoadPdfPagesAsync(_item.FilePath, true);
                }
                else
                {
                    RebuildPdfGrid();
                }
            }
            else
            {
                _isPdfEditorMode = false;
                WebPreview.Visibility = Visibility.Visible;
                PdfEditorGrid.Visibility = Visibility.Collapsed;
                PdfAddBtn.Visibility = Visibility.Collapsed;
                PdfSaveBtn.Visibility = Visibility.Collapsed;
                PdfManageBtn.Appearance = WpfUi.ControlAppearance.Secondary;
                PdfManageBtn.ToolTip = "Manage Pages (Reorder / Add)";
            }
            });
        }

        private async System.Threading.Tasks.Task LoadPdfPagesAsync(string path, bool isInitial = false)
        {
            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                var file = await StorageFile.GetFileFromPathAsync(path);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                
                string fileName = System.IO.Path.GetFileName(path);

                for (uint i = 0; i < pdfDoc.PageCount; i++)
                {
                    var entry = new PageEntry
                    {
                        OriginalPage = (int)i + 1,
                        SourceFile = path,
                        SourceLabel = isInitial ? "" : fileName,
                        IsExternal = !isInitial
                    };
                    _pdfPageEntries.Add(entry);

                    // Load thumbnail
                    using (var page = pdfDoc.GetPage(i))
                    using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        var options = new WinPdf.PdfPageRenderOptions
                        {
                            DestinationWidth = 200,
                            BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                        };
                        await page.RenderToStreamAsync(stream, options);
                        
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream.AsStream();
                        bitmap.EndInit();
                        bitmap.Freeze();

                        _pdfThumbnails[$"{path}:{i+1}"] = bitmap;
                    }
                }

                RebuildPdfGrid();
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"PDF Load Error: {ex.Message}");
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void RebuildPdfGrid()
        {
            PdfThumbnailPanel.Children.Clear();
            for (int i = 0; i < _pdfPageEntries.Count; i++)
            {
                var entry = _pdfPageEntries[i];
                var tile = new PdfPageTile
                {
                    PageIndex = i,
                    SourceFile = entry.SourceFile
                };

                string key = $"{entry.SourceFile}:{entry.OriginalPage}";
                if (_pdfThumbnails.TryGetValue(key, out var bmp))
                {
                    tile.SetThumbnail(bmp);
                }

                tile.SetPageInfo(i + 1, entry.SourceLabel, entry.RotationDegrees);
                
                tile.DeleteRequested += (s, idx) => {
                    _pdfPageEntries.RemoveAt(idx);
                    _isPdfModified = true;
                    RebuildPdfGrid();
                };

                tile.RotateRequested += (s, idx) => {
                    _pdfPageEntries[idx].RotationDegrees = (tile.Rotation);
                    _isPdfModified = true;
                };

                // Simple drag-and-drop support
                Point tileDragStart = default;
                tile.PreviewMouseLeftButtonDown += (s, e) => {
                    tileDragStart = e.GetPosition(null);
                };

                tile.MouseMove += (s, e) => {
                    if (e.LeftButton == MouseButtonState.Pressed && tile.ActionsOverlay.Visibility != Visibility.Visible)
                    {
                        Point currentPos = e.GetPosition(null);
                        Vector diff = tileDragStart - currentPos;
                        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                        {
                            try
                            {
                                DragDrop.DoDragDrop(tile, tile, DragDropEffects.Move);
                            }
                            catch (Exception ex)
                            {
                                Classes.Logger.LogAction("PDF_TILE_DRAG", $"DoDragDrop failed: {ex.Message}");
                            }
                        }
                    }
                };

                tile.Drop += (s, e) => {
                    try
                    {
                        if (e.Data.GetData(typeof(PdfPageTile)) is PdfPageTile sourceTile)
                        {
                            int oldIndex = sourceTile.PageIndex;
                            int newIndex = tile.PageIndex;
                            if (oldIndex != newIndex && oldIndex >= 0 && oldIndex < _pdfPageEntries.Count && newIndex >= 0 && newIndex <= _pdfPageEntries.Count)
                            {
                                var item = _pdfPageEntries[oldIndex];
                                _pdfPageEntries.RemoveAt(oldIndex);
                                if (newIndex > _pdfPageEntries.Count) newIndex = _pdfPageEntries.Count;
                                _pdfPageEntries.Insert(newIndex, item);
                                _isPdfModified = true;
                                RebuildPdfGrid();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("PDF_TILE_DROP", $"Drop failed: {ex.Message}");
                    }
                };
                tile.AllowDrop = true;

                PdfThumbnailPanel.Children.Add(tile);
            }
        }

        private async void PdfAdd_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PDF Files|*.pdf",
                Title = "Add Pages from PDF",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string file in dlg.FileNames)
                {
                    await LoadPdfPagesAsync(file, false);
                }
                _isPdfModified = true;
            }
            });
        }

        private void PdfSave_Click(object sender, RoutedEventArgs e)
        {
            if (PdfSaveBtn.ContextMenu != null)
            {
                PdfSaveBtn.ContextMenu.PlacementTarget = PdfSaveBtn;
                PdfSaveBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                PdfSaveBtn.ContextMenu.IsOpen = true;
            }
        }

        private async void PdfSaveOverwrite_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            await SavePdfChangesAsync(_item.FilePath);
            });
        }

        private async void PdfSaveAs_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            // Save directly to same directory as source — no file picker
            string sourceDir = Path.GetDirectoryName(_item.FilePath) ?? Path.GetTempPath();
            string baseName = Path.GetFileNameWithoutExtension(_item.FilePath) + $"_Edited_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string outputPath = Path.Combine(sourceDir, baseName);

            await SavePdfChangesAsync(outputPath);

            if (File.Exists(outputPath))
            {
                // Drop into clipboard via HandleDrop
                var dataObj = new System.Windows.DataObject();
                dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPath });
                var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                (mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                FlyShelf.Windows.ToastWindow.ShowToast($"PDF saved as copy {Path.GetFileName(outputPath)}");
                mainWin?.ScrollClipboardToTop();
            }
            });
        }

        private async void PdfExportImages_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_item?.FilePath) || !File.Exists(_item.FilePath)) return;

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                FlyShelf.Windows.ToastWindow.ShowToast("Exporting PDF pages to PNG images...");

                var images = await FlyShelf.Classes.Utils.PdfToImageExporter.ExportPagesToImagesAsync(_item.FilePath);

                if (images != null && images.Count > 0)
                {
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                    if (vm != null)
                    {
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, images.ToArray());
                        vm.HandleDrop(dataObj, true);
                        mainWin?.ScrollClipboardToTop();
                    }
                    FlyShelf.Windows.ToastWindow.ShowToast($"Exported {images.Count} page(s) to shelf! 🖼️");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No pages could be exported ❌");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Export failed: {ex.Message} ❌");
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void PdfCompress_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_item?.FilePath) || !File.Exists(_item.FilePath)) return;

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                FlyShelf.Windows.ToastWindow.ShowToast("Compressing & optimizing PDF...");

                var (outputPath, origSize, compSize) = await FlyShelf.Classes.Utils.PdfCompressor.CompressPdfAsync(_item.FilePath);

                if (File.Exists(outputPath))
                {
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                    if (vm != null)
                    {
                        var newItem = new FlyShelf.ViewModels.ClipboardItem(outputPath);
                        vm.DroppedItems.Insert(0, newItem);
                        FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(newItem);
                        mainWin?.ScrollClipboardToTop();
                    }

                    double origMb = origSize / (1024.0 * 1024.0);
                    double compMb = compSize / (1024.0 * 1024.0);
                    int pct = origSize > 0 ? (int)Math.Round((1.0 - (double)compSize / origSize) * 100.0) : 0;
                    FlyShelf.Windows.ToastWindow.ShowToast($"🎉 PDF Compressed: {origMb:F1}MB → {compMb:F1}MB ({pct}% smaller) ⚡");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Compression failed: {ex.Message} ❌");
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void PdfProtect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_item?.FilePath) || !File.Exists(_item.FilePath)) return;

            string password = PromptForPassword("Set PDF Encryption Password:");
            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    LoadingProgress.Visibility = Visibility.Visible;
                    FlyShelf.Windows.ToastWindow.ShowToast("Encrypting PDF with 128-bit AES...");

                    string protectedPath = await FlyShelf.Classes.Utils.PdfSecurityHelper.ProtectPdfAsync(_item.FilePath, password);
                    if (File.Exists(protectedPath))
                    {
                        var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                        var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                        if (vm != null)
                        {
                            var newItem = new FlyShelf.ViewModels.ClipboardItem(protectedPath);
                            vm.DroppedItems.Insert(0, newItem);
                            FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(newItem);
                            mainWin?.ScrollClipboardToTop();
                        }
                        FlyShelf.Windows.ToastWindow.ShowToast($"PDF Encrypted & Locked 🔒 {Path.GetFileName(protectedPath)}");
                    }
                }
                catch (Exception ex)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Encryption failed: {ex.Message} ❌");
                }
                finally
                {
                    LoadingProgress.Visibility = Visibility.Collapsed;
                }
            }
        }

        private string PromptForPassword(string title)
        {
            var dlg = new MicaWPF.Controls.MicaWindow
            {
                Title = "PDF Security",
                Width = 360,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x25))
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var pwdBox = new System.Windows.Controls.PasswordBox
            {
                FontSize = 14,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 15)
            };
            stack.Children.Add(pwdBox);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okBtn = new Wpf.Ui.Controls.Button
            {
                Content = "Protect PDF",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Margin = new Thickness(0, 0, 8, 0)
            };
            string result = null;
            okBtn.Click += (s, e) => { result = pwdBox.Password; dlg.DialogResult = true; dlg.Close(); };

            var cancelBtn = new Wpf.Ui.Controls.Button { Content = "Cancel", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary };
            cancelBtn.Click += (s, e) => { dlg.DialogResult = false; dlg.Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            stack.Children.Add(btnPanel);
            dlg.Content = stack;

            dlg.Loaded += (s, e) => pwdBox.Focus();
            pwdBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) okBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };

            return dlg.ShowDialog() == true ? result : null;
        }

        private async System.Threading.Tasks.Task SavePdfChangesAsync(string targetPath)
        {
            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                FlyShelf.Windows.ToastWindow.ShowToast("Saving PDF changes...");

                bool isOverwrite = string.Equals(targetPath, _item.FilePath, StringComparison.OrdinalIgnoreCase);

                // If overwriting, detach WebView2 from target file to release file locks
                if (isOverwrite && WebPreview != null)
                {
                    try { WebPreview.NavigateToString("<html><body style='background:#181825'></body></html>"); } catch { }
                    await System.Threading.Tasks.Task.Delay(100);
                }

                await System.Threading.Tasks.Task.Run(() =>
                {
                    using (var outDoc = new PdfDocument())
                    {
                        var sourceDocs = new Dictionary<string, PdfDocument>();

                        try
                        {
                            for (int i = 0; i < _pdfPageEntries.Count; i++)
                            {
                                var entry = _pdfPageEntries[i];
                                if (_pdfModifiedPages.TryGetValue(i, out var modImagePath))
                                {
                                    // Use modified doodled image page
                                    string pagePdf = ConversionUtils.ConvertImageToPdf(modImagePath);
                                    using (var tempDoc = PdfReader.Open(pagePdf, PdfDocumentOpenMode.Import))
                                    {
                                        var p = outDoc.AddPage(tempDoc.Pages[0]);
                                        if (entry.RotationDegrees != 0)
                                        {
                                            p.Rotate = (p.Rotate + entry.RotationDegrees) % 360;
                                        }
                                    }
                                }
                                else
                                {
                                    if (!sourceDocs.TryGetValue(entry.SourceFile, out var srcDoc))
                                    {
                                        srcDoc = PdfReader.Open(entry.SourceFile, PdfDocumentOpenMode.Import);
                                        sourceDocs[entry.SourceFile] = srcDoc;
                                    }

                                    var page = outDoc.AddPage(srcDoc.Pages[entry.OriginalPage - 1]);
                                    if (entry.RotationDegrees != 0)
                                    {
                                        page.Rotate = (page.Rotate + entry.RotationDegrees) % 360;
                                    }
                                }
                            }

                            string finalPath = targetPath;
                            if (isOverwrite)
                            {
                                finalPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");
                            }

                            outDoc.Save(finalPath);

                            if (isOverwrite)
                            {
                                // CRITICAL: Dispose all source documents BEFORE overwriting the original file
                                foreach (var doc in sourceDocs.Values) doc.Dispose();
                                sourceDocs.Clear();

                                System.IO.File.Copy(finalPath, targetPath, true);
                                try { System.IO.File.Delete(finalPath); } catch { }
                            }
                        }
                        finally
                        {
                            foreach (var doc in sourceDocs.Values) doc.Dispose();
                            sourceDocs.Clear();
                        }
                    }
                });

                FlyShelf.Windows.ToastWindow.ShowToast(isOverwrite ? "PDF overwritten successfully! 💾" : "PDF saved as new copy! 📄");
                _isPdfModified = false;
                _pdfThumbnails.Clear();
                
                if (!isOverwrite)
                {
                    var mainWin = System.Windows.Application.Current.MainWindow as FlyShelf.MainWindow;
                    var vm = mainWin?.DataContext as FlyShelf.ViewModels.FlyShelfViewModel;
                    if (vm != null)
                    {
                        var newItem = new FlyShelf.ViewModels.ClipboardItem(targetPath);
                        vm.DroppedItems.Insert(0, newItem);
                        FlyShelf.Classes.ClipboardHistoryManager.AppendToJournal(newItem);
                        mainWin?.ScrollClipboardToTop();
                    }
                }

                // Return to preview mode and display updated document
                _isPdfEditorMode = false;
                WebPreview.Visibility = Visibility.Visible;
                PdfEditorGrid.Visibility = Visibility.Collapsed;
                PdfAddBtn.Visibility = Visibility.Collapsed;
                PdfSaveBtn.Visibility = Visibility.Collapsed;
                PdfManageBtn.Appearance = WpfUi.ControlAppearance.Secondary;
                PdfManageBtn.ToolTip = "Manage Pages (Reorder / Rotate / Add)";

                WebPreview.Source = new Uri(targetPath);
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Save Failed: {ex.Message} ❌");
                FlyShelf.Classes.Logger.LogAction("PDF_SAVE_ERR", ex.ToString());
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double scale = e.NewValue;
            if ((_isPdfMode || _isDocxMode) && !_isPdfEditorMode && WebPreview != null && WebPreview.CoreWebView2 != null)
            {
                WebPreview.ZoomFactor = scale;
            }
            else if (_isPdfEditorMode && PdfThumbnailPanel != null)
            {
                foreach (UIElement child in PdfThumbnailPanel.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        fe.Width = 130 * scale;
                        fe.Height = 170 * scale;
                    }
                }
            }
        }

        /// <summary>
        /// Renders a PDF page to a BitmapImage and sets it as the PreviewImage source.
        /// Used for doodle mode on PDF pages.
        /// </summary>
        private async System.Threading.Tasks.Task RenderPdfPageToImage(int pageIndex)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(EffectivePdfPath);
                var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                if (pageIndex >= pdfDoc.PageCount) return;

                using (var page = pdfDoc.GetPage((uint)pageIndex))
                using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(stream);
                    
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 2048; // PERF: Cap resolution to prevent excessive memory for large/high-DPI PDFs
                    bitmap.StreamSource = stream.AsStream();
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    PreviewImage.Source = bitmap;
                    _isImageLoaded = true;
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"PDF Render Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Renders a PDF page to a PNG file at the given output path.
        /// Used for PDF page-to-image export.
        /// </summary>
        private async System.Threading.Tasks.Task RenderPdfPageToImage(int pageIndex, string outputPath)
        {
            await System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(EffectivePdfPath);
                    var pdfDoc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
                    using (var page = pdfDoc.GetPage((uint)pageIndex))
                    {
                        using (var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream())
                        {
                            await page.RenderToStreamAsync(stream);
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                            var storageFile = await StorageFile.GetFileFromPathAsync(outputPath);
                            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                                await storageFile.OpenAsync(FileAccessMode.ReadWrite));
                            encoder.SetSoftwareBitmap(softwareBitmap);
                            await encoder.FlushAsync();
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PDF Page Render Error: {ex.Message}"); }
            });
        }
    }
}
