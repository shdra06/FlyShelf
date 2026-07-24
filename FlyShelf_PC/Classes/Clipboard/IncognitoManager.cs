using System;
using System.IO;
using System.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Manages clipboard incognito mode — while active, clipboard captures are suppressed.
    /// Supports timed sessions with persistence across app restarts.
    /// </summary>
    public static class IncognitoManager
    {
        /// <summary>Raised when incognito mode is enabled or disabled. Parameter is the new state.</summary>
        public static event Action<bool> IncognitoStateChanged;

        /// <summary>Whether incognito mode is currently active.</summary>
        public static bool IsIncognito { get; private set; }

        /// <summary>The UTC time when the current incognito session expires, or null if not active.</summary>
        public static DateTime? IncognitoEndTime { get; private set; }

        /// <summary>
        /// Human-readable remaining time, e.g. "2h 15m remaining", "45m remaining".
        /// Returns empty string if not in incognito mode.
        /// </summary>
        public static string RemainingTimeText
        {
            get
            {
                if (!IsIncognito || IncognitoEndTime == null)
                    return string.Empty;

                var remaining = IncognitoEndTime.Value - DateTime.UtcNow;
                if (remaining.TotalSeconds <= 0)
                    return string.Empty;

                int hours = (int)remaining.TotalHours;
                int minutes = remaining.Minutes;

                if (hours > 0)
                    return $"{hours}h {minutes}m remaining";

                return $"{Math.Max(1, minutes)}m remaining";
            }
        }

        private static Timer _checkTimer;
        private static readonly object _lock = new();
        private static readonly string _stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "incognito_state.txt");

        /// <summary>
        /// Loads persisted incognito state from disk. Call once during app startup.
        /// If a saved session is still valid, incognito mode is resumed automatically.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    string content = FileRetryHelper.RunWithRetry(() => File.ReadAllText(_stateFilePath)).Trim();
                    if (DateTime.TryParse(content, null, System.Globalization.DateTimeStyles.RoundtripKind, out var endTime))
                    {
                        if (endTime > DateTime.UtcNow)
                        {
                            IsIncognito = true;
                            IncognitoEndTime = endTime;
                            StartTimer();
                            Logger.LogAction("INCOGNITO", $"Resumed incognito session from disk — expires at {endTime:u}");
                        }
                        else
                        {
                            // Expired while app was closed — clean up
                            DeleteStateFile();
                            Logger.LogAction("INCOGNITO", "Previous incognito session had already expired — cleared");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("INCOGNITO", $"Failed to load persisted state: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables incognito mode for the specified number of hours.
        /// </summary>
        /// <param name="hours">Duration in hours (must be greater than 0).</param>
        public static void EnableIncognito(int hours)
        {
            if (hours <= 0) return;

            lock (_lock)
            {
                IncognitoEndTime = DateTime.UtcNow.AddHours(hours);
                IsIncognito = true;
                SaveStateFile();
                StartTimer();
            }

            Logger.LogAction("INCOGNITO", $"Enabled for {hours}h — expires at {IncognitoEndTime:u}");
            IncognitoStateChanged?.Invoke(true);
        }

        /// <summary>
        /// Disables incognito mode immediately (manual or programmatic).
        /// </summary>
        public static void DisableIncognito()
        {
            lock (_lock)
            {
                if (!IsIncognito) return;

                IsIncognito = false;
                IncognitoEndTime = null;
                StopTimer();
                DeleteStateFile();
            }

            Logger.LogAction("INCOGNITO", "Disabled manually");
            IncognitoStateChanged?.Invoke(false);
        }

        /// <summary>
        /// Checks whether the incognito session has expired and auto-disables if so.
        /// Called periodically by the internal timer.
        /// </summary>
        public static void CheckAndAutoDisable()
        {
            lock (_lock)
            {
                if (!IsIncognito || IncognitoEndTime == null)
                    return;

                if (DateTime.UtcNow < IncognitoEndTime.Value)
                    return;

                IsIncognito = false;
                IncognitoEndTime = null;
                StopTimer();
                DeleteStateFile();
            }

            Logger.LogAction("INCOGNITO", "Session expired — auto-disabled");
            IncognitoStateChanged?.Invoke(false);
        }

        // ── Private helpers ──────────────────────────────────────────────

        private static void StartTimer()
        {
            StopTimer();
            _checkTimer = new Timer(_ => CheckAndAutoDisable(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private static void StopTimer()
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
        }

        private static void SaveStateFile()
        {
            try
            {
                string dir = Path.GetDirectoryName(_stateFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                // Atomic write: write to .tmp first, then rename to prevent data loss on crash
                string tmpPath = _stateFilePath + ".tmp";
                File.WriteAllText(tmpPath, IncognitoEndTime?.ToString("o") ?? string.Empty);
                File.Move(tmpPath, _stateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("INCOGNITO", $"Failed to save state file: {ex.Message}");
            }
        }

        private static void DeleteStateFile()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                    File.Delete(_stateFilePath);
            }
            catch (Exception ex)
            {
                Logger.LogAction("INCOGNITO", $"Failed to delete state file: {ex.Message}");
            }
        }
    }
}
