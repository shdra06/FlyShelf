// ═══════════════════════════════════════════════════════════════
// usePcUrlResolver — Extracted from index.tsx (C1 decomposition)
// Manages PC URL discovery, caching, and resolution priority chain.
// Priority: LAN IPs → TLS LAN → Cloudflare → Firebase → Manual IP
// ═══════════════════════════════════════════════════════════════
import { useRef, useCallback } from 'react';
import { ref, get } from 'firebase/database';
import { database } from '../firebaseConfig';
import { getSecureItem, removeSecureItem } from '../utils/secureStorage';
import { fetchWithTimeout, resolveOptimalUrl, getDeviceUrls, isValidPairingKey } from '../utils/networkHelpers';
import { NetworkClock } from '../utils/networkClock';
import { syncLog } from '../utils/debugLog';

/** Cached active device from Firebase */
export interface ActiveDeviceInfo {
  DeviceId?: string;
  DeviceName?: string;
  DeviceType?: string;
  LocalIp?: string;
  GlobalUrl?: string;
  TlsUrl?: string;
  IsOnline?: boolean;
  Timestamp?: number;
  [key: string]: any;
}

/** URL cache TTL in ms (15 seconds) */
const URL_CACHE_TTL = 15_000;

/**
 * Hook for resolving the optimal PC URL with multi-priority fallback chain.
 * Extracted from SyncScreen to reduce the 3400+ line monolith.
 *
 * @param pairingKeyRef - Ref to current pairing key
 * @param activeDevicesRef - Ref to active devices list from Firebase listener
 * @param pcLocalIp - Manual IP from settings (legacy fallback)
 */
