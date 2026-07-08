// ---------------------------------------------------------------
// ISettingsManager — Interface for application settings access.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Provides access to application settings (theme, behavior, sync preferences).
    /// </summary>
    public interface ISettingsManager
    {
        string CurrentTheme { get; set; }
        bool RunAtStartup { get; set; }
        bool SyncEnabled { get; set; }
        bool IncognitoMode { get; set; }
        int MaxClipboardItems { get; set; }
        bool ShowInTaskbar { get; set; }
        string Language { get; set; }
        void Save();
        void Load();
    }
}
