// ═══════════════════════════════════════════════════════════════
// usePcUrlResolver — Extracted from index.tsx (C1 decomposition)
// Optimized: Parallel discovery races, subnet scanning, and fast LAN failover.
// Priority: LAN IPs → Subnet Scan → TLS LAN → Cloudflare → Firebase
// ═══════════════════════════════════════════════════════════════
import { useRef, useCallback } from 'react';
import { ref, get } from 'firebase/database';
import { database } from '../firebaseConfig';
import { getSecureItem, removeSecureItem } from '../utils/secureStorage';
import { fetchWithTimeout, resolveOptimalUrl, getDeviceUrls, isValidPairingKey, scanSubnetForPc } from '../utils/networkHelpers';
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

/** URL cache TTL in ms (30 seconds for stable connections) */
const URL_CACHE_TTL = 30_000;

/**
 * Hook for resolving the optimal PC URL with multi-priority fallback chain.
 */
export function usePcUrlResolver(
  pairingKeyRef: React.MutableRefObject<string>,
  activeDevicesRef: React.MutableRefObject<ActiveDeviceInfo[]>,
  pcLocalIp?: string,
) {
  const cachedPcUrlRef = useRef<string | null>(null);
  const cachedPcUrlTimestampRef = useRef<number>(0);
  const activeUrlResolutionPromiseRef = useRef<Promise<string> | null>(null);

  /** Cloudflare consecutive failure counter */
  const cloudflareFailCountRef = useRef<number>(0);

  /** Invalidate the cache and force a fresh resolution */
  const invalidateCache = useCallback(() => {
    cachedPcUrlRef.current = null;
    cachedPcUrlTimestampRef.current = 0;
  }, []);

  const recordCloudflareFailure = useCallback(() => {
    cloudflareFailCountRef.current++;
    if (cloudflareFailCountRef.current >= 3) {
      syncLog('URL-RESOLVE', `Cloudflare failed 3x — forcing URL re-resolution`);
      cloudflareFailCountRef.current = 0;
      invalidateCache();
      return true;
    }
    return false;
  }, [invalidateCache]);

  const resetCloudflareFailCount = useCallback(() => {
    cloudflareFailCountRef.current = 0;
  }, []);

  /**
   * Returns the best available PC URL using parallel races.
   */
  const getCachedPcUrl = useCallback(async (): Promise<string> => {
    const now = NetworkClock.now();
    if (cachedPcUrlRef.current && (now - cachedPcUrlTimestampRef.current) < URL_CACHE_TTL) {
      return cachedPcUrlRef.current;
    }

    if (activeUrlResolutionPromiseRef.current) {
      return activeUrlResolutionPromiseRef.current;
    }

    const runResolution = async (): Promise<string> => {
      const startNow = NetworkClock.now();
      const pk = pairingKeyRef.current;
      const headers = { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk || '' };

      const tryResolve = async (urls: string[], timeout = 2000): Promise<string | null> => {
        if (urls.length === 0) return null;
        try {
          return await Promise.any(
            urls.map(async (url) => {
              const res = await fetchWithTimeout(`${url}/api/health`, { headers }, timeout);
              if (res.ok) return url;
              throw new Error();
            })
          );
        } catch {
          return null;
        }
      };

      // 1. Check Pairing URLs & Local IP together (Highest priority)
      const storedLocal = await getSecureItem('pairedLocalUrl');
      const storedTls = await getSecureItem('pairedTlsUrl');
      const storedGlobal = await getSecureItem('pairedGlobalUrl');
      
      const lanCandidates: string[] = [];
      if (storedLocal) lanCandidates.push(...storedLocal.split(',').map(s => s.trim()));
      if (storedTls) lanCandidates.push(storedTls.trim());
      if (pcLocalIp) lanCandidates.push(...pcLocalIp.split(',').map(s => s.trim()));

      const resolvedLan = await tryResolve(lanCandidates, 1500);
      if (resolvedLan) {
        cachedPcUrlRef.current = resolvedLan;
        cachedPcUrlTimestampRef.current = startNow;
        return resolvedLan;
      }

      // 2. Subnet Discovery (Scan for PC without Firebase)
      if (pcLocalIp) {
        syncLog('URL-RESOLVE', 'LAN IPs unreachable — triggering subnet scan discovery...');
        const discovered = await scanSubnetForPc(pcLocalIp.split(',')[0]);
        if (discovered.length > 0) {
          cachedPcUrlRef.current = discovered[0];
          cachedPcUrlTimestampRef.current = startNow;
          return discovered[0];
        }
      }

      // 3. Fallback to Firebase Discovered Devices (Race all known URLs)
      const pc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
      if (pc) {
        const resolved = await resolveOptimalUrl(pc, fetchWithTimeout, pk);
        if (resolved) {
          cachedPcUrlRef.current = resolved;
          cachedPcUrlTimestampRef.current = startNow;
          return resolved;
        }
      }

      // 4. Global Cloudflare Fallback
      if (storedGlobal && storedGlobal.includes('trycloudflare.com')) {
        const resolvedGlobal = await tryResolve([storedGlobal], 3000);
        if (resolvedGlobal) {
          cachedPcUrlRef.current = resolvedGlobal;
          cachedPcUrlTimestampRef.current = startNow;
          return resolvedGlobal;
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
    cachedPcUrlRef,
    cachedPcUrlTimestampRef,
  };
}
