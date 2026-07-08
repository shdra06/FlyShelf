// ---------------------------------------------------------------
// ILicenseManager — Interface for license validation and management.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Validates license keys, manages trial periods, and controls feature gating.
    /// </summary>
    public interface ILicenseManager
    {
        bool IsPro { get; }
        bool IsTrialActive { get; }
        int TrialDaysRemaining { get; }
        void Load();
        void Save();
    }
}
