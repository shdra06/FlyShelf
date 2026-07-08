// ---------------------------------------------------------------
// NotesSidebarItem — ViewModel for sidebar day/month display.
// Extracted from MainWindow.Notes.cs for modularity.
// ---------------------------------------------------------------
using System.ComponentModel;

namespace FlyShelf.Models
{
    /// <summary>ViewModel for sidebar display representing day or month box.</summary>
    public class NotesSidebarItem : INotifyPropertyChanged
    {
        public bool IsMonthHeader { get; set; }
        public string Label { get; set; } = "";
        public string MonthLabel { get; set; } = "";
        public string FullLabel { get; set; } = "";
        public bool IsToday { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public FlyShelf.Classes.NoteDay Day { get; set; } = null!;
        public int MonthValue { get; set; }
        public int YearValue { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
