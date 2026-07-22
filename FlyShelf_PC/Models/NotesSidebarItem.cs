// ---------------------------------------------------------------
// NotesSidebarItem — ViewModel for sidebar day/month display.
// Extracted from MainWindow.Notes.cs for modularity.
// ---------------------------------------------------------------
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlyShelf.Models
{
    /// <summary>ViewModel for sidebar display representing day or month box.</summary>
    public partial class NotesSidebarItem : ObservableObject
    {
        public bool IsMonthHeader { get; set; }
        public string Label { get; set; } = "";
        public string MonthLabel { get; set; } = "";
        public string FullLabel { get; set; } = "";
        public bool IsToday { get; set; }

        [ObservableProperty]
        private bool _isSelected;

        public FlyShelf.Classes.NoteDay Day { get; set; } = null!;
        public int MonthValue { get; set; }
        public int YearValue { get; set; }
    }
}
