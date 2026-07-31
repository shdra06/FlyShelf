using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyShelf.Classes;
using FlyShelf.Helpers;

namespace FlyShelf.Windows
{
    /// <summary>
    /// Sticky-note style expanded view for freeform notes.
    /// Uses a RichTextBox for real bold/italic/strikethrough formatting and inline images.
    /// Persists formatted content as XAML in FreeformSection.RichContent,
    /// while keeping plain-text Content in sync for backward compatibility.
    /// Images are stored on disk and tracked in FreeformSection.Images.
    /// </summary>
    public partial class NoteExpandWindow : Window
    {
        private readonly FreeformSection _section;
        private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
        private bool _isPinned = false;
        private bool _isDirty = false;
        private bool _isLoading = false;

        // Font size cycling
        private static readonly double[] _fontSizes = { 12, 13, 14, 16, 18, 20 };
        private int _fontSizeIndex = 1; // default 13

        public NoteExpandWindow(FreeformSection section, string dayLabel = "Note")
        {
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            this.Closed += (s, e) => FlyShelf.Classes.SmoothScrollFeature.Detach(this);
            _section = section ?? throw new ArgumentNullException(nameof(section));

            _saveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                SaveContent();
            };

            HeaderTitle.Text = dayLabel;
            LoadContent();
            LoadSectionImages();
            UpdateWordCount();

