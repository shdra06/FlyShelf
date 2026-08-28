import { useEffect, useRef } from 'react'; // L-2: Removed unused React default import
import { useLatest } from '../../hooks/useLatest';
import { Platform, NativeModules, ToastAndroid, AppState } from 'react-native';
import { toast } from '../../context/ToastContext';
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

// H-9 FIX: Extracted shared file download URL resolution helper to eliminate duplication
async function resolveFileDownloadUrl(params: {
  dlPath: string;
  targetUrl: string;
  lastWorkingPcUrlRef: React.MutableRefObject<string | null>;
  pcLocalIp: string;
  pairingKeyRef: React.MutableRefObject<string>;
  cachedPcUrlRef: React.MutableRefObject<string | null>;
}): Promise<{ fileUrl: string; dlSource: string }> {
  const { dlPath, targetUrl, lastWorkingPcUrlRef, pcLocalIp, pairingKeyRef, cachedPcUrlRef } = params;
  const pathPart = dlPath.includes('?path=') ? dlPath.substring(dlPath.indexOf('?path=')) : '';
  let fileUrl = '';
  let dlSource = 'Cloud';

  if (pathPart) {
    // Try LAN first (fast)
    let lanBase = '';
    const lanCandidates = [
      ...(targetUrl && !targetUrl.includes('trycloudflare.com') ? [targetUrl] : []),
      ...(lastWorkingPcUrlRef.current && !lastWorkingPcUrlRef.current.includes('trycloudflare.com') ? [lastWorkingPcUrlRef.current] : []),
      ...(pcLocalIp ? pcLocalIp.split(',').map(s => s.trim()).filter(Boolean).map(ip => ip.startsWith('http') ? ip.replace(/\/$/, '') : `http://${ip.includes(':') ? ip : ip + ':8999'}`) : []),
    ].filter((v, i, a) => a.indexOf(v) === i);

    // AUDIT FIX: Probe all LAN candidates in parallel (Promise.any) instead of sequential
    // Reduces worst-case from N*1200ms to just 1200ms
    if (lanCandidates.length > 0) {
      try {
        const winner = await Promise.any(lanCandidates.map(async (candidate) => {
          const ctrl = new AbortController();
          const timer = setTimeout(() => ctrl.abort(), 1200);
          try {
            const h = await fetch(`${candidate}/api/health`, {
              headers: { 'X-FlyShelf-Client': 'MobileCompanion', 'X-Pairing-Key': pairingKeyRef.current || '' },
              signal: ctrl.signal,
            });
            clearTimeout(timer);
            // AUDIT FIX #8: Validate response is actually FlyShelf, not a random server
            if (h.ok) {
              try {
                const body = await h.json();
                if (body?.app === 'FlyShelf') return candidate;
              } catch { /* non-JSON response — not FlyShelf */ }
            }
            throw new Error('not FlyShelf');
          } catch (e) {
            clearTimeout(timer);
            syncLog('FILE-DL', `LAN probe failed for ${candidate}: ${(e as any)?.message || e}`);
            throw e;
          }
        }));
        lanBase = winner;
      } catch { /* all candidates failed */ }
    }
    if (lanBase) {
      fileUrl = `${lanBase}/download${pathPart}`;
      dlSource = 'LAN';
    } else {
      // Cloudflare fallback via Firebase
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
    // Last resort: cached/resolved PC URL
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
    fileUrl = dlPath;
  }
  return { fileUrl, dlSource };
}

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
  const lastSyncTimestampRef = useRef<number>(0); // Incremental delta sync timestamp
  const isFirstSyncRef = useRef<boolean>(true); // Initial pairing sync tracker
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
        const syncHeaders: Record<string, string> = { 'X-FlyShelf-Client': 'MobileCompanion', 'Connection': 'keep-alive' };
        if (pairingKeyRef.current) syncHeaders['X-Pairing-Key'] = pairingKeyRef.current;
        const pollStart = performance.now();
        const syncUrl = lastSyncTimestampRef.current > 0
          ? `${targetUrl}/api/sync?since=${lastSyncTimestampRef.current}`
          : `${targetUrl}/api/sync?limit=3`;
        const response = await fetchWithTimeout(syncUrl, { headers: syncHeaders }, timeout);
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
          setConnectionInfo(null); // Clear stale status — subtitle goes back to "Searching..."
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
            for (const item of data) {
              if (item.Timestamp && item.Timestamp > lastSyncTimestampRef.current) {
                lastSyncTimestampRef.current = item.Timestamp;
              }
            }

            const isInitialPair = isFirstSyncRef.current;
            isFirstSyncRef.current = false;

            // On initial pair, process all 3 initial items chronologically (oldest to newest)
            const itemsToProcess = isInitialPair ? [...data].reverse() : data;

            for (const latest of itemsToProcess) {
              // Guard: skip oversized items to prevent OOM
              if ((latest.Raw || '').length > 5_000_000) {
                continue;
              }

              // ═══ DEDUP: Check EventId FIRST — before contentKey ═══
              const lanEventId = latest.EventId || '';
              if (lanEventId && processedEventsRef.current.has(lanEventId)) continue;

              // ═══ GUARD: Skip items from BEFORE this device paired (with 2-minute clock skew tolerance) ═══
              if (!isInitialPair && pairingTimestampRef.current > 0 && latest.Timestamp && latest.Timestamp < (pairingTimestampRef.current - 120_000)) {
                continue;
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
                      toast.clipboard(`Synced from ${latest.SourceDeviceName || 'PC'}`, latestRaw);
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

                        // AUDIT FIX: Probe all LAN candidates in parallel
                        if (lanCandidates.length > 0) {
                          try {
                            const winner = await Promise.any(lanCandidates.map(async (candidate) => {
                              const ctrl = new AbortController();
                              const timer = setTimeout(() => ctrl.abort(), 1500);
                              try {
                                const h = await fetch(`${candidate}/api/health`, {
                                  headers: { 
                                    'X-FlyShelf-Client': 'MobileCompanion',
                                    'X-Pairing-Key': pairingKeyRef.current || ''
                                  },
                                  signal: ctrl.signal,
                                });
                                clearTimeout(timer);
                                // AUDIT FIX #8: Validate response is actually FlyShelf
                                if (h.ok) {
                                  try {
                                    const body = await h.json();
                                    if (body?.app === 'FlyShelf') return candidate;
                                  } catch { /* non-JSON — not FlyShelf */ }
                                }
                                throw new Error('not FlyShelf');
                              } catch (e) {
                                clearTimeout(timer);
                                syncLog('FILE-DL', `LAN health probe failed for ${candidate}: ${(e as any)?.message || e}`);
                                throw e;
                              }
                            }));
                            lanBase = winner;
                          } catch { /* all candidates failed */ }
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

                // AUDIT FIX: Probe all LAN candidates in parallel
                if (lanCandidates.length > 0) {
                  try {
                    const winner = await Promise.any(lanCandidates.map(async (candidate) => {
                      const ctrl = new AbortController();
                      const timer = setTimeout(() => ctrl.abort(), 1500);
                      try {
                        const h = await fetch(`${candidate}/api/health`, {
                          headers: { 
                            'X-FlyShelf-Client': 'MobileCompanion',
                            'X-Pairing-Key': pairingKeyRef.current || ''
                          },
                          signal: ctrl.signal,
                        });
                        clearTimeout(timer);
                        // AUDIT FIX #8: Validate response is actually FlyShelf
                        if (h.ok) {
                          try {
                            const body = await h.json();
                            if (body?.app === 'FlyShelf') return candidate;
                          } catch { /* non-JSON — not FlyShelf */ }
                        }
                        throw new Error('not FlyShelf');
                      } catch (e) {
                        clearTimeout(timer);
                        syncLog('FILE-DL', `LAN probe failed for ${candidate}: ${(e as any)?.message || e}`);
                        throw e;
                      }
                    }));
                    lanBase = winner;
                  } catch { /* all candidates failed */ }
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
        // C-2 FIX: filedl:: keys now expire after 24h instead of being permanent
        if (recentSyncFingerprintsRef.current.size > 200) {
          const entries = [...recentSyncFingerprintsRef.current.entries()];
          const cutoff = Date.now() - 60000; // 60s window for non-filedl keys
          const fileDlCutoff = Date.now() - 24 * 60 * 60 * 1000; // 24h for filedl keys
          for (const [key, ts] of entries) {
            if (key.startsWith('filedl::')) {
              if (ts < fileDlCutoff) recentSyncFingerprintsRef.current.delete(key);
              continue;
            }
            if (ts < cutoff) recentSyncFingerprintsRef.current.delete(key);
          }
        }

        // Cleanup stale processed events — prevent unbounded growth
        // L-5 FIX: Tighter cleanup with 5min window (was 10min)
        if (processedEventsRef.current.size > 300) {
          const entries = [...processedEventsRef.current.entries()];
          const cutoff = Date.now() - 300000; // 5min window
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
        pollRetryCountRef.current = Math.min(pollRetryCountRef.current + 1, 4); // Capped at 4 for fast recovery
        
        // Invalidate in-memory cache so next poll runs fresh resolution (checking Firebase if needed)
        cachedPcUrlRef.current = null;
        recordCloudflareFailure();
        markPcUnreachable();
        setConnectionInfo(null);
      }
      } finally { pollLockRef.current = false; }
    };
    // Adaptive polling: 2s (LAN active) → 4s (Cloud) → 4s (retry) → re-evaluate every cycle
    lastActivityRef.current = NetworkClock.now();
    const getAdaptiveInterval = () => {
      const retries = pollRetryCountRef.current;
      // Fast retry while app is active — capped at 4s (never hang for 30-60s)
      if (retries > 0) return Math.min(1000 * retries, 4000);
      const url = cachedPcUrlRef.current || '';
      const idleSecs = (NetworkClock.now() - lastActivityRef.current) / 1000;
      if (!url) return 4000; // No PC found — continuously retry every 4s
      if (url.includes('trycloudflare')) return idleSecs > 120 ? 6000 : 3000; // Cloud: 3s active, 6s idle
      return idleSecs > 120 ? 4000 : 2000; // LAN: 2s active, 4s idle
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

    // ─── Instant Reconnect on App Foreground ───
    const appStateSub = AppState.addEventListener('change', (state) => {
      if (state === 'active') {
        syncLog('PC-POLL', '⚡ App foregrounded — resetting backoff and forcing instant probe');
        pollRetryCountRef.current = 0;
        cachedPcUrlRef.current = null;
        if (pollTimer !== null) clearTimeout(pollTimer);
        pollFn().then(() => schedulePoll());
      }
    });

    // ─── Unified Real-Time Duplex Engine (WebSocket Primary + Fallback Pipeline) ───
    let wsInstance: WebSocket | null = null;
    let wsPingInterval: any = null;
    let isTornDown = false;
    let longPollActive = false;
    let currentLongPollController: AbortController | null = null;

    const startLongPollFallback = async () => {
      if (longPollActive || isTornDown) return;
      longPollActive = true;
      syncLog('LONG-POLL', 'Engaging fallback long-poll listener');
      while (longPollActive && !isTornDown) {
        try {
          const url = cachedPcUrlRef.current || (await getCachedPcUrl().catch(() => ''));
          if (!url) {
            await new Promise(r => setTimeout(r, 2000));
            continue;
          }
          const pairingKey = pairingKeyRef.current;
          const lpHeaders: any = { 'X-FlyShelf-Client': 'MobileCompanion', 'Connection': 'keep-alive' };
          if (pairingKey) lpHeaders['X-Pairing-Key'] = pairingKey;
          const controller = new AbortController();
          currentLongPollController = controller;
          const timeoutId = setTimeout(() => controller.abort(), 35000);
          const res = await fetch(`${url}/api/events`, { headers: lpHeaders, signal: controller.signal });
          clearTimeout(timeoutId);
          if (res.status === 200) {
            lastActivityRef.current = NetworkClock.now();
            syncLog('LONG-POLL', '⚡ Instant push notification — delta sync fetching now');
            await pollFn();
          }
        } catch (innerErr: any) {
          if ((innerErr as any)?.name === 'AbortError') continue;
          if (isTornDown) break;
          await new Promise(r => setTimeout(r, 2000));
        }
      }
    };

    const startWebSocketEngine = async () => {
      if (isTornDown) return;
      try {
        const targetUrl = cachedPcUrlRef.current || (await getCachedPcUrl().catch(() => ''));
        if (!targetUrl || isTornDown) {
          setTimeout(startWebSocketEngine, 3000);
          return;
        }

        const pk = pairingKeyRef.current;
        const devId = deviceName || 'Mobile';
        const wsProto = targetUrl.startsWith('https') ? 'wss:' : 'ws:';
        const hostPart = targetUrl.replace(/^https?:\/\//, '').replace(/\/$/, '');
        const wsUrl = `${wsProto}//${hostPart}/ws/peer?key=${encodeURIComponent(pk || '')}&deviceId=${encodeURIComponent(devId)}`;

        syncLog('WS-PEER', `⚡ Opening persistent duplex socket: ${wsUrl}`);
        const ws = new WebSocket(wsUrl);
        wsInstance = ws;

        let wsConnected = false;
        const connType = targetUrl.includes('trycloudflare.com') ? 'Cloud' as const : 'LAN' as const;

        ws.onopen = () => {
          if (isTornDown) { ws.close(); return; }
          wsConnected = true;
          longPollActive = false; // Disable fallback when WebSocket is live
          markPcReachable();
          resetCloudflareFailCount();
          pollRetryCountRef.current = 0;
          syncLog('WS-PEER', `✅ Persistent Duplex Socket ESTABLISHED to ${targetUrl} (${connType})`);

          // Send initial ping and start 5s heartbeat
          const sendPing = () => {
            if (ws.readyState === WebSocket.OPEN) {
              const pingPayload = JSON.stringify({ type: 'Ping', ts: Date.now() });
              ws.send(pingPayload);
            }
          };
          sendPing();
          clearInterval(wsPingInterval);
          wsPingInterval = setInterval(sendPing, 5000);
        };

        ws.onmessage = (event) => {
          try {
            const raw = typeof event.data === 'string' ? event.data : '';
            if (!raw) return;
            if (raw === 'pong') {
              markPcReachable();
              return;
            }

            if (raw.startsWith('{')) {
              const msg = JSON.parse(raw);
              if (msg.type === 'Pong') {
                const now = Date.now();
                const latency = msg.ts ? Math.max(1, Math.round(now - msg.ts)) : 5;
                setConnectionInfo({ url: targetUrl, latencyMs: latency, type: connType });
                pairedDevicesRef.current.filter(d => d.deviceType === 'PC').forEach(d => {
                  updateDeviceStatus(d.deviceId, { isOnline: true, connectionType: connType, latencyMs: latency, lastSeen: NetworkClock.now() });
                });
                markPcReachable();
              } else if (msg.type === 'ClipboardPush' || msg.type === 'clipboard' || msg.type === 'SyncText') {
                syncLog('WS-PEER', `⚡ Instant Push from PC via WebSocket (${msg.type})`);
                lastActivityRef.current = NetworkClock.now();
                pollFn().catch(() => {});
              }
            }
          } catch (e: any) {
            syncLog('WS-PEER', `Message parse error: ${e?.message || e}`);
          }
        };

        ws.onerror = (err) => {
          syncLog('WS-PEER', `WebSocket error: ${(err as any)?.message || 'Socket error'}`);
        };

        ws.onclose = (ev) => {
          clearInterval(wsPingInterval);
          if (isTornDown) return;
          syncLog('WS-PEER', `WebSocket closed (code=${ev.code}). Engaging fallback & reconnecting.`);
          
          if (!wsConnected && !longPollActive) {
            startLongPollFallback();
          }
          
          setTimeout(startWebSocketEngine, 3000);
        };
      } catch (err: any) {
        if (!isTornDown) setTimeout(startWebSocketEngine, 3000);
      }
    };

    startWebSocketEngine();

    return () => {
      isTornDown = true;
      appStateSub.remove();
      if (pollTimer !== null) { clearTimeout(pollTimer); pollTimer = null; }
      clearInterval(wsPingInterval);
      if (wsInstance) {
        try { wsInstance.close(); } catch {}
      }
      longPollActive = false;
      if (currentLongPollController) currentLongPollController.abort();
    };
  }, [isGlobalSyncEnabled, pcLocalIp, deviceName]);
}
