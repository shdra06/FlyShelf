using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FlyShelf.Classes
{
    /// <summary>
    /// In-memory ring buffer logger for real-time application diagnostics and UI display.
    /// Retains up to 500 most recent log entries in memory.
    /// </summary>
    public static class AppLogger
    {
        private const int MaxEntries = 500;
        private static readonly object _lock = new();
        private static readonly List<string> _entries = new(MaxEntries);

        [ThreadStatic]
        private static bool _inAppLogger;

        public static event Action<string>? LogAdded;
        public static event Action? LogsCleared;

        static AppLogger()
        {
            try
            {
                // Pre-populate with existing recent logs from disk if available
                string logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlyShelf", "Logs");
                string netLog = Path.Combine(logsDir, "network_diagnostics.txt");
                string actLog = Path.Combine(logsDir, "activity_log.txt");

                string? sourceFile = File.Exists(netLog) ? netLog : (File.Exists(actLog) ? actLog : null);
                if (sourceFile != null)
                {
                    var lines = File.ReadAllLines(sourceFile);
                    int start = Math.Max(0, lines.Length - 100);
                    lock (_lock)
                    {
                        foreach (var line in lines.Skip(start))
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                _entries.Add(line);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort initialization
            }

            Log("STARTUP", "AppLogger initialized.");
        }

        public static void Log(string category, string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string formatted = $"[{timestamp}] [{(string.IsNullOrWhiteSpace(category) ? "APP" : category.ToUpperInvariant())}] {message}";

                AddRawEntry(formatted);

                // Forward to disk logger if available, without recursing
                _inAppLogger = true;
                try
                {
                    Logger.LogAction(category, message);
                }
                finally
                {
                    _inAppLogger = false;
                }
            }
            catch
            {
                // Prevent any logging failure from interrupting application execution
            }
        }

        public static void Log(string message) => Log("APP", message);

        internal static void AddRawEntry(string entry)
        {
            if (_inAppLogger) return;

            lock (_lock)
            {
                if (_entries.Count >= MaxEntries)
                {
                    _entries.RemoveAt(0);
                }
                _entries.Add(entry);
            }

            try
            {
                LogAdded?.Invoke(entry);
            }
            catch
            {
                // Ignore listener exceptions
            }
        }

        public static List<string> GetLogs()
        {
            lock (_lock)
            {
                return new List<string>(_entries);
            }
        }

        public static string GetAllLogsText()
        {
            lock (_lock)
            {
                return string.Join(Environment.NewLine, _entries);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }

            try
            {
                LogsCleared?.Invoke();
            }
            catch
            {
                // Ignore listener exceptions
            }
        }
    }
}
