// ---------------------------------------------------------------
// INoteManager — Interface for notes data management.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
using System.Collections.Generic;

namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Manages note days, bullets, and persistence.
    /// </summary>
    public interface INoteManager
    {
        IReadOnlyList<FlyShelf.Classes.NoteDay> Days { get; }
        FlyShelf.Classes.NoteDay GetOrCreateToday();
        FlyShelf.Classes.NoteDay GetOrCreateDay(System.DateTime targetDate);
        void Save();
        void Load();
        void DeleteDay(FlyShelf.Classes.NoteDay day);
        IEnumerable<FlyShelf.Classes.NoteDay> GetDaysForMonth(int month, int year);
    }
}
