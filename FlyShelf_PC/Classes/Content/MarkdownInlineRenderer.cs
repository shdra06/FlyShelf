using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Converts markdown text into WPF TextBlock.Inlines for rich rendering
    /// in the clipboard card preview. This is much lighter than WebView2 or
    /// FlowDocument and works well inside a virtualized ListView.
    /// 
    /// Attached property usage:
    ///   local:MarkdownInlineRenderer.MarkdownText="{Binding MarkdownPreviewContent}"
    /// </summary>
    public static class MarkdownInlineRenderer
    {
        // ═══════════════════════════════════════════════════════════
        // ATTACHED PROPERTY
        // ═══════════════════════════════════════════════════════════

        public static readonly DependencyProperty MarkdownTextProperty =
            DependencyProperty.RegisterAttached(
                "MarkdownText",
                typeof(string),
                typeof(MarkdownInlineRenderer),
                new PropertyMetadata(null, OnMarkdownTextChanged));

        public static string GetMarkdownText(DependencyObject obj) => (string)obj.GetValue(MarkdownTextProperty);
        public static void SetMarkdownText(DependencyObject obj, string value) => obj.SetValue(MarkdownTextProperty, value);

        // ═══════════════════════════════════════════════════════════
        // THEME COLORS (adapts to dark/light mode via hardcoded dark palette)
        // ═══════════════════════════════════════════════════════════

        private static readonly Brush HeadingBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)); // Catppuccin text
        private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromRgb(0xBA, 0xC2, 0xDE));    // Catppuccin subtext
        private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8));    // Catppuccin pink
        private static readonly Brush CodeBgBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)); // Subtle bg
        private static readonly Brush QuoteBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xE2, 0xD5));   // Catppuccin teal
        private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA));    // Catppuccin blue
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        private static readonly Brush BulletBrush = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8));  // Catppuccin overlay
        private static readonly Brush StrikeBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, monospace");
        private static readonly FontFamily SansFont = new FontFamily("Segoe UI, Inter");

        // Inline regex patterns (compiled for performance)
        private static readonly Regex RxBold = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
        private static readonly Regex RxItalic = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
        private static readonly Regex RxCode = new(@"`([^`]+?)`", RegexOptions.Compiled);
        private static readonly Regex RxLink = new(@"\[([^\]]+)\]\(([^\)]+)\)", RegexOptions.Compiled);
        private static readonly Regex RxStrike = new(@"~~(.+?)~~", RegexOptions.Compiled);
        private static readonly Regex RxHeading = new(@"^#{1,6}\s+", RegexOptions.Compiled);
        private static readonly Regex RxUnorderedList = new(@"^\s*[-*+]\s+", RegexOptions.Compiled);
        private static readonly Regex RxOrderedList = new(@"^\s*(\d+\.)\s+", RegexOptions.Compiled);
        private static readonly Regex RxBlockquote = new(@"^>\s?", RegexOptions.Compiled);
        private static readonly Regex RxHorizontalRule = new(@"^(---+|\*\*\*+|___+)\s*$", RegexOptions.Compiled);
        private static readonly Regex RxCodeFence = new(@"^```", RegexOptions.Compiled);

        static MarkdownInlineRenderer()
        {
            // Freeze brushes for cross-thread safety
            HeadingBrush.Freeze(); BodyBrush.Freeze(); CodeBrush.Freeze();
            CodeBgBrush.Freeze(); QuoteBrush.Freeze(); LinkBrush.Freeze();
            DimBrush.Freeze(); BulletBrush.Freeze(); StrikeBrush.Freeze();
        }

        // ═══════════════════════════════════════════════════════════
        // ENTRY POINT
        // ═══════════════════════════════════════════════════════════

        private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock textBlock) return;

            textBlock.Inlines.Clear();
            var markdown = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(markdown))
                return;

            try
            {
                RenderMarkdown(textBlock, markdown);
            }
            catch
            {
                // Fallback: show raw text if rendering fails
                textBlock.Inlines.Add(new Run(markdown) { Foreground = BodyBrush });
            }
        }

        // ═══════════════════════════════════════════════════════════
        // BLOCK-LEVEL RENDERER (processes line by line)
        // ═══════════════════════════════════════════════════════════

        private static void RenderMarkdown(TextBlock tb, string markdown)
        {
            var lines = markdown.Split('\n');
            bool inCodeBlock = false;
            bool isFirstElement = true;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');

                // ─── Code fence toggle ───
                if (RxCodeFence.IsMatch(line.TrimStart()))
                {
                    inCodeBlock = !inCodeBlock;
                    if (inCodeBlock && !isFirstElement)
                        tb.Inlines.Add(new LineBreak());
                    isFirstElement = false;
                    continue;
                }

                // ─── Inside code block ───
                if (inCodeBlock)
                {
                    if (!isFirstElement)
                        tb.Inlines.Add(new LineBreak());
                    isFirstElement = false;

                    tb.Inlines.Add(new Run(line)
                    {
                        FontFamily = MonoFont,
                        Foreground = CodeBrush,
                        FontSize = 11,
                        Background = CodeBgBrush
                    });
                    continue;
                }

                // Skip empty lines (add spacing)
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!isFirstElement)
                        tb.Inlines.Add(new LineBreak());
                    isFirstElement = false;
                    continue;
                }

                if (!isFirstElement)
                    tb.Inlines.Add(new LineBreak());
                isFirstElement = false;

                // ─── Horizontal rule ───
                if (RxHorizontalRule.IsMatch(line.Trim()))
                {
                    tb.Inlines.Add(new Run("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")
                    {
                        Foreground = DimBrush,
                        FontSize = 8
                    });
                    continue;
                }

                // ─── Headings ───
                var headingMatch = RxHeading.Match(line);
                if (headingMatch.Success)
                {
                    int level = headingMatch.Value.TrimEnd().Length; // Count #s
                    string headingText = line.Substring(headingMatch.Length).TrimEnd();

                    double fontSize = level switch
                    {
                        1 => 17,
                        2 => 15,
                        3 => 13.5,
                        _ => 12.5
                    };

                    AddInlinesParsed(tb, headingText, fontSize, FontWeights.Bold, HeadingBrush);
                    continue;
                }

                // ─── Blockquote ───
                if (RxBlockquote.IsMatch(line))
                {
                    string quoteText = RxBlockquote.Replace(line, "", 1);
                    tb.Inlines.Add(new Run("┃ ") { Foreground = QuoteBrush, FontWeight = FontWeights.Bold });
                    AddInlinesParsed(tb, quoteText, 12, FontWeights.Normal, QuoteBrush, FontStyles.Italic);
                    continue;
                }

                // ─── Unordered list ───
                if (RxUnorderedList.IsMatch(line))
                {
                    string itemText = RxUnorderedList.Replace(line, "", 1);
                    int indent = line.Length - line.TrimStart().Length;
                    string prefix = indent > 0 ? new string(' ', indent) + "  • " : "  • ";
                    tb.Inlines.Add(new Run(prefix) { Foreground = BulletBrush, FontWeight = FontWeights.Bold });
                    AddInlinesParsed(tb, itemText, 12, FontWeights.Normal, BodyBrush);
                    continue;
                }

                // ─── Ordered list ───
                var orderedMatch = RxOrderedList.Match(line);
                if (orderedMatch.Success)
                {
                    string itemText = RxOrderedList.Replace(line, "", 1);
                    string number = orderedMatch.Groups[1].Value;
                    tb.Inlines.Add(new Run($"  {number} ") { Foreground = BulletBrush, FontWeight = FontWeights.SemiBold });
                    AddInlinesParsed(tb, itemText, 12, FontWeights.Normal, BodyBrush);
                    continue;
                }

                // ─── Normal paragraph text ───
                AddInlinesParsed(tb, line, 12, FontWeights.Normal, BodyBrush);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // INLINE MARKDOWN PARSER (bold, italic, code, links, strikethrough)
        // ═══════════════════════════════════════════════════════════

        private static void AddInlinesParsed(TextBlock tb, string text, double fontSize,
            FontWeight fontWeight, Brush foreground, FontStyle? fontStyle = null)
        {
            var inlines = ParseInlineMarkdown(text, fontSize, fontWeight, foreground, fontStyle ?? FontStyles.Normal);
            foreach (var inline in inlines)
                tb.Inlines.Add(inline);
        }

        private static List<Inline> ParseInlineMarkdown(string text, double fontSize,
            FontWeight fontWeight, Brush foreground, FontStyle fontStyle)
        {
            var result = new List<Inline>();
            if (string.IsNullOrEmpty(text))
                return result;

            // Build a sorted list of all inline matches
            var segments = new List<(int Start, int End, string Type, string Content, string Extra)>();

            foreach (Match m in RxBold.Matches(text))
                segments.Add((m.Index, m.Index + m.Length, "bold", m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, ""));

            foreach (Match m in RxCode.Matches(text))
                segments.Add((m.Index, m.Index + m.Length, "code", m.Groups[1].Value, ""));

            foreach (Match m in RxLink.Matches(text))
                segments.Add((m.Index, m.Index + m.Length, "link", m.Groups[1].Value, m.Groups[2].Value));

            foreach (Match m in RxStrike.Matches(text))
                segments.Add((m.Index, m.Index + m.Length, "strike", m.Groups[1].Value, ""));

            // Remove overlapping segments (first match wins)
            segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            var filtered = new List<(int Start, int End, string Type, string Content, string Extra)>();
            int lastEnd = 0;
            foreach (var seg in segments)
            {
                if (seg.Start >= lastEnd)
                {
                    filtered.Add(seg);
                    lastEnd = seg.End;
                }
            }

            // Now check for italic in non-overlapping gaps (italic regex can conflict with bold)
            var gapSegments = new List<(int Start, int End, string Type, string Content, string Extra)>();
            lastEnd = 0;
            foreach (var seg in filtered)
            {
                if (seg.Start > lastEnd)
                {
                    string gap = text.Substring(lastEnd, seg.Start - lastEnd);
                    foreach (Match m in RxItalic.Matches(gap))
                    {
                        string content = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        gapSegments.Add((lastEnd + m.Index, lastEnd + m.Index + m.Length, "italic", content, ""));
                    }
                }
                lastEnd = seg.End;
            }
            // Check trailing gap
            if (lastEnd < text.Length)
            {
                string gap = text.Substring(lastEnd);
                foreach (Match m in RxItalic.Matches(gap))
                {
                    string content = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    gapSegments.Add((lastEnd + m.Index, lastEnd + m.Index + m.Length, "italic", content, ""));
                }
            }

            filtered.AddRange(gapSegments);
            filtered.Sort((a, b) => a.Start.CompareTo(b.Start));

            // Remove overlapping again after adding italic
            var final = new List<(int Start, int End, string Type, string Content, string Extra)>();
            lastEnd = 0;
            foreach (var seg in filtered)
            {
                if (seg.Start >= lastEnd)
                {
                    final.Add(seg);
                    lastEnd = seg.End;
                }
            }

            // Build inlines
            int pos = 0;
            foreach (var seg in final)
            {
                // Plain text before this segment
                if (seg.Start > pos)
                {
                    result.Add(new Run(text.Substring(pos, seg.Start - pos))
                    {
                        FontSize = fontSize,
                        FontWeight = fontWeight,
                        Foreground = foreground,
                        FontStyle = fontStyle,
                        FontFamily = SansFont
                    });
                }

                switch (seg.Type)
                {
                    case "bold":
                        result.Add(new Run(seg.Content)
                        {
                            FontSize = fontSize,
                            FontWeight = FontWeights.Bold,
                            Foreground = foreground,
                            FontStyle = fontStyle,
                            FontFamily = SansFont
                        });
                        break;

                    case "italic":
                        result.Add(new Run(seg.Content)
                        {
                            FontSize = fontSize,
                            FontWeight = fontWeight,
                            Foreground = foreground,
                            FontStyle = FontStyles.Italic,
                            FontFamily = SansFont
                        });
                        break;

                    case "code":
                        result.Add(new Run(seg.Content)
                        {
                            FontSize = fontSize - 0.5,
                            FontFamily = MonoFont,
                            Foreground = CodeBrush,
                            Background = CodeBgBrush
                        });
                        break;

                    case "link":
                        result.Add(new Run(seg.Content)
                        {
                            FontSize = fontSize,
                            FontWeight = fontWeight,
                            Foreground = LinkBrush,
                            FontFamily = SansFont,
                            TextDecorations = TextDecorations.Underline
                        });
                        break;

                    case "strike":
                        result.Add(new Run(seg.Content)
                        {
                            FontSize = fontSize,
                            FontWeight = fontWeight,
                            Foreground = StrikeBrush,
                            FontStyle = fontStyle,
                            FontFamily = SansFont,
                            TextDecorations = TextDecorations.Strikethrough
                        });
                        break;
                }

                pos = seg.End;
            }

            // Trailing text
            if (pos < text.Length)
            {
                result.Add(new Run(text.Substring(pos))
                {
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    Foreground = foreground,
                    FontStyle = fontStyle,
                    FontFamily = SansFont
                });
            }

            return result;
        }
    }
}
