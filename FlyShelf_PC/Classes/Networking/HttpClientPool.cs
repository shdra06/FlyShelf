using System;
using System.Net.Http;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Centralized HttpClient pool — avoids socket exhaustion from per-request instances.
    /// Use these shared instances instead of creating new HttpClient() anywhere.
    /// </summary>
    public static class HttpClientPool
    {
        /// <summary>Default client with 15s timeout — for most API calls.</summary>
        public static HttpClient Default { get; } = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>Short timeout client (5s) — for health checks, diagnostics, logging.</summary>
        public static HttpClient Quick { get; } = new HttpClient() { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>Long timeout client (10min) — for file downloads.</summary>
        public static HttpClient Download { get; } = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        }) { Timeout = TimeSpan.FromMinutes(10) };

        /// <summary>Medium timeout client (30s) — for sync operations.</summary>
        public static HttpClient Sync { get; } = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
    }
}
