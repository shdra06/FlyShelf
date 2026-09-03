// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Doodle.cs — Doodle/drawing mode: InkCanvas annotations,
// undo/redo, color palette, eraser, save-to-image, and brush size control.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.Classes;
using FlyShelf.Helpers;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        // ═════════════════════════════════════════════════════════════
        // DOODLE / DRAWING MODE
        // ═════════════════════════════════════════════════════════════

        private async void DoodleButton_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (_isDoodleMode)
            {
                ExitDoodleMode();
            }
            else
            {
                if (_isPdfMode)
                {
                    // For PDF, we enter doodle mode by rendering the first page 
                    // (or the one they are looking at) to the ImageModeGrid
                    await RenderPdfPageToImage(0); // Default to first page for now
                    WebPreview.Visibility = Visibility.Collapsed;
                    PdfEditorGrid.Visibility = Visibility.Collapsed;
                    ImageModeGrid.Visibility = Visibility.Visible;
                }
                EnterDoodleMode();
            }
            });
        }

        private void EnterDoodleMode()
        {
            _isDoodleMode = true;

            // Size InkCanvas to match image container
            if (ImageContainerGrid != null)
            {
                DoodleCanvas.Width = ImageContainerGrid.Width;
                DoodleCanvas.Height = ImageContainerGrid.Height;
            }

            // Set default drawing attributes
            var da = new System.Windows.Ink.DrawingAttributes
            {
                Color = Colors.White,
                Width = 3,
                Height = 3,
                FitToCurve = true,
                StylusTip = System.Windows.Ink.StylusTip.Ellipse,
                IsHighlighter = false
            };
            DoodleCanvas.DefaultDrawingAttributes = da;

            // Show doodle UI
            DoodleCanvas.Visibility = Visibility.Visible;
            DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
            DoodleToolbar.Visibility = Visibility.Visible;
            DoodleUndoBtn.Visibility = Visibility.Visible;
            DoodleRedoBtn.Visibility = Visibility.Visible;

            // Update doodle button appearance to show it's active
            DoodleBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)); // green when active
            DoodleBtn.ToolTip = "Exit Doodle Mode";

            // Hide OCR overlay to prevent interaction conflicts
            if (OcrOverlayCanvas.Visibility == Visibility.Visible)
            {
                OcrOverlayCanvas.Visibility = Visibility.Collapsed;
            }

            // Set default color highlight
            _activeDoodleColorBorder = DoodleColorWhite;
            UpdateDoodleColorHighlight();

            // Populate color palette grid (7 columns × 5 rows = 35 colors)
            PopulateDoodlePalette();

            // Sync slider
            DoodleSizeSlider.Value = da.Width;
            DoodleSizeLabel.Text = ((int)da.Width).ToString(CultureInfo.InvariantCulture);

            UpdateDoodleButtonStates();

            FlyShelf.Classes.Logger.LogAction("DOODLE", "Entered doodle mode");
        }

        private void ExitDoodleMode()
        {
            _isDoodleMode = false;

            // Hide doodle UI
            DoodleCanvas.Visibility = Visibility.Collapsed;
            DoodleToolbar.Visibility = Visibility.Collapsed;
            DoodleUndoBtn.Visibility = Visibility.Collapsed;
            DoodleRedoBtn.Visibility = Visibility.Collapsed;
            DoodleSaveBtn.Visibility = Visibility.Collapsed;

            // Reset doodle button appearance
            DoodleBtn.Foreground = new SolidColorBrush(ThemeColors.VioletLight); // original purple
            DoodleBtn.ToolTip = "Draw / Annotate";

            // Restore OCR overlay if we had results
            if (_mergedOcrWords != null || _ocrResult != null)
            {
                OcrOverlayCanvas.Visibility = Visibility.Visible;
            }

            FlyShelf.Classes.Logger.LogAction("DOODLE", "Exited doodle mode");
        }

        private void DoodleCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            // Push to undo stack and clear redo (new stroke breaks redo chain)
            _doodleUndoStack.Push(e.Stroke);
            _doodleRedoStack.Clear();
            _hasUnsavedDoodle = true;
            UpdateDoodleButtonStates();
        }

        private void DoodleCanvas_StrokeErased(object sender, RoutedEventArgs e)
        {
            // When strokes are erased, we can't easily push them to undo,
            // but we mark as unsaved and clear redo
            _doodleRedoStack.Clear();
            _hasUnsavedDoodle = true;
            UpdateDoodleButtonStates();
        }

        private void DoodleUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_doodleUndoStack.Count == 0) return;

            var stroke = _doodleUndoStack.Pop();
            if (DoodleCanvas.Strokes.Remove(stroke))
            {
                _doodleRedoStack.Push(stroke);
            }
            _hasUnsavedDoodle = DoodleCanvas.Strokes.Count > 0;
            UpdateDoodleButtonStates();
        }

        private void DoodleRedo_Click(object sender, RoutedEventArgs e)
        {
            if (_doodleRedoStack.Count == 0) return;

            var stroke = _doodleRedoStack.Pop();
            DoodleCanvas.Strokes.Add(stroke);
            _doodleUndoStack.Push(stroke);
            _hasUnsavedDoodle = true;
            UpdateDoodleButtonStates();
        }

        private void UpdateDoodleButtonStates()
        {
            DoodleUndoBtn.IsEnabled = _doodleUndoStack.Count > 0;
            DoodleRedoBtn.IsEnabled = _doodleRedoStack.Count > 0;
            DoodleSaveBtn.Visibility = _hasUnsavedDoodle ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void DoodleSave_Click(object sender, RoutedEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            if (_isPdfMode)
            {
                try
                {
                    LoadingProgress.Visibility = Visibility.Visible;
                    // Flatten doodle to image
                    var rtb = new RenderTargetBitmap((int)ImageContainerGrid.Width, (int)ImageContainerGrid.Height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(ImageContainerGrid);
                    
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    
                    string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FlyShelf_PdfDoodle_{Guid.NewGuid()}.png");
                    if (!FlyShelf.Classes.DiskSpaceHelper.HasSufficientDiskSpace(tempPath, 10_000_000))
                    {
                        FlyShelf.Classes.Logger.LogAction("IMAGE_SAVE", "Insufficient disk space");
                        return;
                    }
                    using (var fs = System.IO.File.OpenWrite(tempPath))
                    {
                        encoder.Save(fs);
                    }
                    
                    // We assume doodle was on page 0 for now (the one loaded in RenderPdfPageToImage)
                    // In a full impl, we'd track WHICH page was being doodled.
                    _pdfModifiedPages[0] = tempPath;
                    _isPdfModified = true;
                    
                    FlyShelf.Windows.ToastWindow.ShowToast("Annotation applied to page");
                    ExitDoodleMode();
                }
                catch (Exception ex)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast($"Doodle Save Error: {ex.Message}");
                }
                finally
                {
                    LoadingProgress.Visibility = Visibility.Collapsed;
                }
                return;
            }
            if (!_isImageLoaded || string.IsNullOrEmpty(_item?.FilePath)) return;
            if (DoodleCanvas.Strokes.Count == 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("No strokes to save");
                return;
            }

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                DoodleSaveBtn.IsEnabled = false;

                string filePath = _item.FilePath;
                string ext = Path.GetExtension(filePath).ToLower(CultureInfo.InvariantCulture);

                // Capture strokes on UI thread before going to background
                var strokesCopy = new StrokeCollection(DoodleCanvas.Strokes);
                double canvasW = DoodleCanvas.Width;
                double canvasH = DoodleCanvas.Height;

                // Get the source bitmap for compositing
                var sourceImage = PreviewImage.Source as BitmapSource;
                if (sourceImage == null)
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Cannot save: no image loaded");
                    return;
                }

                // Render strokes onto the image at full resolution
                int pixelW = sourceImage.PixelWidth;
                int pixelH = sourceImage.PixelHeight;

                // Create a DrawingVisual that composites the original image + strokes
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    // Draw the original image
                    dc.DrawImage(sourceImage, new Rect(0, 0, pixelW, pixelH));

                    // Scale factor from canvas DIPs to image pixels
                    double scaleX = pixelW / canvasW;
                    double scaleY = pixelH / canvasH;

                    // Draw each stroke scaled to pixel coordinates
                    foreach (var stroke in strokesCopy)
                    {
                        // Create a scaled copy of the stroke
                        var points = stroke.StylusPoints;
                        var scaledPoints = new StylusPointCollection();
                        foreach (var pt in points)
                        {
                            scaledPoints.Add(new StylusPoint(pt.X * scaleX, pt.Y * scaleY, pt.PressureFactor));
                        }

                        var scaledAttrs = stroke.DrawingAttributes.Clone();
                        scaledAttrs.Width *= scaleX;
                        scaledAttrs.Height *= scaleY;

                        var scaledStroke = new Stroke(scaledPoints, scaledAttrs);
                        scaledStroke.Draw(dc);
                    }
                }

                var rtb2 = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
                rtb2.Render(dv);
                rtb2.Freeze();

                // Encode and save on background thread
                await System.Threading.Tasks.Task.Run(() =>
                {
                    BitmapEncoder encoder;
                    if (ext == ".png") encoder = new PngBitmapEncoder();
                    else if (ext == ".bmp") encoder = new BmpBitmapEncoder();
                    else encoder = new JpegBitmapEncoder { QualityLevel = 95 };

                    encoder.Frames.Add(BitmapFrame.Create(rtb2));

                    if (!FlyShelf.Classes.DiskSpaceHelper.HasSufficientDiskSpace(filePath, 10_000_000))
                    {
                        FlyShelf.Classes.Logger.LogAction("IMAGE_SAVE", "Insufficient disk space");
                        return;
                    }
                    using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                    {
                        encoder.Save(fs);
                    }
                });

                // Reload the saved image to show the baked result
                var freshBmp = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var _fi = new System.IO.FileInfo(filePath!);
                        if (_fi.Length > 100_000_000)
                        {
                            System.Diagnostics.Debug.WriteLine($"[QUICKLOOK] Skipped reload — file too large ({_fi.Length} bytes): {filePath}");
                            return null;
                        }
                        byte[] bytes = File.ReadAllBytes(filePath);
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(bytes))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.DecodePixelWidth = 2048;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        return bmp;
                    }
                    catch { return null; }
                });

                if (freshBmp != null)
                {
                    PreviewImage.Source = freshBmp;
                }

                // Clear strokes (they're now baked into the image)
                DoodleCanvas.Strokes.Clear();
                _doodleUndoStack.Clear();
                _doodleRedoStack.Clear();
                _hasUnsavedDoodle = false;
                UpdateDoodleButtonStates();

                // ── Auto-copy edited image to system clipboard ──
                try
                {
                    if (freshBmp != null)
                    {
                        Clipboard.SetImage(freshBmp);
                    }
                    else if (rtb2 != null)
                    {
                        // Fallback: use the rendered bitmap if disk reload failed
                        Clipboard.SetImage(rtb2);
                    }
                }
                catch (Exception copyEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DOODLE] Clipboard copy failed: {copyEx.Message}");
                }

                // ── Force-refresh thumbnail in clipboard list ──
                try
                {
                    _item?.ForceRefreshThumbnail();
                }
                catch (Exception thumbEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DOODLE] Thumbnail refresh failed: {thumbEx.Message}");
                }

                FlyShelf.Windows.ToastWindow.ShowToast("Annotated image saved & copied!");
                FlyShelf.Classes.Logger.LogAction("DOODLE", $"Saved annotated image: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("DOODLE", $"Save failed: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast("Save failed: " + ex.Message);
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                DoodleSaveBtn.IsEnabled = true;
            }
            });
        }

        private void DoodleColorPick_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border colorBorder && colorBorder.Tag is string hexColor)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hexColor);
                    DoodleCanvas.DefaultDrawingAttributes.Color = color;

                    // Update highlight
                    _activeDoodleColorBorder = colorBorder;
                    UpdateDoodleColorHighlight();

                    // Reset palette button to rainbow gradient (preset selected, not custom)
                    var rainbow = new LinearGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(ThemeColors.ErrorRed, 0),
                        new GradientStop(ThemeColors.WarningAmber, 0.25),
                        new GradientStop(ThemeColors.SuccessGreen, 0.5),
                        new GradientStop(ThemeColors.Blue500, 0.75),
                        new GradientStop(ThemeColors.VioletAccent, 1),
                    }, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
                    rainbow.Freeze();
                    DoodlePaletteBtn.Background = rainbow;

                    // Switch back to ink mode if in eraser mode
                    if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
                    {
                        DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                        DoodleEraserBtn.Background = Brushes.Transparent;
                        DoodleEraserLabel.Text = "Eraser";
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }
            e.Handled = true;
        }

        private void UpdateDoodleColorHighlight()
        {
            var accentBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xA7, 0x8B, 0xFA));
            accentBrush.Freeze();
            var transparentBrush = Brushes.Transparent;

            // Reset all color borders
            foreach (var cb in new[] { DoodleColorWhite, DoodleColorRed, DoodleColorYellow, DoodleColorGreen, DoodleColorBlue })
            {
                cb.BorderThickness = new Thickness(1);
                cb.BorderBrush = transparentBrush;
            }

            // Highlight active
            if (_activeDoodleColorBorder != null)
            {
                _activeDoodleColorBorder.BorderThickness = new Thickness(2);
                _activeDoodleColorBorder.BorderBrush = accentBrush;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // COLOR PALETTE — Full color grid with custom picker
        // ═══════════════════════════════════════════════════════════════

        private static readonly string[] _paletteColors = new[]
        {
            // Row 1: Pure + Warm tones
            "#FFFFFF", "#C0C0C0", "#808080", "#404040", "#000000", "#FFF1F2", "#FEF3C7",
            // Row 2: Reds
            "#FCA5A5", "#F87171", "#EF4444", "#DC2626", "#B91C1C", "#991B1B", "#7F1D1D",
            // Row 3: Oranges + Yellows
            "#FDBA74", "#FB923C", "#F59E0B", "#EAB308", "#CA8A04", "#A16207", "#854D0E",
            // Row 4: Greens + Teals
            "#86EFAC", "#4ADE80", "#22C55E", "#10B981", "#059669", "#047857", "#065F46",
            // Row 5: Blues + Purples
            "#93C5FD", "#60A5FA", "#3B82F6", "#6366F1", "#8B5CF6", "#A855F7", "#EC4899",
        };

        private void PopulateDoodlePalette()
        {
            if (DoodlePaletteGrid.Children.Count > 0) return; // Already populated

            foreach (string hex in _paletteColors)
            {
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(1.5),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = hex,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = hex == "#FFFFFF"
                        ? new SolidColorBrush(Color.FromArgb(0x40, 0x94, 0xA3, 0xB8))
                        : Brushes.Transparent,
                    ToolTip = hex,
                };

                // Hover effect
                swatch.MouseEnter += (s, _) =>
                {
                    if (s is Border b) b.RenderTransform = new System.Windows.Media.ScaleTransform(1.15, 1.15, 11, 11);
                };
                swatch.MouseLeave += (s, _) =>
                {
                    if (s is Border b) b.RenderTransform = null;
                };

                swatch.MouseLeftButtonDown += DoodlePaletteSwatch_Click;
                DoodlePaletteGrid.Children.Add(swatch);
            }
        }

        private void DoodlePalette_Click(object sender, MouseButtonEventArgs e)
        {
            DoodlePalettePopup.IsOpen = !DoodlePalettePopup.IsOpen;
            e.Handled = true;
        }

        private void DoodlePaletteSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border swatch && swatch.Tag is string hex)
            {
                ApplyDoodleColor(hex);
                DoodlePalettePopup.IsOpen = false;
            }
            e.Handled = true;
        }

        private void DoodleCustomColor_Click(object sender, MouseButtonEventArgs e)
        {
            DoodlePalettePopup.IsOpen = false;

            // WPF-native hex color input dialog
            var currentColor = DoodleCanvas.DefaultDrawingAttributes.Color;
            string currentHex = $"#{currentColor.R:X2}{currentColor.G:X2}{currentColor.B:X2}";

            var inputWin = new Window
            {
                Title = "Custom Color",
                Width = 320,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)),
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            var label = new TextBlock
            {
                Text = "Enter hex color (e.g. #FF5733):",
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(label);

            var inputRow = new StackPanel { Orientation = Orientation.Horizontal };
            var hexInput = new TextBox
            {
                Text = currentHex,
                Width = 160,
                FontSize = 14,
                FontFamily = new FontFamily("Consolas"),
                Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA7, 0x8B, 0xFA)),
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var previewSwatch = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 0, 0, 0),
                Background = new SolidColorBrush(currentColor),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
            };
            hexInput.TextChanged += (_, _) =>
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(hexInput.Text.Trim());
                    previewSwatch.Background = new SolidColorBrush(c);
                }
                catch { }
            };
            inputRow.Children.Add(hexInput);
            inputRow.Children.Add(previewSwatch);
            panel.Children.Add(inputRow);

            string result = null;
            var okBtn = new System.Windows.Controls.Button
            {
                Content = "Apply",
                Width = 80,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(0, 6, 0, 6),
                FontSize = 12,
            };
            okBtn.Click += (_, _) => { result = hexInput.Text.Trim(); inputWin.Close(); };
            panel.Children.Add(okBtn);

            inputWin.Content = panel;
            hexInput.SelectAll();
            hexInput.Focus();
            inputWin.ShowDialog();

            if (!string.IsNullOrEmpty(result))
            {
                try
                {
                    // Validate it parses
                    ColorConverter.ConvertFromString(result);
                    ApplyDoodleColor(result);
                }
                catch
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Invalid color — use format like #FF5733");
                }
            }
            e.Handled = true;
        }

        private void ApplyDoodleColor(string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                DoodleCanvas.DefaultDrawingAttributes.Color = color;

                // Clear preset highlight (custom color selected)
                _activeDoodleColorBorder = null;
                UpdateDoodleColorHighlight();

                // Update palette button to show the chosen color
                DoodlePaletteBtn.Background = new SolidColorBrush(color);

                // Switch back to ink mode if in eraser mode
                if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
                {
                    DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    DoodleEraserBtn.Background = Brushes.Transparent;
                    DoodleEraserLabel.Text = "Eraser";
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void DoodleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DoodleCanvas == null) return;
            int size = (int)e.NewValue;
            DoodleCanvas.DefaultDrawingAttributes.Width = size;
            DoodleCanvas.DefaultDrawingAttributes.Height = size;
            if (DoodleSizeLabel != null) DoodleSizeLabel.Text = size.ToString(CultureInfo.InvariantCulture);
        }

        private void DoodleEraser_Click(object sender, MouseButtonEventArgs e)
        {
            if (DoodleCanvas.EditingMode == InkCanvasEditingMode.EraseByStroke)
            {
                // Switch back to ink
                DoodleCanvas.EditingMode = InkCanvasEditingMode.Ink;
                DoodleEraserBtn.Background = Brushes.Transparent;
                DoodleEraserLabel.Text = "Eraser";
            }
            else
            {
                // Switch to eraser
                DoodleCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
                DoodleEraserBtn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xEF, 0x44, 0x44));
                DoodleEraserLabel.Text = "Drawing";
            }
            e.Handled = true;
        }

        private void DoodleClearAll_Click(object sender, MouseButtonEventArgs e)
        {
            if (DoodleCanvas.Strokes.Count == 0) return;

            // Push all current strokes to undo before clearing
            foreach (var stroke in DoodleCanvas.Strokes)
            {
                _doodleUndoStack.Push(stroke);
            }
            DoodleCanvas.Strokes.Clear();
            _doodleRedoStack.Clear();
            _hasUnsavedDoodle = false;
            UpdateDoodleButtonStates();

            FlyShelf.Windows.ToastWindow.ShowToast("All strokes cleared");
            e.Handled = true;
        }
    }
}
