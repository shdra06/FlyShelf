// ---------------------------------------------------------------
// NotesMonthPickerItem — ViewModel for month picker popup items.
// Extracted from MainWindow.Notes.cs for modularity.
// ---------------------------------------------------------------
namespace FlyShelf.Models
{
    /// <summary>ViewModel for the month picker popup items.</summary>
    public class NotesMonthPickerItem
    {
        public string MonthName { get; set; } = "";
        public string YearText { get; set; } = "";
        public string DayCount { get; set; } = "";
        public int Month { get; set; }
        public int Year { get; set; }
    }
}
