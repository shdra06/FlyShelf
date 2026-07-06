using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace FlyShelf.Windows
{
    /// <summary>
    /// Wraps a PDF file for the merge window — holds page count, selected pages, and display info.
    /// </summary>
    public class PdfMergeItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileSize { get; set; }
        public int TotalPages { get; private set; }
        public string Error { get; private set; }
        public bool IsValid => string.IsNullOrEmpty(Error);
        
        // Which pages are selected (1-indexed). null = all pages
        private HashSet<int> _selectedPages;
        // Custom page order from reorder window (0-indexed). null = default order
        private List<PageOrderEntry> _customPageOrder;
        
        public class PageOrderEntry
        {
            public int PageIndex { get; set; }
            public int Rotation { get; set; }
        }
        
        public string PageRangeText
        {
            get
            {
                if (_selectedPages == null || _selectedPages.Count == TotalPages)
                    return "";
                if (_selectedPages.Count == 0)
                    return "None";
                return FormatPageRange(_selectedPages);
            }
        }

        public string PageInfo => IsValid 
            ? (_customPageOrder != null 
                ? $"{TotalPages} pages • {_customPageOrder.Count} reordered" 
                : $"{TotalPages} pages • {PageRangeText} selected") 
            : $"⚠ {Error}";

        // For the visual grid — which pages are toggled
        private bool[] _pageSelected;
        public bool[] PageSelected => _pageSelected;

        public PdfMergeItem(string filePath, bool skipLoad = false)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            
            try
            {
                var fi = new FileInfo(filePath);
                FileSize = fi.Length > 1_048_576 
                    ? string.Create(CultureInfo.InvariantCulture, $"{fi.Length / 1_048_576.0:F1} MB") 
                    : string.Create(CultureInfo.InvariantCulture, $"{fi.Length / 1024.0:F0} KB");
            }
            catch { FileSize = ""; }

            if (!skipLoad) LoadPageCount();
        }

        /// <summary>
        /// Async factory method — use instead of constructor to avoid blocking the UI thread.
        /// </summary>
        public static async Task<PdfMergeItem> CreateAsync(string filePath)
        {
            var item = new PdfMergeItem(filePath, skipLoad: true);
            await item.LoadPageCountAsync();
            return item;
        }

        private void LoadPageCount()
        {
            try
            {
                using (var doc = PdfReader.Open(FilePath, PdfDocumentOpenMode.Import))
                {
                    TotalPages = doc.PageCount;
                }
                _pageSelected = new bool[TotalPages];
                for (int i = 0; i < TotalPages; i++) _pageSelected[i] = true; // All selected by default
                _selectedPages = null; // null = all
                Error = null;
            }
            catch (Exception ex)
            {
                TotalPages = 0;
                _pageSelected = Array.Empty<bool>();
                Error = ex.Message.Length > 60 ? string.Concat(ex.Message.AsSpan(0, 60), "...") : ex.Message;
            }
        }

        /// <summary>
        /// Async version of LoadPageCount — offloads PdfReader.Open to a background thread.
        /// </summary>
        private async Task LoadPageCountAsync()
        {
            try
            {
                int count = await Task.Run(() =>
                {
                    using (var doc = PdfReader.Open(FilePath, PdfDocumentOpenMode.Import))
                    {
                        return doc.PageCount;
                    }
                });
                TotalPages = count;
                _pageSelected = new bool[TotalPages];
                for (int i = 0; i < TotalPages; i++) _pageSelected[i] = true; // All selected by default
                _selectedPages = null; // null = all
                Error = null;
            }
            catch (Exception ex)
            {
                TotalPages = 0;
                _pageSelected = Array.Empty<bool>();
                Error = ex.Message.Length > 60 ? string.Concat(ex.Message.AsSpan(0, 60), "...") : ex.Message;
            }
        }

        /// <summary>
        /// Returns the 0-indexed page entries (index + rotation) to include in the merge.
        /// </summary>
        public List<PageOrderEntry> GetSelectedPageEntries()
        {
            if (_customPageOrder != null)
                return new List<PageOrderEntry>(_customPageOrder);

            if (_selectedPages == null)
            {
                return Enumerable.Range(0, TotalPages).Select(i => new PageOrderEntry { PageIndex = i, Rotation = 0 }).ToList();
            }
            return _selectedPages.OrderBy(p => p).Select(p => new PageOrderEntry { PageIndex = p - 1, Rotation = 0 }).ToList();
        }

        /// <summary>
        /// Legacy compatibility: Returns 0-indexed page indices.
        /// </summary>
        public List<int> GetSelectedPageIndices() => GetSelectedPageEntries().Select(e => e.PageIndex).ToList();

        /// <summary>
        /// Set a custom page order from the reorder window.
        /// </summary>
        public void SetCustomPageOrder(List<PageOrderEntry> order)
        {
            _customPageOrder = order;
            // Also update the selection set to match
            _selectedPages = new HashSet<int>(order.Select(e => e.PageIndex + 1));
            for (int i = 0; i < TotalPages; i++)
                _pageSelected[i] = order.Any(e => e.PageIndex == i);
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>Clears custom page order, reverting to standard selection mode.</summary>
        public void ClearCustomPageOrder()
        {
            _customPageOrder = null;
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Toggle a specific page (1-indexed).
        /// </summary>
        public void TogglePage(int pageNum)
        {
            EnsureSelectedPagesInitialized();
            if (_selectedPages.Contains(pageNum))
            {
                _selectedPages.Remove(pageNum);
                _pageSelected[pageNum - 1] = false;
            }
            else
            {
                _selectedPages.Add(pageNum);
                _pageSelected[pageNum - 1] = true;
            }
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Select all pages.
        /// </summary>
        public void SelectAll()
        {
            _selectedPages = null;
            for (int i = 0; i < TotalPages; i++) _pageSelected[i] = true;
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Deselect all pages.
        /// </summary>
        public void DeselectAll()
        {
            EnsureSelectedPagesInitialized();
            _selectedPages.Clear();
            for (int i = 0; i < TotalPages; i++) _pageSelected[i] = false;
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Set pages from a range string like "1-5, 8, 10-12"
        /// </summary>
        public bool SetPageRange(string rangeText)
        {
            if (string.IsNullOrWhiteSpace(rangeText) || string.Equals(rangeText.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            {
                SelectAll();
                return true;
            }

            var pages = new HashSet<int>();
            try
            {
                foreach (var part in rangeText.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Contains('-', StringComparison.Ordinal))
                    {
                        var bounds = trimmed.Split('-');
                        int start = int.Parse(bounds[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        int end = int.Parse(bounds[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        for (int i = start; i <= end && i <= TotalPages; i++)
                            if (i >= 1) pages.Add(i);
                    }
                    else
                    {
                        int p = int.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
                        if (p >= 1 && p <= TotalPages) pages.Add(p);
                    }
                }

                _selectedPages = pages;
                for (int i = 0; i < TotalPages; i++)
                    _pageSelected[i] = pages.Contains(i + 1);
                
                OnPropertyChanged(nameof(PageRangeText));
                OnPropertyChanged(nameof(PageInfo));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Toggle an entire row in a grid with given column count.
        /// </summary>
        public void ToggleRow(int row, int columns)
        {
            EnsureSelectedPagesInitialized();
            int start = row * columns + 1;
            int end = Math.Min(start + columns - 1, TotalPages);
            
            // If all pages in row are selected, deselect them; otherwise select all
            bool allSelected = true;
            for (int p = start; p <= end; p++)
                if (!_selectedPages.Contains(p)) { allSelected = false; break; }

            for (int p = start; p <= end; p++)
            {
                if (allSelected)
                {
                    _selectedPages.Remove(p);
                    _pageSelected[p - 1] = false;
                }
                else
                {
                    _selectedPages.Add(p);
                    _pageSelected[p - 1] = true;
                }
            }
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Toggle an entire column in a grid with given column count.
        /// </summary>
        public void ToggleColumn(int col, int columns)
        {
            EnsureSelectedPagesInitialized();
            var pagesInCol = new List<int>();
            for (int p = col + 1; p <= TotalPages; p += columns)
                pagesInCol.Add(p);

            bool allSelected = pagesInCol.All(p => _selectedPages.Contains(p));
            foreach (int p in pagesInCol)
            {
                if (allSelected)
                {
                    _selectedPages.Remove(p);
                    _pageSelected[p - 1] = false;
                }
                else
                {
                    _selectedPages.Add(p);
                    _pageSelected[p - 1] = true;
                }
            }
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        private void EnsureSelectedPagesInitialized()
        {
            if (_selectedPages == null)
            {
                _selectedPages = new HashSet<int>(Enumerable.Range(1, TotalPages));
            }
        }

        private static string FormatPageRange(HashSet<int> pages)
        {
            if (pages.Count == 0) return "None";
            var sorted = pages.OrderBy(p => p).ToList();
            var ranges = new List<string>();
            int start = sorted[0], end = sorted[0];
            
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] == end + 1)
                {
                    end = sorted[i];
                }
                else
                {
                    ranges.Add(start == end ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start}-{end}"));
                    start = end = sorted[i];
                }
            }
            ranges.Add(start == end ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start}-{end}"));
            
            string result = string.Join(", ", ranges);
            return result.Length > 30 ? string.Concat(result.AsSpan(0, 27), "...") : result;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
