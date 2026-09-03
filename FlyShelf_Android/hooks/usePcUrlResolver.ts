// ═══════════════════════════════════════════════════════════════
// usePcUrlResolver — Extracted from index.tsx (C1 decomposition)
// Tiered Connection Engine:
// 1. Firebase Direct PC Lookup (Cloudflare Public URL)
// 2. LAN Fallback (Local IP & Port Probing)
// 3. Firebase URL Request Signal (Prompts PC to re-publish fresh URLs)
// 4. Clean Wait & Reactive Reconnect
// ═══════════════════════════════════════════════════════════════
import { useRef, useCallback, useEffect } from 'react';

import { getSecureItem, setSecureItem, removeSecureItem } from '../utils/secureStorage';
import { fetchWithTimeout, decryptDeviceList, decryptDevice } from '../utils/networkHelpers';
import { discoverPcOnLan, addToPcIpCache } from '../utils/lanDiscovery';
import { NetworkClock } from '../utils/networkClock';
import { syncLog } from '../utils/debugLog';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { getLocalPairedDevices, updatePairedDeviceIp } from './useLanPresence';
import { database, ensureFirebaseAuth } from '../firebaseConfig';
import { ref, get, set } from 'firebase/database';
import NetInfo from '@react-native-community/netinfo';
// Audit: moved ActiveDeviceInfo to shared utils/deviceTypes.ts (was duplicated here)
import { ActiveDeviceInfo } from '../utils/deviceTypes';
// Re-export so any existing imports from this module keep working
export type { ActiveDeviceInfo } from '../utils/deviceTypes';

