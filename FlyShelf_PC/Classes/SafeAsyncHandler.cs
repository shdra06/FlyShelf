using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Prevents unhandled exceptions in async void event handlers from crashing the app.
    /// Usage: await SafeAsyncHandler.RunAsync(async () => { ... });
    /// </summary>
    public static class SafeAsyncHandler
    {
        /// <summary>
        /// Wraps an async operation with exception handling. Use in async void event handlers.
        /// </summary>
        public static async Task RunAsync(Func<Task> action, [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation — don't log as error
            }
            catch (Exception ex)
            {
                var shortFile = System.IO.Path.GetFileName(file);
                Logger.LogAction("SAFE_ASYNC", $"⚠ Exception in {caller} ({shortFile}): {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[SafeAsync] {caller}: {ex}");
            }
        }

        /// <summary>
        /// Safe wrapper for System.Threading.Timer callbacks (which are implicitly async void).
        /// </summary>
        public static async void SafeTimerCallback(Func<Task> action, [CallerMemberName] string caller = "")
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.LogAction("SAFE_TIMER", $"⚠ Timer exception in {caller}: {ex.Message}");
            }
        }

        /// <summary>
        /// Fire-and-forget with safety. Use for background operations where you don't need to await.
        /// </summary>
        public static void FireAndForget(Func<Task> action, [CallerMemberName] string caller = "")
        {
            Task.Run(async () =>
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Logger.LogAction("FIRE_FORGET", $"⚠ Background exception in {caller}: {ex.Message}");
                }
            });
        }
    }
}