export function usePcUrlResolver(
  pairingKeyRef: React.MutableRefObject<string>,
  activeDevicesRef: React.MutableRefObject<ActiveDeviceInfo[]>,
  pcLocalIp?: string,
) {
  const cachedPcUrlRef = useRef<string | null>(null);
  const cachedPcUrlTimestampRef = useRef<number>(0);
  const activeUrlResolutionPromiseRef = useRef<Promise<string> | null>(null);

  /** Cloudflare consecutive failure counter — forces URL re-resolution after 3 failures */
  const cloudflareFailCountRef = useRef<number>(0);

  /** Invalidate the cache and force a fresh resolution */
  const invalidateCache = useCallback(() => {
    cachedPcUrlRef.current = null;
    cachedPcUrlTimestampRef.current = 0;
  }, []);

  /** Increment cloudflare failure count. Returns true if threshold reached (3) and cache was invalidated. */
  const recordCloudflareFailure = useCallback(() => {
    cloudflareFailCountRef.current++;
    if (cloudflareFailCountRef.current >= 3) {
      syncLog('URL-RESOLVE', `Cloudflare failed ${cloudflareFailCountRef.current}x — forcing URL re-resolution`);
      cloudflareFailCountRef.current = 0;
      invalidateCache();
      return true;
    }
    return false;
  }, [invalidateCache]);

  /** Reset cloudflare failure count (call on successful Cloudflare request) */
  const resetCloudflareFailCount = useCallback(() => {
    cloudflareFailCountRef.current = 0;
  }, []);

  /**
   * Returns the best available PC URL, with caching (15s TTL) and
   * coalesced concurrent calls to prevent thundering herd.
   *
   * Priority chain:
   * 1. Cached URL (if < 15s old)
   * 2. Stored LAN IPs from pairing
   * 3. Stored TLS LAN URL (encrypted local, preferred over Cloudflare)
   * 4. Stored Cloudflare URL
   * 5. Firebase active_devices PC entries
   * 6. Firebase nodes/ PC entries
   * 7. Manual IP from Settings (legacy)
   */
  const getCachedPcUrl = useCallback(async (): Promise<string> => {
    // Return cached URL if fresh
    const now = NetworkClock.now();
    if (cachedPcUrlRef.current && (now - cachedPcUrlTimestampRef.current) < URL_CACHE_TTL) {
      return cachedPcUrlRef.current;
    }

    // Coalesce concurrent calls
    if (activeUrlResolutionPromiseRef.current) {
      return activeUrlResolutionPromiseRef.current;
    }

    const runResolution = async (): Promise<string> => {
      const startNow = NetworkClock.now();
      const pk = pairingKeyRef.current;
      const headers = { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk || '' };

      // Priority 2: Stored pairing URLs
      try {
        const storedLocal = await getSecureItem('pairedLocalUrl');
        const storedTls = await getSecureItem('pairedTlsUrl');
        const storedGlobal = await getSecureItem('pairedGlobalUrl');
        const candidates: string[] = [];
        if (storedLocal) {
          candidates.push(...storedLocal.split(',').map(s => s.trim()).filter(Boolean));
        }
        if (storedTls && storedTls.startsWith('https://')) {
          candidates.push(storedTls.trim());
        }
        if (storedGlobal) {
          candidates.push(storedGlobal.trim());
        }
        for (const url of candidates) {
          try {
            const res = await fetchWithTimeout(`${url}/api/health`, { headers }, 2000);
            if (res.ok) {
              cachedPcUrlRef.current = url;
              cachedPcUrlTimestampRef.current = startNow;
              return url;
            }
          } catch (e) { syncLog('URL-RESOLVE', `Health probe failed for ${url}: ${(e as any)?.message || e}`); }
        }
      } catch {
        const pairedGlobal = await getSecureItem('pairedGlobalUrl').catch(() => null);
        if (pairedGlobal && pairedGlobal.includes('trycloudflare.com')) {
          removeSecureItem('pairedGlobalUrl').catch(() => {});
        }
      }

      // Priority 3: Last-known Cloudflare URL
      try {
        const lastCfUrl = await getSecureItem('lastCloudflareUrl');
        if (lastCfUrl && lastCfUrl.includes('trycloudflare.com')) {
          try {
            const res = await fetchWithTimeout(`${lastCfUrl}/api/health`, { headers }, 3000);
            if (res.ok) {
              cachedPcUrlRef.current = lastCfUrl;
              cachedPcUrlTimestampRef.current = startNow;
              return lastCfUrl;
            }
          } catch {
            removeSecureItem('lastCloudflareUrl').catch(() => {});
          }
        }
      } catch {}

      // Priority 4: Firebase auto-discovered devices
      const pc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
      if (pc) {
        const urls = getDeviceUrls(pc);
        const resolved = urls.length === 1 ? urls[0] : await resolveOptimalUrl(pc, fetchWithTimeout, pk);
        if (resolved) {
          cachedPcUrlRef.current = resolved;
          cachedPcUrlTimestampRef.current = startNow;
          return resolved;
        }
      }

      // Priority 5: Direct Firebase query for PC nodes
      if (pk && isValidPairingKey(pk)) {
        try {
          const nodesSnap = await get(ref(database, `nodes/${pk}`));
          if (nodesSnap.exists()) {
            const nodes = nodesSnap.val();
            for (const key of Object.keys(nodes)) {
              const node = nodes[key];
              if (node.DeviceType === 'PC') {
                const urls = getDeviceUrls(node);
                for (const url of urls) {
                  try {
                    const res = await fetchWithTimeout(`${url}/api/health`, { headers: { ...headers, 'X-Pairing-Key': pk } }, 2500);
                    if (res.ok) {
                      cachedPcUrlRef.current = url;
                      cachedPcUrlTimestampRef.current = startNow;
                      return url;
                    }
                  } catch (e) { syncLog('URL-RESOLVE', `Firebase node probe failed for ${url}: ${(e as any)?.message || e}`); }
                }
              }
            }
          }
        } catch (e) { syncLog('URL-RESOLVE', `Firebase nodes query failed: ${(e as any)?.message || e}`); }
      }

      // Priority 6: Manual IP from Settings (legacy fallback)
      const raw = pcLocalIp?.trim();
      if (raw) {
        const parts = raw.split(',');
        for (const part of parts) {
          const trimmed = part.trim();
          if (!trimmed) continue;
          return trimmed.startsWith('http') ? trimmed.replace(/\/$/, '') : `http://${trimmed.includes(':') ? trimmed : trimmed + ':8999'}`;
        }
      }

      return '';
    };

    activeUrlResolutionPromiseRef.current = runResolution();
    try {
      return await activeUrlResolutionPromiseRef.current;
    } finally {
      activeUrlResolutionPromiseRef.current = null;
    }
  }, [pcLocalIp]);

  return {
    getCachedPcUrl,
    invalidateCache,
    recordCloudflareFailure,
    resetCloudflareFailCount,
    /** Direct ref access for cases where code needs to set the URL directly */
    cachedPcUrlRef,
    cachedPcUrlTimestampRef,
  };
}
