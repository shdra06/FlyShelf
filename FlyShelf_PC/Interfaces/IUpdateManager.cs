// ---------------------------------------------------------------
// IUpdateManager — Interface for application update management.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
using System.Threading.Tasks;

namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Checks for and applies application updates.
    /// </summary>
    public interface IUpdateManager
    {
        bool IsUpdateAvailable { get; }
        string LatestVersion { get; }
        Task CheckForUpdatesAsync();
        Task DownloadAndInstallAsync();
    }
}
