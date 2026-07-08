// ---------------------------------------------------------------
// IClipboardHistoryManager — Interface for clipboard persistence.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
using System.Collections.Generic;

namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Persists clipboard history (text + images) to disk so items survive app restarts.
    /// </summary>
    public interface IClipboardHistoryManager
    {
        void Save();
        void Load();
        void Clear();
        int Count { get; }
    }
}