            // Reset dirty status after initial load
            _isDirty = false;
            _saveTimer.Stop();
            if (FooterStatus != null) FooterStatus.Text = "Ready";
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                NoteRichTextBox.Focus();
                NoteRichTextBox.CaretPosition = NoteRichTextBox.Document.ContentEnd;
            }
            catch { } // Best-effort: failure is acceptable
        }

        // ═══════════════════════════════════════════════════════════
        // LOAD / SAVE
        // ═══════════════════════════════════════════════════════════

        private void LoadContent()
        {
            _isLoading = true;
            try
            {
                // Try loading rich content first
                if (!string.IsNullOrEmpty(_section.RichContent))
                {
                    try
                    {
                        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(_section.RichContent));
                        var range = new TextRange(
                            NoteRichTextBox.Document.ContentStart,
                            NoteRichTextBox.Document.ContentEnd);
                        range.Load(ms, DataFormats.Xaml);
                        NoteRichTextBox.Document.PagePadding = new Thickness(0);
                        return;
                    }
                    catch { /* Fall through to plain text */ }
                }

                // Fallback: load plain text content
                NoteRichTextBox.Document = new FlowDocument(
                    new Paragraph(new Run(_section.Content ?? "")))
                {
                    PagePadding = new Thickness(0),
                    Foreground = new SolidColorBrush(ThemeColors.LightLavender),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13
                };
            }
            finally
            {
                _isLoading = false;
            }
        }

        /// <summary>Insert images from section.Images into the document at the top.</summary>
        private void LoadSectionImages()
        {
            if (_section.Images == null || _section.Images.Count == 0) return;

            _isLoading = true;
            try
            {
                Block firstBlock = NoteRichTextBox.Document.Blocks.FirstBlock;
                Block lastInserted = null;

                foreach (var img in _section.Images)
                {
                    if (!img.HasImage) continue;

                    try
                    {
                        var imageEl = CreateImageElement(img.ImagePath, img.DisplayWidth);
                        var imgParagraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                        imgParagraph.Inlines.Add(new InlineUIContainer(imageEl));

                        if (lastInserted != null)
                            NoteRichTextBox.Document.Blocks.InsertAfter(lastInserted, imgParagraph);
                        else if (firstBlock != null)
                            NoteRichTextBox.Document.Blocks.InsertBefore(firstBlock, imgParagraph);
                        else
                            NoteRichTextBox.Document.Blocks.Add(imgParagraph);

                        lastInserted = imgParagraph;
                    }
                    catch { } // Best-effort: failure is acceptable
                }
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void SaveContent()
        {
            if (!_isDirty) return;

            // ═══ FIX: Do NOT remove image blocks from the live FlowDocument ═══
            // Removing InlineUIContainer elements invalidates the TextContainer's character
            // offset map. When the TSF/IME subsystem requests layout (GetTextExt), it accesses
            // stale offsets causing Invariant.FailFast → app crash.
            // Instead, build a clean text-only FlowDocument for serialization.

            // Save formatted text as XAML (images excluded) using a temporary FlowDocument
            try
            {
                // Create a new FlowDocument with only text blocks (no images)
                var tempDoc = new FlowDocument();
                tempDoc.PagePadding = new Thickness(0);

                foreach (var block in NoteRichTextBox.Document.Blocks.ToList())
                {
                    if (block is Paragraph para)
                    {
                        // Skip paragraphs that contain ONLY an image (no text)
                        bool hasOnlyImage = para.Inlines.Count == 1
                            && para.Inlines.FirstInline is InlineUIContainer iuc
                            && iuc.Child is System.Windows.Controls.Image;
                        if (hasOnlyImage) continue;

                        // Clone text-only content from this paragraph
                        var clonedPara = new Paragraph { Margin = para.Margin };
                        foreach (var inline in para.Inlines)
                        {
                            if (inline is InlineUIContainer) continue; // skip images
                            // Clone text runs by copying their text range
                            var inlineRange = new TextRange(inline.ContentStart, inline.ContentEnd);
                            string text = inlineRange.Text;
                            if (!string.IsNullOrEmpty(text))
                            {
                                var run = new Run(text);
                                // Copy formatting
                                if (inline is Run srcRun)
                                {
                                    run.FontWeight = srcRun.FontWeight;
                                    run.FontStyle = srcRun.FontStyle;
                                    run.TextDecorations = srcRun.TextDecorations;
                                    run.FontFamily = srcRun.FontFamily;
                                    run.FontSize = srcRun.FontSize;
                                    run.Foreground = srcRun.Foreground;
                                }
                                clonedPara.Inlines.Add(run);
                            }
                        }
                        if (clonedPara.Inlines.Count > 0 || !string.IsNullOrEmpty(new TextRange(para.ContentStart, para.ContentEnd).Text.Trim()))
                            tempDoc.Blocks.Add(clonedPara);
                    }
                    else if (block is Section || block is List || block is Table || block is BlockUIContainer)
                    {
                        // For non-paragraph blocks, serialize via text range
                        var blockRange = new TextRange(block.ContentStart, block.ContentEnd);
                        string blockText = blockRange.Text;
                        if (!string.IsNullOrEmpty(blockText?.Trim()))
                        {
                            tempDoc.Blocks.Add(new Paragraph(new Run(blockText)));
                        }
                    }
                }

                // Serialize the temp document (no InlineUIContainers → safe to serialize)
                using var ms = new MemoryStream();
                var range = new TextRange(tempDoc.ContentStart, tempDoc.ContentEnd);
                range.Save(ms, DataFormats.Xaml);
                _section.RichContent = Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES_EXPAND", $"SaveContent XAML serialization failed: {ex.Message}");
            }

            // Sync plain text for backward compat (main window card)
            try
            {
                var textRange = new TextRange(
                    NoteRichTextBox.Document.ContentStart,
                    NoteRichTextBox.Document.ContentEnd);
                _section.Content = textRange.Text?.TrimEnd('\r', '\n') ?? "";
            }
            catch { } // Best-effort: failure is acceptable

            _isDirty = false;
            if (FooterStatus != null) FooterStatus.Text = "✓ Saved";
            try { NoteManager.SaveNow(); } catch { } // Best-effort: failure is acceptable
        }

        // ═══════════════════════════════════════════════════════════
        // IMAGE HELPERS
        // ═══════════════════════════════════════════════════════════

        /// <summary>Create an Image element for embedding in the FlowDocument.</summary>
        private System.Windows.Controls.Image CreateImageElement(string imagePath, double width = 200)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(imagePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = Math.Min((int)width * 2, 800); // Memory-efficient decode
            bmp.EndInit();
            bmp.Freeze();

            var image = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = Math.Min(width, 460),
                MaxWidth = 500,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 4, 8, 4),
                Cursor = Cursors.Hand,
                Tag = imagePath // store path for reference
            };

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            // Scroll-to-resize on the image
            image.MouseWheel += (s, ev) =>
            {
                if (s is System.Windows.Controls.Image img)
                {
                    double delta = ev.Delta > 0 ? 20 : -20;
                    img.Width = Math.Clamp(img.Width + delta, 60, 500);
                    // Sync DisplayWidth back
                    if (img.Tag is string p)
                    {
                        var fi = _section.Images.FirstOrDefault(i => i.ImagePath == p);
                        if (fi != null) fi.DisplayWidth = img.Width;
                    }
                    _isDirty = true;
                    ev.Handled = true;
                }
            };

            // Click to open in default viewer
            image.MouseLeftButtonDown += (s, ev) =>
            {
                if (s is System.Windows.Controls.Image img && img.Tag is string p)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
                    }
                    catch { } // Best-effort: failure is acceptable
                }
                ev.Handled = true;
            };

            return image;
        }

        /// <summary>Insert an image at the caret position in the RichTextBox.</summary>
        private void InsertImageAtCaret(string imagePath, double displayWidth = 200)
        {
            try
            {
                var image = CreateImageElement(imagePath, displayWidth);
                var imgParagraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                imgParagraph.Inlines.Add(new InlineUIContainer(image));

                var caret = NoteRichTextBox.CaretPosition;
                var currentPara = caret.Paragraph;

                if (currentPara != null)
                    NoteRichTextBox.Document.Blocks.InsertAfter(currentPara, imgParagraph);
                else
                    NoteRichTextBox.Document.Blocks.Add(imgParagraph);

                // Move caret after the image paragraph
                NoteRichTextBox.CaretPosition = imgParagraph.ContentEnd;
                _isDirty = true;
            }
            catch { } // Best-effort: failure is acceptable
        }

        /// <summary>Check if we can add another image to this section (tier limits).</summary>
        private bool CanAddImage()
        {
            int maxImages = LicenseManager.IsPro
                ? LicenseManager.PRO_NOTE_IMAGES_PER_CARD
                : LicenseManager.FREE_NOTE_IMAGES_PER_CARD;

            if (_section.Images.Count >= maxImages)
            {
                if (!LicenseManager.IsPro)
                    UpgradePrompt.ShowNoteImageLimit();
                else
                    ToastWindow.ShowToast($"Max {LicenseManager.PRO_NOTE_IMAGES_PER_CARD} images per card");
                return false;
            }
            return true;
        }

        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
        }

        // ═══════════════════════════════════════════════════════════
        // IMAGE PASTE HANDLER
        // ═══════════════════════════════════════════════════════════

        private async void NoteRichTextBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            await SafeAsyncHandler.RunAsync(async () =>
            {
            var dataObject = e.DataObject;
            if (dataObject == null) return;

            try
            {
                // Handle bitmap paste (screenshots, copied images)
                if (dataObject.GetDataPresent(DataFormats.Bitmap) ||
                    dataObject.GetDataPresent(typeof(BitmapSource)) ||
                    dataObject.GetDataPresent("DeviceIndependentBitmap"))
                {
                    // Check if there's also text (user might have copied formatted text WITH an image)
                    // Only intercept if there's NO text data — otherwise let default paste handle text
                    bool hasText = dataObject.GetDataPresent(DataFormats.UnicodeText) ||
                                   dataObject.GetDataPresent(DataFormats.Text);

                    // If clipboard has ONLY an image (no text), handle it as an image paste
                    if (!hasText)
                    {
                        e.CancelCommand(); // Prevent default paste

                        BitmapSource img = null;
                        if (dataObject.GetDataPresent(DataFormats.Bitmap))
                            img = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                        if (img == null && dataObject.GetDataPresent(typeof(BitmapSource)))
                            img = dataObject.GetData(typeof(BitmapSource)) as BitmapSource;
                        if (img == null)
                            img = Clipboard.GetImage();
                        if (img != null && img.CanFreeze) img.Freeze();

                        if (img != null)
                        {
                            if (!CanAddImage()) return;

                            string path = await NoteManager.SaveImage(img);
                            var freeformImg = new FreeformImage
                            {
                                ImagePath = path,
                                DisplayWidth = Math.Min(img.PixelWidth, 300)
                            };
                            _section.Images.Add(freeformImg);
                            InsertImageAtCaret(path, freeformImg.DisplayWidth);
                            NoteManager.MarkDirty();
                            FooterStatus.Text = "✓ Image pasted";
                        }
                    }
                }
                // Handle file drop (dragged image files)
                else if (dataObject.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                    if (files != null)
                    {
                        foreach (string f in files)
                        {
                            if (f != null && IsImageFile(f))
                            {
                                e.CancelCommand();

                                if (!CanAddImage()) break;

                                string destDir = NoteManager.GetImagesDirectory();
                                string destFile = Path.Combine(destDir,
                                    $"note_img_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}_{Path.GetFileName(f)}");
                                await Task.Run(() => File.Copy(f, destFile, overwrite: true));

                                var freeformImg = new FreeformImage
                                {
                                    ImagePath = destFile,
                                    DisplayWidth = 200
                                };
                                _section.Images.Add(freeformImg);
                                InsertImageAtCaret(destFile, 200);
                                NoteManager.MarkDirty();
                                FooterStatus.Text = "✓ Image added";
                                break; // one at a time
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NOTES_EXPAND", $"Paste error: {ex.Message}");
            }
            });
        }

        // ═══════════════════════════════════════════════════════════
        // TEXT CHANGE & CURSOR TRACKING
        // ═══════════════════════════════════════════════════════════

        private void NoteRichTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isLoading) return;
            _isDirty = true;
            UpdateWordCount();
            if (FooterStatus != null) FooterStatus.Text = "Editing...";
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        private void NoteRichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateCursorPosition();
        }

        private void UpdateWordCount()
        {
            if (WordCountBadge == null || CharCountLabel == null) return;
            try
            {
                var textRange = new TextRange(
                    NoteRichTextBox.Document.ContentStart,
                    NoteRichTextBox.Document.ContentEnd);
                var text = textRange.Text ?? "";
                var charCount = text.TrimEnd('\r', '\n').Length;
                var wordCount = string.IsNullOrWhiteSpace(text)
                    ? 0
                    : text.Split(' ', '\n', '\r', '\t').Length;

                WordCountBadge.Text = $"{wordCount} word{(wordCount == 1 ? "" : "s")}";
                CharCountLabel.Text = $"{charCount} char{(charCount == 1 ? "" : "s")}";
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void UpdateCursorPosition()
        {
            if (CursorPosLabel == null) return;
            try
            {
                var caretPos = NoteRichTextBox.CaretPosition;
                var docStart = NoteRichTextBox.Document.ContentStart;
                string textBefore = new TextRange(docStart, caretPos).Text;
                int line = textBefore.Count(c => c == '\n') + 1;
                int lastNewline = textBefore.LastIndexOf('\n');
                int col = (lastNewline < 0 ? textBefore.Length : textBefore.Length - lastNewline - 1) + 1;
                CursorPosLabel.Text = $"Ln {line}, Col {col}";
            }
            catch { CursorPosLabel.Text = ""; }
        }

        // ═══════════════════════════════════════════════════════════
        // FORMAT TOOLBAR — REAL INLINE FORMATTING
        // ═══════════════════════════════════════════════════════════

        private void BoldBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.ToggleBold.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void ItalicBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.ToggleItalic.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void StrikeBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ToggleStrikethrough();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void BulletBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.ToggleBullets.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void CheckboxBtn_Click(object sender, MouseButtonEventArgs e)
        {
            InsertOrToggleCheckbox();
            e.Handled = true;
        }

        private void DividerBtn_Click(object sender, MouseButtonEventArgs e)
        {
            var caret = NoteRichTextBox.CaretPosition;
            var currentPara = caret?.Paragraph;
            var divider = new Paragraph()
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x55)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 8, 0, 8),
                FontSize = 1
            };
            if (currentPara != null)
                NoteRichTextBox.Document.Blocks.InsertAfter(currentPara, divider);
            else
                NoteRichTextBox.Document.Blocks.Add(divider);
            NoteRichTextBox.CaretPosition = divider.ContentEnd;
            NoteRichTextBox.Focus();
            _isDirty = true;
            e.Handled = true;
        }

        private void TimestampBtn_Click(object sender, MouseButtonEventArgs e)
        {
            InsertText($"[{DateTime.Now:hh:mm tt}] ");
            e.Handled = true;
        }

        private void FontSizeBtn_Click(object sender, MouseButtonEventArgs e)
        {
            CycleFontSize();
            e.Handled = true;
        }

        // ─── NEW TOOLBAR HANDLERS ───

        private void UnderlineBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.ToggleUnderline.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void HeadingBtn_Click(object sender, MouseButtonEventArgs e)
        {
            CycleHeading();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void NumberedListBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.ToggleNumbering.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void IndentBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.IncreaseIndentation.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void OutdentBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.DecreaseIndentation.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void AlignLeftBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.AlignLeft.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void AlignCenterBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.AlignCenter.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void AlignRightBtn_Click(object sender, MouseButtonEventArgs e)
        {
            EditingCommands.AlignRight.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void LinkBtn_Click(object sender, MouseButtonEventArgs e)
        {
            InsertHyperlink();
            e.Handled = true;
        }

        private void QuoteBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ToggleBlockquote();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void CodeBlockBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ToggleCodeBlock();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void TextColorBtn_Click(object sender, MouseButtonEventArgs e)
        {
            CycleTextColor();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void HighlightBtn_Click(object sender, MouseButtonEventArgs e)
        {
            CycleHighlightColor();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void ClearFormatBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ClearFormatting();
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void UndoBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ApplicationCommands.Undo.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        private void RedoBtn_Click(object sender, MouseButtonEventArgs e)
        {
            ApplicationCommands.Redo.Execute(null, NoteRichTextBox);
            NoteRichTextBox.Focus();
            e.Handled = true;
        }

        // ═══════════════════════════════════════════════════════════
        // FORMAT HELPERS
        // ═══════════════════════════════════════════════════════════

        private void ToggleStrikethrough()
        {
            var selection = NoteRichTextBox.Selection;
            if (selection.IsEmpty) return;

            var currentDecor = selection.GetPropertyValue(Inline.TextDecorationsProperty);
            bool hasStrike = currentDecor is TextDecorationCollection tdc
                && tdc.Any(td => td.Location == TextDecorationLocation.Strikethrough);

            selection.ApplyPropertyValue(
                Inline.TextDecorationsProperty,
                hasStrike ? new TextDecorationCollection() : TextDecorations.Strikethrough);
        }

        private void InsertText(string text)
        {
            NoteRichTextBox.Selection.Text = text;
            NoteRichTextBox.CaretPosition = NoteRichTextBox.Selection.End;
            NoteRichTextBox.Focus();
        }

        private void InsertOrToggleCheckbox()
        {
            try
            {
                var para = NoteRichTextBox.CaretPosition?.Paragraph;
                if (para != null)
                {
                    string paraText = new TextRange(para.ContentStart, para.ContentEnd).Text;
                    if (paraText.StartsWith('☐') || paraText.StartsWith('☑'))
                    {
                        var pos = para.ContentStart;
                        while (pos != null && pos.CompareTo(para.ContentEnd) < 0)
                        {
                            if (pos.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                            {
                                var nextPos = pos.GetPositionAtOffset(1);
                                if (nextPos != null)
                                {
                                    string ch = new TextRange(pos, nextPos).Text;
                                    if (ch == "☐") { new TextRange(pos, nextPos).Text = "☑"; return; }
                                    if (ch == "☑") { new TextRange(pos, nextPos).Text = "☐"; return; }
                                }
                                break;
                            }
                            pos = pos.GetNextContextPosition(LogicalDirection.Forward);
                        }
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable

            InsertText("☐");
        }

        private void CycleFontSize()
        {
            _fontSizeIndex = (_fontSizeIndex + 1) % _fontSizes.Length;
            double size = _fontSizes[_fontSizeIndex];
            NoteRichTextBox.FontSize = size;
            NoteRichTextBox.Document.FontSize = size;
            UpdateFontSizeLabel();
            FooterStatus.Text = $"Font: {size:0}px";
        }

        private void UpdateFontSizeLabel()
        {
            if (FontSizeLabel != null)
                FontSizeLabel.Text = $"{NoteRichTextBox.FontSize:0}";
        }

        // ─── HEADING ───
        private static readonly (double size, FontWeight weight, string label)[] _headingLevels =
        {
            (13, FontWeights.Normal, "¶"),   // Normal paragraph
            (24, FontWeights.Bold, "H1"),
            (20, FontWeights.Bold, "H2"),
            (17, FontWeights.SemiBold, "H3"),
        };
        private int _headingIndex = 0;

        private void CycleHeading()
        {
            var para = NoteRichTextBox.CaretPosition?.Paragraph;
            if (para == null) return;

            _headingIndex = (_headingIndex + 1) % _headingLevels.Length;
            var (size, weight, label) = _headingLevels[_headingIndex];

            para.FontSize = size;
            para.FontWeight = weight;
            para.Margin = _headingIndex > 0 ? new Thickness(0, 8, 0, 4) : new Thickness(0);

            if (HeadingLabel != null) HeadingLabel.Text = label;
            FooterStatus.Text = _headingIndex == 0 ? "Paragraph" : label;
        }

        // ─── REAL BULLET LIST (replaces fake "• " insertion) ───
        // Note: The old BulletBtn_Click just did InsertText("• "). Now we use proper FlowDocument List:
        // We override it in the existing BulletBtn_Click to use EditingCommands.ToggleBullets

        // ─── LINK INSERTION ───
        private void InsertHyperlink()
        {
            try
            {
                string selectedText = NoteRichTextBox.Selection.Text?.Trim() ?? "";
                string clipboardText = "";
                try { clipboardText = Clipboard.GetText()?.Trim() ?? ""; } catch { }

                // Determine URL and display text
                string url;
                string displayText;

                if (selectedText.StartsWith("http", StringComparison.Ordinal) || selectedText.StartsWith("www.", StringComparison.Ordinal))
                {
                    url = selectedText;
                    displayText = selectedText;
                }
                else if (clipboardText.StartsWith("http", StringComparison.Ordinal) || clipboardText.StartsWith("www.", StringComparison.Ordinal))
                {
                    url = clipboardText;
                    displayText = string.IsNullOrEmpty(selectedText) ? clipboardText : selectedText;
                }
                else
                {
                    FooterStatus.Text = "Copy a URL first, then select text and click Link";
                    return;
                }

                if (!url.StartsWith("http", StringComparison.Ordinal)) url = "https://" + url;

                var hyperlink = new Hyperlink(new Run(displayText))
                {
                    NavigateUri = new Uri(url),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x63, 0x84, 0xFF)),
                    TextDecorations = TextDecorations.Underline,
                    ToolTip = url
                };
                hyperlink.RequestNavigate += (s, ev) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(ev.Uri.AbsoluteUri) { UseShellExecute = true });
                    }
                    catch { } // Best-effort: failure is acceptable
                    ev.Handled = true;
                };

                if (!NoteRichTextBox.Selection.IsEmpty)
                    NoteRichTextBox.Selection.Text = "";

                var insertPos = NoteRichTextBox.CaretPosition;
                var para = insertPos.Paragraph ?? NoteRichTextBox.Document.Blocks.LastBlock as Paragraph;
                if (para == null)
                {
                    para = new Paragraph();
                    NoteRichTextBox.Document.Blocks.Add(para);
                }
                para.Inlines.Add(hyperlink);
                para.Inlines.Add(new Run(" ")); // space after link

                FooterStatus.Text = "✓ Link added";
                _isDirty = true;
            }
            catch (Exception ex)
            {
                FooterStatus.Text = $"Link error: {ex.Message}";
            }
        }

        // ─── BLOCKQUOTE ───
        private void ToggleBlockquote()
        {
            var para = NoteRichTextBox.CaretPosition?.Paragraph;
            if (para == null) return;

            bool isQuote = para.BorderBrush != null && para.BorderThickness.Left > 0;
            if (isQuote)
            {
                // Remove quote styling
                para.BorderBrush = null;
                para.BorderThickness = new Thickness(0);
                para.Padding = new Thickness(0);
                para.Background = null;
                para.FontStyle = FontStyles.Normal;
                FooterStatus.Text = "Quote removed";
            }
            else
            {
                // Apply quote styling: left border + slight indent + italic
                para.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x9B));
                para.BorderThickness = new Thickness(3, 0, 0, 0);
                para.Padding = new Thickness(12, 4, 0, 4);
                para.Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
                para.FontStyle = FontStyles.Italic;
                FooterStatus.Text = "Blockquote applied";
            }
            _isDirty = true;
        }

        // ─── CODE BLOCK ───
        private void ToggleCodeBlock()
        {
            var para = NoteRichTextBox.CaretPosition?.Paragraph;
            if (para == null) return;

            bool isCode = para.FontFamily?.Source?.Contains("Consolas", StringComparison.Ordinal) == true
                       || para.FontFamily?.Source?.Contains("Cascadia", StringComparison.Ordinal) == true;
            if (isCode)
            {
                // Remove code block styling
                para.FontFamily = new FontFamily("Segoe UI");
                para.Background = null;
                para.Padding = new Thickness(0);
                para.Margin = new Thickness(0);
                FooterStatus.Text = "Code block removed";
            }
            else
            {
                // Apply code block styling
                para.FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New");
                para.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
                para.Padding = new Thickness(8, 4, 8, 4);
                para.Margin = new Thickness(0, 4, 0, 4);
                FooterStatus.Text = "Code block applied";
            }
            _isDirty = true;
        }

        // ─── TEXT COLOR ───
        private static readonly (string name, Color color)[] _textColors =
        {
            ("Default", ThemeColors.LightLavender),
            ("Red", ThemeColors.ErrorRed),
            ("Orange", ThemeColors.WarningAmber),
            ("Green", ThemeColors.SuccessGreen),
            ("Blue", ThemeColors.Blue500),
            ("Purple", ThemeColors.VioletAccent),
        };
        private int _textColorIndex = 0;

        private void CycleTextColor()
        {
            var selection = NoteRichTextBox.Selection;
            if (selection.IsEmpty)
            {
                FooterStatus.Text = "Select text first";
                return;
            }

            _textColorIndex = (_textColorIndex + 1) % _textColors.Length;
            var (name, color) = _textColors[_textColorIndex];

            selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
            if (TextColorIndicator != null)
                TextColorIndicator.Background = new SolidColorBrush(color);
            FooterStatus.Text = $"Text: {name}";
            _isDirty = true;
        }

        // ─── HIGHLIGHT ───
        private static readonly (string name, Color color)[] _highlightColors =
        {
            ("None", Colors.Transparent),
            ("Yellow", Color.FromArgb(0x50, 0xFB, 0xBF, 0x24)),
            ("Green", Color.FromArgb(0x50, 0x10, 0xB9, 0x81)),
            ("Blue", Color.FromArgb(0x50, 0x3B, 0x82, 0xF6)),
            ("Purple", Color.FromArgb(0x50, 0x8B, 0x5C, 0xF6)),
            ("Pink", Color.FromArgb(0x50, 0xEC, 0x48, 0x99)),
        };
        private int _highlightColorIndex = 0;

        private void CycleHighlightColor()
        {
            var selection = NoteRichTextBox.Selection;
            if (selection.IsEmpty)
            {
                FooterStatus.Text = "Select text first";
                return;
            }

            _highlightColorIndex = (_highlightColorIndex + 1) % _highlightColors.Length;
            var (name, color) = _highlightColors[_highlightColorIndex];

            selection.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(color));
            if (HighlightColorIndicator != null)
                HighlightColorIndicator.Background = new SolidColorBrush(color == Colors.Transparent ? Color.FromRgb(0xFB, 0xBF, 0x24) : color);
            FooterStatus.Text = $"Highlight: {name}";
            _isDirty = true;
        }

        // ─── CLEAR FORMATTING ───
        private void ClearFormatting()
        {
            var selection = NoteRichTextBox.Selection;
            if (selection.IsEmpty)
            {
                FooterStatus.Text = "Select text first";
                return;
            }

            selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(ThemeColors.LightLavender));
            selection.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
            selection.ApplyPropertyValue(TextElement.FontSizeProperty, NoteRichTextBox.FontSize);
            selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));

            FooterStatus.Text = "Formatting cleared";
            _isDirty = true;
        }

        // ═══════════════════════════════════════════════════════════
        // CTRL+SCROLL ZOOM
        // ═══════════════════════════════════════════════════════════

        private void NoteRichTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                double current = NoteRichTextBox.FontSize;
                double next = e.Delta > 0
                    ? Math.Min(current + 1, 24)
                    : Math.Max(current - 1, 10);
                NoteRichTextBox.FontSize = next;
                NoteRichTextBox.Document.FontSize = next;
                UpdateFontSizeLabel();
                FooterStatus.Text = $"Font: {next:0}px";
                e.Handled = true;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // HEADER BUTTONS
        // ═══════════════════════════════════════════════════════════

        private void CopyBtn_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var textRange = new TextRange(
                    NoteRichTextBox.Document.ContentStart,
                    NoteRichTextBox.Document.ContentEnd);
                string text = textRange.Text?.TrimEnd('\r', '\n') ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    ClipboardHelper.SafeSetText(text);
                    FooterStatus.Text = "✓ Copied";
                }
            }
            catch { } // Best-effort: failure is acceptable
            e.Handled = true;
        }

        private void PinBtn_Click(object sender, MouseButtonEventArgs e)
        {
            _isPinned = !_isPinned;
            Topmost = _isPinned;
            PinIcon.Symbol = _isPinned
                ? Wpf.Ui.Controls.SymbolRegular.Pin24
                : Wpf.Ui.Controls.SymbolRegular.PinOff24;
            FooterStatus.Text = _isPinned ? "Pinned" : "Unpinned";
            e.Handled = true;
        }

        private void CloseBtn_Click(object sender, MouseButtonEventArgs e)
        {
            Close();
            e.Handled = true;
        }

        // ═══════════════════════════════════════════════════════════
        // WINDOW CHROME
        // ═══════════════════════════════════════════════════════════

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (Height < 500)
                { Width = 520; Height = 600; }
                else
                { Width = 360; Height = 420; }
            }
            else
            {
                try { DragMove(); } catch { } // Best-effort: failure is acceptable
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _saveTimer.Stop();
            SaveContent();
        }

        // ═══════════════════════════════════════════════════════════
        // KEYBOARD SHORTCUTS
        // (Ctrl+B and Ctrl+I are handled natively by RichTextBox)
        // ═══════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════
        // SAFE IMAGE DELETION (prevents WPF TextContainer crash)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Intercepts Delete/Backspace when the caret or selection is adjacent to or
        /// contains an InlineUIContainer (image). Removes the image paragraph safely
        /// to avoid WPF's TextContainer.CreatePointerAtCharOffset crash.
        /// Returns true if an image was handled (caller should set e.Handled = true).
        /// </summary>
        private bool TryHandleImageDeletion(bool isBackspace)
        {
            var caret = NoteRichTextBox.CaretPosition;
            if (caret == null) return false;

            // Case 1: Selection spans an image → remove it
            if (!NoteRichTextBox.Selection.IsEmpty)
            {
                var selectedBlocks = NoteRichTextBox.Document.Blocks
                    .OfType<Paragraph>()
                    .Where(p => p.Inlines.Count == 1
                        && p.Inlines.FirstInline is InlineUIContainer iuc
                        && iuc.Child is System.Windows.Controls.Image)
                    .Where(p =>
                    {
                        // Check if block overlaps with selection
                        var blockStart = p.ContentStart.GetOffsetToPosition(NoteRichTextBox.Selection.Start);
                        var blockEnd = p.ContentEnd.GetOffsetToPosition(NoteRichTextBox.Selection.End);
                        return blockStart <= 0 && blockEnd >= 0; // selection contains block
                    })
                    .ToList();

                if (selectedBlocks.Count > 0)
                {
                    _isLoading = true; // suppress TextChanged/SaveContent during manipulation
                    try
                    {
                        foreach (var imgBlock in selectedBlocks)
                        {
                            RemoveImageBlock(imgBlock);
                        }
                        // Delete remaining selected text normally
                        NoteRichTextBox.Selection.Text = "";
                    }
                    finally
                    {
                        _isLoading = false;
                    }
                    _isDirty = true;
                    // Defer UI updates to let TextContainer stabilize
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                    {
                        UpdateWordCount();
                        _saveTimer?.Stop();
                        _saveTimer?.Start();
                    }));
                    return true;
                }
            }

            // Case 2: Caret is adjacent to an image paragraph (Backspace before or Delete after)
            Paragraph adjacentImagePara = null;

            if (isBackspace)
            {
                // Backspace: check the paragraph ending right before the caret
                var prevBlock = caret.Paragraph;
                if (prevBlock == null)
                {
                    // Caret might be between blocks; check the block before
                    var pointer = caret.GetNextInsertionPosition(LogicalDirection.Backward);
                    if (pointer?.Paragraph is Paragraph p && IsImageOnlyParagraph(p))
                        adjacentImagePara = p;
                }
                else if (IsImageOnlyParagraph(prevBlock))
                {
                    adjacentImagePara = prevBlock;
                }
                else
                {
                    // Check if caret is at the very start of the current paragraph,
                    // meaning Backspace would merge with previous block
                    var paraStart = prevBlock.ContentStart;
                    if (caret.GetOffsetToPosition(paraStart) >= 0)
                    {
                        // At the start → previous block might be an image
                        var prev = prevBlock.PreviousBlock as Paragraph;
                        if (prev != null && IsImageOnlyParagraph(prev))
                            adjacentImagePara = prev;
                    }
                }
            }
            else
            {
                // Delete: check if next block is an image paragraph
                var currentPara = caret.Paragraph;
                if (currentPara != null)
                {
                    // Check if caret is at the end of the current paragraph
                    var paraEnd = currentPara.ContentEnd;
                    if (paraEnd.GetOffsetToPosition(caret) >= 0 || 
                        new TextRange(caret, paraEnd).Text.Length == 0)
                    {
                        var nextBlock = currentPara.NextBlock as Paragraph;
                        if (nextBlock != null && IsImageOnlyParagraph(nextBlock))
                            adjacentImagePara = nextBlock;
                    }
                }
                else if (IsImageOnlyParagraph(caret.Paragraph))
                {
                    adjacentImagePara = caret.Paragraph;
                }
            }

            if (adjacentImagePara != null)
            {
                _isLoading = true;
                try
                {
                    RemoveImageBlock(adjacentImagePara);
                }
                finally
                {
                    _isLoading = false;
                }
                _isDirty = true;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    UpdateWordCount();
                    _saveTimer?.Stop();
                    _saveTimer?.Start();
                }));
                return true;
            }

            return false;
        }

        /// <summary>Returns true if the paragraph contains only an image (InlineUIContainer).</summary>
        private static bool IsImageOnlyParagraph(Paragraph p)
        {
            return p != null
                && p.Inlines.Count == 1
                && p.Inlines.FirstInline is InlineUIContainer iuc
                && iuc.Child is System.Windows.Controls.Image;
        }

        /// <summary>
        /// Safely removes an image-only paragraph from the document and cleans up
        /// the section's image list.
        /// </summary>
        private void RemoveImageBlock(Paragraph imgParagraph)
        {
            // Find and remove the image path from _section.Images
            var iuc = imgParagraph.Inlines.FirstInline as InlineUIContainer;
            if (iuc?.Child is System.Windows.Controls.Image img && img.Tag is string imagePath)
            {
                var freeformImg = _section.Images.FirstOrDefault(i => i.ImagePath == imagePath);
                if (freeformImg != null)
                    _section.Images.Remove(freeformImg);
            }

            // Remove the paragraph from the live document
            NoteRichTextBox.Document.Blocks.Remove(imgParagraph);
            NoteManager.MarkDirty();

            if (FooterStatus != null) FooterStatus.Text = "Image removed";
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // ═══ FIX: Intercept Delete/Backspace near InlineUIContainer (image) ═══
            // WPF's default handler corrupts TextContainer offsets when deleting
            // InlineUIContainer elements, causing Invariant.FailFast in OnTextViewUpdatedWorker.
            // We manually remove the image paragraph to avoid the crash.
            if ((e.Key == Key.Delete || e.Key == Key.Back) && Keyboard.Modifiers == ModifierKeys.None)
            {
                try
                {
                    if (TryHandleImageDeletion(e.Key == Key.Back))
                    {
                        e.Handled = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("NOTES_EXPAND", $"Image deletion handler error: {ex.Message}");
                }
            }

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _saveTimer.Stop();
                SaveContent();
                FooterStatus.Text = "✓ Saved";
                e.Handled = true;
            }
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                InsertText("────────────────");
                e.Handled = true;
            }
            else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
            {
                InsertText($"[{DateTime.Now:hh:mm tt}] ");
                e.Handled = true;
            }
            else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
            {
                EditingCommands.ToggleBullets.Execute(null, NoteRichTextBox);
                e.Handled = true;
            }
            else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                InsertHyperlink();
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                InsertOrToggleCheckbox();
                e.Handled = true;
            }
            else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                double size = Math.Min(NoteRichTextBox.FontSize + 1, 24);
                NoteRichTextBox.FontSize = size;
                NoteRichTextBox.Document.FontSize = size;
                UpdateFontSizeLabel();
                FooterStatus.Text = $"Font: {size:0}px";
                e.Handled = true;
            }
            else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                double size = Math.Max(NoteRichTextBox.FontSize - 1, 10);
                NoteRichTextBox.FontSize = size;
                NoteRichTextBox.Document.FontSize = size;
                UpdateFontSizeLabel();
                FooterStatus.Text = $"Font: {size:0}px";
                e.Handled = true;
            }
        }
    }
}
