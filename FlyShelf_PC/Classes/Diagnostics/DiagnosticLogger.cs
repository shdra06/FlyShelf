using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight diagnostic logger for best-effort operations.
    /// Replaces empty catch blocks with optional trace-level logging.
    /// Enable via DiagnosticLogger.IsVerbose = true for debugging.
    /// </summary>
    public static class DiagnosticLogger
    {
        /// <summary>When true, best-effort failures are written to trace output.</summary>
        public static bool IsVerbose { get; set; }

        /// <summary>
        /// Log a best-effort failure. Only writes when IsVerbose is true.
        /// Use this instead of empty catch blocks.
        /// </summary>
        public static void LogBestEffort(
            Exception ex,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
        {
            if (!IsVerbose) return;
            var fileName = Path.GetFileName(file);
            Trace.WriteLine($"[BestEffort] {fileName}.{caller}: {ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>
        /// Log a best-effort failure with a custom context message.
        /// </summary>
        public static void LogBestEffort(
            string context,
            Exception ex,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
        {
            if (!IsVerbose) return;
            var fileName = Path.GetFileName(file);
            Trace.WriteLine($"[BestEffort] {fileName}.{caller} ({context}): {ex.GetType().Name}: {ex.Message}");
        }
    }
}
