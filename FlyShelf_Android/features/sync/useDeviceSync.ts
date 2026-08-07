import React, { useEffect, useRef } from 'react';
import { useLatest } from '../../hooks/useLatest';
import { Platform, NativeModules, ToastAndroid, AppState } from 'react-native';
import * as Clipboard from 'expo-clipboard';
import * as Notifications from 'expo-notifications';
import { database } from '../../firebaseConfig';
import { syncLog } from '../../utils/debugLog';
import { ref, get } from 'firebase/database';
import { ClipItem, getDownloadPath } from '../../utils/clipTypes';
import { fetchWithTimeout } from '../../utils/networkHelpers';
import { decryptDevice } from '../../utils/networkHelpers';
import { NetworkClock } from '../../utils/networkClock';
import { setSecureItem, removeSecureItem } from '../../utils/secureStorage';
import { createTimeoutSignal } from '../../utils/timeoutSignal';
import { normalizeTextForFingerprint } from '../../utils/textNormalize';
import AsyncStorage from '@react-native-async-storage/async-storage';

const { AdvanceOverlay } = NativeModules;

// Audit Task 1: normalizeTextForFingerprint now imported from utils/textNormalize.ts

export function useDeviceSync(params: {
  isGlobalSyncEnabled: boolean;
  pcLocalIp: string;
  deviceName: string;
  pairingKeyRef: React.MutableRefObject<string>;
  clipsStateRef: React.MutableRefObject<ClipItem[]>;
  pairedDevices: any[];
  getSyncPrefsForDevice: (id: string) => { clipboard: boolean };
  getCachedPcUrl: () => Promise<string>;
  cachedPcUrlRef: React.MutableRefObject<string | null>;
  cachedPcUrlTimestampRef: React.MutableRefObject<number>;
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
  recordCloudflareFailure: () => boolean;
  resetCloudflareFailCount: () => void;
  invalidatePcUrlCache: () => void;
  markPcReachable: () => void;
  markPcUnreachable: () => void;
  activeDevicesRef: React.MutableRefObject<any[]>;
  lastActivityRef: React.MutableRefObject<number>;
  lastSyncedContentRef: React.MutableRefObject<string>;
  processedEventsRef: React.MutableRefObject<Map<string, number>>;
  recentSyncFingerprintsRef: React.MutableRefObject<Map<string, number>>;
  sentContentFingerprintsRef: React.MutableRefObject<Set<string>>;
  pairingTimestampRef: React.MutableRefObject<number>;
  enqueueDownload: (item: any) => void;
  normalizeTextForFingerprint: (text: string) => string;
  setClips: React.Dispatch<React.SetStateAction<ClipItem[]>>;
  setConnectionInfo: (info: any) => void;
  setLastCopiedText: (text: string) => void;
  lastCopiedRef: React.MutableRefObject<string>;
  setImageDownloadTrigger: React.Dispatch<React.SetStateAction<number>>;
  setRichMediaDownloadTrigger: React.Dispatch<React.SetStateAction<number>>;
  updateDeviceStatus: (id: string, status: any) => void;
  isFloatingBallEnabled: boolean;
  scrollToTop: () => void;
  localDeletedIds: Set<string>;
  setLocalDeletedIds: React.Dispatch<React.SetStateAction<Set<string>>>;
}): void {
  const {
    isGlobalSyncEnabled,
    pcLocalIp,
    deviceName,
    pairingKeyRef,
    clipsStateRef,
    pairedDevices,
    getSyncPrefsForDevice,
    getCachedPcUrl,
    cachedPcUrlRef,
    cachedPcUrlTimestampRef,
    lastWorkingPcUrlRef,
    recordCloudflareFailure,
    resetCloudflareFailCount,
    invalidatePcUrlCache,
    markPcReachable,
    markPcUnreachable,
    activeDevicesRef,
    lastActivityRef,
    lastSyncedContentRef,
    processedEventsRef,
    recentSyncFingerprintsRef,
    sentContentFingerprintsRef,
    pairingTimestampRef,
    enqueueDownload,
    setClips,
    setConnectionInfo,
    setLastCopiedText,
    lastCopiedRef,
    setImageDownloadTrigger,
    setRichMediaDownloadTrigger,
    updateDeviceStatus,
    isFloatingBallEnabled,
    scrollToTop,
    localDeletedIds,
    setLocalDeletedIds,
  } = params;

  // ─── Stale-closure guards: wrap values that change independently of useEffect deps ───
  const pairedDevicesRef = useLatest(pairedDevices);
  const getSyncPrefsRef = useLatest(getSyncPrefsForDevice);
  const isFloatingBallEnabledRef = useLatest(isFloatingBallEnabled);

  // ─── Local PC Polling ───
  const pollLockRef = useRef(false); // Prevents concurrent pollFn from timer + long-poll
  const pollRetryCountRef = useRef(0); // Exponential backoff counter for failed polls
  const shortcutSyncTimestampRef = useRef<number>(0); // Throttle: sync shortcuts every 60s
  useEffect(() => {
    const pollFn = async () => {
      if (pollLockRef.current) return; // Already running — skip this invocation
      pollLockRef.current = true;
      try {
      // Gate: check if any paired device has clipboard sync enabled
      const currentPairedDevices = pairedDevicesRef.current;
      const currentGetSyncPrefs = getSyncPrefsRef.current;
      const anySyncEnabled = currentPairedDevices.length === 0 || currentPairedDevices.some(d => currentGetSyncPrefs(d.deviceId).clipboard);
      if (!anySyncEnabled) { pollLockRef.current = false; return; }
      const targetUrl = await getCachedPcUrl().catch(() => '');
      // Guard: skip poll entirely if no valid PC URL is available yet
      if (!targetUrl || !targetUrl.startsWith('http')) {
        syncLog('PC-POLL', 'No valid PC URL — skipping poll');
        pollLockRef.current = false;
        return;
      }
      if (Platform.OS === 'android' && AdvanceOverlay && targetUrl) {
        try { AdvanceOverlay.setPcUrl(targetUrl); } catch(e) {}
        try { if (deviceName) AdvanceOverlay.setDeviceName(deviceName); } catch(e) {}
      }
      try {
        const timeout = targetUrl.includes('trycloudflare.com') ? 5000 : 2000;
        const syncHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
        if (pairingKeyRef.current) syncHeaders['X-Pairing-Key'] = pairingKeyRef.current;
        const pollStart = performance.now();
        const response = await fetchWithTimeout(`${targetUrl}/api/sync`, { headers: syncHeaders }, timeout);
        if (!response.ok) {
          cachedPcUrlRef.current = null;
          // Track Cloudflare failures for forced re-resolution (Issue #7)
          if (targetUrl.includes('trycloudflare.com')) {
            if (recordCloudflareFailure()) {
              removeSecureItem('lastCloudflareUrl').catch(() => {});
              removeSecureItem('pairedGlobalUrl').catch(() => {});
            }
          }
          markPcUnreachable();
          return;
        }
        {
          // Phase 1: PC is reachable — disconnect Firebase listener if active
          markPcReachable();
          resetCloudflareFailCount();
          pollRetryCountRef.current = 0; // Reset backoff on successful connection
          // H-3: Connection quality indicator
          const pollLatency = Math.round(performance.now() - pollStart);
          setConnectionInfo({ url: targetUrl, latencyMs: pollLatency, type: targetUrl.includes('trycloudflare.com') ? 'Cloud' : 'LAN' });
          // Update paired device status with connection type & latency
          const connType = targetUrl.includes('trycloudflare.com') ? 'Cloud' as const : 'LAN' as const;
          pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
            updateDeviceStatus(d.deviceId, { isOnline: true, connectionType: connType, latencyMs: pollLatency, lastSeen: NetworkClock.now() });
          });
          // Mark this URL as proven-working for the image sweep to use
          lastWorkingPcUrlRef.current = targetUrl;
          // Phase 4: Read X-Global-Url header — PC sends its current Cloudflare URL in every response
          try {
            const globalUrl = response.headers.get('X-Global-Url');
            if (globalUrl && globalUrl.includes('trycloudflare.com')) {
              // Only persist if different from current cache (avoid redundant writes)
              const currentCached = cachedPcUrlRef.current;
              if (!currentCached || !currentCached.includes(globalUrl.split('//')[1]?.split('/')[0] || '')) {
                setSecureItem('lastCloudflareUrl', globalUrl).catch(() => {});
              }
            }
          } catch (e) { syncLog('PC-POLL', `Failed to read X-Global-Url header: ${(e as any)?.message || e}`); }
          const data = await response.json();
          if (Array.isArray(data) && data.length > 0) {
            const latest = data[0];

            // Guard: skip oversized items to prevent OOM
            if ((latest.Raw || '').length > 5_000_000) {
              syncLog('PC-POLL', 'Skipping oversized item');
              pollLockRef.current = false;
              return;
            }

            // ═══ DEDUP: Check EventId FIRST — before contentKey ═══
            // When items are deleted on PC, older items shift to data[0].
            // The EventId check must happen BEFORE contentKey comparison,
            // otherwise the changed contentKey bypasses EventId dedup.
            const lanEventId = latest.EventId || '';
            if (lanEventId && processedEventsRef.current.has(lanEventId)) return;

            // ═══ GUARD: Skip items from BEFORE this device paired ═══
            if (pairingTimestampRef.current > 0 && latest.Timestamp && latest.Timestamp < (pairingTimestampRef.current - 5000)) {
              return; // Pre-pairing item — don't sync to Android
            }
            const contentKey = `${latest.Type}_${latest.Title}_${latest.Timestamp}`;
            if (contentKey !== lastSyncedContentRef.current) {
              lastSyncedContentRef.current = contentKey;
              // Record this EventId as processed
              if (lanEventId) processedEventsRef.current.set(lanEventId, NetworkClock.now());

              const crossFp = `${latest.Type}::${(latest.Raw || '').substring(0, 150)}`;
              recentSyncFingerprintsRef.current.set(crossFp, NetworkClock.now());
              const isOwnEcho = (latest.SourceDeviceName && deviceName && latest.SourceDeviceName === deviceName) || (latest.SourceDeviceType === 'Mobile' && (!latest.SourceDeviceName || latest.SourceDeviceName === deviceName));

              if (!isOwnEcho) {
                lastActivityRef.current = NetworkClock.now(); // Keep adaptive polling at high frequency
                syncLog('PC-POLL', `New from PC: ${latest.Type} - ${(latest.Title || '').substring(0, 50)}`);
                if (latest.Type === 'Text' || latest.Type === 'Code' || latest.Type === 'Url') {
                  const latestRaw = latest.Raw;
                  if (latestRaw) {
                    const currentContent = await Clipboard.getStringAsync();
                    const normCurrent = normalizeTextForFingerprint(currentContent);
                    const normLatest = normalizeTextForFingerprint(latestRaw);
                    if (normCurrent !== normLatest) {
                      if (Platform.OS === 'android' && AdvanceOverlay) {
                        try { AdvanceOverlay.setClipboardSuppressed(latestRaw); } catch(e) { await Clipboard.setStringAsync(latestRaw); }
                      } else { await Clipboard.setStringAsync(latestRaw); }
                      setLastCopiedText(latestRaw);
                      lastCopiedRef.current = latestRaw;
                      if (Platform.OS === 'android') ToastAndroid.show(`📋 ${latestRaw.substring(0, 40)}...`, ToastAndroid.SHORT);
                      Notifications.scheduleNotificationAsync({
                        content: { title: '📋 Clipboard Synced', body: latestRaw?.substring(0, 80) || 'New content from PC' },
                        trigger: null,
                      }).catch(() => {});
                    }
                  }
                } else if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                  // Add image to feed immediately with FULL LAN URLs resolved
                  // Use the currently-connected targetUrl for immediate download
                  const resolvedItem = { ...latest };
                  if (resolvedItem.PreviewUrl?.startsWith('/')) resolvedItem.PreviewUrl = `${targetUrl}${resolvedItem.PreviewUrl}`;
                  if (resolvedItem.DownloadUrl?.startsWith('/')) resolvedItem.DownloadUrl = `${targetUrl}${resolvedItem.DownloadUrl}`;
                  if (resolvedItem.Raw?.startsWith('/')) resolvedItem.Raw = `${targetUrl}${resolvedItem.Raw}`;
                  setClips(prev => {
                    // Check ALL clips for duplicate by id, title, or raw content
                    const dupIdx = prev.findIndex(c =>
                      (c.id && latest.id && c.id === latest.id) ||
                      (c.Title && latest.Title && c.Title === latest.Title) ||
                      (c.Raw && latest.Raw && c.Raw.substring(0, 200) === latest.Raw.substring(0, 200))
                    );
                    if (dupIdx >= 0) {
                      // Dup found — update in place, do NOT move to top or scroll
                      const updated = [...prev];
                      updated[dupIdx] = { ...updated[dupIdx], ...resolvedItem, _needsDownload: !updated[dupIdx].CachedUri };
                      return updated;
                    }
                    // Genuinely new item — prepend and scroll
                    scrollToTop();
                    return [{ ...resolvedItem, _needsDownload: true, _receivedVia: 'LAN' as const } as any, ...prev];
                  });
                } else if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(latest.Type)) {
                  // Dedup by filename+timestamp (same key used by Firebase listener path)
                  const fileDedupKey = `filedl::${latest.Title || ''}::${latest.Timestamp || ''}`;
                  if (recentSyncFingerprintsRef.current.has(fileDedupKey)) {
                    // Already handled by Firebase path — skip
                  } else {
                    recentSyncFingerprintsRef.current.set(fileDedupKey, NetworkClock.now());
                    try {
                      // ── Smart URL resolution: extract path, probe LAN, fallback to Cloudflare ──
                      const dlPath = latest.DownloadUrl || latest.Raw || '';
                      const pathPart = dlPath.includes('?path=') ? dlPath.substring(dlPath.indexOf('?path=')) : '';
                      let fileUrl = '';
                      let dlSource = 'Cloud';

                      if (pathPart) {
                        // Try LAN first (fast) — check multiple known LAN endpoints
                        let lanBase = '';
                        const lanCandidates = [
                          ...(targetUrl && !targetUrl.includes('trycloudflare.com') ? [targetUrl] : []),
                          ...(lastWorkingPcUrlRef.current && !lastWorkingPcUrlRef.current.includes('trycloudflare.com') ? [lastWorkingPcUrlRef.current] : []),
                          ...(pcLocalIp ? pcLocalIp.split(',').map(s => s.trim()).filter(Boolean).map(ip => ip.startsWith('http') ? ip.replace(/\/$/, '') : `http://${ip.includes(':') ? ip : ip + ':8999'}`) : []),
                        ].filter((v, i, a) => a.indexOf(v) === i); // deduplicate

                        for (const candidate of lanCandidates) {
                          try {
                            const ctrl = new AbortController();
                            const timer = setTimeout(() => ctrl.abort(), 3000);
                            const h = await fetch(`${candidate}/api/health`, {
                              headers: { 
                                'X-FlyShelf-Client': 'MobileCompanion',
                                'X-Pairing-Key': pairingKeyRef.current || ''
                              },
                              signal: ctrl.signal,
                            });
                            clearTimeout(timer);
                            if (h.ok) { lanBase = candidate; break; }
                          } catch (e) { syncLog('FILE-DL', `LAN health probe failed for ${candidate}: ${(e as any)?.message || e}`); }
                        }
                        if (lanBase) {
                          fileUrl = `${lanBase}/download${pathPart}`;
                          dlSource = 'LAN';
                        } else {
                          // Cloudflare fallback — find from active devices
                          try {
                            const pk = pairingKeyRef.current;
                            if (pk) {
                              const devSnap = await get(ref(database, `active_devices/${pk}`));
                              if (devSnap.exists()) {
                                const devs = devSnap.val();
                                for (const dk of Object.keys(devs)) {
                                  const d = await decryptDevice(devs[dk]);
                                  if (d.GlobalUrl?.includes('trycloudflare.com') && d.DeviceType === 'PC') {
                                    fileUrl = `${d.GlobalUrl.replace(/\/$/, '')}/download${pathPart}`;
                                    break;
                                  }
                                }
                              }
                            }
                          } catch (e) { syncLog('FILE-DL', `Cloudflare device lookup failed: ${(e as any)?.message || e}`); }
                          // Also try if targetUrl itself is Cloudflare
                          if (!fileUrl && targetUrl?.includes('trycloudflare.com')) {
                            fileUrl = `${targetUrl}/download${pathPart}`;
                          }
                        }
                        // Last resort: use cached/resolved PC URL (works when CF is disabled)
                        if (!fileUrl && !lanBase) {
                          try {
                            const cachedUrl = cachedPcUrlRef.current || lastWorkingPcUrlRef.current;
                            if (cachedUrl) {
                              fileUrl = `${cachedUrl.replace(/\/$/, '')}/download${pathPart}`;
                              dlSource = cachedUrl.includes('trycloudflare.com') ? 'Cloud' : 'LAN';
                            }
                          } catch {}
                        }
                      } else if (dlPath.startsWith('http')) {
                        // Full absolute URL — use as-is (already has host)
                        fileUrl = dlPath;
                      }

                      syncLog('PC-POLL', `File DL: ${latest.Title} → ${dlSource}: ${fileUrl.substring(0, 80)}`);
                      if (fileUrl) {
                        const subfolder = latest.Type === 'Pdf' ? 'PDFs' : latest.Type === 'Video' ? 'Videos' : latest.Type === 'Audio' ? 'Audio' : 'Documents';
                        const safeName = (latest.Title || `file_${NetworkClock.now()}`).replace(/[^a-zA-Z0-9._-]/g, '_');
                        const destPath = await getDownloadPath(subfolder, safeName);
                        enqueueDownload({
                          id: latest.id || '', title: latest.Title || safeName, type: latest.Type,
                          fileUrl, destPath, source: dlSource,
                          sourceDevice: latest.SourceDeviceName || 'PC',
                        });
                      } else {
                        syncLog('PC-POLL', `File DL: ${latest.Title} — no download URL resolved`);
                      }
                    } catch (dlErr: any) {
                      syncLog('PC-POLL', `File DL error: ${latest.Title} — ${dlErr?.message || dlErr}`);
                    }
                  }
                  // Add file entry to feed so the user sees it (show filename, not URL)
                  setClips(prev => {
                    // Check ALL clips for duplicate by id, title, or raw content
                    const dupIdx = prev.findIndex(c =>
                      (c.id && latest.id && c.id === latest.id) ||
                      (c.Title && latest.Title && c.Title === latest.Title) ||
                      (c.Raw && latest.Raw && c.Raw.substring(0, 100) === latest.Raw.substring(0, 100))
                    );
                    if (dupIdx >= 0) {
                      // Dup found — update in place, do NOT move to top or scroll
                      const updated = [...prev];
                      updated[dupIdx] = { ...updated[dupIdx], Raw: updated[dupIdx].Title || updated[dupIdx].Raw };
                      return updated;
                    }
                    // Genuinely new item — prepend and scroll
                    scrollToTop();
                    return [{ ...latest, Raw: latest.Title || latest.Raw, Timestamp: NetworkClock.now(), _receivedVia: 'LAN' as const } as any, ...prev];
                  });
                  // REMOVED: UN-DELETE logic that brought back deleted items
                  // If user deleted an item locally, it stays deleted even if PC re-sends it
                }
                if (Platform.OS === 'android' && AdvanceOverlay && isFloatingBallEnabledRef.current) {
                  try {
                    if (latest.Type === 'Image' || latest.Type === 'ImageLink' || latest.Type === 'QRCode') {
                      const imgRaw = latest.PreviewUrl?.startsWith('/') ? `${targetUrl}${latest.PreviewUrl}` : latest.DownloadUrl?.startsWith('/') ? `${targetUrl}${latest.DownloadUrl}` : latest.Raw?.startsWith('http') ? latest.Raw : '';
                      if (imgRaw) AdvanceOverlay.pushClipToNativeDB(imgRaw, 'PC');
                    } else {
                      const rawForOverlay = latest.Raw || latest.Title || '';
                      if (rawForOverlay) AdvanceOverlay.pushClipToNativeDB(rawForOverlay, 'PC');
                    }
                  } catch(e: any) { console.warn('Overlay pushClip for sync item: error', e?.message || e); }
                }
                // ═══ Smart Notification (Change 2) ═══
                // Background: send OS notification. Foreground: scrollToTop handles the pill.
                if (AppState.currentState !== 'active') {
                  const typeEmoji: Record<string, string> = { 'Image': '📸', 'ImageLink': '📸', 'Pdf': '📄', 'Document': '📄', 'File': '📎', 'Video': '🎬', 'Audio': '🎵', 'Text': '📝', 'Url': '🔗', 'Code': '💻' };
                  const emoji = typeEmoji[latest.Type] || '📋';
                  const title = latest.Title || latest.Raw?.substring(0, 50) || 'New item';
                  Notifications.scheduleNotificationAsync({
                    content: {
                      title: `${emoji} ${latest.Type} arrived`,
                      body: title.length > 80 ? title.substring(0, 80) + '...' : title,
                      sound: 'default',
                    },
                    trigger: null, // Immediate
                  }).catch(() => {});
                }
              }
            }
          }
          // ── Sweep ALL items for file downloads (not just data[0]) ──
          // The data[0] path above only handles the latest item. Files (PDFs, docs, etc.)
          // that aren't the top item would otherwise be missed entirely.
          // Uses the download queue for sequential, non-blocking processing.
          for (const item of data) {
            if (!['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(item.Type)) continue;
            const isOwnEcho = (item.SourceDeviceName && deviceName && item.SourceDeviceName === deviceName) || (item.SourceDeviceType === 'Mobile' && (!item.SourceDeviceName || item.SourceDeviceName === deviceName));
            if (isOwnEcho) continue;
            const fileDedupKey = `filedl::${item.Title || ''}::${item.Timestamp || ''}`;
            if (recentSyncFingerprintsRef.current.has(fileDedupKey)) continue;
            recentSyncFingerprintsRef.current.set(fileDedupKey, NetworkClock.now());

            // Smart URL resolution
            try {
              const dlPath = item.DownloadUrl || item.Raw || '';
              const pathPart = dlPath.includes('?path=') ? dlPath.substring(dlPath.indexOf('?path=')) : '';
              let fileUrl = '';
              let dlSource = 'Cloud';

              if (pathPart) {
                let lanBase = '';
                const lanCandidates = [
                  ...(targetUrl && !targetUrl.includes('trycloudflare.com') ? [targetUrl] : []),
                  ...(lastWorkingPcUrlRef.current && !lastWorkingPcUrlRef.current.includes('trycloudflare.com') ? [lastWorkingPcUrlRef.current] : []),
                          ...(pcLocalIp ? pcLocalIp.split(',').map(s => s.trim()).filter(Boolean).map(ip => ip.startsWith('http') ? ip.replace(/\/$/, '') : `http://${ip.includes(':') ? ip : ip + ':8999'}`) : []),
                ].filter((v, i, a) => a.indexOf(v) === i);

                for (const candidate of lanCandidates) {
                  try {
                    const ctrl = new AbortController();
                    const timer = setTimeout(() => ctrl.abort(), 3000);
                    const h = await fetch(`${candidate}/api/health`, {
                      headers: { 
                        'X-FlyShelf-Client': 'MobileCompanion',
                        'X-Pairing-Key': pairingKeyRef.current || ''
                      },
                      signal: ctrl.signal,
                    });
                    clearTimeout(timer);
                    if (h.ok) { lanBase = candidate; break; }
                  } catch (e) { syncLog('FILE-DL', `LAN probe failed for ${candidate}: ${(e as any)?.message || e}`); }
                }
                if (lanBase) {
                  fileUrl = `${lanBase}/download${pathPart}`;
                  dlSource = 'LAN';
                } else {
                  try {
                    const pk = pairingKeyRef.current;
                    if (pk) {
                      const devSnap = await get(ref(database, `active_devices/${pk}`));
                      if (devSnap.exists()) {
                        const devs = devSnap.val();
                        for (const dk of Object.keys(devs)) {
                          const d = await decryptDevice(devs[dk]);
                          if (d.GlobalUrl?.includes('trycloudflare.com') && d.DeviceType === 'PC') {
                            fileUrl = `${d.GlobalUrl.replace(/\/$/, '')}/download${pathPart}`;
                            break;
                          }
                        }
                      }
                    }
                  } catch (e) { syncLog('FILE-DL', `Firebase device lookup failed: ${(e as any)?.message || e}`); }
                  if (!fileUrl && targetUrl?.includes('trycloudflare.com')) {
                    fileUrl = `${targetUrl}/download${pathPart}`;
                  }
                }
              } else if (dlPath.startsWith('http')) {
                fileUrl = dlPath;
              }

              if (fileUrl) {
                const subfolder = item.Type === 'Pdf' ? 'PDFs' : item.Type === 'Video' ? 'Videos' : item.Type === 'Audio' ? 'Audio' : 'Documents';
                const safeName = (item.Title || `file_${NetworkClock.now()}`).replace(/[^a-zA-Z0-9._-]/g, '_');
                const destPath = await getDownloadPath(subfolder, safeName);
                syncLog('PC-POLL', `File sweep → queue: ${item.Title} via ${dlSource}`);
                enqueueDownload({
                  id: item.id || '', title: item.Title || safeName, type: item.Type,
                  fileUrl, destPath, source: dlSource,
                  sourceDevice: item.SourceDeviceName || 'PC',
                });
              }
            } catch (dlErr: any) {
              syncLog('PC-POLL', `File sweep error: ${item.Title} — ${dlErr?.message || dlErr}`);
            }
          }
          setClips(current => {
            let merged = [...current];
            let changed = false;
            let hasNewItems = false;
            data.forEach((localItem: any) => {
              // Skip image items — background sweep handles feed entry + local download
              if (localItem.Type === 'Image' || localItem.Type === 'ImageLink' || localItem.Type === 'QRCode') return;
              // File types: show title instead of download URL
              if (['Pdf', 'Document', 'File', 'Video', 'Audio', 'Archive', 'Presentation'].includes(localItem.Type)) {
                localItem = { ...localItem, Raw: localItem.Title || localItem.Raw };
              }
              // Check ALL clips for duplicate by id, title, or raw content
              const dupIdx = merged.findIndex(m =>
                (m.id && localItem.id && m.id === localItem.id) ||
                (m.Title && localItem.Title && m.Title === localItem.Title) ||
                (m.Raw && localItem.Raw && m.Raw.substring(0, 100) === localItem.Raw.substring(0, 100))
              );
              if (dupIdx >= 0) {
                // Dup found — update in place, do NOT move to top
                merged[dupIdx] = { ...merged[dupIdx], ...localItem };
                changed = true;
              } else {
                // Genuinely new — add to top
                merged.unshift({ ...localItem, _receivedVia: 'Cloud' });
                changed = true;
                hasNewItems = true;
              }
            });
            if (!changed) return current;
            if (hasNewItems) scrollToTop(); // Only scroll for genuinely new items
            return merged;
          });
          // Trigger background download effects for new clips from LAN poll
          setImageDownloadTrigger(t => t + 1);
          setRichMediaDownloadTrigger(t => t + 1);
        }

        // Cleanup stale fingerprints (LAN path) — prevent unbounded growth
        if (recentSyncFingerprintsRef.current.size > 200) {
          const entries = [...recentSyncFingerprintsRef.current.entries()];
          const cutoff = Date.now() - 60000; // 60s window
          for (const [key, ts] of entries) {
            if (ts < cutoff) recentSyncFingerprintsRef.current.delete(key);
          }
        }

        // Cleanup stale processed events — prevent unbounded growth
        if (processedEventsRef.current.size > 500) {
          const entries = [...processedEventsRef.current.entries()];
          const cutoff = Date.now() - 600000; // 10min window
          for (const [key, ts] of entries) {
            if (ts < cutoff) processedEventsRef.current.delete(key);
          }
        }

        // ═══ Shortcut Sync — piggyback on successful PC connection ═══
        if (Platform.OS === 'android' && AdvanceOverlay && targetUrl) {
          const now = Date.now();
          if (!shortcutSyncTimestampRef.current || (now - shortcutSyncTimestampRef.current) > 60000) {
            shortcutSyncTimestampRef.current = now;
            try {
              const scHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion' };
              if (pairingKeyRef.current) scHeaders['X-Pairing-Key'] = pairingKeyRef.current;
              const scTimeout = targetUrl.includes('trycloudflare.com') ? 5000 : 2000;
              const scRes = await fetchWithTimeout(`${targetUrl}/api/shortcuts`, { headers: scHeaders }, scTimeout);
              if (scRes.ok) {
                const scData = await scRes.json();
                if (Array.isArray(scData)) {
                  try { AdvanceOverlay.syncShortcuts(JSON.stringify(scData)); } catch(e) {}
                  syncLog('SHORTCUTS', `Synced ${scData.length} shortcuts to overlay`);
                }
              }
            } catch (e) { /* Silent — shortcuts are non-critical */ }
          }
        }
      } catch (e) {
        syncLog('PC-POLL', `Poll failed: ${(e as any)?.message || e}`);
        pollRetryCountRef.current = Math.min(pollRetryCountRef.current + 1, 15); // Increment backoff counter
        // Track Cloudflare failures for forced re-resolution (Issue #7)
        const failUrl = cachedPcUrlRef.current || '';
        cachedPcUrlRef.current = null;
        if (failUrl.includes('trycloudflare.com')) {
          if (recordCloudflareFailure()) {
            removeSecureItem('lastCloudflareUrl').catch(() => {});
            removeSecureItem('pairedGlobalUrl').catch(() => {});
          }
        }
        markPcUnreachable();
      }
      } finally { pollLockRef.current = false; }
    };
    // Adaptive polling: 2s (LAN active) → 5s (Cloud) → 10s (idle/no PC) → re-evaluate every cycle
    lastActivityRef.current = NetworkClock.now();
    const getAdaptiveInterval = () => {
      const retries = pollRetryCountRef.current;
      // After 10 consecutive failures, switch to 60s low-frequency polling
      if (retries >= 10) return 60000;
      // Exponential backoff on failures: 1s, 2s, 4s, 8s... max 30s
      if (retries > 0) return Math.min(1000 * Math.pow(2, retries), 30000);
      const url = cachedPcUrlRef.current || '';
      const idleSecs = (NetworkClock.now() - lastActivityRef.current) / 1000;
      if (!url) return 10000; // No PC found — slow poll
      if (url.includes('trycloudflare')) return idleSecs > 120 ? 10000 : 5000; // Cloud: 5s active, 10s idle
      return idleSecs > 120 ? 5000 : 2000; // LAN: 2s active, 5s idle
    };
    // Initial poll
    pollFn();
    let pollTimer: ReturnType<typeof setTimeout> | null = null;
    const schedulePoll = () => {
      pollTimer = setTimeout(async () => {
        await pollFn();
        if (pollTimer !== null) schedulePoll(); // Re-evaluate interval each cycle
      }, getAdaptiveInterval());
    };
    schedulePoll();

    // ─── Long-Poll for instant notifications ───
    // /api/events blocks for up to 30s until clipboard changes on PC
    // When it returns 200, immediately fetch the new data via pollFn()
    // Guard: prevent double long-poll if effect re-runs before cleanup
    let longPollActive = true;
    let currentLongPollController: AbortController | null = null;
    let longPollBackoff = 0;
    const runLongPoll = async () => {
      // Wait for first successful poll to establish cachedPcUrlRef
      await new Promise(r => setTimeout(r, 3000));
      if (!longPollActive) return; // Bail if already torn down
      syncLog('LONG-POLL', 'Starting long-poll loop');
      while (longPollActive) {
        try {
          // Always resolve fresh URL (don't rely on potentially stale ref)
          const url = cachedPcUrlRef.current || (await getCachedPcUrl());
          if (!url) {
            syncLog('LONG-POLL', 'No PC URL — waiting 5s');
            await new Promise(r => setTimeout(r, 5000));
            continue;
          }
          try {
            const pairingKey = pairingKeyRef.current;
            const lpHeaders: any = { 'X-FlyShelf-Client': 'MobileCompanion' };
            if (pairingKey) lpHeaders['X-Pairing-Key'] = pairingKey;
            const controller = new AbortController();
            currentLongPollController = controller;
            const timeoutId = setTimeout(() => controller.abort(), 35000); // 35s timeout (server blocks 30s)
            const res = await fetch(`${url}/api/events`, { headers: lpHeaders, signal: controller.signal });
            clearTimeout(timeoutId);
            longPollBackoff = 0; // Reset backoff on success
            if (res.status === 200) {
              // Clipboard changed! Fetch the new data immediately
              lastActivityRef.current = NetworkClock.now();
              syncLog('LONG-POLL', '⚡ Instant notification — fetching now');
              await pollFn();
            }
            // 204 = timeout, no new events — loop again immediately
          } catch (innerErr: any) {
            if (!longPollActive) break;
            // Invalidate stale Cloudflare URL on long-poll failure
            if (url && url.includes('trycloudflare.com')) {
              removeSecureItem('lastCloudflareUrl').catch(() => {});
            }
            // Backoff on errors: 1s, 2s, 4s... max 10s (reduced from 30s)
            longPollBackoff = Math.min(longPollBackoff + 1, 4);
            const delay = Math.min(1000 * Math.pow(2, longPollBackoff), 10000);
            syncLog('LONG-POLL', `Error: ${innerErr?.message || innerErr} — retry in ${delay}ms`);
            await new Promise(r => setTimeout(r, delay));
          }
        } catch (outerErr: any) {
          // Top-level catch — NEVER let the loop die
          if (!longPollActive) break; // H-1 fix: prevent zombie loops on unmount
          syncLog('LONG-POLL', `Loop crash prevented: ${outerErr?.message || outerErr}`);
          await new Promise(r => setTimeout(r, 5000));
        }
      }
    };
    runLongPoll(); // Fire and forget — runs in background

    return () => {
      if (pollTimer !== null) { clearTimeout(pollTimer); pollTimer = null; }
      longPollActive = false; // Stop long-poll loop
      if (currentLongPollController) currentLongPollController.abort();
    };
  }, [isGlobalSyncEnabled, pcLocalIp, deviceName]);

  // ─── LAN Heartbeat — detect disconnection faster than cache TTL ───
  const heartbeatFailCountRef = useRef(0);
  const HEARTBEAT_INTERVAL = 10_000; // 10s
  const HEARTBEAT_MAX_FAILURES = 3;

  useEffect(() => {
    if (!isGlobalSyncEnabled) return;

    const heartbeatTimer = setInterval(async () => {
      const url = cachedPcUrlRef.current;
      if (!url) return;

      try {
        const res = await fetchWithTimeout(`${url}/api/health`, {
          headers: {
            'X-FlyShelf-Client': 'MobileCompanion',
            'X-Pairing-Key': pairingKeyRef.current || '',
          },
        }, 3000);
        if (res.ok) {
          heartbeatFailCountRef.current = 0;
          markPcReachable();
          // Track transport type for connection status
          const transport = url.includes('trycloudflare.com') ? 'Cloud' : 'LAN';
          setConnectionInfo({ url, latencyMs: 0, type: transport });
          return;
        }
      } catch {}

      heartbeatFailCountRef.current++;
      syncLog('HEARTBEAT', `PC health check failed (${heartbeatFailCountRef.current}/${HEARTBEAT_MAX_FAILURES})`);

      if (heartbeatFailCountRef.current >= HEARTBEAT_MAX_FAILURES) {
        syncLog('HEARTBEAT', 'PC unreachable — invalidating cache, triggering rediscovery');
        heartbeatFailCountRef.current = 0;
        invalidatePcUrlCache();
        markPcUnreachable();
        setConnectionInfo({ url: '', latencyMs: 0, type: 'Cloud' });
        // Force immediate re-resolution
        getCachedPcUrl().then(newUrl => {
          if (newUrl) {
            syncLog('HEARTBEAT', `Rediscovered PC at: ${newUrl}`);
            markPcReachable();
          }
        }).catch(() => {});
      }
    }, HEARTBEAT_INTERVAL);

    return () => clearInterval(heartbeatTimer);
  }, [isGlobalSyncEnabled]);
}
