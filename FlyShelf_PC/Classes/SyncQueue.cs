using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlyShelf.ViewModels;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Reliable sync queue: guarantees delivery via P2P (LAN/Cloudflare) with retries.
    /// Firebase is NEVER used for content transfer — only direct P2P delivery.
    /// Items are persisted to disk to survive app crashes.
    /// </summary>
    public static class SyncQueue
    {
        private static readonly ConcurrentQueue<SyncJob> _queue = new();
        private static readonly SemaphoreSlim _signal = new(0);
        private static CancellationTokenSource? _cts;
        private static volatile bool _running = false;

        private const int MAX_RETRIES = 3;
        private static readonly int[] RETRY_DELAYS_MS = { 1000, 3000, 5000 };
        private const int MAX_QUEUE_SIZE = 100;
        private const int STALE_THRESHOLD_MS = 15 * 60_000; // 15 minutes
        private const int PERSIST_DEBOUNCE_MS = 500;

        // Bounded concurrency: up to 3 items transfer in parallel
        private static readonly SemaphoreSlim _concurrency = new(3, 3);

        // Persistence
        private static readonly string _persistFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyShelf", "sync_queue.json");
        private static readonly object _persistLock = new();
        private static long _lastPersistTick;

        /// <summary>
        /// Enqueue a clipboard item for sync. Returns immediately — delivery is guaranteed via retries.
        /// </summary>
        public static void Enqueue(ClipboardItem item, string channel = "firebase")
        {
            // SECURITY: Password items must NEVER be synced to any device
            if (item.IsPassword)
            {
                Logger.LogAction("SYNC_QUEUE", "🔒 Blocked password item from sync queue — password items are never synced");
                return;
            }

            // Cap queue to prevent unbounded growth
            if (_queue.Count >= MAX_QUEUE_SIZE)
            {
                Logger.LogAction("SYNC_QUEUE", $"⚠️ Queue full ({MAX_QUEUE_SIZE} items) — dropping oldest");
                _queue.TryDequeue(out _);
            }

            _queue.Enqueue(new SyncJob
            {
                Item = item,
                Channel = channel,
                EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Attempts = 0
            });
            _signal.Release(); // Wake the processor
            DebouncedPersist();
        }

        /// <summary>
        /// Starts the background processor. Call once at app startup.
        /// Recovers any pending items from disk.
        /// </summary>
        public static void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();

            // Recover pending items from previous session
            LoadFromDisk();

            _ = Task.Run(() => ProcessLoop(_cts.Token));
            Logger.LogAction("SYNC_QUEUE", $"Background processor started (concurrent mode, max 3 parallel, {_queue.Count} recovered from disk)");
        }

        /// <summary>
        /// Stops the background processor. Persists remaining items to disk.
        /// </summary>
        public static void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            PersistToDisk(); // Save remaining items for next session
            Logger.LogAction("SYNC_QUEUE", $"Stopped. {_queue.Count} items persisted to disk.");
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
                        // Fire concurrently — don't block the dequeue loop
                        _ = Task.Run(async () =>
                        {
                            await _concurrency.WaitAsync(ct);
                            try
                            {
                                await ProcessJob(job, ct);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogAction("SYNC_QUEUE", $"Job error: {ex.Message}");
                            }
                            finally
                            {
                                _concurrency.Release();
                                DebouncedPersist(); // Update disk after each job completes
                            }
                        }, ct);
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

                    // Skip items older than 15 minutes (they're stale)
                    long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - job.EnqueuedAt;
                    if (ageMs > STALE_THRESHOLD_MS)
                    {
                        Logger.LogAction("SYNC_QUEUE", $"Dropped stale item (age: {ageMs / 1000}s): {job.Item.FileName ?? "text"}");
                        return;
                    }

                    switch (job.Channel)
                    {
                        case "p2p":
                        case "firebase": // legacy callers — all go through P2P now
                            await CloudDiscoveryManager.PushToCloudHub(job.Item);
                            break;
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

        // ═══ Disk Persistence ═══

        private static void DebouncedPersist()
        {
            long now = Environment.TickCount64;
            if (now - _lastPersistTick < PERSIST_DEBOUNCE_MS) return;
            _lastPersistTick = now;
            _ = Task.Run(PersistToDisk);
        }

        private static void PersistToDisk()
        {
            try
            {
                lock (_persistLock)
                {
                    var snapshot = _queue.ToArray();
                    // Only persist items that are still valid (not stale)
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var valid = snapshot.Where(j => now - j.EnqueuedAt < STALE_THRESHOLD_MS).ToArray();

                    var entries = valid.Select(j => new PersistedSyncJob
                    {
                        FileName = j.Item?.FileName ?? "",
                        RawContent = j.Item?.RawContent ?? "",
                        Channel = j.Channel,
                        EnqueuedAt = j.EnqueuedAt,
                        Attempts = j.Attempts
                    }).ToArray();

                    string dir = Path.GetDirectoryName(_persistFile)!;
                    Directory.CreateDirectory(dir);
                    string tmp = _persistFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
                    File.Move(tmp, _persistFile, true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SYNC_QUEUE", $"Persist failed: {ex.Message}");
            }
        }

        private static void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_persistFile)) return;
                string json = File.ReadAllText(_persistFile);
                var entries = JsonSerializer.Deserialize<PersistedSyncJob[]>(json);
                if (entries == null) return;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int recovered = 0;
                foreach (var entry in entries)
                {
                    // Discard stale items
                    if (now - entry.EnqueuedAt > STALE_THRESHOLD_MS) continue;

                    var item = new ClipboardItem
                    {
                        FileName = entry.FileName ?? "",
                        RawContent = entry.RawContent ?? ""
                    };
                    _queue.Enqueue(new SyncJob
                    {
                        Item = item,
                        Channel = entry.Channel ?? "firebase",
                        EnqueuedAt = entry.EnqueuedAt,
                        Attempts = entry.Attempts
                    });
                    _signal.Release();
                    recovered++;
                }

                if (recovered > 0)
                    Logger.LogAction("SYNC_QUEUE", $"Recovered {recovered} pending items from disk");

                // Clean up the persist file after loading
                try { File.Delete(_persistFile); } catch { }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SYNC_QUEUE", $"Load from disk failed: {ex.Message}");
            }
        }

        private class SyncJob
        {
            public ClipboardItem Item { get; set; } = null!;
            public string Channel { get; set; } = "firebase";
            public long EnqueuedAt { get; set; }
            public int Attempts { get; set; }
        }

        private class PersistedSyncJob
        {
            public string? FileName { get; set; }
            public string? RawContent { get; set; }
            public string? Channel { get; set; }
            public long EnqueuedAt { get; set; }
            public int Attempts { get; set; }
        }
    }
}
