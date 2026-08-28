// ═══════════════════════════════════════════════════════════════════════
// MarkdownToPdfConverter.cs — Native C# Markdown-to-PDF Engine
// Pure C# implementation using PDFsharp.
// 100% offline, zero external dependencies (no Node.js or WebView2 needed).
// ═══════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FlyShelf.Classes.Utils
{
    public static class MarkdownToPdfConverter
    {
        private const double PageWidth = 595.28;  // A4 Width in points (72 dpi)
        private const double PageHeight = 841.89; // A4 Height in points
        private const double MarginLeft = 45.0;
        private const double MarginRight = 45.0;
        private const double MarginTop = 45.0;
        private const double MarginBottom = 45.0;
        private const double UsableWidth = PageWidth - MarginLeft - MarginRight;
        private const double UsableHeight = PageHeight - MarginTop - MarginBottom;

        /// <summary>
        /// Converts markdown content to a PDF file using pure C# layout and PDFsharp.
        /// </summary>
        public static bool Convert(string markdownPath, string outputPdfPath)
        {
            if (string.IsNullOrEmpty(markdownPath) || !File.Exists(markdownPath))
                return false;

            try
            {
                string mdText = File.ReadAllText(markdownPath);
                string sourceDir = Path.GetDirectoryName(markdownPath) ?? "";
                return ConvertContent(mdText, outputPdfPath, Path.GetFileNameWithoutExtension(markdownPath), sourceDir);
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD2PDF_NATIVE_ERR", $"Markdown Native conversion failed: {ex.Message}");
                return false;
            }
        }

        public static bool ConvertContent(string markdownContent, string outputPdfPath, string documentTitle = "Document", string sourceDir = "")
        {
            try
            {
                FlyShelfFontResolver.EnsureRegistered();
                if (string.IsNullOrEmpty(markdownContent)) markdownContent = "(empty markdown)";

                using var pdfDoc = new PdfDocument();
                pdfDoc.Info.Title = documentTitle;
                pdfDoc.Info.Creator = "FlyShelf Native Markdown Engine";

                var state = new MdLayoutState
                {
                    Doc = pdfDoc,
                    CurrentY = MarginTop,
                    SourceDir = sourceDir
                };

                state.NewPage();

                // Parse markdown into lines and blocks
                var rawLines = markdownContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                int lineIdx = 0;

                // Skip YAML frontmatter if present
                if (rawLines.Length > 0 && rawLines[0].Trim() == "---")
                {
                    lineIdx = 1;
                    while (lineIdx < rawLines.Length && rawLines[lineIdx].Trim() != "---")
                    {
                        lineIdx++;
                    }
                    if (lineIdx < rawLines.Length && rawLines[lineIdx].Trim() == "---") lineIdx++;
                }

                while (lineIdx < rawLines.Length)
                {
                    string rawLine = rawLines[lineIdx];
                    string trimmed = rawLine.Trim();

                    // 1. Empty lines
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        state.CurrentY += 7.0;
                        lineIdx++;
                        continue;
                    }

                    // 2. Fenced code block (``` or ~~~)
                    if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                    {
                        string lang = trimmed.Substring(3).Trim();
                        var codeLines = new List<string>();
                        lineIdx++;
                        while (lineIdx < rawLines.Length && !rawLines[lineIdx].Trim().StartsWith("```") && !rawLines[lineIdx].Trim().StartsWith("~~~"))
                        {
                            codeLines.Add(rawLines[lineIdx]);
                            lineIdx++;
                        }
                        if (lineIdx < rawLines.Length) lineIdx++; // Skip closing fence

                        RenderCodeBlock(codeLines, lang, state);
                        continue;
                    }

                    // 3. Headings (#..######)
                    var headingMatch = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
                    if (headingMatch.Success)
                    {
                        int level = headingMatch.Groups[1].Value.Length;
                        string text = headingMatch.Groups[2].Value.Trim();
                        RenderHeading(text, level, state);
                        lineIdx++;
                        continue;
                    }

                    // 4. Horizontal rule (---, ***, ___)
                    if (Regex.IsMatch(trimmed, @"^(\-{3,}|\*{3,}|_{3,})$"))
                    {
                        RenderHorizontalRule(state);
                        lineIdx++;
                        continue;
                    }

                    // 5. Blockquote (> ...)
                    if (trimmed.StartsWith(">"))
                    {
                        var quoteLines = new List<string>();
                        while (lineIdx < rawLines.Length && rawLines[lineIdx].Trim().StartsWith(">"))
                        {
                            string ql = rawLines[lineIdx].Trim().Substring(1).TrimStart();
                            quoteLines.Add(ql);
                            lineIdx++;
                        }
                        RenderBlockquote(quoteLines, state);
                        continue;
                    }

                    // 6. Markdown Table (| col1 | col2 |)
                    if (trimmed.StartsWith("|") && trimmed.EndsWith("|") && trimmed.Contains("|"))
                    {
                        var tableRows = new List<string>();
                        while (lineIdx < rawLines.Length && rawLines[lineIdx].Trim().StartsWith("|") && rawLines[lineIdx].Trim().EndsWith("|"))
                        {
                            tableRows.Add(rawLines[lineIdx].Trim());
                            lineIdx++;
                        }
                        RenderTable(tableRows, state);
                        continue;
                    }

                    // 7. Bullet or Ordered list
                    var listMatch = Regex.Match(trimmed, @"^(\*|\-|\+|\d+\.)\s+(.*)$");
                    if (listMatch.Success)
                    {
                        string marker = listMatch.Groups[1].Value;
                        string itemText = listMatch.Groups[2].Value;
                        RenderListItem(marker, itemText, state);
                        lineIdx++;
                        continue;
                    }

                    // 8. Normal paragraph
                    var paraLines = new List<string>();
                    while (lineIdx < rawLines.Length)
                    {
                        string cur = rawLines[lineIdx];
                        string curTrim = cur.Trim();
                        if (string.IsNullOrWhiteSpace(curTrim) ||
                            curTrim.StartsWith("#") ||
                            curTrim.StartsWith("```") ||
                            curTrim.StartsWith("~~~") ||
                            curTrim.StartsWith(">") ||
                            (curTrim.StartsWith("|") && curTrim.EndsWith("|")) ||
                            Regex.IsMatch(curTrim, @"^(\*|\-|\+|\d+\.)\s+") ||
                            Regex.IsMatch(curTrim, @"^(\-{3,}|\*{3,}|_{3,})$"))
                        {
                            break;
                        }
                        paraLines.Add(cur);
                        lineIdx++;
                    }

                    if (paraLines.Count > 0)
                    {
                        string paragraphText = string.Join(" ", paraLines.Select(p => p.Trim()));
                        RenderParagraph(paragraphText, state);
                    }
                }

                // Dispose active graphics context before rendering footers and saving
                state.Gfx?.Dispose();
                state.Gfx = null;

                // Render footers with page numbers
                RenderFooters(pdfDoc);

                string dir = Path.GetDirectoryName(outputPdfPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                pdfDoc.Save(outputPdfPath);
                return File.Exists(outputPdfPath) && new FileInfo(outputPdfPath).Length > 0;
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD2PDF_CONVERT_ERR", $"ConvertContent error: {ex.Message}");
                return false;
            }
        }

        #region Block Renderers

        private static void RenderHeading(string text, int level, MdLayoutState state)
        {
            double fontSize = level switch
            {
                1 => 20.0,
                2 => 16.0,
                3 => 13.5,
                4 => 12.0,
                _ => 11.0
            };

            double preSpacing = level == 1 ? 16.0 : 12.0;
            double postSpacing = level == 1 ? 8.0 : 6.0;

            state.CurrentY += preSpacing;

            var font = new XFont("Segoe UI", fontSize, XFontStyleEx.Bold);
            double lineHeight = fontSize * 1.35;

            var wrapped = WrapText(text, font, UsableWidth, state.Gfx);
            foreach (var line in wrapped)
            {
                if (state.CurrentY + lineHeight > PageHeight - MarginBottom)
                {
                    state.NewPage();
                }

                var brush = new XSolidBrush(XColor.FromArgb(15, 23, 42));
                state.Gfx.DrawString(line, font, brush, new XPoint(MarginLeft, state.CurrentY + lineHeight * 0.8));
                state.CurrentY += lineHeight;
            }

            // Draw subtle divider line for H1 and H2
            if (level <= 2)
            {
                state.CurrentY += 2.0;
                var divPen = new XPen(XColor.FromArgb(226, 232, 240), 0.75);
                state.Gfx.DrawLine(divPen, MarginLeft, state.CurrentY, MarginLeft + UsableWidth, state.CurrentY);
                state.CurrentY += 2.0;
            }

            state.CurrentY += postSpacing;
        }

        private static void RenderParagraph(string text, MdLayoutState state)
        {
            // Check for image syntax: ![alt](path)
            var imgMatch = Regex.Match(text, @"^!\[(.*?)\]\((.*?)\)$");
            if (imgMatch.Success)
            {
                string imgPath = imgMatch.Groups[2].Value.Trim();
                if (!Path.IsPathRooted(imgPath) && !string.IsNullOrEmpty(state.SourceDir))
                {
                    imgPath = Path.Combine(state.SourceDir, imgPath);
                }
                if (File.Exists(imgPath))
                {
                    RenderImage(imgPath, state);
                    return;
                }
            }

            var fragments = ParseInlineMarkdown(text);
            var lines = WordWrapFragments(fragments, UsableWidth, state.Gfx);
            double lineHeight = 11.0 * 1.4;

            foreach (var line in lines)
            {
                if (state.CurrentY + lineHeight > PageHeight - MarginBottom)
                {
                    state.NewPage();
                }

                double curX = MarginLeft;
                foreach (var frag in line)
                {
                    // Inline code background pill
                    if (frag.IsCode)
                    {
                        var pillRect = new XRect(curX - 1, state.CurrentY + 1, frag.Width + 2, lineHeight - 2);
                        state.Gfx.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(241, 245, 249)), pillRect, new XSize(2, 2));
                    }

                    var brush = new XSolidBrush(frag.Color);
                    state.Gfx.DrawString(frag.Text, frag.Font, brush, new XPoint(curX, state.CurrentY + lineHeight * 0.75));

                    if (frag.IsLink)
                    {
                        var pen = new XPen(frag.Color, 0.75);
                        state.Gfx.DrawLine(pen, curX, state.CurrentY + lineHeight * 0.85, curX + frag.Width, state.CurrentY + lineHeight * 0.85);
                    }

                    curX += frag.Width;
                }

                state.CurrentY += lineHeight;
            }

            state.CurrentY += 4.0;
        }

        private static void RenderListItem(string marker, string text, MdLayoutState state)
        {
            double indent = 16.0;
            double bulletX = MarginLeft + 2.0;
            double textX = MarginLeft + indent;
            double availWidth = UsableWidth - indent;

            // Check if task list checkbox: [ ] or [x]
            string cleanText = text;
            string checkSymbol = "";
            if (cleanText.StartsWith("[ ] "))
            {
                checkSymbol = "☐";
                cleanText = cleanText.Substring(4);
            }
            else if (cleanText.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
            {
                checkSymbol = "☑";
                cleanText = cleanText.Substring(4);
            }

            var fragments = ParseInlineMarkdown(cleanText);
            var lines = WordWrapFragments(fragments, availWidth, state.Gfx);
            double lineHeight = 11.0 * 1.35;

            if (lines.Count == 0) return;

            if (state.CurrentY + lineHeight > PageHeight - MarginBottom)
            {
                state.NewPage();
            }

            // Draw bullet or checkbox
            var markerFont = new XFont("Segoe UI", 10.0, XFontStyleEx.Bold);
            string displayMarker = !string.IsNullOrEmpty(checkSymbol) ? checkSymbol : (marker.EndsWith(".") ? marker : "•");
            state.Gfx.DrawString(displayMarker, markerFont, new XSolidBrush(XColor.FromArgb(71, 85, 105)),
                new XPoint(bulletX, state.CurrentY + lineHeight * 0.75));

            // Draw lines
            foreach (var line in lines)
            {
                if (state.CurrentY + lineHeight > PageHeight - MarginBottom)
                {
                    state.NewPage();
                }

                double curX = textX;
                foreach (var frag in line)
                {
                    var brush = new XSolidBrush(frag.Color);
                    state.Gfx.DrawString(frag.Text, frag.Font, brush, new XPoint(curX, state.CurrentY + lineHeight * 0.75));
                    curX += frag.Width;
                }

                state.CurrentY += lineHeight;
            }

            state.CurrentY += 2.0;
        }

        private static void RenderBlockquote(List<string> quoteLines, MdLayoutState state)
        {
            state.CurrentY += 4.0;
            double quoteIndent = 16.0;
            double availWidth = UsableWidth - quoteIndent;
            double lineHeight = 10.5 * 1.35;

            var allWrappedLines = new List<string>();
            var quoteFont = new XFont("Segoe UI", 10.0, XFontStyleEx.Italic);

            foreach (var ql in quoteLines)
            {
                var wrapped = WrapText(ql, quoteFont, availWidth, state.Gfx);
                allWrappedLines.AddRange(wrapped);
            }

            double totalBlockHeight = (allWrappedLines.Count * lineHeight) + 6.0;

            if (state.CurrentY + totalBlockHeight > PageHeight - MarginBottom)
            {
                state.NewPage();
            }

            double startY = state.CurrentY;

            // Draw left quote bar
            var barPen = new XPen(XColor.FromArgb(99, 102, 241), 3.0); // Indigo accent
            state.Gfx.DrawLine(barPen, MarginLeft + 2, startY, MarginLeft + 2, startY + totalBlockHeight);

            // Draw quote text
            var quoteBrush = new XSolidBrush(XColor.FromArgb(71, 85, 105));
            double textY = startY + 3.0;

            foreach (var line in allWrappedLines)
            {
                state.Gfx.DrawString(line, quoteFont, quoteBrush, new XPoint(MarginLeft + quoteIndent, textY + lineHeight * 0.75));
                textY += lineHeight;
            }

            state.CurrentY = startY + totalBlockHeight + 6.0;
        }

        private static void RenderCodeBlock(List<string> codeLines, string lang, MdLayoutState state)
        {
            state.CurrentY += 6.0;
            var codeFont = new XFont("Consolas", 9.0, XFontStyleEx.Regular);
            double lineHeight = 13.0;
            double padding = 8.0;

            double blockHeight = (codeLines.Count * lineHeight) + (padding * 2);
            if (!string.IsNullOrEmpty(lang)) blockHeight += 14.0;

            if (state.CurrentY + blockHeight > PageHeight - MarginBottom)
            {
                state.NewPage();
            }

            double blockY = state.CurrentY;

            // Background card (dark modern slate)
            var bgBrush = new XSolidBrush(XColor.FromArgb(15, 23, 42)); // Slate-900
            var bgRect = new XRect(MarginLeft, blockY, UsableWidth, blockHeight);
            state.Gfx.DrawRoundedRectangle(bgBrush, bgRect, new XSize(4, 4));

            // Language badge header
            double codeStartY = blockY + padding;
            if (!string.IsNullOrEmpty(lang))
            {
                var langFont = new XFont("Segoe UI", 8.0, XFontStyleEx.Bold);
                var langBrush = new XSolidBrush(XColor.FromArgb(148, 163, 184));
                state.Gfx.DrawString(lang.ToUpperInvariant(), langFont, langBrush, new XPoint(MarginLeft + padding, blockY + 12));

                var divPen = new XPen(XColor.FromArgb(30, 41, 59), 0.75);
                state.Gfx.DrawLine(divPen, MarginLeft, blockY + 16, MarginLeft + UsableWidth, blockY + 16);
                codeStartY += 14.0;
            }

            // Draw code lines
            var textBrush = new XSolidBrush(XColor.FromArgb(226, 232, 240)); // Slate-200
            double lineY = codeStartY;

            foreach (var cl in codeLines)
            {
                // Truncate or wrap to fit container
                string displayCode = cl;
                var size = state.Gfx.MeasureString(displayCode, codeFont);
                if (size.Width > UsableWidth - (padding * 2))
                {
                    while (displayCode.Length > 4 && state.Gfx.MeasureString(displayCode + "...", codeFont).Width > UsableWidth - (padding * 2))
                    {
                        displayCode = displayCode.Substring(0, displayCode.Length - 1);
                    }
                    displayCode += "...";
                }

                state.Gfx.DrawString(displayCode, codeFont, textBrush, new XPoint(MarginLeft + padding, lineY + lineHeight * 0.75));
                lineY += lineHeight;
            }

            state.CurrentY = blockY + blockHeight + 8.0;
        }

        private static void RenderTable(List<string> tableRows, MdLayoutState state)
        {
            if (tableRows.Count < 2) return;

            state.CurrentY += 6.0;

            // Parse cells per row
            var parsedRows = new List<List<string>>();
            foreach (var tr in tableRows)
            {
                string clean = tr.Trim('|');
                var cells = clean.Split('|').Select(c => c.Trim()).ToList();
                parsedRows.Add(cells);
            }

            // Filter out alignment divider row (e.g. |---|---|)
            parsedRows.RemoveAll(r => r.All(c => Regex.IsMatch(c, @"^:?-+:?$")));
            if (parsedRows.Count == 0) return;

            int colCount = parsedRows.Max(r => r.Count);
            if (colCount == 0) return;

            double colWidth = UsableWidth / colCount;
            var cellFont = new XFont("Segoe UI", 9.0, XFontStyleEx.Regular);
            var headerFont = new XFont("Segoe UI", 9.0, XFontStyleEx.Bold);
            var borderPen = new XPen(XColor.FromArgb(203, 213, 225), 0.75);
            var headerBg = new XSolidBrush(XColor.FromArgb(241, 245, 249));

            bool isHeader = true;
            foreach (var row in parsedRows)
            {
                double rowHeight = 20.0;
                var wrappedCells = new List<List<string>>();

                for (int c = 0; c < colCount; c++)
                {
                    string cellText = c < row.Count ? row[c] : "";
                    var font = isHeader ? headerFont : cellFont;
                    var wrapped = WrapText(cellText, font, colWidth - 10.0, state.Gfx);
                    wrappedCells.Add(wrapped);
                    double cellH = Math.Max(20.0, (wrapped.Count * 12.0) + 8.0);
                    if (cellH > rowHeight) rowHeight = cellH;
                }

                if (state.CurrentY + rowHeight > PageHeight - MarginBottom)
                {
                    state.NewPage();
                }

                double rowX = MarginLeft;
                for (int c = 0; c < colCount; c++)
                {
                    var rect = new XRect(rowX, state.CurrentY, colWidth, rowHeight);
                    if (isHeader) state.Gfx.DrawRectangle(headerBg, rect);
                    state.Gfx.DrawRectangle(borderPen, rect);

                    if (c < wrappedCells.Count)
                    {
                        var lines = wrappedCells[c];
                        var font = isHeader ? headerFont : cellFont;
                        var brush = isHeader ? new XSolidBrush(XColor.FromArgb(30, 41, 59)) : new XSolidBrush(XColor.FromArgb(51, 65, 85));

                        double textY = state.CurrentY + 4.0;
                        foreach (var l in lines)
                        {
                            state.Gfx.DrawString(l, font, brush, new XPoint(rowX + 5.0, textY + 9.0));
                            textY += 12.0;
                        }
                    }

                    rowX += colWidth;
                }

                state.CurrentY += rowHeight;
                isHeader = false;
            }

            state.CurrentY += 8.0;
        }

        private static void RenderHorizontalRule(MdLayoutState state)
        {
            state.CurrentY += 8.0;
            if (state.CurrentY > PageHeight - MarginBottom)
            {
                state.NewPage();
            }

            var pen = new XPen(XColor.FromArgb(203, 213, 225), 1.0);
            state.Gfx.DrawLine(pen, MarginLeft, state.CurrentY, MarginLeft + UsableWidth, state.CurrentY);
            state.CurrentY += 8.0;
        }

        private static void RenderImage(string imagePath, MdLayoutState state)
        {
            try
            {
                using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var xImg = XImage.FromStream(fs);

                double w = xImg.PointWidth;
                double h = xImg.PointHeight;
                if (w <= 0 || h <= 0) return;

                double maxW = UsableWidth;
                double maxH = UsableHeight * 0.55;
                double scale = Math.Min(1.0, Math.Min(maxW / w, maxH / h));

                double finalW = w * scale;
                double finalH = h * scale;

                if (state.CurrentY + finalH > PageHeight - MarginBottom)
                {
                    state.NewPage();
                }

                double imgX = MarginLeft + (UsableWidth - finalW) / 2.0;
                state.Gfx.DrawImage(xImg, imgX, state.CurrentY, finalW, finalH);

                state.CurrentY += finalH + 8.0;
            }
            catch (Exception ex)
            {
                Logger.LogAction("MD_IMAGE_RENDER_ERR", $"Failed to embed {Path.GetFileName(imagePath)}: {ex.Message}");
            }
        }

        #endregion

        #region Inline Markdown Parser & Helpers

        private class MdLayoutState
        {
            public PdfDocument Doc;
            public double CurrentY;
            public XGraphics Gfx;
            public string SourceDir;

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

        private class MdInlineFragment
        {
            public string Text;
            public XFont Font;
            public XColor Color;
            public bool IsBold;
            public bool IsItalic;
            public bool IsCode;
            public bool IsLink;
            public double Width;
        }

        private static List<MdInlineFragment> ParseInlineMarkdown(string text)
        {
            var fragments = new List<MdInlineFragment>();
            if (string.IsNullOrEmpty(text)) return fragments;

            // Pattern for inline code, bold, italic, links
            var regex = new Regex(@"(`(?<codeblock>[^`]+)`)|(\*\*(?<bold>[^\*]+)\*\*)|(\*(?<italic>[^\*]+)\*)|(\[(?<linktext>[^\]]+)\]\((?<linkurl>[^\)]+)\))");

            int lastIdx = 0;
            var matches = regex.Matches(text);

            foreach (Match m in matches)
            {
                if (m.Index > lastIdx)
                {
                    string plain = text.Substring(lastIdx, m.Index - lastIdx);
                    fragments.Add(CreateFragment(plain, false, false, false, false));
                }

                if (m.Groups["codeblock"].Success)
                {
                    fragments.Add(CreateFragment(m.Groups["codeblock"].Value, false, false, true, false));
                }
                else if (m.Groups["bold"].Success)
                {
                    fragments.Add(CreateFragment(m.Groups["bold"].Value, true, false, false, false));
                }
                else if (m.Groups["italic"].Success)
                {
                    fragments.Add(CreateFragment(m.Groups["italic"].Value, false, true, false, false));
                }
                else if (m.Groups["linktext"].Success)
                {
                    fragments.Add(CreateFragment(m.Groups["linktext"].Value, false, false, false, true));
                }

                lastIdx = m.Index + m.Length;
            }

            if (lastIdx < text.Length)
            {
                string remainder = text.Substring(lastIdx);
                fragments.Add(CreateFragment(remainder, false, false, false, false));
            }

            return fragments;
        }

        private static MdInlineFragment CreateFragment(string text, bool bold, bool italic, bool code, bool link)
        {
            double fontSize = 11.0;
            string fontName = code ? "Consolas" : "Segoe UI";
            var style = XFontStyleEx.Regular;

            if (bold && italic) style = XFontStyleEx.BoldItalic;
            else if (bold) style = XFontStyleEx.Bold;
            else if (italic) style = XFontStyleEx.Italic;

            var font = new XFont(fontName, fontSize, style);
            var color = link ? XColor.FromArgb(37, 99, 235) : (code ? XColor.FromArgb(225, 29, 72) : XColor.FromArgb(30, 41, 59));

            return new MdInlineFragment
            {
                Text = text,
                Font = font,
                Color = color,
                IsBold = bold,
                IsItalic = italic,
                IsCode = code,
                IsLink = link
            };
        }

        private static List<List<MdInlineFragment>> WordWrapFragments(List<MdInlineFragment> fragments, double maxWidth, XGraphics gfx)
        {
            var lines = new List<List<MdInlineFragment>>();
            var currentLine = new List<MdInlineFragment>();
            double currentLineWidth = 0;

            foreach (var frag in fragments)
            {
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
                        lines.Add(currentLine);
                        currentLine = new List<MdInlineFragment>();
                        currentLineWidth = 0;
                    }

                    var wordFrag = new MdInlineFragment
                    {
                        Text = chunk,
                        Font = frag.Font,
                        Color = frag.Color,
                        IsBold = frag.IsBold,
                        IsItalic = frag.IsItalic,
                        IsCode = frag.IsCode,
                        IsLink = frag.IsLink,
                        Width = w
                    };

                    currentLine.Add(wordFrag);
                    currentLineWidth += w;
                }
            }

            if (currentLine.Count > 0) lines.Add(currentLine);
            return lines;
        }

        private static List<string> WrapText(string text, XFont font, double maxWidth, XGraphics gfx)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return lines;

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

            return lines;
        }

        private static void RenderFooters(PdfDocument doc)
        {
            var footerFont = new XFont("Segoe UI", 8.5, XFontStyleEx.Regular);
            var footerBrush = new XSolidBrush(XColor.FromArgb(148, 163, 184));

            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                string pageNumText = $"{i + 1} / {doc.PageCount}";
                var size = gfx.MeasureString(pageNumText, footerFont);

                double x = (PageWidth - size.Width) / 2.0;
                double y = PageHeight - (MarginBottom / 2.0) + (size.Height / 2.0);

                gfx.DrawString(pageNumText, footerFont, footerBrush, new XPoint(x, y));
            }
        }

        #endregion
    }
}
