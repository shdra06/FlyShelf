// ═══════════════════════════════════════════════════════════════
// usePcUrlResolver — Extracted from index.tsx (C1 decomposition)
// Optimized: Parallel discovery races, subnet scanning, and fast LAN failover.
// Priority: LAN IPs → Subnet Scan → TLS LAN → Cloudflare → Firebase
// ═══════════════════════════════════════════════════════════════
import { useRef, useCallback } from 'react';

import { getSecureItem, setSecureItem, removeSecureItem } from '../utils/secureStorage';
import { fetchWithTimeout, resolveOptimalUrl, scanSubnetForPc, decryptDeviceList } from '../utils/networkHelpers';
import { discoverPcOnLan, addToPcIpCache } from '../utils/lanDiscovery';
import { NetworkClock } from '../utils/networkClock';
import { syncLog } from '../utils/debugLog';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { getLocalPairedDevices, updatePairedDeviceIp } from './useLanPresence';
import { database } from '../firebaseConfig';
import { ref, get } from 'firebase/database';
import NetInfo from '@react-native-community/netinfo';
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

  /** Load persisted Cloudflare and LAN URLs on mount for instant reconnect */
  const persistedUrlLoadedRef = useRef(false);
  if (!persistedUrlLoadedRef.current) {
    persistedUrlLoadedRef.current = true;
    Promise.all([
      AsyncStorage.getItem('@flyshelf_last_lan_url').catch(() => null),
      AsyncStorage.getItem('lastCloudflareUrl').catch(() => null),
      AsyncStorage.getItem('pairedGlobalUrl').catch(() => null),
      getSecureItem('pairedGlobalUrl').catch(() => null),
    ]).then(([lanUrl, cfUrl, astGlobal, secGlobal]) => {
      const bestCf = (cfUrl && cfUrl.includes('trycloudflare.com')) ? cfUrl
        : ((astGlobal && astGlobal.includes('trycloudflare.com')) ? astGlobal
        : ((secGlobal && secGlobal.includes('trycloudflare.com')) ? secGlobal : null));

      if (bestCf && !cachedPcUrlRef.current) {
        cachedPcUrlRef.current = bestCf.trim().replace(/\/$/, '');
        cachedPcUrlTimestampRef.current = NetworkClock.now() - CLOUD_CACHE_TTL + 10_000;
        syncLog('URL-RESOLVE', `Loaded persisted Cloudflare URL: ${cachedPcUrlRef.current}`);
      } else if (lanUrl && !cachedPcUrlRef.current) {
        cachedPcUrlRef.current = lanUrl.trim().replace(/\/$/, '');
        cachedPcUrlTimestampRef.current = NetworkClock.now() - LAN_CACHE_TTL + 10_000;
        syncLog('URL-RESOLVE', `Loaded persisted LAN URL: ${cachedPcUrlRef.current}`);
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
      syncLog('URL-RESOLVE', `Starting parallel resolution — pk=${pk ? 'set' : 'empty'}`);

      const probeHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };

      const probeUrl = async (url: string, timeout = 2000): Promise<string> => {
        try {
          const res = await fetchWithTimeout(`${url}/api/health`, { headers: probeHeaders }, timeout);
          if (res.ok || res.status === 401) {
            // AUDIT FIX #8: Validate response is actually FlyShelf, not a random server on the same port
            try {
              const body = await res.json();
              if (body?.app !== 'FlyShelf') throw new Error('Not FlyShelf');
            } catch { /* Accept 401 without body validation (pre-pairing) */ }
            syncLog('URL-RESOLVE', `✅ Reachable: ${url} (status=${res.status})`);
            return url;
          }
        } catch (e: any) {
          syncLog('URL-RESOLVE', `❌ Probe failed ${url}: ${e?.message || 'timeout'}`);
        }
        throw new Error(`Probe failed for ${url}`);
      };

      // Network state check: If on cellular data (outside the house), skip LAN probing for instant Cloudflare switch
      const netState = await NetInfo.fetch().catch(() => null);
      const isCellular = netState?.type === 'cellular';

      // ── 1. Gather all LAN candidates (only if on Wi-Fi/Ethernet/unknown) ──
      const lanCandidates: string[] = [];
      if (!isCellular) {
        try {
          const localDevices = await getLocalPairedDevices();
          for (const dev of Object.values(localDevices)) {
            if (dev.deviceType === 'PC' && dev.lastKnownIps?.length > 0) {
              for (const ip of dev.lastKnownIps) {
                lanCandidates.push(ip.startsWith('http') ? ip : `http://${ip}`);
              }
            }
          }
        } catch {}

        const storedLocal = (await getSecureItem('pairedLocalUrl')) || (await AsyncStorage.getItem('pairedLocalUrl'));
        const storedTls = (await getSecureItem('pairedTlsUrl')) || (await AsyncStorage.getItem('pairedTlsUrl'));
        if (storedLocal) lanCandidates.push(...storedLocal.split(',').map((s: string) => s.trim()).filter(Boolean));
        if (storedTls) lanCandidates.push(storedTls.trim());
        if (pcLocalIp) lanCandidates.push(...pcLocalIp.split(',').map((s: string) => s.trim()).filter(Boolean));

        try {
          const lastLanUrl = await AsyncStorage.getItem('@flyshelf_last_lan_url');
          if (lastLanUrl) lanCandidates.push(lastLanUrl.trim());
        } catch {}

        // Emulator fallbacks
        if (!lanCandidates.some(u => u.includes('10.0.2.2'))) {
          lanCandidates.push('http://10.0.2.2:8999');
          lanCandidates.push('http://10.0.2.2:8080');
        }
        if (!lanCandidates.some(u => u.includes('localhost') || u.includes('127.0.0.1'))) {
          lanCandidates.push('http://127.0.0.1:8999');
          lanCandidates.push('http://127.0.0.1:8080');
        }
      }

      // Normalize LAN URLs (support both 8999 and 8080)
      const allLan: string[] = [];
      for (const u of lanCandidates) {
        let clean = u.trim().replace(/\/$/, '');
        if (!clean.startsWith('http')) clean = `http://${clean}`;
        if (!clean.replace(/^https?:\/\//, '').includes(':')) {
          allLan.push(`${clean}:8999`);
          allLan.push(`${clean}:8080`);
        } else {
          allLan.push(clean);
        }
      }
      const uniqueLan = Array.from(new Set(allLan));

      // ── 2. Gather all Cloud/Cloudflare candidates ──
      const cfCandidates: string[] = [];
      const storedGlobal = (await getSecureItem('pairedGlobalUrl')) || (await AsyncStorage.getItem('pairedGlobalUrl'));
      if (storedGlobal && storedGlobal.includes('trycloudflare.com')) cfCandidates.push(storedGlobal.trim().replace(/\/$/, ''));
      try {
        const lastCfUrl = await AsyncStorage.getItem('lastCloudflareUrl');
        if (lastCfUrl && lastCfUrl.includes('trycloudflare.com')) cfCandidates.push(lastCfUrl.trim().replace(/\/$/, ''));
      } catch {}

      const pc = activeDevicesRef.current.find((d: any) => d.DeviceType === 'PC');
      if (pc?.GlobalUrl && pc.GlobalUrl.includes('trycloudflare.com')) {
        cfCandidates.push(pc.GlobalUrl.trim().replace(/\/$/, ''));
      }
      if (pc?.LocalIp) {
        const pcLan = pc.LocalIp.startsWith('http') ? pc.LocalIp : `http://${pc.LocalIp}`;
        if (!uniqueLan.includes(pcLan)) uniqueLan.unshift(pcLan);
      }

      const uniqueCloud = Array.from(new Set(cfCandidates));

      // ── 3. Parallel Speed Race ──
      // Launch LAN probe (1500ms) and Cloud probe (2500ms) concurrently.
      // AUDIT FIX #12: Catch-guard promises before race to prevent AggregateError leaks on Hermes
      const lanPromise = uniqueLan.length > 0
        ? Promise.any(uniqueLan.map(url => probeUrl(url, 1500))).catch(() => null as string | null)
        : Promise.resolve(null as string | null);

      const cloudPromise = uniqueCloud.length > 0
        ? Promise.any(uniqueCloud.map(url => probeUrl(url, 2500))).catch(() => null as string | null)
        : Promise.resolve(null as string | null);

      // If LAN connects, it wins immediately (zero delay).
      // If Cloud connects first, give LAN a tiny 150ms window to claim local priority, else accept Cloud.
      try {
        const winner = await Promise.race([
          lanPromise.then(url => ({ type: 'lan' as const, url })),
          cloudPromise.then(async url => {
            // Give LAN 150ms chance to answer if on local network
            const lanQuick = await Promise.race([
              lanPromise.then(u => ({ type: 'lan' as const, url: u })).catch(() => null),
              new Promise<null>(r => setTimeout(() => r(null), 150))
            ]);
            if (lanQuick) return lanQuick;
            return { type: 'cloud' as const, url };
          })
        ]);

        if (winner?.url) {
          cachedPcUrlRef.current = winner.url;
          cachedPcUrlTimestampRef.current = startNow;
          discoveryMethodRef.current = winner.type === 'lan' ? 'stored-lan' : 'cloudflare';
          if (winner.type === 'lan') {
            AsyncStorage.setItem('@flyshelf_last_lan_url', winner.url).catch(() => {});
            try {
              const urlObj = new URL(winner.url);
              addToPcIpCache(urlObj.hostname, parseInt(urlObj.port) || 8999).catch(() => {});
            } catch {}
          } else {
            AsyncStorage.setItem('lastCloudflareUrl', winner.url).catch(() => {});
            AsyncStorage.setItem('pairedGlobalUrl', winner.url).catch(() => {});
          }
          syncLog('URL-RESOLVE', `🚀 Race winner: ${winner.url} (${winner.type}) in ${NetworkClock.now() - startNow}ms`);
          return winner.url;
        }
      } catch {
        // Both initial quick probes failed
      }

      // If fast candidates failed, try whatever remaining promise might finish
      try {
        const fallbackWinner = await Promise.any([lanPromise, cloudPromise]);
        if (fallbackWinner) {
          cachedPcUrlRef.current = fallbackWinner;
          cachedPcUrlTimestampRef.current = startNow;
          discoveryMethodRef.current = fallbackWinner.includes('trycloudflare') ? 'cloudflare' : 'stored-lan';
          return fallbackWinner;
        }
      } catch {}

      // ── 4. Query Firebase Realtime Database for Fresh PC Tunnel URL ──
      if (pk) {
        try {
          syncLog('URL-RESOLVE', '⚡ Probing saved URLs failed — asking Firebase for fresh PC tunnel link...');
          const devSnap = await get(ref(database, `active_devices/${pk}`));
          if (devSnap.exists()) {
            const data = devSnap.val();
            const devList = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline !== false);
            const decryptedList = await decryptDeviceList(devList);
            const freshPc = decryptedList.find(d => d.DeviceType === 'PC');
            if (freshPc?.GlobalUrl && freshPc.GlobalUrl.includes('trycloudflare.com')) {
              const freshUrl = freshPc.GlobalUrl.trim().replace(/\/$/, '');
              syncLog('URL-RESOLVE', `🔍 Firebase returned fresh PC GlobalUrl: ${freshUrl}`);
              try {
                const verifiedFresh = await probeUrl(freshUrl, 3000);
                if (verifiedFresh) {
                  cachedPcUrlRef.current = verifiedFresh;
                  cachedPcUrlTimestampRef.current = startNow;
                  discoveryMethodRef.current = 'cloudflare';
                  AsyncStorage.setItem('lastCloudflareUrl', verifiedFresh).catch(() => {});
                  AsyncStorage.setItem('pairedGlobalUrl', verifiedFresh).catch(() => {});
                  setSecureItem('pairedGlobalUrl', verifiedFresh).catch(() => {});
                  setSecureItem('lastCloudflareUrl', verifiedFresh).catch(() => {});
                  return verifiedFresh;
                }
              } catch {
                // If probe timed out, still assign freshUrl so poller continuously retries it!
                cachedPcUrlRef.current = freshUrl;
                cachedPcUrlTimestampRef.current = startNow;
                discoveryMethodRef.current = 'cloudflare';
                AsyncStorage.setItem('lastCloudflareUrl', freshUrl).catch(() => {});
                return freshUrl;
              }
            }
          }
        } catch (fbErr: any) {
          syncLog('URL-RESOLVE', `Firebase fresh URL query error: ${fbErr?.message || fbErr}`);
        }
      }

      // ── 5. Non-blocking Background Subnet Discovery ──
      const myIp = pcLocalIp?.split(',')[0]?.trim();
      if (myIp) {
        discoverPcOnLan(myIp).then(discovered => {
          if (discovered?.url) {
            cachedPcUrlRef.current = discovered.url;
            cachedPcUrlTimestampRef.current = NetworkClock.now();
            AsyncStorage.setItem('@flyshelf_last_lan_url', discovered.url).catch(() => {});
            syncLog('URL-RESOLVE', `📡 Background LAN scan found PC: ${discovered.url}`);
          }
        }).catch(() => {});
      }

      // ── 6. Persistent Cloud Target Fallback ──
      // If all quick strategies exhausted, keep trying the saved Cloudflare URL instead of giving up
      if (uniqueCloud.length > 0) {
        const defaultCf = uniqueCloud[0];
        cachedPcUrlRef.current = defaultCf;
        cachedPcUrlTimestampRef.current = startNow;
        discoveryMethodRef.current = 'cloudflare';
        syncLog('URL-RESOLVE', `🔄 Falling back to persistent target: ${defaultCf}`);
        return defaultCf;
      }

      syncLog('URL-RESOLVE', '❌ All quick resolution strategies exhausted');
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