/** URL cache TTL in ms — adaptive based on connection type */
const LAN_CACHE_TTL = 30_000;  // 30s
const CLOUD_CACHE_TTL = 30_000; // 30s for Cloudflare

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
  const lastUrlRequestSentRef = useRef<number>(0);

  /** Load persisted Cloudflare and LAN URLs on mount for instant reconnect */
  const persistedUrlLoadedRef = useRef(false);
  useEffect(() => {
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
  }, []);

  /** Cloudflare consecutive failure counter */
  const cloudflareFailCountRef = useRef<number>(0);

  /** Invalidate the cache and force a fresh resolution */
  const invalidateCache = useCallback(() => {
    cachedPcUrlRef.current = null;
    cachedPcUrlTimestampRef.current = 0;
    activeUrlResolutionPromiseRef.current = null;
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
   * Returns the best available PC URL using tiered discovery.
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
      syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] Starting tiered resolution — pk=${pk ? pk.substring(0, 8) + '...' : 'empty'}`);

      const probeHeaders: Record<string, string> = {
        'X-FlyShelf-Client': 'MobileCompanion',
        ...(pk ? { 'X-Pairing-Key': pk } : {})
      };

      const probeUrl = async (url: string, timeout = 2500, signal?: AbortSignal): Promise<string> => {
        try {
          const res = await fetchWithTimeout(`${url}/api/health`, { headers: probeHeaders, signal }, timeout);
          if (res.ok || res.status === 401) {
            if (res.ok) {
              try {
                const pcTimeHeader = res.headers.get('X-Server-Time');
                if (pcTimeHeader) {
                  const pcTime = parseInt(pcTimeHeader, 10);
                  if (!isNaN(pcTime)) NetworkClock.calibratePeer(pcTime, timeout);
                }
                const body = await res.json();
                if (body?.app !== 'FlyShelf') throw new Error(`Probe signature mismatch for ${url}`);
              } catch (e: any) {
                if (e?.message?.includes('signature mismatch')) throw e;
              }
            }
            syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ✅ Reachable: ${url} (status=${res.status})`);
            return url;
          }
        } catch (e: any) {
          syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ❌ Probe failed ${url}: ${e?.message || 'timeout'}`);
          throw e;
        }
        throw new Error(`Probe failed for ${url}`);
      };

      let livePcDevice: ActiveDeviceInfo | null = null;

      // FAST PATH: Probe proven stored LAN URLs first (completes in ~50ms on local Wi-Fi)
      try {
        const storedLocal = (await getSecureItem('pairedLocalUrl')) || (await AsyncStorage.getItem('pairedLocalUrl'));
        const lastLan = await AsyncStorage.getItem('@flyshelf_last_lan_url');
        const fastCandidates = Array.from(new Set([storedLocal, lastLan].filter(Boolean) as string[]));
        if (fastCandidates.length > 0) {
          const fastWinner = await Promise.any(fastCandidates.map(u => probeUrl(u.trim().replace(/\/$/, ''), 1200))).catch(() => null);
          if (fastWinner) {
            cachedPcUrlRef.current = fastWinner;
            cachedPcUrlTimestampRef.current = startNow;
            discoveryMethodRef.current = 'stored-lan';
            syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚡ Fast LAN Connected in ${NetworkClock.now() - startNow}ms: ${fastWinner}`);
            return fastWinner;
          }
        }
      } catch {}

      // ═══════════════════════════════════════════════════════════════
      // TIER 1: Direct Firebase PC Lookup (Cloudflare Public URL)
      // ═══════════════════════════════════════════════════════════════
      if (pk) {
        try {
          await ensureFirebaseAuth().catch(() => {});
          const devSnap = await get(ref(database, `active_devices/${pk}`));
          if (devSnap.exists()) {
            const data = devSnap.val();
            const devList = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline !== false);
            const decryptedList = await decryptDeviceList(devList, pk);
            const freshPc = decryptedList.find(d => d.DeviceType === 'PC');
            if (freshPc) {
              livePcDevice = freshPc;
              if (freshPc.GlobalUrl && freshPc.GlobalUrl.includes('trycloudflare.com')) {
                const freshCloudUrl = freshPc.GlobalUrl.trim().replace(/\/$/, '');
                syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🔍 Firebase returned PC Cloudflare URL: ${freshCloudUrl} — probing...`);
                try {
                  const verifiedCloud = await probeUrl(freshCloudUrl, 2500);
                  if (verifiedCloud) {
                    cachedPcUrlRef.current = verifiedCloud;
                    cachedPcUrlTimestampRef.current = startNow;
                    discoveryMethodRef.current = 'cloudflare';
                    AsyncStorage.setItem('lastCloudflareUrl', verifiedCloud).catch(() => {});
                    AsyncStorage.setItem('pairedGlobalUrl', verifiedCloud).catch(() => {});
                    setSecureItem('pairedGlobalUrl', verifiedCloud).catch(() => {});
                    setSecureItem('lastCloudflareUrl', verifiedCloud).catch(() => {});
                    syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🚀 Cloudflare Connected in ${NetworkClock.now() - startNow}ms: ${verifiedCloud}`);
                    return verifiedCloud;
                  }
                } catch {
                  syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚠️ Cloudflare probe timed out/failed on ${freshCloudUrl} — falling back to LAN`);
                }
              }
            }
          }
        } catch (fbErr: any) {
          syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE ERROR] ⚠️ Firebase lookup error: ${fbErr?.message || fbErr}`);
        }
      }

      // Check if we have PC device in activeDevicesRef as fallback
      if (!livePcDevice && activeDevicesRef.current.length > 0) {
        livePcDevice = activeDevicesRef.current.find(d => d.DeviceType === 'PC') || null;
      }

      // ═══════════════════════════════════════════════════════════════
      // TIER 2: LAN Fallback (Local IPs & Ports)
      // ═══════════════════════════════════════════════════════════════
      const lanCandidates: string[] = [];

      // 1. LocalIp from live PC record
      if (livePcDevice?.LocalIp) {
        const parts = livePcDevice.LocalIp.split(',').map(s => s.trim()).filter(Boolean);
        lanCandidates.push(...parts);
      }
      if (livePcDevice?.Url && !livePcDevice.Url.includes('trycloudflare.com')) {
        lanCandidates.push(livePcDevice.Url.trim());
      }

      // 2. Stored LAN URLs from storage
      try {
        const storedLocal = (await getSecureItem('pairedLocalUrl')) || (await AsyncStorage.getItem('pairedLocalUrl'));
        if (storedLocal) lanCandidates.push(...storedLocal.split(',').map(s => s.trim()).filter(Boolean));
        const lastLan = await AsyncStorage.getItem('@flyshelf_last_lan_url');
        if (lastLan) lanCandidates.push(lastLan.trim());
      } catch {}

      // 3. User configured LAN IP in settings
      if (pcLocalIp) {
        lanCandidates.push(...pcLocalIp.split(',').map(s => s.trim()).filter(Boolean));
      }

      // 4. Paired devices cache
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

      // Normalize LAN URLs (ensure http:// and ports 8999 & 8080)
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

      if (uniqueLan.length > 0) {
        syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🔍 Probing ${uniqueLan.length} LAN candidate(s)...`);
        try {
          const BATCH_SIZE = 10;
          for (let i = 0; i < uniqueLan.length; i += BATCH_SIZE) {
            const batch = uniqueLan.slice(i, i + BATCH_SIZE);
            const controller = new AbortController();
            try {
              const results = await Promise.allSettled(batch.map(url => probeUrl(url, 1500, controller.signal)));
              const found = results.find((r: any) => r.status === 'fulfilled');
              if (found) {
                controller.abort();
                const lanWinner = (found as PromiseFulfilledResult<string>).value;
                cachedPcUrlRef.current = lanWinner;
                cachedPcUrlTimestampRef.current = startNow;
                discoveryMethodRef.current = 'stored-lan';
                AsyncStorage.setItem('@flyshelf_last_lan_url', lanWinner).catch(() => {});
                try {
                  const urlObj = new URL(lanWinner);
                  addToPcIpCache(urlObj.hostname, parseInt(urlObj.port) || 8999).catch(() => {});
                } catch {}
                syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🚀 LAN Connected in ${NetworkClock.now() - startNow}ms: ${lanWinner}`);
                return lanWinner;
              }
            } finally {
              controller.abort();
            }
          }
        } catch {
          syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚠️ All LAN probes failed`);
        }
      }

      // ═══════════════════════════════════════════════════════════════
      // TIER 3: Signal to Firebase (URL Request) & Wait
      // ═══════════════════════════════════════════════════════════════
      if (pk) {
        const nowMs = NetworkClock.now();
        // Throttle signals to once every 10 seconds to avoid Firebase flooding
        if (nowMs - lastUrlRequestSentRef.current > 10_000) {
          lastUrlRequestSentRef.current = nowMs;
          const pcId = livePcDevice?.DeviceId || livePcDevice?._key || 'PC';
          const myDevName = (await AsyncStorage.getItem('@deviceName')) || 'Mobile';
          const myDevId = (await AsyncStorage.getItem('@deviceId')) || `Mobile_${myDevName.replace(/[^a-zA-Z0-9_]/g, '_')}`;

          syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚡ Sending URL refresh signal to PC via Firebase...`);
          try {
            await ensureFirebaseAuth().catch(() => {});
            const signalPayload = {
              requestedAt: nowMs,
              requestedBy: myDevName,
              deviceId: myDevId,
              client: 'MobileCompanion'
            };

            // Write signal under active_devices/{pk}/{pcDeviceId}/urlRequest and active_devices/{pk}/wakeSignal
            await Promise.all([
              set(ref(database, `active_devices/${pk}/${pcId}/urlRequest`), signalPayload).catch(() => {}),
              set(ref(database, `active_devices/${pk}/urlRequest`), signalPayload).catch(() => {}),
              set(ref(database, `active_devices/${pk}/wakeSignal`), {
                type: 'url_request',
                sender: myDevName,
                deviceId: myDevId,
                ts: nowMs
              }).catch(() => {})
            ]);
            syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚡ URL refresh signal sent — waiting for PC response.`);
          } catch (sigErr: any) {
            syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE ERROR] Failed to send URL request signal: ${sigErr?.message || sigErr}`);
          }
        }
      }

      // ═══════════════════════════════════════════════════════════════
      // TIER 4: Persistent Target Fallback for Background Poller
      // ═══════════════════════════════════════════════════════════════
      // If we have a live PC Cloudflare URL from Firebase, retain it as target so poller keeps checking
      if (livePcDevice?.GlobalUrl && livePcDevice.GlobalUrl.includes('trycloudflare.com')) {
        const fallbackTarget = livePcDevice.GlobalUrl.trim().replace(/\/$/, '');
        cachedPcUrlRef.current = fallbackTarget;
        cachedPcUrlTimestampRef.current = startNow;
        discoveryMethodRef.current = 'cloudflare';
        syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⏳ Waiting on target Cloudflare URL: ${fallbackTarget}`);
        return fallbackTarget;
      }

      // Background Subnet Discovery (Non-blocking)
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

      syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⏳ PC unreachable on Cloud & LAN. Awaiting PC response...`);
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

