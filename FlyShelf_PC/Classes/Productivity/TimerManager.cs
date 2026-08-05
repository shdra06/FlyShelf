using System;
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Centralized timer pool for managing DispatcherTimer lifecycles.
    /// Provides named timers with automatic cleanup, preventing orphaned timers
    /// from continuing to fire after their parent context is destroyed.
    /// 
    /// Usage:
    ///   TimerManager.StartOrRestart("SearchDebounce", TimeSpan.FromMilliseconds(300), OnSearchDebounce);
    ///   TimerManager.Stop("SearchDebounce");
    ///   TimerManager.StopAll(); // Call on app shutdown
    /// </summary>
    public static class TimerManager
    {
        private static readonly ConcurrentDictionary<string, (DispatcherTimer timer, EventHandler handler)> _timers = new();

        /// <summary>
        /// Gets or creates a named timer. If the timer already exists, updates its interval
        /// and handler. Does NOT auto-start — call Start() or use StartOrRestart().
        /// </summary>
        public static DispatcherTimer GetOrCreate(string name, TimeSpan interval, EventHandler handler)
        {
            if (_timers.TryGetValue(name, out var existing))
            {
                existing.timer.Stop();
                existing.timer.Interval = interval;
                // Remove old handlers and add new one
                existing.timer.Tick -= existing.handler; // Safe even if not subscribed
                existing.timer.Tick += handler;
                _timers[name] = (existing.timer, handler);
                return existing.timer;
            }

            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += handler;
            _timers[name] = (timer, handler);
            return timer;
        }

        /// <summary>
        /// Starts or restarts a named timer. Creates it if it doesn't exist.
        /// If already running, stops and restarts (resetting the interval countdown).
        /// </summary>
        public static void StartOrRestart(string name, TimeSpan interval, EventHandler handler)
        {
            var timer = GetOrCreate(name, interval, handler);
            timer.Stop();
            timer.Start();
        }

        /// <summary>
        /// Stops a named timer without removing it. Can be restarted later.
        /// </summary>
        public static void Stop(string name)
        {
            if (_timers.TryGetValue(name, out var existing))
                existing.timer.Stop();
        }

        /// <summary>
        /// Stops and removes a named timer permanently.
        /// </summary>
        public static void Remove(string name)
        {
            if (_timers.TryRemove(name, out var existing))
                existing.timer.Stop();
        }

        /// <summary>
        /// Returns true if the named timer exists and is currently running.
        /// </summary>
        public static bool IsRunning(string name)
            => _timers.TryGetValue(name, out var existing) && existing.timer.IsEnabled;

        /// <summary>
        /// Stops ALL managed timers. Call this on application shutdown.
        /// </summary>
        public static void StopAll()
        {
            foreach (var kv in _timers)
            {
                try { kv.Value.timer.Stop(); } catch { } // Best-effort: failure is acceptable
            }
        }

        /// <summary>
        /// Stops and removes ALL managed timers. Call on final cleanup.
        /// </summary>
        public static void DisposeAll()
        {
            StopAll();
            _timers.Clear();
        }
    }
}
