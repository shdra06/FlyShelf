// Network Clock helper for FlyShelf Android
// Establishes a highly accurate global UTC time baseline using HTTP HEAD queries to NTP-aligned fast servers
// Solves pairing clock drift without native modules or UDP packages.
import { syncLog } from './debugLog';

let clockOffsetMs = 0;
let isClockSynced = false;
let _syncRetryCount = 0; // M-2: Bounded retry counter
const MAX_SYNC_RETRIES = 5; // M-2: Cap retries to prevent infinite chain

export const NetworkClock = {
  /** Get current clock offset relative to the local device system clock */
  get offsetMs(): number {
    return clockOffsetMs;
  },
  
  /** True if HTTP HEAD time sync completed successfully */
  get isSynced(): boolean {
    return isClockSynced;
  },

  /** Returns NTP-corrected current time in milliseconds */
  now(): number {
    return Math.round(Date.now() + clockOffsetMs);
  },

  /** Returns NTP-corrected current Date object */
  date(): Date {
    return new Date(this.now());
  },

  /**
   * One-shot time sync using ultra-lightweight HTTP HEAD request.
   * Measures network RTT to correct latency and determines absolute time difference.
   * 
   * ACCURACY LIMITATION: HTTP Date headers have ±1 second resolution (RFC 7231).
   * Combined with variable network RTT (especially on mobile), the effective accuracy
   * is approximately ±1-2 seconds. This is sufficient for FlyShelf's pairing clock
   * drift detection but should NOT be used for sub-second time-critical operations.
   */
  async sync(): Promise<number> {
    const timeServers = [
      'https://www.google.com',
      'https://www.cloudflare.com',
      'https://www.facebook.com'
    ];

    for (const server of timeServers) {
      try {
        const start = Date.now();
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 3000);
        
        const response = await fetch(server, {
          method: 'HEAD',
          signal: controller.signal,
          headers: { 'Cache-Control': 'no-cache' }
        });
        clearTimeout(timeout);

        const end = Date.now();
        const dateHeader = response.headers.get('date');
        if (!dateHeader) {
          syncLog('CLOCK', `No Date header returned by ${server}`);
          continue;
        }

        const serverTime = new Date(dateHeader).getTime();
        const rtt = end - start;
        // Estimate true server time at the moment the request completed:
        const estimatedServerTime = serverTime + (rtt / 2);
        
        clockOffsetMs = estimatedServerTime - end;
        isClockSynced = true;
        
        const offsetSec = (clockOffsetMs / 1000).toFixed(1);
        if (Math.abs(clockOffsetMs) > 5000) {
          syncLog('CLOCK', `⚠️ Local clock is drifted by ${offsetSec}s relative to ${server}. Applied offset correction.`);
        } else {
          syncLog('CLOCK', `✅ Local clock matches NTP time baseline (drift: ${clockOffsetMs}ms).`);
        }
        return clockOffsetMs;
      } catch (err: any) {
        syncLog('CLOCK', `NTP HTTP HEAD failed for ${server}: ${err?.message || err}`);
      }
    }
    
    // M-2 FIX: Bounded retry instead of infinite chain
    _syncRetryCount++;
    if (_syncRetryCount <= MAX_SYNC_RETRIES) {
      const retryDelay = Math.min(30000 * _syncRetryCount, 120000); // 30s, 60s, 90s, 120s, 120s
      syncLog('CLOCK', `All HEAD sync servers failed — retry ${_syncRetryCount}/${MAX_SYNC_RETRIES} in ${retryDelay / 1000}s`);
      setTimeout(() => { NetworkClock.sync().catch(() => {}); }, retryDelay);
    } else {
      syncLog('CLOCK', `All HEAD sync servers failed — max retries (${MAX_SYNC_RETRIES}) exhausted. Use resync() to try again.`);
    }
    return clockOffsetMs;
  },

  /**
   * Force a re-sync of the clock offset.
   * Resets the synced state and performs a fresh sync.
   * Useful when network conditions change or sync seems stale.
   */
  async resync(): Promise<number> {
    isClockSynced = false;
    clockOffsetMs = 0;
    _syncRetryCount = 0; // M-2: Reset retry counter on manual resync
    return this.sync();
  }
};
