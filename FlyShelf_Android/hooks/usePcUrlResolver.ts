// ═══════════════════════════════════════════════════════════════
// usePcUrlResolver — Extracted from index.tsx (C1 decomposition)
// Optimized: Parallel discovery races, subnet scanning, and fast LAN failover.
// Priority: LAN IPs → Subnet Scan → TLS LAN → Cloudflare → Firebase
// ═══════════════════════════════════════════════════════════════
import { useRef, useCallback } from 'react';

import { getSecureItem, removeSecureItem } from '../utils/secureStorage';
import { fetchWithTimeout, resolveOptimalUrl, scanSubnetForPc } from '../utils/networkHelpers';
import { discoverPcOnLan, addToPcIpCache } from '../utils/lanDiscovery';
import { NetworkClock } from '../utils/networkClock';
import { syncLog } from '../utils/debugLog';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { getLocalPairedDevices, updatePairedDeviceIp } from './useLanPresence';
// Audit: moved ActiveDeviceInfo to shared utils/deviceTypes.ts (was duplicated here)
import { ActiveDeviceInfo } from '../utils/deviceTypes';
// Re-export so any existing imports from this module keep working
export type { ActiveDeviceInfo } from '../utils/deviceTypes';

/** URL cache TTL in ms — adaptive based on connection type */
const LAN_CACHE_TTL = 30_000;  // 30s — reduced from 60s for faster stale detection
const CLOUD_CACHE_TTL = 30_000; // 30s for Cloudflare (tunnels rotate)

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
  const discoveryMethodRef = useRef<'stored-lan' | 'subnet-scan' | 'firebase' | 'cloudflare' | null>(null);

  /** Load persisted LAN URL on mount for instant reconnect */
  const persistedUrlLoadedRef = useRef(false);
  if (!persistedUrlLoadedRef.current) {
    persistedUrlLoadedRef.current = true;
    AsyncStorage.getItem('@flyshelf_last_lan_url').then(url => {
      if (url && !cachedPcUrlRef.current) {
        cachedPcUrlRef.current = url;
        cachedPcUrlTimestampRef.current = NetworkClock.now() - LAN_CACHE_TTL + 10_000; // Valid for 10s to allow fresh probe
        syncLog('URL-RESOLVE', `Loaded persisted LAN URL: ${url}`);
      }
    }).catch(() => {});
  }

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
    const cacheTtl = (cachedPcUrlRef.current?.includes('trycloudflare.com')) ? CLOUD_CACHE_TTL : LAN_CACHE_TTL;
    if (cachedPcUrlRef.current && (now - cachedPcUrlTimestampRef.current) < cacheTtl) {
      return cachedPcUrlRef.current;
    }

    if (activeUrlResolutionPromiseRef.current) {
      return activeUrlResolutionPromiseRef.current;
    }

    const runResolution = async (): Promise<string> => {
      const startNow = NetworkClock.now();
      const pk = pairingKeyRef.current;
      syncLog('URL-RESOLVE', `Starting resolution — pk=${pk ? 'set' : 'empty'}`);

      const tryResolve = async (urls: string[], timeout = 2000): Promise<string | null> => {
        if (urls.length === 0) return null;
        // v6.0.2 audit: health probes should NOT send X-Pairing-Key
        // The health endpoint is just a reachability check — no auth needed
        const probeHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
        syncLog('URL-RESOLVE', `Probing ${urls.length} URL(s): ${urls.slice(0, 3).join(', ')}`);
        try {
          return await Promise.any(
            urls.map(async (url) => {
              try {
                const res = await fetchWithTimeout(`${url}/api/health`, { headers: probeHeaders }, timeout);
                // Accept 200 (ok) or 401 (server reachable but needs auth on other endpoints)
                if (res.ok || res.status === 401) {
                  syncLog('URL-RESOLVE', `✅ Reachable: ${url} (status=${res.status})`);
                  return url;
                }
                syncLog('URL-RESOLVE', `❌ Bad status ${res.status} for ${url}`);
              } catch (e: any) {
                syncLog('URL-RESOLVE', `❌ Probe failed ${url}: ${e?.message}`);
              }
              throw new Error();
            })
          );
        } catch {
          // v6.0.2 fallback: return first URL as "best guess" even if all probes failed
          // The PC may still be reachable — don't give up
          syncLog('URL-RESOLVE', `⚠️ All probes failed — using best-guess: ${urls[0]}`);
          return urls[0];
        }
      };

      // 0. Probe locally stored paired device IPs (fastest — from pairing/last connection)
      try {
        const localDevices = await getLocalPairedDevices();
        const allIps: string[] = [];
        for (const dev of Object.values(localDevices)) {
          if (dev.deviceType === 'PC' && dev.lastKnownIps?.length > 0) {
            for (const ip of dev.lastKnownIps) {
              allIps.push(`http://${ip}`);
            }
          }
        }
        if (allIps.length > 0) {
          syncLog('URL-RESOLVE', `Step 0: Probing ${allIps.length} stored device IPs`);
          const localHit = await tryResolve(allIps, 1500);
          if (localHit) {
            cachedPcUrlRef.current = localHit;
            cachedPcUrlTimestampRef.current = startNow;
            discoveryMethodRef.current = 'stored-lan';
            // Update stored IP timestamp
            try {
              const urlObj = new URL(localHit);
              for (const dev of Object.values(localDevices)) {
                if (dev.deviceType === 'PC') {
                  await updatePairedDeviceIp(dev.deviceId, `${urlObj.hostname}:${urlObj.port || '8999'}`);
                }
              }
            } catch {}
            return localHit;
          }
        }
      } catch {}

      // 1. Check Pairing URLs & Local IP together (Highest priority)
      // v6.0.2 audit: read from BOTH SecureStore AND AsyncStorage
      // Old versions stored URLs in AsyncStorage — migration may not have happened
      const storedLocal = (await getSecureItem('pairedLocalUrl')) || (await AsyncStorage.getItem('pairedLocalUrl'));
      const storedTls = (await getSecureItem('pairedTlsUrl')) || (await AsyncStorage.getItem('pairedTlsUrl'));
      const storedGlobal = (await getSecureItem('pairedGlobalUrl')) || (await AsyncStorage.getItem('pairedGlobalUrl'));
      // Also check old Cloudflare cache key from v6.0.2
      let lastCfUrl: string | null = null;
      try { lastCfUrl = await AsyncStorage.getItem('lastCloudflareUrl'); } catch {}
      
      const lanCandidates: string[] = [];
      if (storedLocal) lanCandidates.push(...storedLocal.split(',').map((s: string) => s.trim()).filter(Boolean));
      if (storedTls) lanCandidates.push(storedTls.trim());
      if (pcLocalIp) lanCandidates.push(...pcLocalIp.split(',').map((s: string) => s.trim()).filter(Boolean));

      // Add cached last successful LAN URL
      try {
        const lastLanUrl = await AsyncStorage.getItem('@flyshelf_last_lan_url');
        if (lastLanUrl) lanCandidates.push(lastLanUrl.trim());
      } catch {}

      // Emulator fallback: host machine is always at 10.0.2.2 from inside an Android emulator
      // localhost:8999 also works when 'adb reverse tcp:8999 tcp:8999' is active
      // This fixes the case where the PC's pairedLocalUrl is 192.168.x.x (unreachable from emulator)
      if (!lanCandidates.some(u => u.includes('10.0.2.2'))) {
        lanCandidates.push('http://10.0.2.2:8999');
      }
      if (!lanCandidates.some(u => u.includes('localhost') || u.includes('127.0.0.1'))) {
        lanCandidates.push('http://127.0.0.1:8999');
      }

      const resolvedLan = await tryResolve(lanCandidates, 1500);
      if (resolvedLan) {
        cachedPcUrlRef.current = resolvedLan;
        cachedPcUrlTimestampRef.current = startNow;
        discoveryMethodRef.current = 'stored-lan';
        // Persist LAN URL for instant reconnect on next app launch
        if (!resolvedLan.includes('trycloudflare.com')) {
          AsyncStorage.setItem('@flyshelf_last_lan_url', resolvedLan).catch(() => {});
          try {
            const urlObj = new URL(resolvedLan);
            addToPcIpCache(urlObj.hostname, parseInt(urlObj.port) || 8999).catch(() => {});
          } catch {}
        }
        return resolvedLan;
      }

      // 2. Enhanced LAN Discovery (cached IPs + priority DHCP ranges + subnet scan)
      const myIp = pcLocalIp?.split(',')[0]?.trim();
      if (myIp) {
        syncLog('URL-RESOLVE', 'LAN IPs unreachable — triggering enhanced LAN discovery...');
        const discovered = await discoverPcOnLan(myIp);
        if (discovered) {
          cachedPcUrlRef.current = discovered.url;
          cachedPcUrlTimestampRef.current = startNow;
          discoveryMethodRef.current = 'subnet-scan';
          AsyncStorage.setItem('@flyshelf_last_lan_url', discovered.url).catch(() => {});
          return discovered.url;
        }
      }

      // 3. Fallback to Firebase Discovered Devices (Race all known URLs)
      const pc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
      if (pc) {
        const resolved = await resolveOptimalUrl(pc, fetchWithTimeout, pk);
        if (resolved) {
          cachedPcUrlRef.current = resolved;
          cachedPcUrlTimestampRef.current = startNow;
          discoveryMethodRef.current = 'firebase';
          return resolved;
        }
      }

      // 4. Global Cloudflare Fallback (check both new and old storage keys)
      const cfCandidates: string[] = [];
      if (storedGlobal && storedGlobal.includes('trycloudflare.com')) cfCandidates.push(storedGlobal);
      if (lastCfUrl && lastCfUrl.includes('trycloudflare.com') && !cfCandidates.includes(lastCfUrl)) cfCandidates.push(lastCfUrl);
      if (cfCandidates.length > 0) {
        const resolvedGlobal = await tryResolve(cfCandidates, 3000);
        if (resolvedGlobal) {
          cachedPcUrlRef.current = resolvedGlobal;
          cachedPcUrlTimestampRef.current = startNow;
          discoveryMethodRef.current = 'cloudflare';
          return resolvedGlobal;
        }
      }

      syncLog('URL-RESOLVE', '❌ All resolution strategies exhausted');
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
    discoveryMethodRef,
  };
}
