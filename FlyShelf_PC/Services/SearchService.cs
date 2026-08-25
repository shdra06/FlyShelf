// ---------------------------------------------------------------
// SearchService — Clipboard search and filter logic.
// Extracted from MainWindow.Search.cs — pure business logic
// that can be unit-tested without UI dependencies.
// ---------------------------------------------------------------
using FlyShelf.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;

namespace FlyShelf.Services
{
    /// <summary>
    /// Provides high-performance clipboard item search and filtering capabilities.
    /// Uses zero-allocation span checks and precomputed lowercase fields.
    /// </summary>
    public class SearchService
    {
        /// <summary>
        /// Tests whether a clipboard item matches the given search query.
        /// Uses fuzzy matching on content, file name, extension, and type.
        /// </summary>
        public bool IsMatch(ClipboardItem item, string query)
        {
            if (item == null || string.IsNullOrWhiteSpace(query))
                return true;

            string q = query.Trim();
            var qSpan = q.AsSpan().TrimStart('.');

            // 1. Fuzzy match in text content or name (handles typos + word-order)
            if (Classes.FuzzyMatcher.IsMatchAny(q, item.LowerFileName, item.LowerContent, item.FileName, item.RawContent))
                return true;

            // 2. Fast check extension match (zero substring allocations)
            if (!string.IsNullOrEmpty(item.Extension))
            {
                var extSpan = item.Extension.AsSpan().TrimStart('.');
                if (extSpan.Equals(qSpan, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (!string.IsNullOrEmpty(item.FilePath))
            {
                var extSpan = Path.GetExtension(item.FilePath.AsSpan()).TrimStart('.');
                if (extSpan.Equals(qSpan, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 3. Fast type check without enum.ToString() string allocation
            if (MatchesItemTypeName(item.ItemType, qSpan))
                return true;

            return false;
        }

        private static bool MatchesItemTypeName(ClipboardItemType type, ReadOnlySpan<char> name)
        {
            return type switch
            {
                ClipboardItemType.Text => name.Equals("Text", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Image => name.Equals("Image", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.File => name.Equals("File", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Folder => name.Equals("Folder", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Code => name.Equals("Code", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Url => name.Equals("Url", StringComparison.OrdinalIgnoreCase) || name.Equals("Link", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Pdf => name.Equals("Pdf", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Document => name.Equals("Document", StringComparison.OrdinalIgnoreCase) || name.Equals("Doc", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Archive => name.Equals("Archive", StringComparison.OrdinalIgnoreCase) || name.Equals("Zip", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Video => name.Equals("Video", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Audio => name.Equals("Audio", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.Presentation => name.Equals("Presentation", StringComparison.OrdinalIgnoreCase) || name.Equals("Ppt", StringComparison.OrdinalIgnoreCase),
                ClipboardItemType.QRCode => name.Equals("QRCode", StringComparison.OrdinalIgnoreCase) || name.Equals("QR", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        /// <summary>
        /// Tests whether a clipboard item matches the given category filter.
        /// </summary>
        public bool MatchesCategory(ClipboardItem item, string category)
        {
            if (item == null || string.IsNullOrWhiteSpace(category))
                return true;

            return category.ToUpperInvariant() switch
            {
                "PINNED" => item.IsPinned,
                "PDF" => item.ItemType == ClipboardItemType.Pdf ||
                         (!string.IsNullOrEmpty(item.Extension) && item.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)),
                "IMAGE" => item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode,
                "FILE" => item.ItemType == ClipboardItemType.File || item.ItemType == ClipboardItemType.Pdf ||
                          item.ItemType == ClipboardItemType.Document || item.ItemType == ClipboardItemType.Archive ||
                          item.ItemType == ClipboardItemType.Video || item.ItemType == ClipboardItemType.Audio ||
                          item.ItemType == ClipboardItemType.Presentation || item.ItemType == ClipboardItemType.Folder,
                "TEXT" => item.ItemType == ClipboardItemType.Text && !item.IsPassword,
                "CODE" => item.ItemType == ClipboardItemType.Code,
                "PASSWORD" => item.IsPassword,
                "URL" or "LINK" => item.ItemType == ClipboardItemType.Url,
                "DOCUMENT" or "DOCS" => item.ItemType == ClipboardItemType.Document || item.ItemType == ClipboardItemType.Pdf || item.ItemType == ClipboardItemType.Presentation,
                "ARCHIVE" or "ZIP" => item.ItemType == ClipboardItemType.Archive,
                "MEDIA" => item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.Video || item.ItemType == ClipboardItemType.Audio,
                _ => true
            };
        }
    }
}
