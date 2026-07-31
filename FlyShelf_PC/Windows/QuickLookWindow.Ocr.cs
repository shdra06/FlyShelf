// ═══════════════════════════════════════════════════════════════════════
// QuickLookWindow.Ocr.cs — OCR text detection, overlay rendering,
// drag-to-select, and clipboard copy functionality.
// Part of the QuickLookWindow partial class split.
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class QuickLookWindow : Window
    {
        // ═══════════════════════════════════════════════════════════
        // OCR STATE (only used by OCR methods)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Tracks which word overlays are currently "selected" (highlighted) for multi-word Ctrl+C copy.
        /// </summary>
        private readonly System.Collections.Generic.List<System.Windows.Controls.Border> _selectedWordBorders = new();
        private readonly System.Collections.Generic.List<string> _selectedWordTexts = new();

        // Drag-to-select state
        private bool _isDragSelecting = false;
        private bool _ocrCanvasEventsAttached = false;
        private Point _dragStartPoint;

        // Frozen brushes reused across render and drag-selection (initialized in RenderOcrOverlay)
        private System.Windows.Media.SolidColorBrush _ocrHoverBg;
        private System.Windows.Media.SolidColorBrush _ocrHoverBorder;
        private System.Windows.Media.SolidColorBrush _ocrSelectedBg;
        private System.Windows.Media.SolidColorBrush _ocrSelectedBorder;

        private async void OcrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null || string.IsNullOrEmpty(_item.FilePath) || !System.IO.File.Exists(_item.FilePath)) return;

            try
            {
                LoadingProgress.Visibility = Visibility.Visible;
                OcrBtn.IsEnabled = false;

                // Run OCR on background thread
                var ocrResultTuple = await System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        using (var stream = System.IO.File.OpenRead(_item.FilePath))
                        {
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                            // CRITICAL: Convert to Bgra8/Premultiplied — the OCR engine requires this
                            // pixel format for reliable recognition. Without conversion, many images
                            // return empty or garbled results.
                            if (softwareBitmap.BitmapPixelFormat != global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8 ||
                                softwareBitmap.BitmapAlphaMode != global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
                            {
                                var originalBitmap = softwareBitmap;
                                softwareBitmap = global::Windows.Graphics.Imaging.SoftwareBitmap.Convert(
                                    softwareBitmap,
                                    global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                                originalBitmap.Dispose();
                            }

                            // Store the actual OCR bitmap pixel dimensions for coordinate mapping.
                            uint ocrW = (uint)softwareBitmap.PixelWidth;
                            uint ocrH = (uint)softwareBitmap.PixelHeight;

                            // For small/medium images, upscale 3x for better OCR text detection.
                            // The OCR engine struggles with text smaller than ~12px.
                            if (Math.Max(ocrW, ocrH) < 2800)
                            {
                                global::Windows.Storage.Streams.InMemoryRandomAccessStream? inMemStream = null;
                                try
                                {
                                    uint newW = ocrW * 3;
                                    uint newH = ocrH * 3;
                                    // Cap at 4000px (OCR engine max)
                                    if (newW > 4000) { newW = 4000; newH = (uint)(ocrH * (4000.0 / ocrW)); }
                                    if (newH > 4000) { newH = 4000; newW = (uint)(ocrW * (4000.0 / ocrH)); }

                                    // Encode original → InMemoryStream with BitmapTransform scaling → Decode back
                                    inMemStream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                                    var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                                        global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
                                    encoder.SetSoftwareBitmap(softwareBitmap);
                                    encoder.BitmapTransform.ScaledWidth = newW;
                                    encoder.BitmapTransform.ScaledHeight = newH;
                                    encoder.BitmapTransform.InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
                                    await encoder.FlushAsync();

                                    inMemStream.Seek(0);
                                    var dec2 = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inMemStream);
                                    var scaledBitmap = await dec2.GetSoftwareBitmapAsync(
                                        global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                                        global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                                    var priorBitmap = softwareBitmap;
                                    softwareBitmap = scaledBitmap;
                                    priorBitmap.Dispose();
                                    ocrW = (uint)softwareBitmap.PixelWidth;
                                    ocrH = (uint)softwareBitmap.PixelHeight;
                                }
                                catch (Exception upscaleEx)
                                {
                                    FlyShelf.Classes.Logger.LogAction("OCR_UPSCALE", $"Upscale failed (using original): {upscaleEx.Message}");
                                    // Continue with original bitmap — upscale is best-effort
                                }
                                finally
                                {
                                    inMemStream?.Dispose();
                                }
                            }

                            // ── Multi-pass OCR with RESULT MERGING ──
                            // Runs OCR on all preprocessing variants and MERGES words from ALL results.
                            // This ensures text detected by ANY variant is included (e.g. header "61%"
                            // that only the Original or BradleyRoth variant can detect).
                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                            if (ocrEngine == null)
                            {
                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                            }

                            if (ocrEngine != null)
                            {
                                var variants = FlyShelf.Classes.OcrPreprocessor.CreateOcrVariants(softwareBitmap);
                                var allResults = new System.Collections.Generic.List<global::Windows.Media.Ocr.OcrResult>();

                                for (int v = 0; v < variants.Length; v++)
                                {
                                    try
                                    {
                                        var varResult = await ocrEngine.RecognizeAsync(variants[v].bitmap);
                                        if (varResult != null && varResult.Lines.Count > 0)
                                        {
                                            allResults.Add(varResult);
                                            int charCount = 0;
                                            foreach (var line in varResult.Lines)
                                                foreach (var word in line.Words)
                                                    charCount += word.Text.Length;
                                            FlyShelf.Classes.Logger.LogAction("OCR_MULTIPASS",
                                                $"QuickLook {variants[v].name}: {varResult.Lines.Count} lines, {charCount} chars");
                                        }
                                    }
                                    catch { } // Best-effort: failure is acceptable
                                    finally
                                    {
                                        variants[v].bitmap.Dispose();
                                    }
                                }

                                // Merge words from ALL results
                                var mergeResult = FlyShelf.Classes.OcrPreprocessor.MergeOcrResults(allResults);
                                return (mergeResult.words, mergeResult.mergedText, (double)ocrW, (double)ocrH);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FlyShelf.Classes.Logger.LogAction("QUICKLOOK_OCR_FAIL", ex.Message);
                    }
                    return (null, null, 0.0, 0.0);
                });

                var mergedWords = ocrResultTuple.Item1;
                var mergedText = ocrResultTuple.Item2;
                if (mergedWords != null && mergedWords.Count > 0 && !string.IsNullOrWhiteSpace(mergedText))
                {
                    _mergedOcrWords = mergedWords;
                    _mergedOcrText = mergedText;
                    _ocrResult = null; // Using merged results instead
                    _ocrBitmapWidth = ocrResultTuple.Item3;
                    _ocrBitmapHeight = ocrResultTuple.Item4;
                    OcrOverlayCanvas.Visibility = Visibility.Visible;
                    CopyAllOcrBtn.Visibility = Visibility.Visible;
                    RenderOcrOverlay();
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"OCR Complete! {mergedWords.Count} words detected. Select text to copy.");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("No text detected in image.");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("OCR Failed: " + ex.Message);
            }
            finally
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                OcrBtn.IsEnabled = true;
            }
        }

        private void CopyAllOcrButton_Click(object sender, RoutedEventArgs e)
        {
            if ((_mergedOcrText == null || string.IsNullOrWhiteSpace(_mergedOcrText)) &&
                (_ocrResult == null || string.IsNullOrWhiteSpace(_ocrResult.Text))) return;

            string textToCopy = _mergedOcrText ?? _ocrResult?.Text ?? "";
            try
            {
                if (ClipboardHelper.SafeSetTextAllowCapture(textToCopy))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("All Image Text Copied to Clipboard!");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed: " + ex.Message);
            }
        }

        private void CopyQrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item == null || string.IsNullOrWhiteSpace(_item.RawContent)) return;

            try
            {
                if (ClipboardHelper.SafeSetText(_item.RawContent))
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("QR Code Text Copied!");
                }
                else
                {
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed: " + ex.Message);
            }
        }

        private void RenderOcrOverlay()
        {
            if (_mergedOcrWords == null && _ocrResult == null) return;
            if (_originalWidth == 0 || _originalHeight == 0) return;

            OcrOverlayCanvas.Children.Clear();
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();

            // Calculate size in logical Device-Independent Pixels (DIPs) to match WPF's layout engine.
            // Sizing the canvas and the container grid in DIPs ensures pixel-perfect mapping at any DPI.
            double containerW = _originalWidth / _imageDpiX;
            double containerH = _originalHeight / _imageDpiY;

            if (ImageContainerGrid != null)
            {
                ImageContainerGrid.Width = containerW;
                ImageContainerGrid.Height = containerH;
            }
            OcrOverlayCanvas.Width = containerW;
            OcrOverlayCanvas.Height = containerH;

            // Calculate the upscale scale factor. _ocrBitmapWidth can be larger if OCR upscaling was active.
            double scaleX = _ocrBitmapWidth > 0 ? (_ocrBitmapWidth / _originalWidth) : 1.0;
            double scaleY = _ocrBitmapHeight > 0 ? (_ocrBitmapHeight / _originalHeight) : 1.0;

            // Brushes reused across all words — store as fields for drag-selection reuse
            _ocrHoverBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0x60, 0xA5, 0xFA));
            _ocrHoverBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x35, 0x60, 0xA5, 0xFA));
            _ocrSelectedBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0x60, 0xA5, 0xFA));
            _ocrSelectedBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0x60, 0xA5, 0xFA));
            _ocrHoverBg.Freeze(); _ocrHoverBorder.Freeze(); _ocrSelectedBg.Freeze(); _ocrSelectedBorder.Freeze();
            var hoverBg = _ocrHoverBg;
            var hoverBorder = _ocrHoverBorder;
            var selectedBg = _ocrSelectedBg;
            var selectedBorder = _ocrSelectedBorder;
            var transparentBrush = System.Windows.Media.Brushes.Transparent;

            // Render words from merged results (preferred) or legacy OcrResult
            if (_mergedOcrWords != null && _mergedOcrWords.Count > 0)
            {
                foreach (var word in _mergedOcrWords)
                {
                    var rect = word.BoundingRect;
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    string wordText = word.Text;

                    double scaledLeft = rect.X / (scaleX * _imageDpiX);
                    double scaledTop = rect.Y / (scaleY * _imageDpiY);
                    double scaledWidth = rect.Width / (scaleX * _imageDpiX);
                    double scaledHeight = rect.Height / (scaleY * _imageDpiY);

                    if (scaledWidth <= 0 || scaledHeight <= 0) continue;

                    // Add horizontal/vertical padding for smoother selection
                    double hPad = Math.Max(3, scaledWidth * 0.12);
                    double vPad = Math.Max(1, scaledHeight * 0.08);

                    // Word highlight border — the interactive overlay element
                    var wordBorder = new System.Windows.Controls.Border
                    {
                        Width = scaledWidth + hPad * 2,
                        Height = scaledHeight + vPad * 2,
                        Background = transparentBrush,
                        BorderBrush = transparentBrush,
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(2),
                        Cursor = System.Windows.Input.Cursors.IBeam,
                        ToolTip = wordText,
                        Focusable = true,
                        Tag = wordText // store text in Tag for easy retrieval
                    };

                    // --- Click to select + start drag-to-select ---
                    wordBorder.MouseLeftButtonDown += (s, ev) =>
                    {
                        var border = s as System.Windows.Controls.Border;
                        if (border == null) return;

                        // If Ctrl is NOT held, deselect all other words first
                        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                        if (!ctrlHeld)
                        {
                            DeselectAllOcrWords();
                        }

                        // Select this word
                        SelectWordBorder(border);
                        border.Focus();

                        // Start drag-to-select: capture mouse on the canvas, store start
                        _isDragSelecting = true;
                        _dragStartPoint = ev.GetPosition(OcrOverlayCanvas);
                        OcrOverlayCanvas.CaptureMouse();
                        ev.Handled = true;
                    };

                    // --- Hover effects (only when not selected) ---
                    wordBorder.MouseEnter += (s, ev) =>
                    {
                        var border = s as System.Windows.Controls.Border;
                        if (border != null && !_selectedWordBorders.Contains(border))
                        {
                            border.Background = hoverBg;
                            border.BorderBrush = hoverBorder;
                        }
                    };
                    wordBorder.MouseLeave += (s, ev) =>
                    {
                        var border = s as System.Windows.Controls.Border;
                        if (border != null && !_selectedWordBorders.Contains(border))
                        {
                            border.Background = transparentBrush;
                            border.BorderBrush = transparentBrush;
                        }
                    };

                    // --- Right-click context menu ---
                    var menu = new System.Windows.Controls.ContextMenu();

                    var copyWordItem = new System.Windows.Controls.MenuItem { Header = "Copy Word" };
                    copyWordItem.Click += (s, ev) =>
                    {
                        try
                        {
                            if (ClipboardHelper.SafeSetText(wordText))
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast($"Copied: {wordText}");
                            }
                        }
                        catch { } // Best-effort: failure is acceptable
                    };

                    var copySelectedItem = new System.Windows.Controls.MenuItem { Header = "Copy Selected Words" };
                    copySelectedItem.Click += (s, ev) => { CopySelectedOcrWords(); };

                    var copyLineItem = new System.Windows.Controls.MenuItem { Header = "Copy Full Line" };
                    copyLineItem.Click += (s, ev) =>
                    {
                        try
                        {
                            // In merged mode, copy all selected words as the "line"
                            string lineToCopy = _selectedWordTexts.Count > 0 
                                ? string.Join(" ", _selectedWordTexts) 
                                : wordText;
                            if (ClipboardHelper.SafeSetText(lineToCopy))
                            {
                                FlyShelf.Windows.ToastWindow.ShowToast("Copied full line");
                            }
                        }
                        catch { } // Best-effort: failure is acceptable
                    };

                    menu.Items.Add(copyWordItem);
                    menu.Items.Add(copySelectedItem);
                    menu.Items.Add(new System.Windows.Controls.Separator());
                    menu.Items.Add(copyLineItem);
                    wordBorder.ContextMenu = menu;

                    // Position on Canvas (offset by padding so the visible highlight centers on the word)
                    System.Windows.Controls.Canvas.SetLeft(wordBorder, scaledLeft - hPad);
                    System.Windows.Controls.Canvas.SetTop(wordBorder, scaledTop - vPad);
                    OcrOverlayCanvas.Children.Add(wordBorder);
                }
            }
            else if (_ocrResult != null)
            {
                // Legacy fallback: render from OcrResult (used when pre-loaded from ExtractText)
                foreach (var line in _ocrResult.Lines)
                {
                    if (line.Words == null || line.Words.Count == 0) continue;
                    string fullLineText = line.Text;

                    foreach (var word in line.Words)
                    {
                        var rect = word.BoundingRect;
                        if (rect.Width <= 0 || rect.Height <= 0) continue;

                        string wordText = word.Text;
                        double scaledLeft = rect.X / (scaleX * _imageDpiX);
                        double scaledTop = rect.Y / (scaleY * _imageDpiY);
                        double scaledWidth = rect.Width / (scaleX * _imageDpiX);
                        double scaledHeight = rect.Height / (scaleY * _imageDpiY);

                        if (scaledWidth <= 0 || scaledHeight <= 0) continue;
                        double hPad = Math.Max(3, scaledWidth * 0.12);
                        double vPad = Math.Max(1, scaledHeight * 0.08);

                        var wordBorder = new System.Windows.Controls.Border
                        {
                            Width = scaledWidth + hPad * 2,
                            Height = scaledHeight + vPad * 2,
                            Background = transparentBrush,
                            BorderBrush = transparentBrush,
                            BorderThickness = new Thickness(0),
                            CornerRadius = new CornerRadius(2),
                            Cursor = System.Windows.Input.Cursors.IBeam,
                            ToolTip = wordText,
                            Focusable = true,
                            Tag = wordText
                        };
                        wordBorder.MouseLeftButtonDown += (s, ev) =>
                        {
                            var border = s as System.Windows.Controls.Border;
                            if (border == null) return;
                            bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                            if (!ctrlHeld) DeselectAllOcrWords();
                            SelectWordBorder(border);
                            border.Focus();
                            _isDragSelecting = true;
                            _dragStartPoint = ev.GetPosition(OcrOverlayCanvas);
                            OcrOverlayCanvas.CaptureMouse();
                            ev.Handled = true;
                        };
                        wordBorder.MouseEnter += (s, ev) =>
                        {
                            var border = s as System.Windows.Controls.Border;
                            if (border != null && !_selectedWordBorders.Contains(border))
                            { border.Background = hoverBg; border.BorderBrush = hoverBorder; }
                        };
                        wordBorder.MouseLeave += (s, ev) =>
                        {
                            var border = s as System.Windows.Controls.Border;
                            if (border != null && !_selectedWordBorders.Contains(border))
                            { border.Background = transparentBrush; border.BorderBrush = transparentBrush; }
                        };
                        var menu = new System.Windows.Controls.ContextMenu();
                        var copyWordItem = new System.Windows.Controls.MenuItem { Header = "Copy Word" };
                        copyWordItem.Click += (s, ev) => { try { ClipboardHelper.SafeSetText(wordText); } catch { } /* Best-effort: failure is acceptable */ };
                        var copySelectedItem = new System.Windows.Controls.MenuItem { Header = "Copy Selected Words" };
                        copySelectedItem.Click += (s, ev) => { CopySelectedOcrWords(); };
                        var copyLineItem = new System.Windows.Controls.MenuItem { Header = "Copy Full Line" };
                        copyLineItem.Click += (s, ev) => { try { ClipboardHelper.SafeSetText(fullLineText); } catch { } /* Best-effort: failure is acceptable */ };
                        menu.Items.Add(copyWordItem);
                        menu.Items.Add(copySelectedItem);
                        menu.Items.Add(new System.Windows.Controls.Separator());
                        menu.Items.Add(copyLineItem);
                        wordBorder.ContextMenu = menu;
                        System.Windows.Controls.Canvas.SetLeft(wordBorder, scaledLeft - hPad);
                        System.Windows.Controls.Canvas.SetTop(wordBorder, scaledTop - vPad);
                        OcrOverlayCanvas.Children.Add(wordBorder);
                    }
                }
            }

            // Attach canvas-level mouse handlers for drag-to-select (only once)
            if (!_ocrCanvasEventsAttached)
            {
                _ocrCanvasEventsAttached = true;

                OcrOverlayCanvas.MouseMove += (s, ev) =>
                {
                    if (!_isDragSelecting || ev.LeftButton != MouseButtonState.Pressed) return;

                    // Rectangle-sweep selection: select all words whose bounds intersect
                    // the rectangle formed by drag start → current mouse position.
                    Point currentPt = ev.GetPosition(OcrOverlayCanvas);
                    Rect sweepRect = new Rect(_dragStartPoint, currentPt);

                    // Expand sweep vertically to be more forgiving (catch words on the same line)
                    double vExpand = 6;
                    sweepRect = new Rect(
                        sweepRect.Left, sweepRect.Top - vExpand,
                        sweepRect.Width, sweepRect.Height + vExpand * 2);

                    foreach (var child in OcrOverlayCanvas.Children)
                    {
                        if (child is System.Windows.Controls.Border border && border.Tag is string)
                        {
                            double bLeft = System.Windows.Controls.Canvas.GetLeft(border);
                            double bTop = System.Windows.Controls.Canvas.GetTop(border);
                            var bRect = new Rect(bLeft, bTop, border.Width, border.Height);

                            if (sweepRect.IntersectsWith(bRect))
                            {
                                SelectWordBorder(border);
                            }
                        }
                    }
                    ev.Handled = true;
                };

                OcrOverlayCanvas.MouseLeftButtonUp += (s, ev) =>
                {
                    if (_isDragSelecting)
                    {
                        _isDragSelecting = false;
                        OcrOverlayCanvas.ReleaseMouseCapture();
                        ev.Handled = true;
                    }
                };

                // Also allow starting drag from empty canvas space (between words)
                OcrOverlayCanvas.MouseLeftButtonDown += (s, ev) =>
                {
                    if (ev.OriginalSource == OcrOverlayCanvas)
                    {
                        DeselectAllOcrWords();
                        _isDragSelecting = true;
                        _dragStartPoint = ev.GetPosition(OcrOverlayCanvas);
                        OcrOverlayCanvas.CaptureMouse();
                        ev.Handled = true;
                    }
                };
            }
        }

        /// <summary>
        /// Copies all currently selected OCR words to the clipboard, joined by spaces.
        /// </summary>
        private void CopySelectedOcrWords()
        {
            if (_selectedWordTexts.Count == 0) return;
            try
            {
                string combined = string.Join(" ", _selectedWordTexts);
                FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Copying {_selectedWordTexts.Count} words: [{combined}]");
                
                if (ClipboardHelper.SafeSetTextAllowCapture(combined))
                {
                    // Verify clipboard was actually set
                    try
                    {
                        string verify = System.Windows.Clipboard.GetText();
                        FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Clipboard verified: [{verify}]");
                    }
                    catch { } // Best-effort: failure is acceptable
                    
                    FlyShelf.Windows.ToastWindow.ShowToast($"Copied {_selectedWordTexts.Count} word{(_selectedWordTexts.Count > 1 ? "s" : "")}");
                }
                else
                {
                    FlyShelf.Classes.Logger.LogAction("OCR_COPY", "SafeSetText returned FALSE — clipboard busy");
                    FlyShelf.Windows.ToastWindow.ShowToast("Clipboard busy — try again");
                }
            }
            catch (Exception ex)
            {
                FlyShelf.Classes.Logger.LogAction("OCR_COPY", $"Exception: {ex.Message}");
                FlyShelf.Windows.ToastWindow.ShowToast("Copy failed — try again");
            }
        }

        /// <summary>
        /// Selects a single word border (adds to selection if not already selected).
        /// </summary>
        private void SelectWordBorder(System.Windows.Controls.Border border)
        {
            if (border == null || _selectedWordBorders.Contains(border)) return;
            border.Background = _ocrSelectedBg;
            border.BorderBrush = _ocrSelectedBorder;
            border.BorderThickness = new Thickness(2); // increased to 2 for crisp Viewbox scaling
            _selectedWordBorders.Add(border);
            _selectedWordTexts.Add(border.Tag as string ?? "");
        }

        /// <summary>
        /// Deselects all currently selected OCR word overlays.
        /// </summary>
        private void DeselectAllOcrWords()
        {
            var transparent = System.Windows.Media.Brushes.Transparent;
            foreach (var border in _selectedWordBorders)
            {
                border.Background = transparent;
                border.BorderBrush = transparent;
                border.BorderThickness = new Thickness(0);
            }
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();
        }

        /// <summary>
        /// Selects all OCR word overlays on the canvas.
        /// </summary>
        private void SelectAllOcrWords()
        {
            _selectedWordBorders.Clear();
            _selectedWordTexts.Clear();

            foreach (var child in OcrOverlayCanvas.Children)
            {
                if (child is System.Windows.Controls.Border border && border.Tag is string text)
                {
                    border.Background = _ocrSelectedBg;
                    border.BorderBrush = _ocrSelectedBorder;
                    _selectedWordBorders.Add(border);
                    _selectedWordTexts.Add(text);
                }
            }

            if (_selectedWordBorders.Count > 0)
            {
                FlyShelf.Windows.ToastWindow.ShowToast($"Selected all {_selectedWordBorders.Count} words • Ctrl+C to copy");
            }
        }
    }
}
