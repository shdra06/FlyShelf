// ---------------------------------------------------------------
// PdfToWordConverter — Native C# PDF-to-DOCX converter
// Uses PdfPig (text extraction) + OpenXML (DOCX generation)
// No external dependencies — works without Word or LibreOffice
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Native PDF-to-DOCX converter that runs entirely in-process.
    /// Extracts text blocks, detects tables, and embeds images.
    /// Falls back gracefully for complex layouts.
    /// </summary>
    public static class PdfToWordConverter
    {
        /// <summary>
        /// Converts a PDF file to DOCX format.
        /// Returns true if conversion succeeded.
        /// </summary>
        public static bool Convert(string pdfPath, string outputDocxPath)
        {
            try
            {
                using var pdfDoc = PdfDocument.Open(pdfPath);

                using var wordDoc = WordprocessingDocument.Create(
                    outputDocxPath, WordprocessingDocumentType.Document);

                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                // ═══ Set page size and margins (A4, normal margins) ═══
                var sectionProps = new SectionProperties(
                    new PageSize { Width = 12240, Height = 15840 }, // Letter (8.5x11")
                    new PageMargin
                    {
                        Top = 1440, Right = 1440, Bottom = 1440, Left = 1440,
                        Header = 720, Footer = 720
                    });

                // ═══ Define styles ═══
                AddStyles(mainPart);

                int pageIndex = 0;
                foreach (var page in pdfDoc.GetPages())
                {
                    if (pageIndex > 0)
                    {
                        // Page break between pages
                        var pageBreak = new Paragraph(
                            new Run(new Break { Type = BreakValues.Page }));
                        body.AppendChild(pageBreak);
                    }

                    // ═══ Extract text blocks grouped by lines ═══
                    var textLines = ExtractTextLines(page);

                    // ═══ Detect tables vs regular paragraphs ═══
                    var (tables, paragraphLines) = DetectTables(textLines, page.Width);

                    int lineIdx = 0;
                    foreach (var line in textLines)
                    {
                        // Check if this line is part of a detected table
                        var table = tables.FirstOrDefault(t =>
                            t.StartLineIndex <= lineIdx && lineIdx <= t.EndLineIndex);

                        if (table != null && lineIdx == table.StartLineIndex)
                        {
                            // Render the table
                            var wordTable = CreateWordTable(table);
                            body.AppendChild(wordTable);
                        }
                        else if (table != null)
                        {
                            // Skip — already rendered as part of a table
                        }
                        else
                        {
                            // Regular paragraph
                            var para = CreateParagraph(line);
                            body.AppendChild(para);
                        }

                        lineIdx++;
                    }

                    // ═══ Extract and embed images ═══
                    try
                    {
                        var images = page.GetImages().ToList();
                        foreach (var image in images)
                        {
                            try
                            {
                                if (image.TryGetPng(out var pngBytes))
                                {
                                    AddImageToBody(mainPart, body, pngBytes, "image/png");
                                }
                                else if (image.RawBytes.Count > 0)
                                {
                                    // Try raw bytes as JPEG
                                    AddImageToBody(mainPart, body, image.RawBytes.ToArray(), "image/jpeg");
                                }
                            }
                            catch
                            {
                                // Skip individual image errors — don't fail the whole conversion
                            }
                        }
                    }
                    catch
                    {
                        // Some PDFs have encrypted or malformed image streams — skip gracefully
                    }

                    pageIndex++;
                }

                body.AppendChild(sectionProps);
                mainPart.Document.Save();

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("PDF2WORD_NATIVE", $"Native conversion failed: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════
        // TEXT EXTRACTION
        // ═══════════════════════════════════════════════════════

        private class TextLine
        {
            public double Y { get; set; }
            public List<TextBlock> Blocks { get; } = new();
            public string FullText => string.Join(" ", Blocks.Select(b => b.Text));
            public double FontSize => Blocks.FirstOrDefault()?.FontSize ?? 11;
            public bool IsBold => Blocks.Any(b => b.IsBold);
        }

        private class TextBlock
        {
            public string Text { get; set; } = "";
            public double X { get; set; }
            public double Y { get; set; }
            public double FontSize { get; set; }
            public bool IsBold { get; set; }
            public bool IsItalic { get; set; }
            public double Width { get; set; }
        }

        private static List<TextLine> ExtractTextLines(Page page)
        {
            var blocks = new List<TextBlock>();

            foreach (var word in page.GetWords())
            {
                blocks.Add(new TextBlock
                {
                    Text = word.Text,
                    X = word.BoundingBox.Left,
                    Y = Math.Round(word.BoundingBox.Bottom, 1),
                    FontSize = Math.Round(word.Letters.FirstOrDefault()?.PointSize ?? 11, 1),
                    IsBold = word.Letters.Any(l =>
                        l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true),
                    IsItalic = word.Letters.Any(l =>
                        l.FontName?.Contains("Italic", StringComparison.OrdinalIgnoreCase) == true ||
                        l.FontName?.Contains("Oblique", StringComparison.OrdinalIgnoreCase) == true),
                    Width = word.BoundingBox.Width
                });
            }

            // Group by Y coordinate (same line = within 2pt tolerance)
            var lines = new List<TextLine>();
            foreach (var block in blocks.OrderByDescending(b => b.Y).ThenBy(b => b.X))
            {
                var matchingLine = lines.FirstOrDefault(l =>
                    Math.Abs(l.Y - block.Y) < 2.0);

                if (matchingLine != null)
                {
                    matchingLine.Blocks.Add(block);
                }
                else
                {
                    var newLine = new TextLine { Y = block.Y };
                    newLine.Blocks.Add(block);
                    lines.Add(newLine);
                }
            }

            // Sort blocks within each line by X position (left to right)
            foreach (var line in lines)
                line.Blocks.Sort((a, b) => a.X.CompareTo(b.X));

            return lines;
        }

        // ═══════════════════════════════════════════════════════
        // TABLE DETECTION (heuristic-based)
        // ═══════════════════════════════════════════════════════

        private class DetectedTable
        {
            public int StartLineIndex { get; set; }
            public int EndLineIndex { get; set; }
            public List<List<string>> Rows { get; } = new();
            public int ColumnCount { get; set; }
        }

        private static (List<DetectedTable> tables, List<TextLine> paragraphs) DetectTables(
            List<TextLine> lines, double pageWidth)
        {
            var tables = new List<DetectedTable>();
            var paragraphs = new List<TextLine>();

            // Heuristic: If 3+ consecutive lines have the same number of "columns"
            // (text blocks at similar X positions), treat them as a table

            if (lines.Count < 3) return (tables, lines);

            int i = 0;
            while (i < lines.Count)
            {
                // Check if this could be a table row (has multiple distinct X-position groups)
                var columnPositions = GetColumnPositions(lines[i], pageWidth);
                if (columnPositions.Count >= 2)
                {
                    // Look ahead for more rows with similar column structure
                    int tableStart = i;
                    int tableEnd = i;
                    int colCount = columnPositions.Count;

                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        var nextCols = GetColumnPositions(lines[j], pageWidth);
                        // Allow ±1 column variance
                        if (Math.Abs(nextCols.Count - colCount) <= 1 && nextCols.Count >= 2)
                        {
                            tableEnd = j;
                            colCount = Math.Max(colCount, nextCols.Count);
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Need at least 2 rows to be a table (header + 1 data row)
                    if (tableEnd - tableStart >= 1)
                    {
                        var table = new DetectedTable
                        {
                            StartLineIndex = tableStart,
                            EndLineIndex = tableEnd,
                            ColumnCount = colCount
                        };

                        for (int r = tableStart; r <= tableEnd; r++)
                        {
                            var cells = SplitIntoColumns(lines[r], colCount, pageWidth);
                            table.Rows.Add(cells);
                        }

                        tables.Add(table);
                        i = tableEnd + 1;
                        continue;
                    }
                }

                paragraphs.Add(lines[i]);
                i++;
            }

            return (tables, paragraphs);
        }

        private static List<double> GetColumnPositions(TextLine line, double pageWidth)
        {
            if (line.Blocks.Count <= 1) return new List<double> { 0 };

            var positions = new List<double>();
            double lastRight = 0;

            foreach (var block in line.Blocks.OrderBy(b => b.X))
            {
                // If there's a gap > 5% of page width, it's likely a new column
                if (positions.Count == 0 || (block.X - lastRight) > pageWidth * 0.05)
                {
                    positions.Add(block.X);
                }
                lastRight = block.X + block.Width;
            }

            return positions;
        }

        private static List<string> SplitIntoColumns(TextLine line, int columnCount, double pageWidth)
        {
            if (columnCount <= 1) return new List<string> { line.FullText };

            // Divide page width into equal columns
            double colWidth = pageWidth / columnCount;
            var cells = new List<string>(Enumerable.Repeat("", columnCount));

            foreach (var block in line.Blocks)
            {
                int colIdx = Math.Min((int)(block.X / colWidth), columnCount - 1);
                if (!string.IsNullOrEmpty(cells[colIdx])) cells[colIdx] += " ";
                cells[colIdx] += block.Text;
            }

            return cells;
        }

        // ═══════════════════════════════════════════════════════
        // DOCX GENERATION
        // ═══════════════════════════════════════════════════════

        private static void AddStyles(MainDocumentPart mainPart)
        {
            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new Styles();

            // Default paragraph style
            var defaultStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true,
            };
            defaultStyle.AppendChild(new StyleName { Val = "Normal" });
            defaultStyle.AppendChild(new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                new FontSize { Val = "22" } // 11pt
            ));
            styles.AppendChild(defaultStyle);

            // Heading 1 style
            var h1Style = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading1"
            };
            h1Style.AppendChild(new StyleName { Val = "heading 1" });
            h1Style.AppendChild(new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                new Bold(),
                new FontSize { Val = "32" } // 16pt
            ));
            h1Style.AppendChild(new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "240", After = "120" }));
            styles.AppendChild(h1Style);

            // Heading 2 style
            var h2Style = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading2"
            };
            h2Style.AppendChild(new StyleName { Val = "heading 2" });
            h2Style.AppendChild(new StyleRunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                new Bold(),
                new FontSize { Val = "28" } // 14pt
            ));
            h2Style.AppendChild(new StyleParagraphProperties(
                new SpacingBetweenLines { Before = "200", After = "80" }));
            styles.AppendChild(h2Style);

            stylesPart.Styles = styles;
            stylesPart.Styles.Save();
        }

        private static Paragraph CreateParagraph(TextLine line)
        {
            var para = new Paragraph();

            // Determine if this is a heading based on font size
            double fontSize = line.FontSize;
            if (fontSize >= 16)
            {
                para.AppendChild(new ParagraphProperties(
                    new ParagraphStyleId { Val = "Heading1" }));
            }
            else if (fontSize >= 13)
            {
                para.AppendChild(new ParagraphProperties(
                    new ParagraphStyleId { Val = "Heading2" }));
            }
            else
            {
                // Normal paragraph with spacing
                para.AppendChild(new ParagraphProperties(
                    new SpacingBetweenLines { After = "60", Line = "276", LineRule = LineSpacingRuleValues.Auto }));
            }

            // Create runs for each text block (preserving bold/italic)
            foreach (var block in line.Blocks)
            {
                var run = new Run();
                var runProps = new RunProperties();

                // Font size (in half-points)
                int halfPoints = (int)(block.FontSize * 2);
                if (halfPoints > 0 && halfPoints != 22)
                    runProps.AppendChild(new FontSize { Val = halfPoints.ToString() });

                if (block.IsBold)
                    runProps.AppendChild(new Bold());
                if (block.IsItalic)
                    runProps.AppendChild(new Italic());

                if (runProps.HasChildren)
                    run.AppendChild(runProps);

                run.AppendChild(new Text(block.Text) { Space = SpaceProcessingModeValues.Preserve });
                para.AppendChild(run);

                // Add space between blocks
                var spaceRun = new Run(new Text(" ") { Space = SpaceProcessingModeValues.Preserve });
                para.AppendChild(spaceRun);
            }

            return para;
        }

        private static Table CreateWordTable(DetectedTable detected)
        {
            var table = new Table();

            // Table properties — bordered, full-width
            var tblProps = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "999999" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }
                ),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }, // 100% width
                new TableLayout { Type = TableLayoutValues.Autofit }
            );
            table.AppendChild(tblProps);

            for (int r = 0; r < detected.Rows.Count; r++)
            {
                var row = new TableRow();
                var cells = detected.Rows[r];

                // Pad to column count if needed
                while (cells.Count < detected.ColumnCount)
                    cells.Add("");

                for (int c = 0; c < detected.ColumnCount; c++)
                {
                    var cell = new TableCell();
                    var cellProps = new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Auto }
                    );

                    // First row gets header styling (bold + shaded background)
                    if (r == 0)
                    {
                        cellProps.AppendChild(new Shading
                        {
                            Val = ShadingPatternValues.Clear,
                            Fill = "E8E8E8"
                        });
                    }

                    cell.AppendChild(cellProps);

                    var para = new Paragraph();
                    var run = new Run();
                    if (r == 0)
                        run.AppendChild(new RunProperties(new Bold()));

                    string cellText = c < cells.Count ? cells[c] : "";
                    run.AppendChild(new Text(cellText) { Space = SpaceProcessingModeValues.Preserve });
                    para.AppendChild(run);
                    cell.AppendChild(para);
                    row.AppendChild(cell);
                }

                table.AppendChild(row);
            }

            return table;
        }

        private static void AddImageToBody(MainDocumentPart mainPart, Body body,
            byte[] imageBytes, string contentType)
        {
            if (imageBytes == null || imageBytes.Length < 100) return; // Skip tiny/corrupt images

            var imagePart = mainPart.AddImagePart(
                contentType == "image/png" ? ImagePartType.Png : ImagePartType.Jpeg);

            using var ms = new MemoryStream(imageBytes);
            imagePart.FeedData(ms);

            string relationshipId = mainPart.GetIdOfPart(imagePart);

            // Default image size: 5 inches wide, auto height
            long widthEmu = 5 * 914400; // 5 inches in EMU
            long heightEmu = 3 * 914400; // 3 inches default (will be overridden by aspect ratio)

            // Try to get actual dimensions from image bytes
            try
            {
                using var imgMs = new MemoryStream(imageBytes);
                var bitmapFrame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    imgMs, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);

                double aspectRatio = (double)bitmapFrame.PixelWidth / bitmapFrame.PixelHeight;
                heightEmu = (long)(widthEmu / aspectRatio);
            }
            catch { /* Use default dimensions */ }

            var drawing = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = 1U, Name = "Image" },
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relationshipId },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                    new A.PresetGeometry(new A.AdjustValueList())
                                    { Preset = A.ShapeTypeValues.Rectangle })
                            )
                        ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }
                    )
                )
                {
                    DistanceFromTop = 0U,
                    DistanceFromBottom = 0U,
                    DistanceFromLeft = 0U,
                    DistanceFromRight = 0U
                }
            );

            body.AppendChild(new Paragraph(new Run(drawing)));
        }
    }
}
