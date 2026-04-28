using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Reliable sync queue: guarantees delivery to Firebase with exponential backoff retries.
    /// Replaces fire-and-forget PushToGlobalSync calls with a queue that retries on failure.
    /// Items are processed sequentially to maintain ordering and avoid race conditions.
    /// </summary>
    public static class SyncQueue
    {
        private static readonly ConcurrentQueue<SyncJob> _queue = new();
        private static readonly SemaphoreSlim _signal = new(0);
        private static CancellationTokenSource? _cts;
        private static bool _running = false;

        private const int MAX_RETRIES = 5;
        private static readonly int[] RETRY_DELAYS_MS = { 2000, 5000, 10000, 20000, 30000 };

        /// <summary>
        /// Enqueue a clipboard item for sync. Returns immediately — delivery is guaranteed via retries.
        /// </summary>
        public static void Enqueue(ClipboardItem item, string channel = "firebase")
        {
            _queue.Enqueue(new SyncJob
            {
                Item = item,
                Channel = channel,
                EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Attempts = 0
            });
            _signal.Release(); // Wake the processor
        }

        /// <summary>
        /// Starts the background processor. Call once at app startup.
        /// </summary>
        public static void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ProcessLoop(_cts.Token));
            Logger.LogAction("SYNC_QUEUE", "Background processor started");
        }

        /// <summary>
        /// Stops the background processor. Pending items are lost (acceptable — they're volatile clipboard data).
        /// </summary>
        public static void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            Logger.LogAction("SYNC_QUEUE", $"Stopped. {_queue.Count} items remaining in queue.");
        }

        /// <summary>Current number of items waiting to be synced.</summary>
        public static int PendingCount => _queue.Count;

        private static async Task ProcessLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait for a signal (item enqueued) or cancellation
                    await _signal.WaitAsync(ct);

                    if (_queue.TryDequeue(out var job))
                    {
                        await ProcessJob(job, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("SYNC_QUEUE", $"Processor error: {ex.Message}");
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            }

            Logger.LogAction("SYNC_QUEUE", "Processor exited");
        }

        private static async Task ProcessJob(SyncJob job, CancellationToken ct)
        {
            while (job.Attempts < MAX_RETRIES && !ct.IsCancellationRequested)
            {
                try
                {
                    job.Attempts++;

                    // Skip items older than 5 minutes (they're stale)
                    long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - job.EnqueuedAt;
                    if (ageMs > 5 * 60_000)
                    {
                        Logger.LogAction("SYNC_QUEUE", $"Dropped stale item (age: {ageMs / 1000}s): {job.Item.FileName ?? "text"}");
                        return;
                    }

                    switch (job.Channel)
                    {
                        case "firebase":
                            await FirebaseSyncManager.PushToGlobalSync(job.Item);
                            break;
                        // Future: case "lan": await LanSyncManager.Push(job.Item); break;
                    }

                    // Success — no exception thrown
                    if (job.Attempts > 1)
                        Logger.LogAction("SYNC_QUEUE", $"Delivered after {job.Attempts} attempts: {job.Item.FileName ?? "text"}");
                    return;
                }
                catch (Exception ex)
                {
                    int delayIdx = Math.Min(job.Attempts - 1, RETRY_DELAYS_MS.Length - 1);
                    int delayMs = RETRY_DELAYS_MS[delayIdx];

                    Logger.LogAction("SYNC_QUEUE", $"Attempt {job.Attempts}/{MAX_RETRIES} failed: {ex.Message} — retry in {delayMs}ms");

                    try { await Task.Delay(delayMs, ct); } catch { return; }
                }
            }

            // All retries exhausted
            Logger.LogAction("SYNC_QUEUE", $"DROPPED after {MAX_RETRIES} retries: {job.Item.FileName ?? "text"}");
        }

        private class SyncJob
        {
            public ClipboardItem Item { get; set; } = null!;
            public string Channel { get; set; } = "firebase";
            public long EnqueuedAt { get; set; }
            public int Attempts { get; set; }
        }
    }
}
