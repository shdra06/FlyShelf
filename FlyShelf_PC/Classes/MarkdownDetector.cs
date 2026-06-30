using System;
using System.Text.RegularExpressions;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight heuristic-based Markdown content detector.
    /// Uses a weighted scoring system similar to the existing IsProperCode() approach
    /// to determine if clipboard text is Markdown-formatted content.
    /// 
    /// Detection runs AFTER code detection (IsProperCode) to avoid false positives
    /// on code that contains markdown-like syntax (e.g., # comments in Python).
    /// </summary>
    public static class MarkdownDetector
    {
        // HIGH CONFIDENCE patterns (weight 3)
        private static readonly Regex _rxHeading = new(@"^#{1,6}\s+\S", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxLink = new(@"\[.+?\]\(.+?\)", RegexOptions.Compiled);
        private static readonly Regex _rxImage = new(@"!\[.*?\]\(.+?\)", RegexOptions.Compiled);
        private static readonly Regex _rxFencedCode = new(@"^```[\s\S]*?```", RegexOptions.Multiline | RegexOptions.Compiled);

        // MEDIUM CONFIDENCE patterns (weight 2)
        private static readonly Regex _rxBold = new(@"\*\*.+?\*\*|__.+?__", RegexOptions.Compiled);
        private static readonly Regex _rxBlockquote = new(@"^>\s", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxTable = new(@"^\|.+\|.+\|", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxItalic = new(@"(?<!\*)\*(?!\*).+?(?<!\*)\*(?!\*)|(?<!_)_(?!_).+?(?<!_)_(?!_)", RegexOptions.Compiled);

        // LOW CONFIDENCE patterns (weight 1)
        private static readonly Regex _rxUnorderedList = new(@"^\s*[-*+]\s+\S", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxOrderedList = new(@"^\s*\d+\.\s+\S", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxHorizontalRule = new(@"^(---+|\*\*\*+|___+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex _rxStrikethrough = new(@"~~.+?~~", RegexOptions.Compiled);
        private static readonly Regex _rxInlineCode = new(@"`.+?`", RegexOptions.Compiled);

        /// <summary>
        /// Determines if the given text is Markdown-formatted content.
        /// Returns true only when there's high confidence (score ≥ 6 with at least one
        /// high-confidence pattern match).
        /// 
        /// Must be called AFTER IsProperCode() returns false, as code with # comments
        /// or * pointers could otherwise trigger false positives.
        /// </summary>
        public static bool IsMarkdown(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 30) return false;

            // Use first 10K chars for analysis (performance guard)
            string sample = text.Length > 10000 ? text.Substring(0, 10000) : text;

            int score = 0;
            bool hasHighConfidence = false;

            // HIGH CONFIDENCE (weight 3 each)
            if (_rxHeading.IsMatch(sample)) { score += 3; hasHighConfidence = true; }
            if (_rxLink.IsMatch(sample)) { score += 3; hasHighConfidence = true; }
            if (_rxImage.IsMatch(sample)) { score += 3; hasHighConfidence = true; }
            if (_rxFencedCode.IsMatch(sample)) { score += 3; hasHighConfidence = true; }

            // Early exit: no high-confidence match = not markdown
            if (!hasHighConfidence) return false;

            // MEDIUM CONFIDENCE (weight 2 each)
            if (_rxBold.IsMatch(sample)) score += 2;
            if (_rxBlockquote.IsMatch(sample)) score += 2;
            if (_rxTable.IsMatch(sample)) score += 2;
            if (_rxItalic.IsMatch(sample)) score += 2;

            // LOW CONFIDENCE (weight 1 each)
            if (_rxUnorderedList.IsMatch(sample)) score += 1;
            if (_rxOrderedList.IsMatch(sample)) score += 1;
            if (_rxHorizontalRule.IsMatch(sample)) score += 1;
            if (_rxStrikethrough.IsMatch(sample)) score += 1;
            if (_rxInlineCode.IsMatch(sample)) score += 1;

            // Must have at least one high-confidence pattern AND total score ≥ 6
            return score >= 6;
        }
    }
}
