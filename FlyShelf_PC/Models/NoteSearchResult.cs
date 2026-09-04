// ---------------------------------------------------------------
// NoteSearchResult — Model for note search results displayed in UI.
// Extracted from MainWindow.Notes.cs for modularity.
// ---------------------------------------------------------------
namespace FlyShelf.Models
{
    /// <summary>Represents a single search hit within notes.</summary>
    public class NoteSearchResult
    {
        public string DateLabel { get; set; } = "";
        public string PageLabel { get; set; } = "";
        public bool HasPageLabel => !string.IsNullOrEmpty(PageLabel);
        public string Content { get; set; } = "";
        public FlyShelf.Classes.NoteDay Day { get; set; } = null!;
        public FlyShelf.Classes.NoteBullet Bullet { get; set; } = null!;
        public string? SectionId { get; set; }
    }
}
