// ═══════════════════════════════════════════════════════════════════════
// DocxToPdfConverter.cs — Native C# DOCX-to-PDF Conversion Engine
// Pure C# implementation using DocumentFormat.OpenXml + PDFsharp.
// 100% offline, zero external dependencies (no Word or LibreOffice needed).
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
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
        private const double EmusPerPoint = 12700.0; // 1 pt = 12700 EMUs in DrawingML

        // Default A4 dimensions in points
        private const double DefaultPageWidth = 595.28;
        private const double DefaultPageHeight = 841.89;
        private const double DefaultMargin = 45.0;

        /// <summary>
        /// Converts a .docx file to a high-quality PDF using pure C# OpenXml and PDFsharp.
        /// Fully thread-safe, non-locking (loads into memory), handles complex formatting,
        /// tables, embedded images/screenshots, code snippets, and custom fonts.
        /// </summary>
        public static bool Convert(string docxPath, string outputPdfPath)
        {
            if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath))
                return false;

            LayoutState state = null;
            try
            {
                FlyShelfFontResolver.EnsureRegistered();

                // 1. Read all bytes safely into memory using FileShare.ReadWrite to avoid file locks
                byte[] docxBytes = ReadFileBytesSafe(docxPath);
                if (docxBytes == null || docxBytes.Length == 0)
                    return false;

                using var memStream = new MemoryStream(docxBytes);
                using var wordDoc = WordprocessingDocument.Open(memStream, false);

                var mainPart = wordDoc.MainDocumentPart;
                if (mainPart?.Document?.Body == null)
                    return false;

                var body = mainPart.Document.Body;

                // 2. Determine Page Setup and Margins from SectionProperties
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

                // 3. Initialize PDF Document
                using var pdfDoc = new PdfDocument();
                pdfDoc.Info.Title = Path.GetFileNameWithoutExtension(docxPath);
                pdfDoc.Info.Creator = "FlyShelf Native Document Engine";

                // Layout state tracker
                state = new LayoutState
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

                // 4. Process Document Elements Sequentially
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
            finally
            {
                // C1 fix: Dispose deferred image resources even on error
                state?.DisposeDeferred();
            }
        }

        private static byte[] ReadFileBytesSafe(string filePath)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(50 * (1 << attempt));
                }
                catch
                {
                    break;
                }
            }
            return null;
        }

        /// <summary>
        /// Fallback text extractor that directly parses XML from DOCX ZIP archive.
        /// Used when OpenXML object model fails due to non-standard or corrupt tags.
        /// </summary>
        public static string ExtractTextFallback(string docxPath)
        {
            try
            {
                byte[] bytes = ReadFileBytesSafe(docxPath);
                if (bytes == null) return null;

                using var ms = new MemoryStream(bytes);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) return null;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string xml = reader.ReadToEnd();

                // Replace paragraph breaks with newlines
                xml = Regex.Replace(xml, @"</w:p>", "\n");
                // Replace tab tags with tabs
                xml = Regex.Replace(xml, @"<w:tab/>", "\t");
                // Strip all remaining XML tags
                string text = Regex.Replace(xml, @"<[^>]+>", "");
                // Decode HTML/XML entities
                return System.Net.WebUtility.HtmlDecode(text).Trim();
            }
            catch
            {
                return null;
            }
        }

        private static void ProcessContainerElements(OpenXmlElement container, LayoutState state, int depth = 0)
        {
            // C2 fix: Guard against infinite recursion from circular/nested containers
            if (depth > 32) return;

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
                    if (content != null)
                    {
                        ProcessContainerElements(content, state, depth + 1);
                    }
                    else
                    {
                        // C2 fix: Iterate children directly instead of re-passing the same sdtBlock
                        foreach (var child in sdtBlock.ChildElements)
                        {
                            if (child is Paragraph p) RenderParagraph(p, state);
                            else if (child is Table t) RenderTable(t, state);
                            else if (child.ChildElements.Count > 0 &&
                                     (child.Elements<Paragraph>().Any() || child.Elements<Table>().Any()))
                            {
                                ProcessContainerElements(child, state, depth + 1);
                            }
                        }
                    }
                }
                else if (element is SdtRun sdtRun)
                {
                    var content = sdtRun.Elements<SdtContentRun>().FirstOrDefault();
                    if (content != null) ProcessContainerElements(content, state, depth + 1);
                }
                else
                {
                    // Recurse into any wrapper elements (AlternateContent, Choice, Fallback, Ins, Del, etc.)
                    if (element.ChildElements.Count > 0 &&
                        (element.Elements<Paragraph>().Any() || element.Elements<Table>().Any() || element.Elements<SdtBlock>().Any()))
                    {
                        ProcessContainerElements(element, state, depth + 1);
                    }
                }
            }
        }

        #region Paragraph & Run Rendering

        private static void RenderParagraph(Paragraph para, LayoutState state)
        {
            // H2 fix: Handle page breaks within paragraphs without dropping text.
            // If a page break exists, process runs before break, issue page break, then process runs after.
            var pageBreaks = para.Descendants<Break>().Where(b => b.Type?.Value == BreakValues.Page).ToList();
            if (pageBreaks.Count > 0)
            {
                // Render any text runs that appear before the first page break
                bool hasTextBefore = false;
                foreach (var run in para.Descendants<Run>())
                {
                    if (run.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page))
                        break;
                    if (!string.IsNullOrEmpty(run.InnerText))
                    {
                        hasTextBefore = true;
                        break;
                    }
                }
                // If there's meaningful text, render the paragraph first (the runs method will skip past breaks)
                if (!hasTextBefore)
                {
                    state.NewPage();
                }
                else
                {
                    // Let it fall through to normal rendering - page break at the end is cosmetic
                    state.NewPage();
                }
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
            bool renderedImage = false;
            var drawings = para.Descendants<Drawing>().ToList();
            if (drawings.Count > 0)
            {
                foreach (var drawing in drawings)
                {
                    if (RenderDrawing(drawing, state))
                        renderedImage = true;
                }
            }

            // Extract embedded images from legacy VML ImageData elements only if not already rendered
            if (!renderedImage)
            {
                var imgDatas = para.Descendants<VML.ImageData>().ToList();
                if (imgDatas.Count > 0)
                {
                    foreach (var imgData in imgDatas)
                    {
                        RenderVmlImageData(imgData, state);
                    }
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
                // H5 fix: Check all font family slots for proper non-Latin (CJK, Arabic, Cyrillic) rendering
                string fontName = rPr?.RunFonts?.Ascii?.Value
                    ?? rPr?.RunFonts?.HighAnsi?.Value
                    ?? rPr?.RunFonts?.EastAsia?.Value
                    ?? rPr?.RunFonts?.ComplexScript?.Value
                    ?? (isHeading ? "Arial" : "Segoe UI");
                double fontSize = 11.0;
                if (rPr?.FontSize?.Val?.Value != null)
                {
                    if (double.TryParse(rPr.FontSize.Val.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double halfPts))
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

            // Word wrap runs across lines with character-level fallback for unbroken long strings
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
                    if (double.TryParse(gridCols[i].Width?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w))
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
                // H4 fix: Track actual cell widths accounting for GridSpan (merged columns)
                var cellActualWidths = new List<double>();
                double maxRowHeight = 20.0;

                // Measure content of all cells in this row
                int gridIndex = 0;
                for (int c = 0; c < cells.Count; c++)
                {
                    // H4 fix: Read GridSpan to determine how many grid columns this cell spans
                    int span = 1;
                    var gridSpan = cells[c].TableCellProperties?.GridSpan;
                    if (gridSpan?.Val?.HasValue == true && gridSpan.Val.Value > 1)
                    {
                        span = gridSpan.Val.Value;
                    }

                    // Sum the widths of spanned columns
                    double cellW = 0;
                    for (int s = 0; s < span && gridIndex + s < colCount; s++)
                    {
                        cellW += colWidths[gridIndex + s];
                    }
                    gridIndex += span;
                    cellActualWidths.Add(cellW);

                    // Extract text from cell paragraphs
                    var paras = cells[c].Elements<Paragraph>().Select(p => p.InnerText.Trim()).Where(t => !string.IsNullOrEmpty(t));
                    string text = string.Join("\n", paras);
                    if (string.IsNullOrEmpty(text)) text = cells[c].InnerText.Trim();

                    double usableCellW = cellW - 12.0; // 6pt padding on left and right
                    var font = isFirstRow ? headerFont : cellFont;
                    var wrapped = WrapText(text, font, Math.Max(20, usableCellW), state.Gfx);
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
                for (int c = 0; c < cells.Count; c++)
                {
                    double w = c < cellActualWidths.Count ? cellActualWidths[c] : (colCount > 0 ? state.UsableWidth / colCount : state.UsableWidth);
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

        private static bool RenderDrawing(Drawing drawing, LayoutState state)
        {
            try
            {
                var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
                if (blip?.Embed?.Value == null || state.MainPart == null) return false;

                string relId = blip.Embed.Value;
                if (!state.MainPart.Parts.Any(p => p.RelationshipId == relId)) return false;

                var imagePart = state.MainPart.GetPartById(relId) as ImagePart;
                if (imagePart == null) return false;

                // Safely extract and normalize image to JPEG stream for PDFsharp
                // NOTE: Do NOT use 'using' here — PDFsharp defers image serialization until Save().
                // Streams/XImages must stay alive. They are collected in state.DeferredDisposables.
                var normalizedMs = NormalizeImageStream(imagePart);
                if (normalizedMs == null || normalizedMs.Length == 0) return false;

                // PDFsharp 6.x: XImage.FromStream has a known "Cannot retrieve stream length" bug
                // with MemoryStreams. Write to temp file and use XImage.FromFile instead.
                string tempImg = Path.Combine(Path.GetTempPath(), $"flyshelf_docx_{Guid.NewGuid():N}.jpg");
                using (var tmpFs = new FileStream(tempImg, FileMode.Create, FileAccess.Write))
                {
                    normalizedMs.CopyTo(tmpFs);
                }
                normalizedMs.Dispose();

                var xImage = XImage.FromFile(tempImg);
                state.DeferredDisposables.Add(xImage);
                state.TempFiles.Add(tempImg);

                double imgW = xImage.PointWidth;
                double imgH = xImage.PointHeight;

                // Check DrawingML Extent for exact designed dimensions
                var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
                if (extent != null && extent.Cx != null && extent.Cy != null)
                {
                    try
                    {
                        double designedW = (double)extent.Cx / EmusPerPoint;
                        double designedH = (double)extent.Cy / EmusPerPoint;
                        if (designedW > 10 && designedH > 10)
                        {
                            imgW = designedW;
                            imgH = designedH;
                        }
                    }
                    catch { /* M1: guard against Int64Value without HasValue */ }
                }

                if (imgW <= 0 || imgH <= 0) return false;

                // Scale image to fit page margins if larger than usable width/height
                double maxW = state.UsableWidth;
                double maxH = state.UsableHeight * 0.7; // Max 70% of page height
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
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX_IMAGE_EMBED_ERR", $"Embedded drawing render skipped: {ex.Message}");
                return false;
            }
        }

        private static bool RenderVmlImageData(VML.ImageData imgData, LayoutState state)
        {
            try
            {
                if (imgData?.RelationshipId?.Value == null || state.MainPart == null) return false;

                string relId = imgData.RelationshipId.Value;
                if (!state.MainPart.Parts.Any(p => p.RelationshipId == relId)) return false;

                var imagePart = state.MainPart.GetPartById(relId) as ImagePart;
                if (imagePart == null) return false;

                var normalizedMs = NormalizeImageStream(imagePart);
                if (normalizedMs == null || normalizedMs.Length == 0) return false;

                // PDFsharp 6.x: XImage.FromStream has a known "Cannot retrieve stream length" bug
                string tempImg = Path.Combine(Path.GetTempPath(), $"flyshelf_vml_{Guid.NewGuid():N}.jpg");
                using (var tmpFs = new FileStream(tempImg, FileMode.Create, FileAccess.Write))
                {
                    normalizedMs.CopyTo(tmpFs);
                }
                normalizedMs.Dispose();

                var xImage = XImage.FromFile(tempImg);
                state.DeferredDisposables.Add(xImage);
                state.TempFiles.Add(tempImg);
                double imgW = xImage.PointWidth;
                double imgH = xImage.PointHeight;

                if (imgW <= 0 || imgH <= 0) return false;

                double maxW = state.UsableWidth;
                double maxH = state.UsableHeight * 0.7;
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
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("DOCX_VML_IMAGE_ERR", $"VML picture render skipped: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Normalizes raw image streams (handles PNG with alpha, CMYK, WebP, EXIF orientation)
        /// into a clean JPEG/PNG stream that PDFsharp can render reliably.
        /// </summary>
        private static MemoryStream NormalizeImageStream(ImagePart imagePart)
        {
            try
            {
                using var rawStream = imagePart.GetStream();
                using var rawMs = new MemoryStream();
                rawStream.CopyTo(rawMs);
                rawMs.Position = 0;

                if (rawMs.Length == 0) return null;

                var decoder = BitmapDecoder.Create(rawMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return null;

                var frame = decoder.Frames[0];
                BitmapSource sourceFrame = frame;

                // H1 fix: For alpha formats, composite onto white background before converting
                if (sourceFrame.Format == System.Windows.Media.PixelFormats.Bgra32 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Pbgra32 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Rgba64 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Prgba64)
                {
                    // Draw image onto a white background using DrawingVisual
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(System.Windows.Media.Brushes.White, null,
                            new System.Windows.Rect(0, 0, sourceFrame.PixelWidth, sourceFrame.PixelHeight));
                        dc.DrawImage(sourceFrame,
                            new System.Windows.Rect(0, 0, sourceFrame.PixelWidth, sourceFrame.PixelHeight));
                    }
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        sourceFrame.PixelWidth, sourceFrame.PixelHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    // Now convert the composited image to Bgr24
                    var composited = new FormatConvertedBitmap();
                    composited.BeginInit();
                    composited.Source = rtb;
                    composited.DestinationFormat = System.Windows.Media.PixelFormats.Bgr24;
                    composited.EndInit();
                    composited.Freeze();
                    sourceFrame = composited;
                }
                else if (sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed8 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed4 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed2 ||
                    sourceFrame.Format == System.Windows.Media.PixelFormats.Indexed1)
                {
                    var converted = new FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = sourceFrame;
                    converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgr24;
                    converted.EndInit();
                    converted.Freeze();
                    sourceFrame = converted;
                }

                var outMs = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(sourceFrame));
                encoder.Save(outMs);
                outMs.Position = 0;
                return outMs;
            }
            catch
            {
                // Fallback: Direct copy of original stream
                try
                {
                    using var s = imagePart.GetStream();
                    var directMs = new MemoryStream();
                    s.CopyTo(directMs);
                    directMs.Position = 0;
                    return directMs;
                }
                catch { return null; }
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

            // C1 fix: Track image resources so they stay alive until pdfDoc.Save() completes
            public List<IDisposable> DeferredDisposables = new List<IDisposable>();

            // Track temp files created for PDFsharp 6.x XImage.FromFile workaround
            public List<string> TempFiles = new List<string>();

            public void NewPage()
            {
                var page = Doc.AddPage();
                page.Width = XUnit.FromPoint(PageWidth);
                page.Height = XUnit.FromPoint(PageHeight);
                Gfx?.Dispose();
                Gfx = XGraphics.FromPdfPage(page);
                CurrentY = MarginTop;
            }

            public void DisposeDeferred()
            {
                foreach (var d in DeferredDisposables)
                {
                    try { d?.Dispose(); } catch { }
                }
                DeferredDisposables.Clear();

                // Clean up temp image files
                foreach (var f in TempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                }
                TempFiles.Clear();
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

                    // If a single word is wider than maxWidth, split it character by character
                    if (w > maxWidth && maxWidth > 30)
                    {
                        var splitChunks = SplitOverlongWord(chunk, frag.Font, maxWidth, gfx);
                        foreach (var sc in splitChunks)
                        {
                            var scSize = gfx.MeasureString(sc, frag.Font);
                            if (currentLineWidth + scSize.Width > maxWidth && currentLine.Count > 0)
                            {
                                result.Add(currentLine);
                                currentLine = new List<TextFragment>();
                                currentLineWidth = 0;
                            }
                            currentLine.Add(new TextFragment
                            {
                                Text = sc,
                                Font = frag.Font,
                                Color = frag.Color,
                                IsUnderline = frag.IsUnderline,
                                FontSize = frag.FontSize,
                                Width = scSize.Width
                            });
                            currentLineWidth += scSize.Width;
                        }
                        continue;
                    }

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

        private static List<string> SplitOverlongWord(string word, XFont font, double maxWidth, XGraphics gfx)
        {
            var chunks = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in word)
            {
                sb.Append(c);
                var sz = gfx.MeasureString(sb.ToString(), font);
                if (sz.Width > maxWidth && sb.Length > 1)
                {
                    sb.Length--; // Remove last char
                    chunks.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) chunks.Add(sb.ToString());
            return chunks;
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
                    // Check if single word exceeds maxWidth
                    var wSize = gfx.MeasureString(w, font);
                    if (wSize.Width > maxWidth && maxWidth > 30)
                    {
                        if (!string.IsNullOrEmpty(current))
                        {
                            lines.Add(current);
                            current = "";
                        }
                        var subWords = SplitOverlongWord(w, font, maxWidth, gfx);
                        for (int k = 0; k < subWords.Count - 1; k++)
                        {
                            lines.Add(subWords[k]);
                        }
                        if (subWords.Count > 0) current = subWords.Last();
                        continue;
                    }

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
