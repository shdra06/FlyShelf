// ═══════════════════════════════════════════════════════════════════════
// DocxToPdfConverter.cs — Native C# DOCX-to-PDF Conversion Engine
// Pure C# implementation using DocumentFormat.OpenXml + PDFsharp.
// 100% offline, zero external dependencies (no Word or LibreOffice needed).
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using VML = DocumentFormat.OpenXml.Vml;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace FlyShelf.Classes.Utils
{
    public static class DocxToPdfConverter
    {
        private const double PointsPerInch = 72.0;
        private const double DxaPerPoint = 20.0; // 1 pt = 20 dxa in OpenXml

        // Default A4 dimensions in points
        private const double DefaultPageWidth = 595.28;
        private const double DefaultPageHeight = 841.89;
        private const double DefaultMargin = 45.0;

        /// <summary>
        /// Converts a .docx file to a high-quality PDF using pure C# OpenXml and PDFsharp.
        /// Returns true if conversion succeeded and PDF exists.
        /// </summary>
        public static bool Convert(string docxPath, string outputPdfPath)
        {
            if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath))
                return false;

            try
            {
                FlyShelfFontResolver.EnsureRegistered();

                using var fs = new FileStream(docxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var wordDoc = WordprocessingDocument.Open(fs, false);

                var mainPart = wordDoc.MainDocumentPart;
                if (mainPart?.Document?.Body == null)
                    return false;

                var body = mainPart.Document.Body;

                // 1. Determine Page Setup and Margins from SectionProperties
                double pageW = DefaultPageWidth;
                double pageH = DefaultPageHeight;
                double marginLeft = DefaultMargin;
                double marginRight = DefaultMargin;
                double marginTop = DefaultMargin;
                double marginBottom = DefaultMargin;

                var sectPr = body.Elements<SectionProperties>().LastOrDefault();
                if (sectPr != null)
                {
                    var pageSize = sectPr.Elements<DocumentFormat.OpenXml.Wordprocessing.PageSize>().FirstOrDefault();
                    if (pageSize != null && pageSize.Width != null && pageSize.Height != null)
                    {
                        if (pageSize.Width.HasValue && pageSize.Height.HasValue)
                        {
                            pageW = pageSize.Width.Value / DxaPerPoint;
                            pageH = pageSize.Height.Value / DxaPerPoint;
                        }
                    }

                    var pageMargin = sectPr.Elements<PageMargin>().FirstOrDefault();
                    if (pageMargin != null)
                    {
                        if (pageMargin.Left?.HasValue == true) marginLeft = Math.Max(20, pageMargin.Left.Value / DxaPerPoint);
                        if (pageMargin.Right?.HasValue == true) marginRight = Math.Max(20, pageMargin.Right.Value / DxaPerPoint);
                        if (pageMargin.Top?.HasValue == true) marginTop = Math.Max(25, pageMargin.Top.Value / DxaPerPoint);
                        if (pageMargin.Bottom?.HasValue == true) marginBottom = Math.Max(25, pageMargin.Bottom.Value / DxaPerPoint);
                    }
                }

                double usableWidth = pageW - marginLeft - marginRight;
                double usableHeight = pageH - marginTop - marginBottom;

                // 2. Initialize PDF Document
                using var pdfDoc = new PdfDocument();
                pdfDoc.Info.Title = Path.GetFileNameWithoutExtension(docxPath);
                pdfDoc.Info.Creator = "FlyShelf Native Document Engine";

                // Layout state tracker
                var state = new LayoutState
                {
                    Doc = pdfDoc,
                    PageWidth = pageW,
                    PageHeight = pageH,
                    MarginLeft = marginLeft,
                    MarginRight = marginRight,
                    MarginTop = marginTop,
                    MarginBottom = marginBottom,
                    UsableWidth = usableWidth,
                    UsableHeight = usableHeight,
                    CurrentY = marginTop,
                    MainPart = mainPart
                };

                // Add initial page
                state.NewPage();

                // 3. Process Document Elements Sequentially (Recursively traversing structured tags if needed)
                ProcessContainerElements(body, state);

                // Ensure at least one page exists
                if (pdfDoc.PageCount == 0)
                {
                    state.NewPage();
                }

                // Dispose active graphics context before rendering footers and saving
                state.Gfx?.Dispose();
                state.Gfx = null;

                // Render running footers with page numbers
                RenderFooters(pdfDoc, pageW, pageH, marginBottom);

                // Save PDF to destination
                string dir = Path.GetDirectoryName(outputPdfPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                pdfDoc.Save(outputPdfPath);
                return File.Exists(outputPdfPath) && new FileInfo(outputPdfPath).Length > 0;
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX2PDF_NATIVE_ERR", $"DOCX Native conversion failed for {Path.GetFileName(docxPath)}: {ex.Message}");
                return false;
            }
        }

        private static void ProcessContainerElements(OpenXmlElement container, LayoutState state)
        {
            foreach (var element in container.Elements())
            {
                if (element is SectionProperties) continue;

                if (element is Paragraph para)
                {
                    RenderParagraph(para, state);
                }
                else if (element is Table table)
                {
                    RenderTable(table, state);
                }
                else if (element is SdtBlock sdtBlock)
                {
                    var content = sdtBlock.Elements<SdtContentBlock>().FirstOrDefault();
                    if (content != null) ProcessContainerElements(content, state);
                }
            }
        }

        #region Paragraph & Run Rendering

        private static void RenderParagraph(Paragraph para, LayoutState state)
        {
            // Check for explicit page break in paragraph
            if (para.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page))
            {
                state.NewPage();
                return;
            }

            // Extract paragraph properties
            var pPr = para.ParagraphProperties;
            string styleId = pPr?.ParagraphStyleId?.Val?.Value ?? "";
            var jc = pPr?.Justification?.Val?.Value;

            // Check if paragraph is a Heading
            bool isHeading = false;
            int headingLevel = 0;
            if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            {
                isHeading = true;
                if (int.TryParse(styleId.Substring(7), out int lvl)) headingLevel = lvl;
                else headingLevel = 1;
            }

            // Check for bullet or numbering
            bool isList = pPr?.NumberingProperties != null;

            // Extract embedded images from Drawing elements
            var drawings = para.Descendants<Drawing>().ToList();
            if (drawings.Count > 0)
            {
                foreach (var drawing in drawings)
                {
                    RenderDrawing(drawing, state);
                }
            }

            // Extract embedded images from legacy VML ImageData elements
            var imgDatas = para.Descendants<VML.ImageData>().ToList();
            if (imgDatas.Count > 0)
            {
                foreach (var imgData in imgDatas)
                {
                    RenderVmlImageData(imgData, state);
                }
            }

            // Collect text fragments with their formatting from all descendant runs (including hyperlinks)
            var runs = new List<TextFragment>();
            foreach (var run in para.Descendants<Run>())
            {
                var rPr = run.RunProperties;
                string text = run.InnerText;
                if (string.IsNullOrEmpty(text))
                {
                    if (run.Descendants<Break>().Any())
                    {
                        runs.Add(new TextFragment { Text = "\n", IsLineBreak = true });
                    }
                    continue;
                }

                // Determine font properties
                string fontName = rPr?.RunFonts?.Ascii?.Value ?? (isHeading ? "Arial" : "Segoe UI");
                double fontSize = 11.0;
                if (rPr?.FontSize?.Val?.Value != null)
                {
                    if (double.TryParse(rPr.FontSize.Val.Value, out double halfPts))
                        fontSize = Math.Max(7, halfPts / 2.0); // OpenXml fontSize is in half-points
                }
                else if (isHeading)
                {
                    fontSize = headingLevel switch
                    {
                        1 => 18.0,
                        2 => 15.0,
                        3 => 13.0,
                        4 => 12.0,
                        _ => 11.0
                    };
                }

                bool isBold = rPr?.Bold != null && (rPr.Bold.Val == null || rPr.Bold.Val.Value);
                if (isHeading) isBold = true;
                bool isItalic = rPr?.Italic != null && (rPr.Italic.Val == null || rPr.Italic.Val.Value);
                bool isUnderline = rPr?.Underline != null;

                // Determine text color
                XColor textColor = isHeading ? XColor.FromArgb(30, 41, 59) : XColor.FromArgb(15, 23, 42);
                string colorHex = rPr?.Color?.Val?.Value;
                if (!string.IsNullOrEmpty(colorHex) && colorHex.Length == 6)
                {
                    try
                    {
                        int r = System.Convert.ToInt32(colorHex.Substring(0, 2), 16);
                        int g = System.Convert.ToInt32(colorHex.Substring(2, 2), 16);
                        int b = System.Convert.ToInt32(colorHex.Substring(4, 2), 16);
                        textColor = XColor.FromArgb(r, g, b);
                    }
                    catch { }
                }

                var fontStyle = XFontStyleEx.Regular;
                if (isBold && isItalic) fontStyle = XFontStyleEx.BoldItalic;
                else if (isBold) fontStyle = XFontStyleEx.Bold;
                else if (isItalic) fontStyle = XFontStyleEx.Italic;

                XFont font;
                try { font = new XFont(fontName, fontSize, fontStyle); }
                catch { font = new XFont("Segoe UI", fontSize, fontStyle); }

                runs.Add(new TextFragment
                {
                    Text = text,
                    Font = font,
                    Color = textColor,
                    IsUnderline = isUnderline,
                    FontSize = fontSize
                });
            }

            if (runs.Count == 0)
            {
                // Empty paragraph — add small vertical spacing
                state.CurrentY += 6.0;
                return;
            }

            // Pre-heading spacing
            if (isHeading)
            {
                state.CurrentY += (headingLevel == 1 ? 14.0 : 10.0);
            }

            // Word wrap runs across lines
            double bulletIndent = isList ? 16.0 : 0.0;
            double effectiveWidth = state.UsableWidth - bulletIndent;
            var lines = WordWrapRuns(runs, effectiveWidth, state.Gfx);

            double lineHeight = runs.Max(r => r.FontSize) * 1.35;

            // Draw list bullet if applicable
            if (isList && lines.Count > 0)
            {
                if (state.CurrentY + lineHeight > state.PageHeight - state.MarginBottom)
                {
                    state.NewPage();
                }
                var bulletFont = new XFont("Segoe UI", 10, XFontStyleEx.Bold);
                state.Gfx.DrawString("•", bulletFont, new XSolidBrush(XColor.FromArgb(71, 85, 105)),
                    new XPoint(state.MarginLeft + 4, state.CurrentY + lineHeight * 0.75));
            }

            // Draw wrapped lines
            foreach (var line in lines)
            {
                if (state.CurrentY + lineHeight > state.PageHeight - state.MarginBottom)
                {
                    state.NewPage();
                }

                double lineX = state.MarginLeft + bulletIndent;

                // Handle text alignment (Center, Right)
                double totalLineWidth = line.Sum(f => f.Width);
                if (jc == JustificationValues.Center)
                {
                    lineX += Math.Max(0, (effectiveWidth - totalLineWidth) / 2.0);
                }
                else if (jc == JustificationValues.Right)
                {
                    lineX += Math.Max(0, effectiveWidth - totalLineWidth);
                }

                double curX = lineX;
                foreach (var frag in line)
                {
                    var brush = new XSolidBrush(frag.Color);
                    state.Gfx.DrawString(frag.Text, frag.Font, brush, new XPoint(curX, state.CurrentY + lineHeight * 0.75));

                    if (frag.IsUnderline)
                    {
                        var pen = new XPen(frag.Color, 0.75);
                        state.Gfx.DrawLine(pen, curX, state.CurrentY + lineHeight * 0.85, curX + frag.Width, state.CurrentY + lineHeight * 0.85);
                    }

                    curX += frag.Width;
                }

                state.CurrentY += lineHeight;
            }

            // Post-paragraph spacing
            state.CurrentY += isHeading ? 6.0 : 4.0;
        }

        #endregion

        #region Table Rendering

        private static void RenderTable(Table table, LayoutState state)
        {
            var rows = table.Elements<TableRow>().ToList();
            if (rows.Count == 0) return;

            state.CurrentY += 6.0;

            // Calculate column widths
            var grid = table.Elements<TableGrid>().FirstOrDefault();
            var gridCols = grid?.Elements<GridColumn>().ToList();

            int colCount = rows.Max(r => r.Elements<TableCell>().Count());
            if (colCount == 0) return;

            var colWidths = new double[colCount];
            double totalDxa = 0;

            if (gridCols != null && gridCols.Count >= colCount)
            {
                for (int i = 0; i < colCount; i++)
                {
                    if (double.TryParse(gridCols[i].Width?.Value, out double w))
                    {
                        colWidths[i] = w / DxaPerPoint;
                        totalDxa += colWidths[i];
                    }
                }
            }

            // Normalize or evenly distribute column widths to fit usable width
            if (totalDxa <= 0 || Math.Abs(totalDxa - state.UsableWidth) > 30)
            {
                double defaultColW = state.UsableWidth / colCount;
                for (int i = 0; i < colCount; i++) colWidths[i] = defaultColW;
            }
            else
            {
                double scale = state.UsableWidth / totalDxa;
                for (int i = 0; i < colCount; i++) colWidths[i] *= scale;
            }

            var cellFont = new XFont("Segoe UI", 9.5, XFontStyleEx.Regular);
            var headerFont = new XFont("Segoe UI", 9.5, XFontStyleEx.Bold);
            var borderPen = new XPen(XColor.FromArgb(203, 213, 225), 0.75);
            var headerBrush = new XSolidBrush(XColor.FromArgb(241, 245, 249));

            bool isFirstRow = true;
            foreach (var row in rows)
            {
                var cells = row.Elements<TableCell>().ToList();
                var cellTexts = new List<List<string>>();
                double maxRowHeight = 20.0;

                // Measure content of all cells in this row
                for (int c = 0; c < cells.Count && c < colCount; c++)
                {
                    // Extract text from cell paragraphs
                    var paras = cells[c].Elements<Paragraph>().Select(p => p.InnerText.Trim()).Where(t => !string.IsNullOrEmpty(t));
                    string text = string.Join("\n", paras);
                    if (string.IsNullOrEmpty(text)) text = cells[c].InnerText.Trim();

                    double cellW = colWidths[c] - 12.0; // 6pt padding on left and right
                    var font = isFirstRow ? headerFont : cellFont;
                    var wrapped = WrapText(text, font, Math.Max(20, cellW), state.Gfx);
                    cellTexts.Add(wrapped);

                    double cellHeight = Math.Max(20.0, (wrapped.Count * 13.0) + 10.0);
                    if (cellHeight > maxRowHeight) maxRowHeight = cellHeight;
                }

                // Check page overflow
                if (state.CurrentY + maxRowHeight > state.PageHeight - state.MarginBottom)
                {
                    state.NewPage();
                }

                double rowX = state.MarginLeft;

                // Render each cell background, borders, and text
                for (int c = 0; c < cells.Count && c < colCount; c++)
                {
                    double w = colWidths[c];
                    var rect = new XRect(rowX, state.CurrentY, w, maxRowHeight);

                    // Header row background shading
                    if (isFirstRow)
                    {
                        state.Gfx.DrawRectangle(headerBrush, rect);
                    }

                    // Cell border
                    state.Gfx.DrawRectangle(borderPen, rect);

                    // Cell text
                    if (c < cellTexts.Count)
                    {
                        var lines = cellTexts[c];
                        var font = isFirstRow ? headerFont : cellFont;
                        var textBrush = isFirstRow ? new XSolidBrush(XColor.FromArgb(30, 41, 59)) : new XSolidBrush(XColor.FromArgb(51, 65, 85));

                        double textY = state.CurrentY + 6.0;
                        foreach (var l in lines)
                        {
                            state.Gfx.DrawString(l, font, textBrush, new XPoint(rowX + 6.0, textY + 9.0));
                            textY += 13.0;
                        }
                    }

                    rowX += w;
                }

                state.CurrentY += maxRowHeight;
                isFirstRow = false;
            }

            state.CurrentY += 8.0;
        }

        #endregion

        #region Embedded Image Rendering

        private static void RenderDrawing(Drawing drawing, LayoutState state)
        {
            try
            {
                var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
                if (blip?.Embed?.Value == null || state.MainPart == null) return;

                string relId = blip.Embed.Value;
                if (!state.MainPart.Parts.Any(p => p.RelationshipId == relId)) return;

                var imagePart = state.MainPart.GetPartById(relId) as ImagePart;
                if (imagePart == null) return;

                using var imgStream = imagePart.GetStream();
                using var ms = new MemoryStream();
                imgStream.CopyTo(ms);
                ms.Position = 0;

                using var xImage = XImage.FromStream(ms);
                double imgW = xImage.PointWidth;
                double imgH = xImage.PointHeight;

                if (imgW <= 0 || imgH <= 0) return;

                // Scale image to fit page margins
                double maxW = state.UsableWidth;
                double maxH = state.UsableHeight * 0.6; // Max 60% of page height
                double scale = Math.Min(1.0, Math.Min(maxW / imgW, maxH / imgH));

                double finalW = imgW * scale;
                double finalH = imgH * scale;

                // Check page overflow
                if (state.CurrentY + finalH > state.PageHeight - state.MarginBottom)
                {
                    state.NewPage();
                }

                // Center image horizontally
                double imgX = state.MarginLeft + (state.UsableWidth - finalW) / 2.0;
                state.Gfx.DrawImage(xImage, imgX, state.CurrentY, finalW, finalH);

                state.CurrentY += finalH + 8.0;
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX_IMAGE_EMBED_ERR", $"Embedded image render skipped: {ex.Message}");
            }
        }

        private static void RenderVmlImageData(VML.ImageData imgData, LayoutState state)
        {
            try
            {
                if (imgData?.RelationshipId?.Value == null || state.MainPart == null) return;

                string relId = imgData.RelationshipId.Value;
                if (!state.MainPart.Parts.Any(p => p.RelationshipId == relId)) return;

                var imagePart = state.MainPart.GetPartById(relId) as ImagePart;
                if (imagePart == null) return;

                using var imgStream = imagePart.GetStream();
                using var ms = new MemoryStream();
                imgStream.CopyTo(ms);
                ms.Position = 0;

                using var xImage = XImage.FromStream(ms);
                double imgW = xImage.PointWidth;
                double imgH = xImage.PointHeight;

                if (imgW <= 0 || imgH <= 0) return;

                double maxW = state.UsableWidth;
                double maxH = state.UsableHeight * 0.6;
                double scale = Math.Min(1.0, Math.Min(maxW / imgW, maxH / imgH));

                double finalW = imgW * scale;
                double finalH = imgH * scale;

                if (state.CurrentY + finalH > state.PageHeight - state.MarginBottom)
                {
                    state.NewPage();
                }

                double imgX = state.MarginLeft + (state.UsableWidth - finalW) / 2.0;
                state.Gfx.DrawImage(xImage, imgX, state.CurrentY, finalW, finalH);

                state.CurrentY += finalH + 8.0;
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX_VML_IMAGE_ERR", $"VML picture render skipped: {ex.Message}");
            }
        }

        #endregion

        #region Helper Classes & Text Wrapping

        private class LayoutState
        {
            public PdfDocument Doc;
            public double PageWidth;
            public double PageHeight;
            public double MarginLeft;
            public double MarginRight;
            public double MarginTop;
            public double MarginBottom;
            public double UsableWidth;
            public double UsableHeight;
            public double CurrentY;
            public XGraphics Gfx;
            public MainDocumentPart MainPart;

            public void NewPage()
            {
                var page = Doc.AddPage();
                page.Width = XUnit.FromPoint(PageWidth);
                page.Height = XUnit.FromPoint(PageHeight);
                Gfx?.Dispose();
                Gfx = XGraphics.FromPdfPage(page);
                CurrentY = MarginTop;
            }
        }

        private class TextFragment
        {
            public string Text;
            public XFont Font;
            public XColor Color;
            public bool IsUnderline;
            public bool IsLineBreak;
            public double FontSize;
            public double Width;
        }

        private static List<List<TextFragment>> WordWrapRuns(List<TextFragment> fragments, double maxWidth, XGraphics gfx)
        {
            var result = new List<List<TextFragment>>();
            var currentLine = new List<TextFragment>();
            double currentLineWidth = 0;

            foreach (var frag in fragments)
            {
                if (frag.IsLineBreak)
                {
                    result.Add(currentLine);
                    currentLine = new List<TextFragment>();
                    currentLineWidth = 0;
                    continue;
                }

                string[] words = frag.Text.Split(new[] { ' ' }, StringSplitOptions.None);
                for (int i = 0; i < words.Length; i++)
                {
                    string word = words[i];
                    string chunk = (i == words.Length - 1) ? word : word + " ";
                    if (string.IsNullOrEmpty(chunk)) continue;

                    var size = gfx.MeasureString(chunk, frag.Font);
                    double w = size.Width;

                    if (currentLineWidth + w > maxWidth && currentLine.Count > 0)
                    {
                        result.Add(currentLine);
                        currentLine = new List<TextFragment>();
                        currentLineWidth = 0;
                    }

                    var wordFrag = new TextFragment
                    {
                        Text = chunk,
                        Font = frag.Font,
                        Color = frag.Color,
                        IsUnderline = frag.IsUnderline,
                        FontSize = frag.FontSize,
                        Width = w
                    };

                    currentLine.Add(wordFrag);
                    currentLineWidth += w;
                }
            }

            if (currentLine.Count > 0)
            {
                result.Add(currentLine);
            }

            return result;
        }

        private static List<string> WrapText(string text, XFont font, double maxWidth, XGraphics gfx)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            var rawLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (var rawLine in rawLines)
            {
                string[] words = rawLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    lines.Add("");
                    continue;
                }

                string current = "";
                foreach (var w in words)
                {
                    string test = string.IsNullOrEmpty(current) ? w : current + " " + w;
                    var size = gfx.MeasureString(test, font);
                    if (size.Width > maxWidth && !string.IsNullOrEmpty(current))
                    {
                        lines.Add(current);
                        current = w;
                    }
                    else
                    {
                        current = test;
                    }
                }
                if (!string.IsNullOrEmpty(current)) lines.Add(current);
            }

            return lines;
        }

        private static void RenderFooters(PdfDocument doc, double pageW, double pageH, double marginBottom)
        {
            var footerFont = new XFont("Segoe UI", 8.5, XFontStyleEx.Regular);
            var footerBrush = new XSolidBrush(XColor.FromArgb(148, 163, 184));

            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                string pageNumText = $"{i + 1} / {doc.PageCount}";
                var size = gfx.MeasureString(pageNumText, footerFont);

                double x = (pageW - size.Width) / 2.0;
                double y = pageH - (marginBottom / 2.0) + (size.Height / 2.0);

                gfx.DrawString(pageNumText, footerFont, footerBrush, new XPoint(x, y));
            }
        }

        #endregion
    }
}
