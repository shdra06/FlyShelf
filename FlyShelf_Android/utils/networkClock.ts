// Network Clock helper for FlyShelf Android
// Establishes a highly accurate global UTC time baseline using HTTP HEAD queries to NTP-aligned fast servers
// Solves pairing clock drift without native modules or UDP packages.
import { syncLog } from './debugLog';

let clockOffsetMs = 0;
let isClockSynced = false;

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
    
    syncLog('CLOCK', 'All HEAD sync servers failed — falling back to system clock');
    return clockOffsetMs;
  }
};
