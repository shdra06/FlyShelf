import React, { useEffect, useRef, useMemo } from 'react';
import { Platform, NativeModules } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { database, auth, ensureFirebaseAuth } from '../../firebaseConfig';
import { syncLog } from '../../utils/debugLog';
import { ref, get, set, onValue, update } from 'firebase/database';
import { ClipItem } from '../../utils/clipTypes';
import { isValidPairingKey, decryptDeviceList } from '../../utils/networkHelpers';
import { NetworkClock } from '../../utils/networkClock';
import { setSecureItem } from '../../utils/secureStorage';
import { createTimeoutSignal } from '../../utils/timeoutSignal';
import { ActiveDevice } from '../../components/DeviceHub';

const { AdvanceOverlay } = NativeModules;

// ZERO-TRUST VALIDATION: Strictly sanitize peer URLs received from Firebase
function isValidGlobalTunnelUrl(url?: string): boolean {
  if (!url || typeof url !== 'string') return false;
  return /^https:\/\/[a-zA-Z0-9-]+\.trycloudflare\.com(\/.*)?$/.test(url.trim());
}

function isValidLanHostOrIp(url?: string): boolean {
  if (!url || typeof url !== 'string') return false;
  try {
    const raw = url.trim().replace(/^https?:\/\//, '').split(':')[0].split('/')[0];
    if (raw === 'localhost' || raw === '127.0.0.1') return true;
    if (raw.startsWith('192.168.') || raw.startsWith('10.')) return true;
    if (/^172\.(1[6-9]|2[0-9]|3[0-1])\./.test(raw)) return true;
    return false;
  } catch {
    return false;
  }
}

export function useFirebaseSync(params: {
  isGlobalSyncEnabled: boolean;
  contextPairingKey: string;
  pairedDevices: any[];
  pairingKeyRef: React.MutableRefObject<string>;
  deviceName: string;
  pairingTimestampRef: React.MutableRefObject<number>;
  processedEventsRef: React.MutableRefObject<Map<string, number>>;
  recentSyncFingerprintsRef: React.MutableRefObject<Map<string, number>>;
  localScreenshotsRef: React.MutableRefObject<ClipItem[]>;
  enqueueDownload: (item: any) => void;
  setClips: React.Dispatch<React.SetStateAction<ClipItem[]>>;
  setActiveDevices: React.Dispatch<React.SetStateAction<any[]>>;
  setImageDownloadTrigger: React.Dispatch<React.SetStateAction<number>>;
  setRichMediaDownloadTrigger: React.Dispatch<React.SetStateAction<number>>;
  cachedPcUrlRef: React.MutableRefObject<string | null>;
  cachedPcUrlTimestampRef: React.MutableRefObject<number>;
  updateDeviceStatus: (id: string, status: any) => void;
  updatePairedDeviceLicensing: (id: string, isPro: boolean, licenseKey: string) => void;
  pcLocalIp: string;
  scrollToTop: () => void;
  isFloatingBallEnabled: boolean;
  autoSyncTop5?: boolean;
}): {
  markPcReachable: () => void;
  markPcUnreachable: () => void;
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
  lastSuccessfulPollRef: React.MutableRefObject<number>;
} {
  const {
    isGlobalSyncEnabled,
    contextPairingKey,
    pairedDevices,
    pairingKeyRef,
    deviceName,
    pairingTimestampRef,
    autoSyncTop5: _autoSyncTop5,
    processedEventsRef,
    recentSyncFingerprintsRef,
    localScreenshotsRef,
    enqueueDownload,
    setClips,
    setActiveDevices,
    setImageDownloadTrigger,
    setRichMediaDownloadTrigger,
    cachedPcUrlRef,
    cachedPcUrlTimestampRef,
    updateDeviceStatus,
    updatePairedDeviceLicensing,
    pcLocalIp,
    scrollToTop,
    isFloatingBallEnabled,
  } = params;

  const lastSuccessfulPollRef = useRef<number>(NetworkClock.now());

  // ─── Last proven-working PC URL (set by poll on successful /api/sync) ───
  const lastWorkingPcUrlRef = useRef<string | null>('');

  // AC-3: Stable stringified key for pairedDevices dependency
  // M-8 FIX: Use ref-based comparison to avoid JSON.stringify on every render cycle
  const pairedDeviceIdsRef = useRef<string>('');
  const pairedDeviceKeysStable = useMemo(() => {
    const sorted = JSON.stringify((pairedDevices || []).map((d: any) => d.deviceId || d.DeviceId).sort());
    pairedDeviceIdsRef.current = sorted;
    return sorted;
  }, [pairedDevices]);

  // M-11 FIX: Keep a ref to pairedDevices so markPcReachable/markPcUnreachable always see current value
  const pairedDevicesRef = useRef(pairedDevices);
  pairedDevicesRef.current = pairedDevices;
  const activeDevicesRef = useRef<any[]>([]);

  // Called by LAN/Cloudflare poller on every successful /api/sync response
  const markPcReachable = () => {
    lastSuccessfulPollRef.current = NetworkClock.now();
    pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
      updateDeviceStatus(d.deviceId, { isOnline: true, lastSeen: NetworkClock.now() });
    });
  };

  // Called by LAN/Cloudflare poller when direct poll fails
  const markPcUnreachable = () => {
    lastWorkingPcUrlRef.current = null;
    const pk = pairingKeyRef.current;
    if (!isValidPairingKey(pk)) return;

    // Don't claim "online" just because PC is registered in Firebase —
    // we can't actually reach it, so show honest "Connecting..." status
    pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
      updateDeviceStatus(d.deviceId, { isOnline: false, connectionType: undefined });
    });
  };

  useEffect(() => {
    if (!isGlobalSyncEnabled) return;
    const pk = contextPairingKey || pairingKeyRef.current;
    if (!pk || !isValidPairingKey(pk)) { syncLog('FIREBASE', 'No pairing key yet or invalid key format — waiting for context to load...'); return; }
    pairingKeyRef.current = pk;

    // ─── Active Devices: REAL-TIME onValue listener ───
    // AUDIT FIX #5: PC now preserves URLs in Firebase (no longer auto-deletes after 5s).
    // Listener caches URLs locally and uses Timestamp-based liveness checking.
    const peerDevicesRef = ref(database, `active_devices/${pk}`);
    let debounceTimer: ReturnType<typeof setTimeout> | null = null;
    const processDevicesSnapshot = (snapshot: any) => {
      if (debounceTimer) clearTimeout(debounceTimer);
      debounceTimer = setTimeout(async () => {
      try {
        // Issue #6: Skip expensive LAN probing if PC is already reachable via direct polling
        const pcAlreadyReachable = lastWorkingPcUrlRef.current && (NetworkClock.now() - lastSuccessfulPollRef.current) < 10_000;
        if (!snapshot.exists()) {
          // Firebase entry doesn't exist — keep using cached URLs, don't clear them
          return;
        }
        let rawDevices: any[] = [];
        const data = snapshot.val();
        const now = NetworkClock.now();
        const filtered = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline !== false);
        rawDevices = await decryptDeviceList(filtered, pk);

        // ── Phase 1: Immediately extract & cache Cloudflare/LAN URLs for instant connectivity ──
        for (let i = 0; i < rawDevices.length; i++) {
          const dev = rawDevices[i];
          if (dev.DeviceType === 'PC') {
            // Immediate Cloudflare Tunnel URL processing
            if (isValidGlobalTunnelUrl(dev.GlobalUrl)) {
              const cleanGlobal = dev.GlobalUrl.trim().replace(/\/$/, '');
              setSecureItem('lastCloudflareUrl', cleanGlobal).catch(() => {});
              setSecureItem('pairedGlobalUrl', cleanGlobal).catch(() => {});
              AsyncStorage.setItem('lastCloudflareUrl', cleanGlobal).catch(() => {});
              AsyncStorage.setItem('pairedGlobalUrl', cleanGlobal).catch(() => {});
              cachedPcUrlRef.current = cleanGlobal;
              cachedPcUrlTimestampRef.current = NetworkClock.now();
              syncLog('FIREBASE', `⚡ Instant PC Cloudflare URL cached: ${cleanGlobal}`);
            } else if (dev.GlobalUrl) {
              syncLog('FIREBASE', `⚠️ Discarded non-conforming GlobalUrl: ${dev.GlobalUrl}`);
              dev.GlobalUrl = undefined;
            }
            // Immediate LAN IP caching
            if (dev.LocalIp && typeof dev.LocalIp === 'string') {
              const parts = dev.LocalIp.split(',');
              const normalizedParts = parts.map((part: string) => {
                const trimmed = part.trim();
                if (!trimmed || !isValidLanHostOrIp(trimmed)) return '';
                return trimmed.startsWith('http') ? trimmed.replace(/\/$/, '') : `http://${trimmed.includes(':') ? trimmed : trimmed + ':8999'}`;
              }).filter(Boolean);
              if (normalizedParts.length > 0) {
                setSecureItem('pairedLocalUrl', normalizedParts.join(',')).catch(() => {});
              }
            }
          }
        }

        // ── Phase 2: Asynchronously probe LAN reachability in parallel ──
        for (let i = 0; i < rawDevices.length; i++) {
          const dev = rawDevices[i];
          if (dev.DeviceType === 'PC' && dev.LocalIp && !dev._lanVerified) {
            if (pcAlreadyReachable && lastWorkingPcUrlRef.current && !lastWorkingPcUrlRef.current.includes('trycloudflare.com')) {
              rawDevices[i] = { ...dev, _lanVerified: true, _lanUrl: lastWorkingPcUrlRef.current };
              continue;
            }
            const parts = (dev.LocalIp as string).split(',').map((s: string) => s.trim()).filter(Boolean);
            const candidateUrls: string[] = [];
            for (const part of parts) {
              if (!isValidLanHostOrIp(part)) continue;
              const clean = part.startsWith('http') ? part.replace(/\/$/, '') : `http://${part}`;
              if (!clean.replace(/^https?:\/\//, '').includes(':')) {
                candidateUrls.push(`${clean}:8999`);
                candidateUrls.push(`${clean}:8080`);
              } else {
                candidateUrls.push(clean);
              }
            }
            if (candidateUrls.length > 0) {
              try {
                const verifiedUrl = await Promise.any(candidateUrls.map(async (lanUrl) => {
                  const res = await fetch(`${lanUrl}/api/health`, {
                    method: 'GET',
                    headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk },
                    signal: createTimeoutSignal(1000)
                  });
                  if (res.ok) return lanUrl;
                  throw new Error('fail');
                }));
                if (verifiedUrl) {
                  rawDevices[i] = { ...dev, _lanVerified: true, _lanUrl: verifiedUrl };
                  cachedPcUrlRef.current = verifiedUrl;
                  cachedPcUrlTimestampRef.current = NetworkClock.now();
                  AsyncStorage.setItem('@flyshelf_last_lan_url', verifiedUrl).catch(() => {});
                }
              } catch (e) { /* LAN probes fail when on mobile data/different network */ }
            }
          }
        }
        // Fallback: probe manual IP from Settings in parallel
        const hasPc = rawDevices.some(d => d.DeviceType === 'PC');
        if (!hasPc && pcLocalIp) {
          const parts = pcLocalIp.split(',').map(s => s.trim()).filter(Boolean);
          const probeUrls: string[] = [];
          for (const part of parts) {
            const clean = part.startsWith('http') ? part.replace(/\/$/, '') : `http://${part}`;
            if (!clean.replace(/^https?:\/\//, '').includes(':')) {
              probeUrls.push(`${clean}:8999`);
              probeUrls.push(`${clean}:8080`);
            } else {
              probeUrls.push(clean);
            }
          }
          if (probeUrls.length > 0) {
            try {
              const verifiedManual = await Promise.any(probeUrls.map(async (probeUrl) => {
                const res = await fetch(`${probeUrl}/api/health`, {
                  method: 'GET',
                  headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk },
                  signal: createTimeoutSignal(1500)
                });
                if (res.ok) return probeUrl;
                throw new Error('fail');
              }));
              if (verifiedManual) {
                rawDevices.push({ DeviceName: 'PC (LAN)', DeviceType: 'PC', IsOnline: true, Url: verifiedManual, LocalIp: verifiedManual, _key: 'local_direct', _lanVerified: true, _lanUrl: verifiedManual, Timestamp: NetworkClock.now() });
              }
            } catch (e) { /* LAN probe — expected to fail for unreachable IPs */ }
          }
        } else if (hasPc && pcLocalIp) {
          const parts = pcLocalIp.split(',').map(s => s.trim()).filter(Boolean);
          const manualUrls: string[] = [];
          for (const part of parts) {
            const clean = part.startsWith('http') ? part.replace(/\/$/, '') : `http://${part}`;
            if (!clean.replace(/^https?:\/\//, '').includes(':')) {
              manualUrls.push(`${clean}:8999`);
              manualUrls.push(`${clean}:8080`);
            } else {
              manualUrls.push(clean);
            }
          }
          const nonExisting = manualUrls.filter(m => !rawDevices.some(d => d._lanUrl === m));
          if (nonExisting.length > 0) {
            try {
              const verified = await Promise.any(nonExisting.map(async (manualUrl) => {
                const res = await fetch(`${manualUrl}/api/health`, {
                  method: 'GET',
                  headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk },
                  signal: createTimeoutSignal(1200)
                });
                if (res.ok) return manualUrl;
                throw new Error('fail');
              }));
              if (verified) {
                const pcIdx = rawDevices.findIndex(d => d.DeviceType === 'PC');
                if (pcIdx >= 0) rawDevices[pcIdx] = { ...rawDevices[pcIdx], _lanVerified: true, _lanUrl: verified, LocalIp: verified };
              }
            } catch (e) {}
          }
        }
        activeDevicesRef.current = rawDevices;
        setActiveDevices(rawDevices);
        // Build typed ActiveDevice list for DeviceHub
        const typedList: ActiveDevice[] = rawDevices.map((d: any) => ({
          deviceId: d._key || d.DeviceId || '',
          deviceName: d.DeviceName || 'Unknown',
          deviceType: (d.DeviceType === 'PC' ? 'PC' : d.DeviceType === 'Mobile' ? 'Mobile' : 'Browser') as ActiveDevice['deviceType'],
          isOnline: !!d.IsOnline,
          connectionType: (d._lanVerified ? 'LAN' : d.GlobalUrl ? 'Cloud' : 'Offline') as ActiveDevice['connectionType'],
          latencyMs: undefined,
          localUrl: d._lanUrl || d.LocalIp || undefined,
          globalUrl: d.GlobalUrl || undefined,
          isPro: !!d.IsPro,
          licenseKey: d.LicenseKey || undefined,
          lastSeen: d.Timestamp || undefined,
        }));
        // [REMOVED] setActiveDevicesList — dead state removed
        // Propagate discovery results to global SettingsContext so other tabs (Todo/Notes) benefit
        typedList.forEach(d => {
          updateDeviceStatus(d.deviceId, {
            isOnline: d.isOnline,
            connectionType: d.connectionType,
            localUrl: d.localUrl,
            globalUrl: d.globalUrl,
            lastSeen: d.lastSeen
          });
        });
        const activePc = rawDevices.find(d => d.DeviceType === 'PC');
        if (activePc) {
          const isPro = !!activePc.IsPro;
          const licenseKey = activePc.LicenseKey || '';
          const pcPair = pairedDevicesRef.current.find(d => d.deviceType === 'PC');
          if (pcPair) {
            updatePairedDeviceLicensing(pcPair.deviceId, isPro, licenseKey);
          }
        }
      } catch (e) { syncLog('FIREBASE', `Active devices listener error: ${e}`); }
      }, 500);
    };

    // 1. Ensure Firebase Auth & Room Membership before subscribing to active_devices
    let isCancelled = false;
    let unsubscribeDevices: (() => void) | null = null;

    const setupListener = async () => {
      try {
        syncLog('FIREBASE', '[STEP 2/6: ROOM MEMBERSHIP] Ensuring Firebase Auth...');
        await ensureFirebaseAuth();
        if (isCancelled) return;

        const uid = auth?.currentUser?.uid;
        if (uid) {
          syncLog('FIREBASE', `[STEP 2/6: ROOM MEMBERSHIP] Registering members/${pk.substring(0, 8)}.../${uid.substring(0, 8)}...`);
          await set(ref(database, `members/${pk}/${uid}`), true).catch((e) => {
            syncLog('FIREBASE', `[STEP 2/6: ROOM MEMBERSHIP ERROR] ⚠️ Room write notice: ${e?.message || e}`);
          });
          syncLog('FIREBASE', `[STEP 2/6: ROOM MEMBERSHIP] ✅ Room membership registered`);
        }
        if (isCancelled) return;

        syncLog('FIREBASE', `[STEP 3/6: ACTIVE DEVICES] Subscribing to active_devices/${pk.substring(0, 8)}...`);
        // Instant snapshot + real-time listener
        get(peerDevicesRef).then(processDevicesSnapshot).catch((e) => {
          syncLog('FIREBASE', `[STEP 3/6: ACTIVE DEVICES ERROR] ⚠️ Initial snapshot error: ${e?.message || e}`);
        });
        if (unsubscribeDevices) unsubscribeDevices();

        unsubscribeDevices = onValue(peerDevicesRef, processDevicesSnapshot, (error) => {
          syncLog('FIREBASE', `[STEP 3/6: ACTIVE DEVICES ERROR] ⚠️ Listener error: ${error.message} — will retry`);
          if (!isCancelled) {
            setTimeout(setupListener, 3000);
          }
        });
      } catch (e: any) {
        syncLog('FIREBASE', `[STEP 2/6: ROOM MEMBERSHIP ERROR] ❌ Firebase setup error: ${e?.message || e}`);
        if (!isCancelled) {
          setTimeout(setupListener, 3000);
        }
      }
    };

    setupListener();

    return () => {
      isCancelled = true;
      if (unsubscribeDevices) unsubscribeDevices();
    };
  // AC-3: Stabilize pairedDevices dependency with useMemo to avoid inline JSON.stringify
  }, [isGlobalSyncEnabled, contextPairingKey, pairedDeviceKeysStable]);

  return {
    markPcReachable,
    markPcUnreachable,
    lastWorkingPcUrlRef,
    lastSuccessfulPollRef,
  };
}
