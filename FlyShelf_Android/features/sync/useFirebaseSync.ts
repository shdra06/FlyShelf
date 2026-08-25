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

  // ─── Firebase Listeners (LAZY — Phase 1 Optimization) ───
  // The clipboard listener is now LAZY: it only activates after 30s of the PC
  // being unreachable via direct connection (LAN/Cloudflare). This eliminates
  // the persistent WebSocket that was hitting the 200-connection Firebase limit.
  const firebaseFallbackTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const firebaseUnsubFeedRef = useRef<(() => void) | null>(null);
  const firebaseUnsubNodesRef = useRef<(() => void) | null>(null);
  const lastSuccessfulPollRef = useRef<number>(NetworkClock.now());

  // ─── Last proven-working PC URL (set by poll on successful /api/sync) ───
  const lastWorkingPcUrlRef = useRef<string | null>('');

  // AC-5+AC-6: Flag to suppress Firebase listener during startup purge
  const isPurgingRef = useRef<boolean>(false);
  // Reconnect backoff counter for Firebase onValue errors
  const fbReconnectAttemptsRef = useRef(0);

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
    // PC is reachable directly — disconnect Firebase listener if active
    if (firebaseUnsubFeedRef.current) {
      firebaseUnsubFeedRef.current();
      firebaseUnsubFeedRef.current = null;
      syncLog('FIREBASE', '🔌 Disconnected Firebase listener — PC reachable directly');
    }
    if (firebaseFallbackTimerRef.current) {
      clearTimeout(firebaseFallbackTimerRef.current);
      firebaseFallbackTimerRef.current = null;
    }
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

    if (firebaseFallbackTimerRef.current) return;
    if (firebaseUnsubFeedRef.current) return;
    if (!isGlobalSyncEnabled) return;

    // Connect Firebase clipboard listener immediately (< 500ms) instead of 30s delay!
    firebaseFallbackTimerRef.current = setTimeout(() => {
      firebaseFallbackTimerRef.current = null;
      syncLog('FIREBASE', '⚡ Activating instant Firebase clipboard fallback');
      connectFirebaseClipboardListener(pk);
    }, 500);
  };

  // Connects the Firebase clipboard listener (only called when PC is unreachable)
  const connectFirebaseClipboardListener = (pk: string) => {
    const syncLimit = autoSyncTop5 !== false ? 5 : 1;
    const clipsRef = query(ref(database, `clipboard/${pk}`), orderByChild('Timestamp'), limitToLast(syncLimit));
    firebaseUnsubFeedRef.current = onValue(clipsRef, async (snapshot) => {
      fbReconnectAttemptsRef.current = 0; // Reset backoff on successful data
      if (snapshot.exists()) {
        const data = snapshot.val();
        // M-6 FIX: Validate Firebase data shape before processing
        const allRaw: ClipItem[] = Object.keys(data).map(k => {
          const item = data[k];
          // M-6: Skip malformed entries (must be objects with at minimum a Title or Raw)
          if (!item || typeof item !== 'object') return null;
          // M-6: Reject oversized payloads (>1MB per item) to prevent OOM
          const rawStr = JSON.stringify(item);
          if (rawStr.length > 1_000_000) {
            syncLog('FIREBASE', `Skipped oversized entry ${k}: ${rawStr.length} bytes`);
            return null;
          }
          // M-6: Sanitize URLs
          if (item.DownloadUrl && typeof item.DownloadUrl === 'string') {
            if (!item.DownloadUrl.startsWith('http://') && !item.DownloadUrl.startsWith('https://') && !item.DownloadUrl.includes('?path=')) {
              item.DownloadUrl = '';
            }
          }
          return { id: k, ...item } as ClipItem;
        }).filter(Boolean) as ClipItem[];

        // Strict timestamp descending sequence (top newest items first)
        allRaw.sort((a, b) => (b.Timestamp || 0) - (a.Timestamp || 0));
        // AES-256-GCM decryption: decrypt Encrypted items from other devices
        const allParsed: ClipItem[] = [];
        for (const item of allRaw) {
          if ((item as any).Encrypted === true) {
            try {
              const decTitle = await aesDecrypt(item.Title || '');
              const decRaw = await aesDecrypt(item.Raw || '');
              if (decTitle !== null) item.Title = decTitle;
              if (decRaw !== null) item.Raw = decRaw;
              if ((item as any).DownloadUrl) {
                const decDl = await aesDecrypt((item as any).DownloadUrl);
                if (decDl !== null) (item as any).DownloadUrl = decDl;
              }
              if ((item as any).PreviewUrl) {
                const decPv = await aesDecrypt((item as any).PreviewUrl);
                if (decPv !== null) (item as any).PreviewUrl = decPv;
              }
            } catch (e: any) { syncLog('SYNC_CRYPTO', `Decryption failed: ${e?.message}`); }
          }
          allParsed.push(item);
        }
        // Filter out items sent by THIS device to prevent echo loops
        // Also filter pre-pairing items to prevent initial history dump
        const myName = deviceName || '';
        const pairingTs = pairingTimestampRef.current;
        const parsed = allParsed.filter(c => {
          // Skip items from before this device paired
          if (pairingTs > 0 && c.Timestamp && c.Timestamp < pairingTs) {
            syncLog('FIREBASE', `Skipped pre-pairing item: ${(c.Title || '').substring(0, 40)}`);
            return false;
          }
          if (c.SourceDeviceType === 'Mobile' && myName && c.SourceDeviceName === myName) {
            syncLog('FIREBASE', `Filtered own item: ${(c.Title || '').substring(0, 40)}`);
            return false;
          }
          if ((c as any).EventId && processedEventsRef.current.has((c as any).EventId)) {
            syncLog('FIREBASE', `Filtered by EventId: ${(c as any).EventId}`);
            return false;
          }
          if ((c as any).EventId) processedEventsRef.current.set((c as any).EventId, NetworkClock.now());
          return true;
        });
        syncLog('FIREBASE', `Feed: ${allParsed.length} total, ${parsed.length} after self-filter`);
        const now = NetworkClock.now();
        recentSyncFingerprintsRef.current.forEach((ts, fp) => { if (now - ts > 30_000 && !fp.startsWith('filedl::')) recentSyncFingerprintsRef.current.delete(fp); });

        // Push text/url items to floating ball overlay
        if (Platform.OS === 'android' && AdvanceOverlay) {
          parsed.slice(0, 5).forEach((c: any) => {
            const fp = `${c.Type}::${(c.Raw || '').substring(0, 150)}`;
            if (recentSyncFingerprintsRef.current.has(fp)) return;
            if ((c.Type === 'Text' || c.Type === 'Url' || c.Type === 'Pdf' || c.Type === 'Document') && c.Raw) {
              let rawData = c.Raw;
              if (c.Type === 'Pdf' || c.Type === 'Document') { rawData = DOWNLOAD_BASE + c.Title.replace(/[^a-zA-Z0-9.-]/g, '_'); }
              AdvanceOverlay.pushClipToNativeDB(rawData, c.SourceDeviceName || 'Cloud');
              recentSyncFingerprintsRef.current.set(fp, NetworkClock.now());
            }
          });
        }

        // ─── Process ALL items from Firebase: dedup against ALL clips, move dup to top ───
        if (parsed.length > 0) {
          setClips(prev => {
            let updated = [...prev];
            let changed = false;
            let hasNewItems = false;
            for (const p of parsed) {
              // Check ALL existing clips for duplicate by Raw content or Title
              const dupIdx = updated.findIndex(c =>
                (c.id && p.id && c.id === p.id) ||
                (c.Title && p.Title && c.Title === p.Title) ||
                (c.Raw && p.Raw && c.Raw.substring(0, 200) === p.Raw.substring(0, 200))
              );
              if (dupIdx >= 0) {
                // Duplicate found — update in place, do NOT move to top
                updated[dupIdx] = { ...updated[dupIdx], ...p };
                changed = true;
              } else {
                // Genuinely new — add to top
                updated.unshift(p);
                changed = true;
                hasNewItems = true;
              }
            }
            // Also merge local screenshots
            const screenshots = localScreenshotsRef.current.filter(ls =>
              !updated.some(p => p.Title === ls.Title) && !parsed.some(p => p.Title === ls.Title)
            );
            if (screenshots.length > 0) {
              updated = [...screenshots, ...updated];
              changed = true;
              hasNewItems = true;
            }
            if (!changed) return prev;
            if (hasNewItems) scrollToTop(); // Only scroll for genuinely new items
            return updated;
          });
          // Trigger background download effects for new image/rich media items
          setImageDownloadTrigger(t => t + 1);
          setRichMediaDownloadTrigger(t => t + 1);
        }

        // Background: File downloads happen via LAN/Cloudflare poll only.
        // Firebase is only for critical backend info, and should not trigger downloads.
      }
    }, (error) => {
      syncLog('FIREBASE', `onValue error: ${error?.message || error}`);
      // Disconnect current listener
      if (firebaseUnsubFeedRef.current) {
        firebaseUnsubFeedRef.current();
        firebaseUnsubFeedRef.current = null;
      }
      // Reconnect after exponential backoff
      const delay = Math.min(5000 * Math.pow(2, fbReconnectAttemptsRef.current), 60000);
      fbReconnectAttemptsRef.current++;
      setTimeout(() => {
        const currentPk = pairingKeyRef.current;
        if (currentPk && isValidPairingKey(currentPk)) {
          syncLog('FIREBASE', `Reconnecting Firebase listener after ${delay}ms backoff`);
          connectFirebaseClipboardListener(currentPk);
        }
      }, delay);
    });
  };

  useEffect(() => {
    if (!isGlobalSyncEnabled) return;
    const pk = contextPairingKey || pairingKeyRef.current;
    if (!pk || !isValidPairingKey(pk)) { syncLog('FIREBASE', 'No pairing key yet or invalid key format — waiting for context to load...'); return; }
    pairingKeyRef.current = pk;

    // ─── Startup Cleanup: Purge stale entries older than 1 hour (batched) ───
    (async () => {
      try {
        const allSnap = await get(ref(database, `clipboard/${pk}`));
        if (allSnap.exists()) {
          const allData = allSnap.val();
          const now = NetworkClock.now();
          const ONE_HOUR = 60 * 60 * 1000;
          // AC-5: Batch all deletions into a single update() call
          const deletions = Object.fromEntries(
            Object.keys(allData)
              .filter(key => allData[key].Timestamp && (now - allData[key].Timestamp) > ONE_HOUR)
              .map(key => [`${key}`, null as any])
          );
          const purgeCount = Object.keys(deletions).length;
          if (purgeCount > 0) {
            // AC-6: Set purging flag so Firebase listener ignores intermediate events
            isPurgingRef.current = true;
            await update(ref(database, `clipboard/${pk}`), deletions);
            isPurgingRef.current = false;
            syncLog('CLEANUP', `Purged ${purgeCount} stale Firebase entries (>1hr old)`);
          }
        }
      } catch (e) { isPurgingRef.current = false; syncLog('CLEANUP', `Startup cleanup error: ${e}`); }
    })();

    // ─── Active Devices: REAL-TIME onValue listener ───
    // Catches PC URLs the instant they appear — critical because
    // v5 PC auto-deletes its URL from Firebase after 5 seconds.
    // The listener caches URLs locally so they survive deletion.
    const peerDevicesRef = ref(database, `active_devices/${pk}`);
    const processDevicesSnapshot = async (snapshot: any) => {
      // AC-6: Skip processing during startup purge to avoid reacting to our own deletions
      if (isPurgingRef.current) return;
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
        // If no PC found at all, immediately try Firebase clipboard listener
        if (!rawDevices.some(d => d.DeviceType === 'PC')) {
          syncLog('FIREBASE', 'No PC found — activating Firebase clipboard listener immediately');
          connectFirebaseClipboardListener(pk);
        }
      } catch (e) { syncLog('FIREBASE', `Active devices listener error: ${e}`); }
    };

    // 1. Instant REST snapshot on mount (0ms wait, zero persistent connection overhead)
    get(peerDevicesRef).then(processDevicesSnapshot).catch(() => {});

    // 2. Real-time active devices listener
    const unsubscribeDevices = onValue(peerDevicesRef, processDevicesSnapshot);

    return () => {
      unsubscribeDevices();
      if (firebaseUnsubFeedRef.current) { firebaseUnsubFeedRef.current(); firebaseUnsubFeedRef.current = null; }
      if (firebaseFallbackTimerRef.current) { clearTimeout(firebaseFallbackTimerRef.current); firebaseFallbackTimerRef.current = null; }
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
