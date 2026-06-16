using System;
using System.IO;
using System.Collections;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Compares two ClipboardItem objects based on fuzzy search relevance scoring.
    /// Uses FuzzyMatcher.Score() to rank results by match quality.
    /// 
    /// Priority tiers (highest first):
    ///   1. Exact substring match in FileName or RawContent (score 0.8+)
    ///   2. Word-level or fuzzy match in FileName or RawContent (score 0.2-0.8)
    ///   3. Extension or ItemType exact match
    ///   4. Fallback — lowest relevance
    /// 
    /// Within the same priority, items are sorted by DateCopied (newest first).
    /// </summary>
    public class SearchResultComparer : IComparer
    {
        private readonly string _query;

        public SearchResultComparer(string query)
        {
            _query = (query ?? "").Trim();
        }

        public int Compare(object? x, object? y)
        {
            if (x is ClipboardItem a && y is ClipboardItem b)
            {
                double scoreA = GetRelevanceScore(a);
                double scoreB = GetRelevanceScore(b);

                // Higher score = more relevant = should come first
                int cmp = scoreB.CompareTo(scoreA);
                if (cmp != 0) return cmp;

                // Tie-breaker: newest copies first
                return b.DateCopied.CompareTo(a.DateCopied);
            }
            return 0;
        }

        private double GetRelevanceScore(ClipboardItem item)
        {
            if (string.IsNullOrEmpty(_query)) return 0;

            // Primary: fuzzy score across text content and name
            double contentScore = FuzzyMatcher.ScoreBest(_query, item.RawContent, item.FileName);
            if (contentScore > 0) return contentScore;

            // Secondary: exact extension/type match (lower priority)
            string qLower = _query.ToLowerInvariant();
            bool extMatch = !string.IsNullOrEmpty(item.Extension) && item.Extension.Replace(".", "").Trim().Equals(qLower, StringComparison.OrdinalIgnoreCase);
            bool pathExtMatch = false;
            if (!string.IsNullOrEmpty(item.FilePath))
            {
                try
                {
                    string ext = Path.GetExtension(item.FilePath).Replace(".", "").Trim();
                    pathExtMatch = ext.Equals(qLower, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }
            bool typeMatch = item.ItemType.ToString().Equals(_query, StringComparison.OrdinalIgnoreCase);

            if (extMatch || pathExtMatch || typeMatch)
                return 0.15; // Below fuzzy matches but above unmatched

            return 0;
        }
    }
}
