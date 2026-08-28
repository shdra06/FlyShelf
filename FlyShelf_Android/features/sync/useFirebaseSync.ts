import React, { useEffect, useRef, useMemo } from 'react';
import { Platform, NativeModules } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { database } from '../../firebaseConfig';
import { syncLog } from '../../utils/debugLog';
import { ref, get, onValue, query, limitToLast, orderByChild, update } from 'firebase/database';
import { ClipItem, DOWNLOAD_BASE, getDownloadPath } from '../../utils/clipTypes';
import { isValidPairingKey, decryptDeviceList } from '../../utils/networkHelpers';
import { encrypt as aesEncrypt, decrypt as aesDecrypt } from '../../utils/syncCrypto';
import { NetworkClock } from '../../utils/networkClock';
import { setSecureItem } from '../../utils/secureStorage';
import { createTimeoutSignal } from '../../utils/timeoutSignal';
import { ActiveDevice } from '../../components/DeviceHub';

const { AdvanceOverlay } = NativeModules;

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
    autoSyncTop5,
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

    // If PC is registered in Firebase active_devices, keep it online via Cloud
    const activePcOnline = (activeDevicesRef.current || []).some((d: any) => d.DeviceType === 'PC' && d.IsOnline);
    pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
      updateDeviceStatus(d.deviceId, {
        isOnline: activePcOnline,
        connectionType: activePcOnline ? 'Cloud' : undefined
      });
    });
  };

  useEffect(() => {
    if (!isGlobalSyncEnabled) return;
    const pk = contextPairingKey || pairingKeyRef.current;
    if (!pk || !isValidPairingKey(pk)) { syncLog('FIREBASE', 'No pairing key yet or invalid key format — waiting for context to load...'); return; }
    pairingKeyRef.current = pk;

    // ─── Active Devices: REAL-TIME onValue listener ───
    // Catches PC URLs the instant they appear — critical because
    // v5 PC auto-deletes its URL from Firebase after 5 seconds.
    // The listener caches URLs locally so they survive deletion.
    const peerDevicesRef = ref(database, `active_devices/${pk}`);
    const processDevicesSnapshot = async (snapshot: any) => {
      try {
        // Issue #6: Skip expensive LAN probing if PC is already reachable via direct polling
        const pcAlreadyReachable = lastWorkingPcUrlRef.current && (NetworkClock.now() - lastSuccessfulPollRef.current) < 10_000;
        if (!snapshot.exists()) {
          // Firebase entry was auto-deleted — keep using cached URLs, don't clear them
          return;
        }
        let rawDevices: any[] = [];
        const data = snapshot.val();
        const now = NetworkClock.now();
        const filtered = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline !== false);
        rawDevices = await decryptDeviceList(filtered);

        // ── Phase 1: Immediately extract & cache Cloudflare/LAN URLs for instant connectivity ──
        for (let i = 0; i < rawDevices.length; i++) {
          const dev = rawDevices[i];
          if (dev.DeviceType === 'PC') {
            // Immediate Cloudflare Tunnel URL processing
            if (dev.GlobalUrl && dev.GlobalUrl.includes('trycloudflare.com')) {
              const cleanGlobal = dev.GlobalUrl.trim().replace(/\/$/, '');
              setSecureItem('lastCloudflareUrl', cleanGlobal).catch(() => {});
              setSecureItem('pairedGlobalUrl', cleanGlobal).catch(() => {});
              AsyncStorage.setItem('lastCloudflareUrl', cleanGlobal).catch(() => {});
              AsyncStorage.setItem('pairedGlobalUrl', cleanGlobal).catch(() => {});
              cachedPcUrlRef.current = cleanGlobal;
              cachedPcUrlTimestampRef.current = NetworkClock.now();
              syncLog('FIREBASE', `⚡ Instant PC Cloudflare URL cached: ${cleanGlobal}`);
            }
            // Immediate LAN IP caching
            if (dev.LocalIp) {
              const parts = dev.LocalIp.split(',');
              const normalizedParts = parts.map((part: string) => {
                const trimmed = part.trim();
                if (!trimmed) return '';
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
          const pcPair = pairedDevices.find(d => d.deviceType === 'PC');
          if (pcPair) {
            updatePairedDeviceLicensing(pcPair.deviceId, isPro, licenseKey);
          }
        }
      } catch (e) { syncLog('FIREBASE', `Active devices listener error: ${e}`); }
    };

    // 1. Instant REST snapshot on mount (0ms wait, zero persistent connection overhead)
    get(peerDevicesRef).then(processDevicesSnapshot).catch(() => {});

    // 2. Real-time active devices listener
    const unsubscribeDevices = onValue(peerDevicesRef, processDevicesSnapshot);

    return () => {
      unsubscribeDevices();
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
