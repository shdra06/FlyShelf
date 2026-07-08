import React, { useEffect, useRef, useMemo } from 'react';
import { Platform, NativeModules } from 'react-native';
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

  // AC-3: Stable stringified key for pairedDevices dependency — avoids re-creating JSON.stringify on every render
  const pairedDeviceKeysStable = useMemo(
    () => JSON.stringify((pairedDevices || []).map((d: any) => d.deviceId || d.DeviceId).sort()),
    [pairedDevices]
  );

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
    // Update paired device status to online
    pairedDevices.filter(d => d.deviceType === 'PC').forEach(d => {
      updateDeviceStatus(d.deviceId, { isOnline: true, lastSeen: NetworkClock.now() });
    });
  };

  // Called by LAN/Cloudflare poller when poll fails — starts the 30s countdown
  const markPcUnreachable = () => {
    // M-6: Expire lastWorkingPcUrlRef when PC is unreachable
    lastWorkingPcUrlRef.current = null;
    // Update paired device status to offline
    pairedDevices.filter(d => d.deviceType === 'PC').forEach(d => {
      updateDeviceStatus(d.deviceId, { isOnline: false });
    });
    if (firebaseFallbackTimerRef.current) return; // Already counting down
    if (firebaseUnsubFeedRef.current) return; // Already connected to Firebase
    if (!isGlobalSyncEnabled) return;
    const pk = pairingKeyRef.current;
    if (!isValidPairingKey(pk)) return;
    firebaseFallbackTimerRef.current = setTimeout(() => {
      firebaseFallbackTimerRef.current = null;
      // Only activate if PC is STILL unreachable after 30s
      if (NetworkClock.now() - lastSuccessfulPollRef.current < 25_000) return;
      syncLog('FIREBASE', '🔥 PC unreachable for 30s — activating Firebase fallback listener');
      connectFirebaseClipboardListener(pk);
    }, 30_000);
  };

  // Connects the Firebase clipboard listener (only called when PC is unreachable)
  const connectFirebaseClipboardListener = (pk: string) => {
    if (firebaseUnsubFeedRef.current) return; // Already connected
    const clipsRef = query(ref(database, `clipboard/${pk}`), orderByChild('Timestamp'), limitToLast(10));
    firebaseUnsubFeedRef.current = onValue(clipsRef, async (snapshot) => {
      if (snapshot.exists()) {
        const data = snapshot.val();
        const allRaw: ClipItem[] = Object.keys(data).map(k => ({ id: k, ...data[k] } as ClipItem)).reverse();
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
        recentSyncFingerprintsRef.current.forEach((ts, fp) => { if (now - ts > 30_000) recentSyncFingerprintsRef.current.delete(fp); });

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

        // Background: queue ALL file items for download
        if (Platform.OS === 'android') {
          const fileItems = parsed.filter(c =>
            c.Raw?.startsWith('http') && ['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(c.Type || '')
          );
          for (const fileItem of fileItems) {
            const fileDedupKey = `filedl::${fileItem.Title || ''}::${fileItem.Timestamp || ''}`;
            if (recentSyncFingerprintsRef.current.has(fileDedupKey)) continue;
            recentSyncFingerprintsRef.current.set(fileDedupKey, NetworkClock.now());
            try {
              const subfolder = fileItem.Type === 'Pdf' ? 'PDFs' : fileItem.Type === 'Video' ? 'Videos' : 'Documents';
              const safeName = (fileItem.Title || `file_${NetworkClock.now()}`).replace(/[^a-zA-Z0-9._-]/g, '_');
              const destPath = await getDownloadPath(subfolder, safeName);
              enqueueDownload({
                id: fileItem.id || '', title: fileItem.Title || safeName, type: fileItem.Type || 'File',
                fileUrl: fileItem.Raw!, destPath, source: 'Firebase',
                sourceDevice: fileItem.SourceDeviceName || 'Cloud',
              });
            } catch (e) { syncLog('FIREBASE', `File download queue error: ${(e as any)?.message || e}`); }
          }
        }
      }
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
    const unsubscribeDevices = onValue(peerDevicesRef, async (snapshot) => {
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
        // AM-6: Use 12-minute window (720000ms) to prevent devices flickering offline between 10-min heartbeats
        const filtered = Object.keys(data).map(k => ({ ...data[k], _key: k })).filter(d => d.IsOnline && d.Timestamp && (now - d.Timestamp) < 720_000);
        rawDevices = await decryptDeviceList(filtered);
        // Probe LAN reachability for each PC device
        for (let i = 0; i < rawDevices.length; i++) {
          const dev = rawDevices[i];
          if (dev.DeviceType === 'PC' && dev.LocalIp && !dev._lanVerified) {
            // Issue #6: Skip LAN probing if PC already reachable — saves ~1.5s per callback
            if (pcAlreadyReachable && lastWorkingPcUrlRef.current && !lastWorkingPcUrlRef.current.includes('trycloudflare.com')) {
              rawDevices[i] = { ...dev, _lanVerified: true, _lanUrl: lastWorkingPcUrlRef.current };
              continue;
            }
            const parts = dev.LocalIp.split(',');
            for (const part of parts) {
              const trimmed = part.trim();
              if (!trimmed) continue;
              try {
                const lanUrl = trimmed.startsWith('http') ? trimmed.replace(/\/$/, '') : `http://${trimmed.includes(':') ? trimmed : trimmed + ':8999'}`;
                const res = await fetch(`${lanUrl}/api/health`, { method: 'GET', headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk }, signal: createTimeoutSignal(1500) });
                if (res.ok) {
                  rawDevices[i] = { ...dev, _lanVerified: true, _lanUrl: lanUrl };
                  break;
                }
              } catch (e) { syncLog('LAN-PROBE', `Health check failed for ${trimmed}: ${(e as any)?.message || e}`); }
            }
          }
          // Prefer TLS URL over plain HTTP — encrypts pairing key in transit
          if (dev.DeviceType === 'PC' && dev.TlsUrl && dev.TlsUrl.startsWith('https://') && rawDevices[i]._lanVerified) {
            try {
              const tlsUrl = dev.TlsUrl.replace(/\/$/, '');
              const tlsRes = await fetch(`${tlsUrl}/api/health`, { method: 'GET', headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk }, signal: createTimeoutSignal(2000) });
              if (tlsRes.ok) {
                rawDevices[i] = { ...rawDevices[i], _lanVerified: true, _lanUrl: tlsUrl };
                setSecureItem('pairedTlsUrl', tlsUrl).catch(() => {});
                syncLog('LAN-PROBE', `✅ TLS URL preferred: ${tlsUrl.substring(0, 50)}`);
              }
            } catch (e) { syncLog('LAN-PROBE', `TLS probe failed for ${dev.TlsUrl}: ${(e as any)?.message || e}`); }
          }
          // Cache TLS URL if available (even if probe skipped — save for getCachedPcUrl)
          if (dev.DeviceType === 'PC' && dev.TlsUrl && dev.TlsUrl.startsWith('https://')) {
            setSecureItem('pairedTlsUrl', dev.TlsUrl.replace(/\/$/, '')).catch(() => {});
          }
          // Cache Cloudflare URL locally — survives the 5-second Firebase auto-delete
          if (dev.DeviceType === 'PC' && dev.GlobalUrl && dev.GlobalUrl.includes('trycloudflare.com')) {
            setSecureItem('lastCloudflareUrl', dev.GlobalUrl).catch(() => {});
            setSecureItem('pairedGlobalUrl', dev.GlobalUrl).catch(() => {});
            // Also update the in-memory PC URL cache immediately
            cachedPcUrlRef.current = dev.GlobalUrl;
            cachedPcUrlTimestampRef.current = NetworkClock.now();
            syncLog('PEER SSE', `⚡ PC URL cached: ${dev.GlobalUrl.substring(0, 50)}`);
          }
          // Cache LAN URL if available
          if (dev.DeviceType === 'PC' && dev.LocalIp) {
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
        // Fallback: probe manual IP from Settings
        const hasPc = rawDevices.some(d => d.DeviceType === 'PC');
        if (!hasPc && pcLocalIp) {
          const parts = pcLocalIp.split(',');
          for (const part of parts) {
            const trimmed = part.trim();
            if (!trimmed) continue;
            try {
              const probeUrl = trimmed.startsWith('http') ? trimmed.replace(/\/$/, '') : `http://${trimmed.includes(':') ? trimmed : trimmed.split(':')[0] + ':8999'}`;
              const res = await fetch(`${probeUrl}/api/health`, { method: 'GET', headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk }, signal: createTimeoutSignal(2000) });
              if (res.ok) {
                rawDevices.push({ DeviceName: 'PC (LAN)', DeviceType: 'PC', IsOnline: true, Url: probeUrl, LocalIp: probeUrl, _key: 'local_direct', _lanVerified: true, _lanUrl: probeUrl, Timestamp: NetworkClock.now() });
                break;
              }
            } catch (e) { /* LAN probe — expected to fail for unreachable IPs */ }
          }
        } else if (hasPc && pcLocalIp) {
          const parts = pcLocalIp.split(',');
          for (const part of parts) {
            const trimmed = part.trim();
            if (!trimmed) continue;
            const manualUrl = trimmed.startsWith('http') ? trimmed.replace(/\/$/, '') : `http://${trimmed.includes(':') ? trimmed : trimmed + ':8999'}`;
            const existingLan = rawDevices.some(d => d._lanUrl === manualUrl);
            if (!existingLan) {
              try {
                const res = await fetch(`${manualUrl}/api/health`, { method: 'GET', headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pk }, signal: createTimeoutSignal(1500) });
                if (res.ok) {
                  const pcIdx = rawDevices.findIndex(d => d.DeviceType === 'PC');
                  if (pcIdx >= 0) rawDevices[pcIdx] = { ...rawDevices[pcIdx], _lanVerified: true, _lanUrl: manualUrl, LocalIp: manualUrl };
                  break;
                }
              } catch (e) { syncLog('LAN-PROBE', `Manual IP health check failed for ${manualUrl}: ${(e as any)?.message || e}`); }
            }
          }
        }
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
    });

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
