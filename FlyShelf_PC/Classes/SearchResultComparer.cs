using System;
using System.IO;
using System.Collections;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Compares two ClipboardItem objects based on search query matching priority.
    /// Priority 1 (Highest): Item's FileName or RawContent contains the search query as a substring.
    /// Priority 2 (Medium): Item's Extension, FilePath extension, or ItemType name matches the search query exactly.
    /// Priority 3 (Lowest): General/fallback matches.
    /// If priorities are identical, preserves chronological order (newest DateCopied first).
    /// </summary>
    public class SearchResultComparer : IComparer
    {
        private readonly string _query;

        public SearchResultComparer(string query)
        {
            _query = (query ?? "").ToLowerInvariant().Trim();
        }

        public int Compare(object? x, object? y)
        {
            if (x is ClipboardItem a && y is ClipboardItem b)
            {
                int pA = GetMatchPriority(a);
                int pB = GetMatchPriority(b);

                if (pA != pB)
                {
                    return pA.CompareTo(pB); // Lower value is sorted first
                }

                // Default fallback: newest copies first
                return b.DateCopied.CompareTo(a.DateCopied);
            }
            return 0;
        }

        private int GetMatchPriority(ClipboardItem item)
        {
            if (string.IsNullOrEmpty(_query)) return 3;

            // 1st Priority: Actual substring match in name or text content
            bool nameMatch = !string.IsNullOrEmpty(item.FileName) && item.FileName.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;
            bool contentMatch = !string.IsNullOrEmpty(item.RawContent) && item.RawContent.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0;

            if (nameMatch || contentMatch)
            {
                return 1;
            }

            // 2nd Priority: Exact type/extension match (e.g. searching "pdf" brings up all files with .pdf extension)
            bool extMatch = !string.IsNullOrEmpty(item.Extension) && item.Extension.Replace(".", "").Trim().Equals(_query, StringComparison.OrdinalIgnoreCase);
            bool pathExtMatch = false;
            if (!string.IsNullOrEmpty(item.FilePath))
            {
                try
                {
                    string ext = Path.GetExtension(item.FilePath).Replace(".", "").Trim();
                    pathExtMatch = ext.Equals(_query, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }
            bool typeMatch = item.ItemType.ToString().Equals(_query, StringComparison.OrdinalIgnoreCase);

            if (extMatch || pathExtMatch || typeMatch)
            {
                return 2;
            }

            return 3;
        }
    }
}
