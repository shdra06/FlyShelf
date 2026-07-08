// ---------------------------------------------------------------
// SearchService — Clipboard search and filter logic.
// Extracted from MainWindow.Search.cs — pure business logic
// that can be unit-tested without UI dependencies.
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlyShelf.Services
{
    /// <summary>
    /// Provides clipboard item search and filtering capabilities.
    /// Extracted from MainWindow for testability and single responsibility.
    /// </summary>
    public class SearchService
    {
        /// <summary>
        /// Tests whether a clipboard item matches the given search query.
        /// Uses fuzzy matching on content, file name, extension, and type.
        /// </summary>
        /// <param name="item">The clipboard item to test.</param>
        /// <param name="query">The search query (already trimmed).</param>
        /// <returns>True if the item matches the query.</returns>
        public bool IsMatch(ClipboardItem item, string query)
        {
            if (item == null || string.IsNullOrWhiteSpace(query))
                return true;

            string q = query.Trim();

            // 1. Fuzzy match in text content or name (handles typos + word-order)
            if (Classes.FuzzyMatcher.IsMatchAny(q, item.RawContent, item.FileName))
                return true;

            // 2. Check exact extension match (direct property or via FilePath)
            if (!string.IsNullOrEmpty(item.Extension) &&
                item.Extension.Replace(".", "").Trim().Equals(q, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(item.FilePath))
            {
                try
                {
                    string ext = System.IO.Path.GetExtension(item.FilePath).Replace(".", "").Trim();
                    if (ext.Equals(q, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { } // Best-effort: failure is acceptable
            }

            // 3. Check exact match with the item type string
            if (item.ItemType.ToString().Equals(q, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Tests whether a clipboard item matches the given category filter.
        /// </summary>
        /// <param name="item">The clipboard item to test.</param>
        /// <param name="category">The category name (e.g., "Pinned", "PDF", "Image").</param>
        /// <returns>True if the item belongs to the category.</returns>
        public bool MatchesCategory(ClipboardItem item, string category)
        {
            if (item == null || string.IsNullOrWhiteSpace(category))
                return true;

            return category.ToUpperInvariant() switch
            {
                "PINNED" => item.IsPinned,
                "PDF" => item.ItemType == ClipboardItemType.File &&
                         !string.IsNullOrEmpty(item.Extension) &&
                         item.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase),
                "IMAGE" => item.ItemType == ClipboardItemType.Image,
                "FILE" => item.ItemType == ClipboardItemType.File,
                "TEXT" => item.ItemType == ClipboardItemType.Text,
                "CODE" => item.ItemType == ClipboardItemType.Code,
                "PASSWORD" => item.IsPassword,
                _ => true
            };
        }
    }
}
