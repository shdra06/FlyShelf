// ---------------------------------------------------------------
// ITodoManager — Interface for todo/task data management.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
using System.Collections.Generic;

namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Manages todo days, items, and persistence.
    /// </summary>
    public interface ITodoManager
    {
        IReadOnlyList<FlyShelf.Classes.TodoDay> Days { get; }
        FlyShelf.Classes.TodoDay GetOrCreateToday();
        FlyShelf.Classes.TodoDay GetOrCreateDay(System.DateTime targetDate);
        void Save();
        void Load();
        void DeleteDay(FlyShelf.Classes.TodoDay day);
    }
}
