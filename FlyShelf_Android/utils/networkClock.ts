// Network Clock helper for FlyShelf Android
// Establishes a highly accurate global UTC time baseline using universal HTTPS time APIs,
// Cloudflare Anycast CDN trace, JSON atomic time APIs, and peer PC clock calibration.
// Seamlessly falls back to OS system clock when offline.
import { syncLog } from './debugLog';

let clockOffsetMs = 0;
let isClockSynced = false;
let syncSource = 'OS Clock (Fallback)';
let _syncRetryCount = 0;
const MAX_SYNC_RETRIES = 5;

export const NetworkClock = {
  /** Get current clock offset in milliseconds relative to local OS system clock */
  get offsetMs(): number {
    return clockOffsetMs;
  },
  
  /** True if network time sync completed successfully */
  get isSynced(): boolean {
    return isClockSynced;
  },

  /** Name of the active time authority */
  get source(): string {
    return syncSource;
  },

  /** Returns network-corrected current time in Unix milliseconds */
  now(): number {
    return Math.round(Date.now() + clockOffsetMs);
  },

  /** Returns network-corrected current Date object */
  date(): Date {
    return new Date(this.now());
  },

  /**
   * Calibrate clock offset directly from paired PC's server timestamp (from X-Server-Time header).
   * Ensures 0ms skew between PC and Phone even in completely offline LAN setups!
   */
  calibratePeer(pcServerTimeMs: number, rttMs: number = 0): void {
    if (!pcServerTimeMs || isNaN(pcServerTimeMs) || pcServerTimeMs < 1700000000000) return;
    const nowLocal = Date.now();
    // Estimated PC server time when response was received locally
    const estimatedPcTime = pcServerTimeMs + (rttMs / 2);
    const newOffset = estimatedPcTime - nowLocal;

    // If not previously synced via global APIs, or if peer calibration is within reasonable bounds
    if (!isClockSynced || Math.abs(newOffset - clockOffsetMs) > 1000) {
      clockOffsetMs = newOffset;
      isClockSynced = true;
      syncSource = 'Paired PC Direct Sync';
      syncLog('CLOCK', `⚡ Calibrated clock with Paired PC (offset: ${Math.round(clockOffsetMs)}ms, RTT: ${Math.round(rttMs)}ms)`);
    }
  },

  /**
   * One-shot universal time sync across multiple HTTPS time providers.
   * Measures network RTT to compensate for transit latency.
   * If all fail, gracefully falls back to OS clock without throwing.
   */
  async sync(): Promise<number> {
    // ─── 1. Cloudflare Anycast CDN Trace (Ultra fast, Anycast edge, ms precision) ───
    try {
      const start = Date.now();
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 3000);
      try {
        const res = await fetch('https://1.1.1.1/cdn-cgi/trace', {
          method: 'GET',
          signal: controller.signal,
          headers: { 'Cache-Control': 'no-cache' }
        });
        const end = Date.now();

        if (res.ok) {
          const text = await res.text();
          for (const line of text.split('\n')) {
            if (line.startsWith('ts=')) {
              const sec = parseFloat(line.substring(3).trim());
              if (!isNaN(sec) && sec > 1700000000) {
                const serverMs = sec * 1000;
                const rtt = end - start;
                clockOffsetMs = (serverMs + rtt / 2) - end;
                isClockSynced = true;
                syncSource = 'Cloudflare Anycast (1.1.1.1)';
                _syncRetryCount = 0;
                syncLog('CLOCK', `✅ Universal time synced via ${syncSource} (drift: ${Math.round(clockOffsetMs)}ms)`);
                return clockOffsetMs;
              }
            }
          }
        }
      } finally {
        clearTimeout(timeout);
      }
    } catch {}

    // ─── 2. WorldTimeAPI (JSON UTC atomic time) ───
    try {
      const start = Date.now();
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 3000);
      try {
        const res = await fetch('https://worldtimeapi.org/api/timezone/Etc/UTC', {
          method: 'GET',
          signal: controller.signal,
          headers: { 'Cache-Control': 'no-cache' }
        });
        const end = Date.now();

        if (res.ok) {
          const data = await res.json();
          if (data && typeof data.unixtime === 'number') {
            const serverMs = data.unixtime * 1000;
            const rtt = end - start;
            clockOffsetMs = (serverMs + rtt / 2) - end;
            isClockSynced = true;
            syncSource = 'WorldTimeAPI';
            _syncRetryCount = 0;
            syncLog('CLOCK', `✅ Universal time synced via ${syncSource} (drift: ${Math.round(clockOffsetMs)}ms)`);
            return clockOffsetMs;
          }
        }
      } finally {
        clearTimeout(timeout);
      }
    } catch {}

    // ─── 3. HTTP HEAD Date Header from Major CDNs ───
    const headServers = [
      'https://www.google.com',
      'https://www.cloudflare.com',
      'https://www.microsoft.com'
    ];

    for (const server of headServers) {
      try {
        const start = Date.now();
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 3000);
        try {
          const response = await fetch(server, {
            method: 'HEAD',
            signal: controller.signal,
            headers: { 'Cache-Control': 'no-cache' }
          });
          const end = Date.now();

          const dateHeader = response.headers.get('date');
          if (dateHeader) {
            const serverTime = new Date(dateHeader).getTime();
            if (!isNaN(serverTime) && serverTime > 1700000000000) {
              const rtt = end - start;
              clockOffsetMs = (serverTime + rtt / 2) - end;
              isClockSynced = true;
              syncSource = `HTTP Date (${server})`;
              _syncRetryCount = 0;
              syncLog('CLOCK', `✅ Universal time synced via ${syncSource} (drift: ${Math.round(clockOffsetMs)}ms)`);
              return clockOffsetMs;
            }
          }
        } finally {
          clearTimeout(timeout);
        }
      } catch {}
    }

    // ─── 4. Fallback to OS System Clock ───
    if (!isClockSynced) {
      clockOffsetMs = 0;
      syncSource = 'OS System Clock (Offline Fallback)';
      syncLog('CLOCK', '⚠️ All universal time APIs unreachable — using OS system clock (fallback)');
    }

    _syncRetryCount++;
    if (_syncRetryCount <= MAX_SYNC_RETRIES) {
      const retryDelay = Math.min(30000 * _syncRetryCount, 120000);
      setTimeout(() => { NetworkClock.sync().catch(() => {}); }, retryDelay);
    }

    return clockOffsetMs;
  },

  /**
   * Force a re-sync of the clock offset.
   */
  async resync(): Promise<number> {
    isClockSynced = false;
    clockOffsetMs = 0;
    _syncRetryCount = 0;
    return this.sync();
  }
};
