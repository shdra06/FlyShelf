using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Shared file I/O retry utility. Replaces 4 duplicate RunWithRetry implementations
    /// across ClipboardHistoryManager, NoteManager, TodoManager, and SettingsManager.
    /// Uses async delays instead of Thread.Sleep to avoid blocking ThreadPool threads.
    /// </summary>
    internal static class FileRetryHelper
    {
        /// <summary>
        /// Retries a file I/O operation with exponential backoff (synchronous).
        /// Uses Thread.Sleep — intended for callers that cannot await (Timer callbacks,
        /// lock-guarded code, synchronous startup paths). For async-capable callers,
        /// prefer <see cref="RunWithRetryAsync{T}"/> which uses Task.Delay instead.
        /// </summary>
        public static T RunWithRetry<T>(Func<T> action, int maxRetries = 3, int baseDelayMs = 50, string context = "FILE_IO")
        {
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return action();
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    lastEx = ex;
                    Thread.Sleep(baseDelayMs * (1 << attempt)); // Acceptable: sync overload for non-async callers
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxRetries)
                {
                    lastEx = ex;
                    Thread.Sleep(baseDelayMs * (1 << attempt)); // Acceptable: sync overload for non-async callers
                }
            }
            throw lastEx ?? new InvalidOperationException($"RunWithRetry failed after {maxRetries} retries");
        }

        /// <summary>
        /// Retries a void file I/O operation with exponential backoff (synchronous).
        /// See <see cref="RunWithRetry{T}"/> for usage notes.
        /// </summary>
        public static void RunWithRetry(Action action, int maxRetries = 3, int baseDelayMs = 50, string context = "FILE_IO")
        {
            RunWithRetry<object?>(() => { action(); return null; }, maxRetries, baseDelayMs, context);
        }

        /// <summary>
        /// Async version — uses Task.Delay instead of Thread.Sleep.
        /// Preferred for background operations to avoid blocking ThreadPool threads.
        /// </summary>
        public static async Task<T> RunWithRetryAsync<T>(Func<T> action, int maxRetries = 3, int baseDelayMs = 50, string context = "FILE_IO")
        {
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return action();
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    lastEx = ex;
                    await Task.Delay(baseDelayMs * (1 << attempt));
                }
                catch (UnauthorizedAccessException ex) when (attempt < maxRetries)
                {
                    lastEx = ex;
                    await Task.Delay(baseDelayMs * (1 << attempt));
                }
            }
            throw lastEx ?? new InvalidOperationException($"RunWithRetryAsync failed after {maxRetries} retries");
        }

        /// <summary>
        /// Async version for void operations — uses Task.Delay instead of Thread.Sleep.
        /// </summary>
        public static async Task RunWithRetryAsync(Action action, int maxRetries = 3, int baseDelayMs = 50, string context = "FILE_IO")
        {
            await RunWithRetryAsync<object?>(() => { action(); return null; }, maxRetries, baseDelayMs, context);
        }
    }
}
